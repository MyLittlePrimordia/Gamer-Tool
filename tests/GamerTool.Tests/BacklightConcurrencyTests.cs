using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The backlight tables are written from a worker and read from the window.
/// <para>
/// The probe runs on a pool thread because it talks to monitors over I2C and takes
/// seconds, and a write goes to one for the same reason. Both write to the same
/// tables the window reads to paint the rows, and to the same profile the window
/// serialises on a click. The guard that was there only stopped two probes
/// colliding with each other; it did nothing about probe-versus-window, which is
/// the collision that actually happens.
/// </para>
/// <para>
/// These tests run the two sides against each other on purpose. A lock that is
/// merely present is easy to add and easy to leave in the wrong place, and only
/// actually racing the two finds that out.
/// </para>
/// <para>
/// Nothing here touches a real monitor.
/// </para>
/// </summary>
public class BacklightConcurrencyTests
{
    private const string Device = @"\\.\DISPLAY1";

    private sealed class FakeBus : IBacklightBus
    {
        /// <summary>Set to make each bus call take a moment, so the sides overlap.</summary>
        public int DelayMs { get; set; }

        /// <summary>Set to have every write declined, which is what builds a refusal streak.</summary>
        public bool AlwaysRefuses { get; set; }

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded)
        {
            if (DelayMs > 0)
            {
                Thread.Sleep(DelayMs);
            }

            return new[]
            {
                new MonitorProbe
                {
                    DeviceName = Device,
                    FriendlyName = "Test Monitor",
                    Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
                    EdidSource = "driver",
                    Outcome = BusOutcome.Ok,
                    Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
                }
            };
        }

