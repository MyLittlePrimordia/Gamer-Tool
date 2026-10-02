using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Owns the hardware backlight feature for the window: when it is allowed to
/// probe, what each display can do, and writing a new value.
/// <para>
/// Everything here is per display. There is no "does this machine support
/// DDC/CI" flag anywhere, because that question has no single answer: one
/// display can answer VCP 0x10 while the one next to it has no pathway at all,
/// and a shared flag would disable a working monitor because an unlucky one was
/// plugged in.
/// </para>
/// </summary>
/// <summary>
/// Everything <see cref="BacklightService"/> needs from the hardware, behind a
/// seam.
/// <para>
/// The service otherwise talks straight to a static class that opens an I2C bus,
/// which makes the interesting part of it untestable: the refusal counting, the
/// exclusion list and the busy-skip handling are all pure bookkeeping over
/// answers, and none of it can be exercised without a monitor on the desk. This
/// exists so those rules can be pinned down in CI, and so no test has to be
/// written that touches real hardware.
/// </para>
/// </summary>
public interface IBacklightBus
{
    IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys);

    BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error);
}


/// <summary>The real bus. A thin wrapper so the service has one thing to hold.</summary>
public sealed class SystemBacklightBus : IBacklightBus
{
    public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys) =>
        HardwareBrightness.ProbeAll(excludedDeviceKeys);

    public BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error) =>
        HardwareBrightness.TrySetBrightness(deviceName, value, minimum, maximum, out why, out win32Error);
}


/// <summary>
/// How many times a write is re-sent when the bus was busy rather than refused.
/// <para>
/// Pulled out of the window so it can be tested. The policy is small but it is
/// the difference between a slider that lands where it was left and one that
/// silently does nothing: a write that collided with another monitor's
/// transaction used to be dropped on the floor, the row still showed the new
/// value, and the panel stayed where it was with no error anywhere.
/// </para>
/// <para>
/// The budget is one, deliberately. A retry once handles the ordinary case,
/// where a probe of the next monitor was in flight for a few milliseconds. An
/// unbounded retry would keep re-queueing against a bus that is wedged by a
/// monitor that has stopped answering, which is the one situation where a
/// brightness value must not be chasing the panel forever.
/// </para>
/// </summary>
public sealed class BacklightWriteRetry
{
    /// <summary>Re-sends allowed per display before the value is given up on.</summary>
    public const int MaxRetries = 1;

    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this display may be re-sent now. Counts as consumed when it
    /// says yes, so a second busy answer for the same display says no and the
    /// caller reports the failure instead of looping.
    /// </summary>
    public bool ShouldRetry(string deviceName)
    {
        int used = _attempts.TryGetValue(deviceName, out int seen) ? seen : 0;

        if (used >= MaxRetries)
        {
            return false;
        }

        _attempts[deviceName] = used + 1;
        return true;
    }

    /// <summary>Called on any answer that was not a skip, including a success.</summary>
    public void Reset(string deviceName)
    {
        _attempts.Remove(deviceName);
    }

    /// <summary>Retries still unspent, for the log.</summary>
    public int Remaining(string deviceName) =>
        _attempts.TryGetValue(deviceName, out int used) ? Math.Max(0, MaxRetries - used) : MaxRetries;
}


public sealed class BacklightService
{
    private AppSettings _settings;

    private readonly IBacklightBus _bus;

    private readonly Dictionary<string, MonitorProbe> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards this service's own tables.
    /// <para>
    /// The probe runs on a worker because it talks to monitors over I2C and takes
    /// seconds, and a write goes to a worker for the same reason. Both of them
    /// write to <see cref="_byDevice"/> and <see cref="_refusals"/>, and the window
    /// reads both to paint the rows. <see cref="_probeGate"/> only kept two probes
    /// from colliding with each other; it did nothing about probe-versus-window,
    /// which is the collision that actually happens - opening the Display tab
    /// starts a probe, and the rows are being repainted while it runs.
    /// </para>
    /// <para>
    /// Every public accessor therefore answers from a copy taken under this lock
    /// rather than from the live collection, so no caller can be iterating a
    /// dictionary that a worker is rebuilding. The probe still runs unlocked
    /// against the hardware, because holding a lock across an I2C transaction
    /// would stall the window for the length of it, which is the failure being
    /// fixed.
    /// </para>
    /// </summary>
    private readonly object _gate = new();

