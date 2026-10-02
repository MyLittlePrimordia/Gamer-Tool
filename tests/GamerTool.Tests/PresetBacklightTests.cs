using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A hardware backlight value carried by a display preset.
/// <para>
/// The gamma ramp and the panel brightness are set by different mechanisms, in
/// different parts of the system, and used to be independently controllable: the
/// slider in the Display tab wrote the panel immediately and nothing remembered it,
/// so a slot could not carry a brightness with it. Pressing a slot key set the ramp
/// and left the panel wherever the last slider position had put it, which means a
/// game preset that looked right on one monitor was applied to whichever one
/// happened to be dimmest.
/// </para>
/// <para>
/// <c>uint?</c> rather than a uint, and null is the load-bearing case rather than an
/// afterthought. Null means "leave the panel alone", so every preset written before
/// this field existed keeps doing exactly what it did. That is why the defaults are
/// null, why the neutral presets are null, and why the clamp treats an absurd value
/// as null instead of a number to fix up.
/// </para>
/// </summary>
public class PresetBacklightTests
{
    private const string Device = @"\\.\DISPLAY1";

    // ---- The null default, and why it is the one that matters ----

    [Fact]
    public void A_preset_says_nothing_about_the_panel_by_default()
    {
        // The overwhelmingly common case. A preset that has never had a backlight
        // set on it must not touch the panel, and the only way to say that without
        // a separate flag is for absence to be the default.
        Assert.Null(new DisplayPreset().Backlight);
    }