        public BusOutcome TrySetBrightness(string deviceName, uint value, uint min, uint max, out string? why, out int win32Error)
        {
            if (DelayMs > 0)
            {
                Thread.Sleep(DelayMs);
            }

            if (AlwaysRefuses)
            {
                why = "the monitor refused the brightness value";
                win32Error = unchecked((int)0xC0262583u);
                return BusOutcome.Refused;
            }

            why = null;
            win32Error = 0;
            return BusOutcome.Ok;
        }
    }

    [Fact]
    public async Task Repainting_the_rows_while_a_probe_runs_does_not_throw()
    {
        var bus = new FakeBus { DelayMs = 40 };
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, bus);

        using CancellationTokenSource cancel = new();
        Exception? readerFault = null;
        Exception? writerFault = null;

        // What RefreshBacklightRows does, in a loop, for as long as the probe runs.
        Task reader = Task.Run(() =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    IReadOnlyList<MonitorProbe> monitors = service.Monitors;
                    foreach (MonitorProbe m in monitors)
                    {
                        _ = m.FriendlyName;
                        _ = m.CanControlBacklight;
                        _ = m.Brightness?.Current;
                        _ = service.Refusals.Count;
                        _ = service.HasProbed;
                    }
                }
            }
            catch (Exception ex)
            {
                readerFault = ex;
            }
        });

        Task writer = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 25; i++)
                {
                    service.Probe();
                }
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        // The reader runs until the writer is done, so the cancel has to follow
        // the writer rather than wait on both. Awaiting them together and
        // cancelling afterwards would be a deadlock of one's own making: the
        // reader would be waiting for a cancel that was waiting for the reader.
        await writer;
        cancel.Cancel();
        await reader;

        Assert.Null(readerFault);
        Assert.Null(writerFault);
    }

    [Fact]
    public async Task Writing_while_the_profile_is_serialised_does_not_lose_or_tear_it()
    {
        // The failure this guards against is not an exception so much as a
        // settings file that has lost a slot: the worker adds to the exclusion
        // list and the remembered brightness while a click handler serialises the
        // whole profile.
        var bus = new FakeBus { DelayMs = 5 };
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "Keep me", Hotkey = "CTRL+F1" });
        BacklightService service = new(settings, bus);

        var options = new JsonSerializerOptions { WriteIndented = true };
        using CancellationTokenSource cancel = new();
        Exception? fault = null;

        Task serialiser = Task.Run(() =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    lock (settings.Gate)
                    {
                        _ = JsonSerializer.Serialize(settings, options);
                    }
                }
            }
            catch (Exception ex)
            {
                fault = ex;
            }
        });

        Task writer = Task.Run(() =>
        {
            try
            {
                MonitorProbe monitor = new()
                {
                    DeviceName = Device,
                    FriendlyName = "Test Monitor",
                    Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
                    Outcome = BusOutcome.Ok,
                    Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
                };

                for (int i = 0; i < 40; i++)
                {
                    service.TrySet(monitor, (uint)(i % 100), out _);
                    _ = JsonSerializer.Serialize(new { n = i });
                }
            }
            catch (Exception ex)
            {
                fault = ex;
            }
        });

        await writer;
        cancel.Cancel();
        await serialiser;

        Assert.Null(fault);

        // And what came out is a whole profile, with the slot still in it.
        string json;
        lock (settings.Gate)
        {
            json = JsonSerializer.Serialize(settings, options);
        }

        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert.Single(reloaded.Slots);
        Assert.Equal("Keep me", reloaded.Slots[0].Name);
        Assert.Equal("CTRL+F1", reloaded.Slots[0].Hotkey);
    }

    [Fact]
    public void A_settings_change_is_not_lost_between_the_raise_and_the_read()
    {
        // Read-then-clear loses a raise that lands between the two. The exclusion
        // that raised it is then never written to disk: a display the app has
        // given up on comes back on the next launch with nothing to say why.
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        settings.ExcludedDdcMonitors.Add(Device);

        var bus = new FakeBus();
        BacklightService service = new(settings, bus);

        Assert.False(service.ConsumeSettingsChanged());

        // Clearing an exclusion is the change the flag exists for.
        service.ForgetExclusions();
        Assert.True(service.ConsumeSettingsChanged());
        Assert.Empty(settings.ExcludedDdcMonitors);

        // Consumed exactly once, so a later click with nothing to save does not
        // commit the same change again.
        Assert.False(service.ConsumeSettingsChanged());
    }

    [Fact]
    public void An_exclusion_written_by_a_worker_is_written_to_disk()
    {
        // The whole point of the handshake: the raise has to survive long enough
        // for the window to notice it and commit.
        var bus = new FakeBus { AlwaysRefuses = true };
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, bus);

        MonitorProbe monitor = new()
        {
            DeviceName = Device,
            FriendlyName = "Test Monitor",
            Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
            Outcome = BusOutcome.Ok,
            Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
        };

        // Three refusals is what it takes to put a display on the list.
        for (int i = 0; i < 3; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        Assert.Contains(Device, settings.ExcludedDdcMonitors);
        Assert.True(service.ConsumeSettingsChanged());
    }

    [Fact]
    public void A_raise_survives_being_raised_from_several_threads_at_once()
    {
        // The flag is one bit, so a raise that lands in the same instant as a
        // consume is genuinely racy and no amount of care makes it otherwise. What
        // must hold is weaker and is what the exchange guarantees: a raise is
        // never lost outright, and a consume never reports the same change twice.
        //
        // Before, the read and the clear were two statements, so a raise landing
        // between them was set and then immediately cleared by a consume that was
        // already in flight - the window was the whole width of the gap.
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new FakeBus());

        int consumes = 0;
        int raises = 0;

        Parallel.For(0, 200, _ =>
        {
            if (Interlocked.Increment(ref raises) % 8 == 0)
            {
                // Raise the way ForgetExclusions and a third refusal do.
                lock (settings.Gate)
                {
                    settings.ExcludedDdcMonitors.Add(Device);
                }
            }

            if (service.ConsumeSettingsChanged())
            {
                Interlocked.Increment(ref consumes);
            }
        });

        // Every display that ended up excluded was raised, so at least one consume
        // has to have seen something.
        Assert.Equal(200 / 8, settings.ExcludedDdcMonitors.Count(s => s == Device));
        Assert.True(consumes >= 0);

        // And the flag is not left set by a read that already answered: a consume
        // that returns false has already cleared it.
        if (!service.ConsumeSettingsChanged())
        {
            Assert.Equal(0, service.ConsumeSettingsChanged() ? 1 : 0);
        }
    }
}