    private volatile bool _probed;

    /// <summary>1 while a settings change is waiting to be written.</summary>
    private int _settingsChanged;

    /// <summary>
    /// Set by <see cref="ForgetExclusions"/> so the probe it makes possible does
    /// not count as a round.
    /// <para>
    /// That probe is the app answering its own switch rather than a fresh look at
    /// the hardware: it runs within a second of the user turning the feature back
    /// on, and it is the round that would otherwise consume the first half of the
    /// grace period before the user had closed the window. One-shot, so the launch
    /// after that one counts normally and the streak can still reach the
    /// threshold.
    /// </para>
    /// </summary>
    private bool _nextRoundIsFree;

    public BacklightService(AppSettings settings, IBacklightBus? bus = null)
    {
        _settings = settings;

        // Whatever the last session left in the remembered-brightness map is not a
        // record of anything this one can act on. It records what a display was
        // at while a different run of the app held it, and this session has not
        // touched a display yet, so those numbers say nothing about now.
        //
        // Keeping them is worse than losing them. TrySet only captures a
        // display's brightness when the map has no entry for it, so a leftover
        // entry suppresses the capture entirely, and RestoreAll later puts the
        // display back to a value from a session that has since ended. The map is
        // never read back to recover from a crash - there is no startup restore -
        // so an entry carried across a launch has no upside at all, only this.
        // Started empty, the first write this session captures the value that is
        // actually on the screen right now.
        settings.OriginalHardwareBrightness.Clear();

        // The composite rather than the DDC bus, so a laptop's own screen is
        // reachable by the same code that drives an external monitor. On a machine
        // with no panel the composite returns exactly what the DDC bus would.
        _bus = bus ?? new CompositeBacklightBus();
        Availability.RestoreFrom(settings.HardwareBrightnessFailedRounds, settings.HardwareBrightnessRetired);
    }

    /// <summary>
    /// Points the service at a different settings object.
    /// <para>
    /// The service holds the profile it was built with, so a restore that swaps
    /// the whole object for the incoming one leaves this reading and writing an
    /// orphan: the enabled flag stops reflecting reality, and the remembered
    /// brightness and the exclusion list are written somewhere nothing ever
    /// saves. Rebinding is what keeps one profile the single source of truth.
    /// </para>
    /// </summary>
    public void Rebind(AppSettings settings)
    {
        if (settings is not null)
        {
            _settings = settings;

            // Carried across with the profile rather than left behind with the
            // old one. A restore replaces the whole settings object, so a counter
            // that stayed in memory would be counting rounds against a profile
            // that no longer exists.
            Availability.RestoreFrom(settings.HardwareBrightnessFailedRounds, settings.HardwareBrightnessRetired);
        }
    }


    /// <summary>Raised after a probe finishes, so the row can redraw itself.</summary>
    public event Action? Probed;


    public bool IsEnabled => _settings.HardwareBrightnessEnabled;


    public IReadOnlyList<MonitorProbe> Monitors
    {
        get
        {
            lock (_gate)
            {
                return _byDevice.Values.ToList();
            }
        }
    }