    [Fact]
    public void Every_built_in_preset_leaves_the_panel_alone()
    {
        // These ship with the app. None of them was authored against a specific
        // monitor's scale, so none of them may name a brightness - applying one on
        // an unfamiliar panel would set it to a number that means nothing there.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.Null(preset.Backlight);
        }
    }

    [Fact]
    public void The_neutral_preset_leaves_the_panel_alone()
    {
        // "Back to normal" must not include the panel. What the panel should return
        // to is where the user had it, and that is the restore map's business, not
        // something a gamma preset should be asserting.
        Assert.Null(DisplayPreset.Flat().Backlight);
    }

    // ---- Round-tripping, including the old shape ----

    [Fact]
    public void A_backlight_survives_a_save_and_load()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        settings.CustomDisplayPresets.Add(new DisplayPreset
        {
            Id = "mine",
            Name = "Mine",
            Backlight = 42,
        });

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions())!;

        Assert.Equal(42u, loaded.CustomDisplayPresets[0].Backlight);
    }

    [Fact]
    public void A_profile_written_before_the_field_existed_still_loads()
    {
        // The compatibility promise, pinned. This is the shape of every profile
        // already on disk: no Backlight key anywhere. It has to load to null rather
        // than throwing, and null has to mean "do not touch" - otherwise adding a
        // field silently changes what every existing slot does.
        const string json = """
        {
          "HardwareBrightnessEnabled": true,
          "CustomDisplayPresets": [
            { "Id": "old", "Name": "Old Preset", "Gamma": 1.80 }
          ]
        }
        """;

        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions());

        Assert.NotNull(loaded);
        Assert.Null(loaded!.CustomDisplayPresets[0].Backlight);
    }

    [Fact]
    public void A_zero_is_a_real_value_and_is_kept()
    {
        // Not the same as absent. A user who deliberately dims a panel to nothing for
        // a dark game needs that to survive the round trip; treating 0 as "unset"
        // would quietly refuse to do the thing they asked for, and would leave the
        // panel at whatever the last preset set it to.
        AppSettings settings = new();
        settings.CustomDisplayPresets.Add(new DisplayPreset { Id = "dark", Name = "Dark", Backlight = 0 });

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions());
        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions());

        Assert.Equal(0u, loaded!.CustomDisplayPresets[0].Backlight);
    }

    // ---- Copy ----

    [Fact]
    public void Copying_a_preset_carries_its_backlight()
    {
        // The plus button on a built-in preset. A backlight lost here would give the
        // user a duplicate that silently stopped managing the panel, which is the
        // failure this feature exists to remove.
        DisplayPreset copy = new DisplayPreset { Id = "a", Name = "A", Backlight = 33, Gamma = 1.90 }.Copy();

        Assert.Equal(33u, copy.Backlight);
    }

    [Fact]
    public void Copying_a_preset_with_no_backlight_produces_one_with_no_backlight()
    {
        DisplayPreset copy = new DisplayPreset { Id = "a", Name = "A" }.Copy();

        Assert.Null(copy.Backlight);
    }

    [Fact]
    public void Copying_does_not_share_the_backlight_field()
    {
        // A reference here would mean editing one preset's backlight edited its
        // duplicate too, and the two would drift apart only in the saved file.
        DisplayPreset original = new() { Id = "a", Name = "A", Backlight = 10 };
        DisplayPreset copy = original.Copy();

        copy.Backlight = 90;

        Assert.Equal(10u, original.Backlight);
    }

    // ---- Clamp: the guard on a value that came from somewhere else ----

    [Fact]
    public void A_plausible_backlight_is_kept()
    {
        DisplayPreset preset = new() { Backlight = 60 };

        preset.Clamp();

        Assert.Equal(60u, preset.Backlight);
    }

    [Fact]
    public void An_absurd_backlight_becomes_leave_the_panel_alone()
    {
        // Rejected rather than clamped. The number is on the monitor's own scale,
        // and the widest real panels report 100, so anything past a few hundred is a
        // hand-typed value or a file from another program. Clamping it to the top
        // of the scale would set the panel to full brightness because of a typo,
        // which is worse than doing nothing.
        DisplayPreset preset = new() { Backlight = DisplayPreset.MaxPlausibleBacklight + 1 };

        preset.Clamp();

        Assert.Null(preset.Backlight);
    }

    [Fact]
    public void The_largest_plausible_backlight_is_still_kept()
    {
        // The boundary, in the accepting direction: a wide-range panel reporting a
        // number near the cap is real and must survive.
        DisplayPreset preset = new() { Backlight = DisplayPreset.MaxPlausibleBacklight };

        preset.Clamp();

        Assert.Equal(DisplayPreset.MaxPlausibleBacklight, preset.Backlight);
    }

    [Fact]
    public void Clamping_keeps_the_rest_of_the_preset()
    {
        // The backlight is one field among several. Dropping it must not disturb the
        // ramp, or a malformed profile would come back looking like a different
        // preset than the one the user saved.
        DisplayPreset preset = new() { Id = "a", Name = "A", Backlight = 5000, Gamma = 1.85, Contrast = 0.12 };

        preset.Clamp();

        Assert.Equal("a", preset.Id);
        Assert.Equal("A", preset.Name);
        Assert.Equal(1.85, preset.Gamma, 3);
        Assert.Equal(0.12, preset.Contrast, 3);
    }

    [Fact]
    public void An_absent_backlight_stays_absent_through_a_clamp()
    {
        DisplayPreset preset = new();

        preset.Clamp();

        Assert.Null(preset.Backlight);
    }

    // ---- The write path ----

    [Fact]
    public void A_preset_backlight_lands_on_the_panel()
    {
        // The behaviour the feature is for: the stored value reaches the hardware,
        // and the panel ends up at the brightness the preset names.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new RecordingBus());
        MonitorProbe monitor = LiveMonitor();

        BusOutcome outcome = service.TrySetPresetBacklight(monitor, 40, out string? why);

        Assert.Equal(BusOutcome.Ok, outcome);
        Assert.Null(why);
        Assert.Equal(40u, monitor.Brightness!.Current);
    }

    [Fact]
    public void A_busy_panel_is_retried_and_the_value_still_lands()
    {
        // A preset write shares the bus with whatever else is running, and with
        // three monitors a write to one can collide with a transaction on another.
        // Dropping the value would leave the ramp from this preset and the panel
        // from the last one, with nothing on screen saying so.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new BusyTwiceBus());
        MonitorProbe monitor = LiveMonitor();

        BusOutcome outcome = service.TrySetPresetBacklight(monitor, 35, out _);

        Assert.Equal(BusOutcome.Ok, outcome);
        Assert.Equal(35u, monitor.Brightness!.Current);
    }

    [Fact]
    public void A_bus_that_never_frees_up_is_given_up_on()
    {
        // Bounded, because a panel wedged by a monitor that has stopped answering
        // must not be chased forever. It also has to say what happened: the gamma
        // half of the preset has already been applied by this point, so the screen
        // and the panel are now from two different presets and the user is the only
        // one who can tell.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new AlwaysBusyBus());
        MonitorProbe monitor = LiveMonitor();

        BusOutcome outcome = service.TrySetPresetBacklight(monitor, 40, out string? why);

        Assert.Equal(BusOutcome.Busy, outcome);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_busy_panel_is_only_retried_a_few_times()
    {
        // The number of attempts matters as much as the outcome: each one holds the
        // bus, so an unbounded retry would stall the apply itself.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new AlwaysBusyBus();
        BacklightService service = new(settings, bus);

        service.TrySetPresetBacklight(LiveMonitor(), 40, out _);

        Assert.Equal(3, bus.Calls);
    }

    [Fact]
    public void A_panel_that_refuses_is_not_retried()
    {
        // Every outcome except busy is a real answer from the display. Repeating it
        // would spend the bus claim three times and end in the same place.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new RefusingBus();
        BacklightService service = new(settings, bus);

        service.TrySetPresetBacklight(LiveMonitor(), 40, out _);

        Assert.Equal(1, bus.Calls);
    }

    [Fact]
    public void A_preset_backlight_is_clamped_to_the_panel_it_lands_on()
    {
        // The stored value is on the monitor's scale and so is the target, but they
        // are not the same scale - a stored 80 written to a panel that tops out at
        // 60 has to become 60, not wrap, and not be refused.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new RecordingBus());
        MonitorProbe monitor = LiveMonitor();
        monitor.Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 60 };

        service.TrySetPresetBacklight(monitor, 80, out _);

        Assert.Equal(60u, monitor.Brightness!.Current);
    }

    [Fact]
    public void A_display_that_cannot_be_driven_is_refused_rather_than_written_to()
    {
        // The common case is a laptop panel with no DDC/CI pathway behind it. The
        // preset has to say so rather than quietly pick a different screen.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new RecordingBus());

        var panel = new MonitorProbe
        {
            DeviceName = Device,
            FriendlyName = "Laptop Panel",
            Edid = new EdidReading { Manufacturer = "LAP", Verdict = EdidVerdict.Plausible },
            Brightness = null,
            Outcome = BusOutcome.Ok,
        };

        BusOutcome outcome = service.TrySetPresetBacklight(panel, 40, out string? why);

        Assert.Equal(BusOutcome.Failed, outcome);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_preset_write_records_where_the_panel_was_so_it_can_go_back()
    {
        // Without this there is nothing to restore on panic or exit, and the whole
        // reason the value is remembered separately from the map is that the map is
        // emptied by the restore itself.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new RecordingBus());
        MonitorProbe monitor = LiveMonitor();

        service.TrySetPresetBacklight(monitor, 40, out _);

        Assert.Contains(Device, settings.OriginalHardwareBrightness.Keys);
    }

    private static MonitorProbe LiveMonitor() => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
        Outcome = BusOutcome.Ok,
    };

    private sealed class RecordingBus : IBacklightBus
    {
        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            why = null;
            error = 0;
            return BusOutcome.Ok;
        }
    }

    private sealed class AlwaysBusyBus : IBacklightBus
    {
        public int Calls { get; private set; }

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            Calls++;
            why = "skipped: another call was still on the bus";
            error = 0;
            return BusOutcome.Busy;
        }
    }

    /// <summary>Busy twice, then free. Models a collision that clears.</summary>
    private sealed class BusyTwiceBus : IBacklightBus
    {
        private int _calls;

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            if (_calls++ < 2)
            {
                why = "skipped: another call was still on the bus";
                error = 0;
                return BusOutcome.Busy;
            }

            why = null;
            error = 0;
            return BusOutcome.Ok;
        }
    }

    private sealed class RefusingBus : IBacklightBus
    {
        public int Calls { get; private set; }

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            Calls++;
            why = "the display did not accept the value";
            error = 0;
            return BusOutcome.Failed;
        }
    }
}