    /// <summary>
    /// Reads every display once. Only ever called when the user has opted in, and
    /// never at startup: the first thing this app should not do is touch the I2C
    /// bus before being asked.
    /// <para>
    /// Single flight on purpose. The probe is started from two places, the setup
    /// tab and the tab switch, and the old guard was only set once a probe had
    /// finished, so two could start together. They then cleared and repopulated
    /// the same dictionary underneath each other, and whichever finished last won:
    /// a half filled table, or a display that had been probed and then dropped
    /// without ever having been asked anything.
    /// </para>
    /// </summary>
    public void Probe()
    {
        if (!_settings.HardwareBrightnessEnabled)
        {
            return;
        }

        lock (_probeGate)
        {
            if (_probing)
            {
                // Someone else is already asking the monitors. Joining them is
                // both cheaper and safer than starting a second pass.
                AppLog.Info("backlight probe already running, not starting another");
                return;
            }

            _probing = true;
        }

        try
        {
            // Built beside the live table and swapped in at the end, rather than
            // cleared and refilled in place. The window can be reading the rows
            // from this whole time, and a clear followed by a repopulate is a
            // window in which it sees half a table; swapping means it sees the
            // old one until the new one is whole.
            Dictionary<string, MonitorProbe> found = new(StringComparer.OrdinalIgnoreCase);
            foreach (MonitorProbe monitor in _bus.ProbeAll(_settings.ExcludedDdcMonitors))
            {
                found[monitor.DeviceName] = monitor;
            }

            lock (_gate)
            {
                _byDevice.Clear();
                foreach (KeyValuePair<string, MonitorProbe> entry in found)
                {
                    _byDevice[entry.Key] = entry.Value;
                }
            }

            _probed = true;

            IReadOnlyList<MonitorProbe> all = Monitors;
            foreach (MonitorProbe monitor in all)
            {
                // The probe result is the single most useful line in the log when
                // someone reports that brightness will not move: it says what the
                // driver said, per display, without anyone having to reproduce it.
                // The stage reached and the error are in there deliberately, so
                // the difference between "no handle from the driver" and "declined
                // the code" is readable without a debugger.
                AppLog.Info("backlight " + monitor.DeviceName + " " + monitor.FriendlyName
                    + " edid=" + monitor.Edid.Summary
                    + " edidFrom=" + monitor.EdidSource
                    + (monitor.EdidWithheld ? "(withheld)" : string.Empty)
                    + " hdcpOverride=" + monitor.Protection.State
                    + " target=" + monitor.Target.Health
                    + " link=" + monitor.Link
                    + " capable=" + monitor.CanControlBacklight
                    + " reading=" + (monitor.Brightness?.ToString() ?? "none")
                    + " stage=" + StageOf(monitor)
                    + " outcome=" + monitor.Outcome
                    + " win32=" + (monitor.LastError ?? "none")
                    + " attempts=" + monitor.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " probeMs=" + monitor.ProbeMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " phys=" + (monitor.PhysicalMonitor ?? "none")
                    + " excluded=" + _settings.ExcludedDdcMonitors.Contains(monitor.DeviceName)
                    + " refusals=" + RefusalCountOf(monitor.DeviceName).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " reason=" + (monitor.BlockedReason ?? monitor.NoReplyBecause ?? "none"));
            }

            // Counted after every display has been logged, so a round that turns
            // the feature off still leaves a complete record of why.
            if (_nextRoundIsFree)
            {
                _nextRoundIsFree = false;
                Availability.RecordWithoutCounting(all);
                AppLog.Info(
                    "backlight round not counted, it answers the switch the user just moved; streak="
                    + Availability.ConsecutiveRounds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " of " + BacklightAvailability.RoundsBeforeRetiring.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                Availability.Record(all);
            }

            // Written straight back, because a round spans a launch. Without this
            // the count can only ever reach one, and a rule that retires after two
            // is a rule that never fires. Under the profile's gate, because a
            // click handler on the window can be serialising it right now.
            lock (_settings.Gate)
            {
                (int rounds, bool retired) = Availability.Persistable();
                if (rounds != _settings.HardwareBrightnessFailedRounds
                    || retired != _settings.HardwareBrightnessRetired)
                {
                    _settings.HardwareBrightnessFailedRounds = rounds;
                    _settings.HardwareBrightnessRetired = retired;
                    Interlocked.Exchange(ref _settingsChanged, 1);
                }
            }
        }
        finally
        {
            lock (_probeGate)
            {
                _probing = false;
            }
        }

        Probed?.Invoke();
    }

    /// <summary>
    /// True when the last probe decided the feature should turn itself off.
    /// <para>
    /// Polled once per probe rather than acted on inside
    /// <see cref="Probe"/>, because turning the switch off writes settings and
    /// repaints the settings tab, and a probe runs on a background thread where
    /// neither belongs.
    /// </para>
    /// </summary>
    public bool ConsumeRetirement()
    {
        if (!Availability.JustRetired)
        {
            return false;
        }

        AppLog.Warn("backlight turning itself off: no display could be reached over DDC/CI in "
            + BacklightAvailability.RoundsBeforeRetiring.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " rounds in a row across "
            + Availability.LastRoundDisplays.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " display(s). The settings tab now says so, and turning the switch back on retries");

        // Taken, because the named promise is that it happens once. It did not, and
        // the probe that raised it has stopped running by now - the feature has
        // turned itself off, so there is nothing left to record a round - so
        // nothing would ever have cleared the flag and every later read would have
        // logged this again.
        Availability.AcknowledgeRetirement();
        return true;
    }


    private readonly object _probeGate = new();

    private bool _probing;

private int RefusalCountOf(string deviceName)
    {
        lock (_gate)
        {
            return _refusals.TryGetValue(deviceName, out int seen) ? seen : 0;
        }
    }

    /// <summary>
    /// The furthest point the probe got, in words. This is the difference between
    /// a monitor that said no and a machine that never asked, and it is the first
    /// thing worth reading when a display is greyed out for no visible reason.
    /// </summary>
    public static string StageOf(MonitorProbe monitor)
    {
        if (monitor.BlockedReason is not null)
        {
            return monitor.BlockedReason.StartsWith("on your exclusion", StringComparison.Ordinal)
                ? "excluded"
                : "edid-preflight";
        }

        return monitor.Outcome switch
        {
            BusOutcome.Ok => "vcp-read-ok",
            BusOutcome.NoDdcPathway => monitor.Target.Health == TargetHealth.Stub
                ? "no-ddc-handle/target-stub"
                : "no-ddc-handle",
            BusOutcome.Refused => "vcp-declined",
            BusOutcome.TimedOut => "timeout",
            BusOutcome.Busy => "bus-busy",
            _ => "failed"
        };
    }


    public bool HasProbed => _probed;


public MonitorProbe? Find(string deviceName)
    {
        lock (_gate)
        {
            return _byDevice.TryGetValue(deviceName, out MonitorProbe? probe) ? probe : null;
        }
    }

    /// <summary>
    /// How many refusals in a row before a display is put on the exclusion list.
    /// <para>
    /// One is not enough, and the reason is written all over the code that talks
    /// to these panels: a dropped first packet is normal, three attempts are made
    /// before a read is called a refusal, and a two second timeout is reported as
    /// a wedged bus. A single failure is therefore an everyday event on hardware
    /// that is otherwise perfectly good, and treating it as proof that a display
    /// is dangerous threw the feature away for a monitor that would have worked
    /// on the next drag. Excluding is still right eventually, because a panel
    /// that keeps refusing is a panel to leave alone, but it now takes a run of
    /// them.
    /// </para>
    /// </summary>
    private const int RefusalsBeforeExclusion = 3;

    private readonly Dictionary<string, int> _refusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive refusals per display, for the badge and the log.</summary>
    public IReadOnlyDictionary<string, int> Refusals
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_refusals, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Whether the feature has turned itself off, and the note the settings tab
    /// shows about it. Never silently: a switch that changes itself and says
    /// nothing is indistinguishable from a bug.
    /// </summary>
    public BacklightAvailability Availability { get; } = new();

    /// <summary>
    /// Writes a new brightness. The first time a display is touched its current
    /// value is remembered so it can be handed back on the way out.
    /// <para>
    /// Returns the outcome rather than a bool, because the caller has to tell
    /// three things apart: it landed, the display refused, or the bus was busy
    /// and the display was never asked. Collapsing those into a false is what
    /// made a skipped write vanish, since the only sensible thing to do with a
    /// bool false is to show the user that it failed.
    /// </para>
    /// </summary>
    /// <summary>
    /// How long to wait between attempts, and how many to make, when a preset's
    /// backlight collides with something already on the bus.
    /// <para>
    /// A preset apply is a discrete write that the user is waiting on, unlike a
    /// slider drag which is already debounced. Two things can hold the bus at that
    /// moment: the periodic DDC probe, and the tail of a drag the user just let
    /// go of. Both are short, so a small bounded retry catches them - and bounded
    /// is the load-bearing word, because the alternative is a preset that silently
    /// fails to set its backlight because a probe was three milliseconds away from
    /// finishing.
    /// </para>
    /// </summary>
    private const int PresetWriteAttempts = 3;

    private const int PresetWriteRetryMs = 50;

    /// <summary>
    /// Applies a preset's own backlight value.
    /// <para>
    /// A separate entry point from <see cref="TrySet"/> because the two callers
    /// want opposite things. A slider drag wants the newest value and does not
    /// mind being told "busy" - the next drag will send it again. A preset wants
    /// the value to land before the slot is considered loaded, and "busy" means the
    /// screen and the sound are now from one preset and the panel from another.
    /// </para>
    /// <para>
    /// Returns the outcome rather than throwing, and says so through
    /// <paramref name="why"/> for the same reason every other bus call does: the
    /// gamma half of the preset has already been applied by the time this runs, so
    /// there is nothing to abort.
    /// </para>
    /// </summary>
    public BusOutcome TrySetPresetBacklight(MonitorProbe monitor, uint value, out string? why)
    {
        BusOutcome outcome = BusOutcome.Busy;
        why = null;

        for (int attempt = 1; attempt <= PresetWriteAttempts; attempt++)
        {
            outcome = TrySet(monitor, value, out why);

            // Only Busy is worth another go. Every other outcome is a real answer
            // from the display, and repeating it would just spend the bus claim
            // three times and end in the same place.
            if (outcome != BusOutcome.Busy)
            {
                return outcome;
            }

            if (attempt < PresetWriteAttempts)
            {
                TraceLog.Write("backlight preset write busy, retrying " + attempt.ToString(CultureInfo.InvariantCulture)
                    + "/" + PresetWriteAttempts.ToString(CultureInfo.InvariantCulture));

                Thread.Sleep(PresetWriteRetryMs);
            }
        }

        // Last word: the bus never freed up, which is worth saying plainly rather
        // than reporting it as a refusal. Nothing was written.
        why = "the display bus was busy and did not free up";
        return outcome;
    }

    public BusOutcome TrySet(MonitorProbe monitor, uint value, out string? why)
    {
        why = null;

        if (!monitor.CanControlBacklight || monitor.Brightness is null)
        {
            why = "this display does not support hardware brightness";
            return BusOutcome.Failed;
        }

        // Taken once, here, and used for the rest of the call. The property is
        // written by whichever write finishes next - including this one, further
        // down - so reading it four times across a bus transaction that can take
        // two seconds is reading a value that can change under the middle of the
        // call. One snapshot is both the correct range for this write and the one
        // that goes into the recorded reading afterwards, so the two cannot
        // disagree.
        BrightnessReading live = monitor.Brightness;
        uint low = live.Minimum;
        uint high = live.Maximum;
        if (low > high)
        {
            (low, high) = (high, low);
        }

        // What this panel is at right now, which is the value a restore would put back -
        // so it has to be read before the write. Taken to a local rather than
        // recorded in place, because recording it here is what orphaned the entry:
        // the map was written before the bus call, so a write that then failed
        // still left a claim on a panel this app had never actually changed. The
        // restore would later push that value onto it anyway, overriding whatever
        // the user had set by hand in the meantime.
        uint before = live.Current;

        BusOutcome outcome = _bus.TrySetBrightness(
            monitor.DeviceName,
            value,
            low,
            high,
            out why,
            out int win32Error);

        if (outcome == BusOutcome.Busy)
        {
            // Not a refusal, and not the display's fault. Nothing is recorded and
            // nothing is cleared: the reading stays live, and the caller is
            // expected to send the same value again rather than leaving the row
            // showing a brightness the panel is not actually at.
            return BusOutcome.Busy;
        }

        if (outcome != BusOutcome.Ok)
        {
            string detail = win32Error == 0
                ? why ?? "unknown"
                : (why ?? "unknown") + " [" + DdcErrors.Describe(win32Error) + "]";

            AppLog.Warn("backlight write on " + monitor.DeviceName + " ended " + outcome
                + ": " + detail);

            int count;
            lock (_gate)
            {
                count = _refusals.TryGetValue(monitor.DeviceName, out int seen) ? seen + 1 : 1;
                _refusals[monitor.DeviceName] = count;
            }

            monitor.Outcome = outcome;

            if (count < RefusalsBeforeExclusion)
            {
                // Not the end of it. The reading is kept so the row stays live and
                // the next drag can still try, and the reason is recorded so the
                // badge can say the display is being difficult rather than just
                // going quiet.
                monitor.NoReplyBecause = why;
                why = (why ?? "the monitor refused the value")
                    + " (attempt " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " of " + RefusalsBeforeExclusion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", will try again)";
                return outcome;
            }

            // A run of them. Now the display is put on the exclusion list so the
            // next launch does not probe it either, and the reading is cleared so
            // the row greys out rather than failing every remaining drag. The
            // Hardware brightness switch clears this again, so it is a pause
            // rather than a life sentence.
            lock (_settings.Gate)
            {
                if (!_settings.ExcludedDdcMonitors.Contains(monitor.DeviceName))
                {
                    _settings.ExcludedDdcMonitors.Add(monitor.DeviceName);
                    Interlocked.Exchange(ref _settingsChanged, 1);
                    AppLog.Warn("backlight excluding " + monitor.FriendlyOrDevice()
                        + " after " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " refusals: " + detail
                        + ". Turn the Hardware brightness switch off and on to clear this.");
                }
            }

            monitor.NoReplyBecause = why;
            monitor.Brightness = null;
            return outcome;
        }

        lock (_gate)
        {
            _refusals.Remove(monitor.DeviceName);
        }

        monitor.NoReplyBecause = null;
        monitor.Outcome = BusOutcome.Ok;

        // The baseline is claimed only now, after the panel has actually taken the
        // value, and under the profile's gate because it is a dictionary the window
        // can be serialising at this instant. If absent, so it is the value from
        // before this session touched the panel rather than the value the last
        // preset happened to leave - which is the whole reason the map exists.
        lock (_settings.Gate)
        {
            if (!_settings.OriginalHardwareBrightness.ContainsKey(monitor.DeviceName))
            {
                _settings.OriginalHardwareBrightness[monitor.DeviceName] = before;
            }
        }

        // Built from the snapshot taken at the top of this call rather than from
        // whatever monitor.Brightness says now: the field is replaced on every
        // write and nulled on every refusal, so reading it here after a two second
        // bus transaction could read a different answer, or nothing at all.
        monitor.Brightness = new BrightnessReading
        {
            Minimum = low,
            Current = Math.Clamp(value, low, high),
            Maximum = high,
            CodeType = live.CodeType
        };

        return BusOutcome.Ok;
    }


    /// <summary>
    /// Forgets every exclusion and every refusal count, and allows a fresh probe.
    /// <para>
    /// Wired to the Hardware brightness switch going from off to on. The
    /// exclusion list is persisted, so before this a monitor that had been
    /// written off once stayed written off for good with no way back except
    /// editing settings.json by hand, and switching the feature off and on again
    /// did nothing at all because the probe guard was still set.
    /// </para>
    /// <para>
    /// Also clears the failure streak behind the automatic turn-off and marks the
    /// probe it makes possible as uncounted. It does not un-retire the feature:
    /// that is the one piece of evidence that the situation has changed, and it
    /// comes from the switch being moved, not from this being called.
    /// </para>
    /// </summary>
    public void ForgetExclusions()
    {
        bool hadAny;
        Availability.ResetCount();

        lock (_settings.Gate)
        {
            hadAny = _settings.ExcludedDdcMonitors.Count > 0;

            // This call is what lets a probe run again, and that probe is the one the
            // user just asked for by moving the switch. Spent here, once, so the
            // launch that follows is the first round that counts towards retiring.
            _nextRoundIsFree = true;

            foreach (string device in _settings.ExcludedDdcMonitors.ToList())
            {
                AppLog.Info("backlight clearing exclusion for " + device);
            }

            _settings.ExcludedDdcMonitors.Clear();
        }

        lock (_gate)
        {
            hadAny |= _refusals.Count > 0;
            _refusals.Clear();
            _byDevice.Clear();
        }

        _probed = false;

        if (hadAny)
        {
            Interlocked.Exchange(ref _settingsChanged, 1);
            AppLog.Info("backlight exclusions and refusal counts cleared, will probe again");
        }
    }

    /// <summary>
    /// True when <see cref="AppSettings.ExcludedDdcMonitors"/> has grown and the profile has
    /// not been written yet. The window commits once this is set, rather than the
    /// exclusion being written to memory and then quietly forgotten, which is
    /// what used to happen.
    /// <para>
    /// An exchange rather than a read followed by a write, because the flag is
    /// raised on a worker and read on the window. Read-then-clear loses a raise
    /// that lands between the two, and the exclusion that raised it is then never
    /// written to disk: a display the app has given up on comes back on the next
    /// launch with nothing to say why.
    /// </para>
    /// </summary>
    public bool ConsumeSettingsChanged() =>
        Interlocked.Exchange(ref _settingsChanged, 0) == 1;


    /// <summary>
    /// Drops every device not in <paramref name="attached"/>, from both the probe
    /// table and the baseline record.
    /// <para>
    /// Called when the display topology changes, and it is what makes the skip in
    /// <see cref="RestoreAll"/> load-bearing rather than incidental.
    /// </para>
    /// <para>
    /// Without it, restoring consults a table that still describes hardware which
    /// is no longer there. An undocked monitor is off the DDC/CI bus, but Windows
    /// reassigns "\\.\DISPLAY2" to whatever is plugged in next, so a later write to
    /// that name is a successful write to different hardware - one panel's captured
    /// brightness landing on another. Only a table refreshed after the change can
    /// tell the difference, and the exit path has no time to re-probe: a DDC
    /// transaction takes seconds and the shutdown budget is about one.
    /// </para>
    /// <para>
    /// So the record is pruned here, while a rescan is already running, and the
    /// exit restore can then only ever write to devices that were attached at the
    /// last topology change.
    /// </para>
    /// </summary>
    public void ForgetDetached(IReadOnlyCollection<string> attached)
    {
        if (attached is null)
        {
            return;
        }

        // Deliberately DisplayService's matcher rather than a second copy of it. The
        // rule is "a suffix match has to land on a path separator", which exists
        // because "\\.\DISPLAY2" and "\\.\DISPLAY20" share a suffix - and a rule
        // this important should have exactly one owner. Two copies is how they
        // drift, and a version that had lost the separator check would have meant
        // pruning one display's baseline because another one was plugged in.
        bool attached_to(string device) =>
            attached.Any(a => DisplayService.DeviceMatches(a, device));

        // Both prunes are driven by the attached list directly rather than by the
        // difference against _byDevice, and that is not a style preference.
        //
        // The probe rebuilds _byDevice on every refresh, so by the time this runs
        // the departed device is usually *already* gone from it - which meant a
        // prune computed as "was in the table, is not any more" found nothing to
        // do and left the baseline record untouched. The record is the thing that
        // matters, and it outlives any probe: a display can leave the table while
        // its baseline entry stays behind, and it is that entry which is written on
        // the way out.
        lock (_gate)
        {
            List<string> stale = _byDevice.Keys
                .Where(device => !attached_to(device))
                .ToList();

            foreach (string device in stale)
            {
                _byDevice.Remove(device);
            }
        }

        List<string> released;

        lock (_settings.Gate)
        {
            released = _settings.OriginalHardwareBrightness.Keys
                .Where(device => !attached_to(device))
                .ToList();

            foreach (string device in released)
            {
                _settings.OriginalHardwareBrightness.Remove(device);
            }
        }

        if (released.Count > 0)
        {
            // The profile changed, so it has to be written. Not cosmetic: an entry
            // left behind would be read back by the next launch as "already
            // captured", which is how a baseline outlives the session that made it.
            Interlocked.Exchange(ref _settingsChanged, 1);

            AppLog.Warn("backlight baseline dropped for " + string.Join(", ", released)
                + ": no longer attached, and its brightness must not be written to whatever takes that name");
        }
    }

    /// <summary>
    /// Puts every display back where it was found. Called on the way out, and by
    /// the crash handler, because the one unacceptable outcome here is a monitor
    /// left blindingly bright or unreadably dim because the app died.
    /// </summary>
    public void RestoreAll()
    {
        // Snapshotted under the profile's gate and then emptied, both before any
        // bus write rather than after. The writes below are the slow part and must
        // not be inside the lock, but nothing between reading the map and clearing
        // it may be - and a write that put the value back and then died before the
        // clear would leave the map claiming there is still something outstanding,
        // so the map is emptied first and the writes use the copy.
        List<KeyValuePair<string, uint>> pending;
        lock (_settings.Gate)
        {
            pending = _settings.OriginalHardwareBrightness.ToList();
            _settings.OriginalHardwareBrightness.Clear();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((string device, uint original) in pending)
        {
            // Skipped, and this is the whole fix.
            //
            // "\\.\DISPLAY2" is not an identity. Windows reassigns those names
            // across dock, KVM and GPU transitions, so a name remembered before an
            // undock can name a completely different physical panel afterwards.
            // The old fallback here - Find returning nothing, so assume a maximum
            // and write anyway - meant the exit path took one monitor's captured
            // baseline and applied it to whichever monitor had been handed that
            // handle. That is not a failed write, it is a successful write to the
            // wrong hardware: a laptop's internal panel flashed to full, or a
            // newly plugged monitor blasted.
            //
            // There is nothing to salvage by trying anyway. A detached panel is off
            // the DDC/CI bus entirely, so the write cannot reach it; it retains its
            // last hardware brightness until it is powered off or adjusted by hand,
            // which is how every DDC utility behaves and is the correct outcome.
            // Protecting the panels that *are* attached outranks restoring one that
            // is not.
            MonitorProbe? monitor = Find(device);
            if (monitor is null)
            {
                AppLog.Warn("backlight restore skipped " + device
                    + ": no attached display answers to that name any more");
                continue;
            }

            uint maximum = monitor.Brightness?.Maximum ?? Math.Max(original, 100);

            // No UI, no opt-in check, no probing: this runs while the app is
            // already on its way out and has to work with what is already known.
            // The outcome is ignored on purpose. A display that will not take its
            // value back on the way out is a problem worth a line in the log, but
            // not one to be shouted about while the app is closing.
            BusOutcome outcome = _bus.TrySetBrightness(device, original, 0, maximum, out string? why, out int win32Error);

            if (outcome != BusOutcome.Ok)
            {
                AppLog.Warn("backlight could not restore " + device + " to "
                    + original.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": " + outcome + " " + (why ?? string.Empty)
                    + (win32Error == 0 ? string.Empty : " [" + DdcErrors.Describe(win32Error) + "]"));
            }
        }
    }
}
