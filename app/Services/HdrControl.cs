using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>Which API answered the HDR question.</summary>
public enum HdrReadSource
{
    /// <summary>Nobody answered.</summary>
    Unknown,

    /// <summary>The 24H2 <c>GET_ADVANCED_COLOR_INFO_2</c>, which can tell real HDR from ACM SDR.</summary>
    Info2,

    /// <summary>The legacy advanced-colour read, which cannot.</summary>
    Legacy,
}

/// <summary>
/// What one screen is doing with HDR right now.
/// <para>
/// <see cref="Active"/> is the state being driven, not the switch position. They
/// can disagree when policy holds HDR down, and every decision in this app is
/// about what the panel is actually doing, because that is what decides whether
/// a gamma ramp will be honoured.
/// </para>
/// </summary>
public readonly record struct HdrReading(
    bool Known,
    bool Supported,
    bool Active,
    bool UserEnabled,
    bool LimitedByPolicy,
    HdrReadSource Source)
{
    public static HdrReading Unknown { get; } =
        new(false, false, false, false, false, HdrReadSource.Unknown);

    /// <summary>True when the answer came from the legacy call and cannot tell HDR from ACM.</summary>
    public bool IsLegacy => Source == HdrReadSource.Legacy;
}

/// <summary>The result of one native setter call, before verification.</summary>
public readonly record struct HdrSetResult(bool Ok, string Error)
{
    public static HdrSetResult Success { get; } = new(true, string.Empty);

    public static HdrSetResult Failure(string error) => new(false, error);
}

/// <summary>What one apply did, for the toast and the log.</summary>
public sealed record HdrApplyResult(bool Applied, bool Skipped, string Warning, bool LegacyPath)
{
    public static HdrApplyResult NotRequested { get; } = new(false, true, string.Empty, false);

    public static HdrApplyResult AppliedNow(bool legacyPath) => new(true, false, string.Empty, legacyPath);

    public static HdrApplyResult SkippedBecause(string warning) => new(false, true, warning, false);

    public static HdrApplyResult Failed(string warning) => new(false, false, warning, false);
}

/// <summary>
/// Whether a flip is allowed right now, as a pure decision.
/// <para>
/// The minimum interval exists because HDR is a display-mode change: it blanks
/// the screen for a moment and costs the compositor a re-mode. Two slots with
/// opposite HDR settings and an alt-tab between their games would otherwise
/// flap the panel, and the user would see the app fighting itself rather than
/// loading a profile.
/// </para>
/// </summary>
public static class HdrFlipDecision
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    public static HdrFlipPlan Decide(
        bool current,
        bool wanted,
        DateTime lastFlipUtc,
        DateTime nowUtc,
        bool inProgress)
    {
        if (inProgress)
        {
            return new HdrFlipPlan(false, false, "HDR is already switching on this screen");
        }

        if (current == wanted)
        {
            return new HdrFlipPlan(false, true, string.Empty);
        }

        if (lastFlipUtc != DateTime.MinValue && nowUtc - lastFlipUtc < MinimumInterval)
        {
            return new HdrFlipPlan(false, false, "HDR changed a moment ago");
        }

        return new HdrFlipPlan(true, false, string.Empty);
    }
}

/// <summary>One flip decision, with the reason when it was refused.</summary>
public readonly record struct HdrFlipPlan(bool Flip, bool IsNoOp, string Reason);

/// <summary>
/// The screen's HDR state before the first apply and after the last one, keyed
/// by the panel rather than by the slot.
/// <para>
/// First-before and last-after, exactly like the audio route memory. Restoring a
/// later slot's starting point would put the first slot's state back, which is
/// not where the user was before any of this. The after value is what makes the
/// hand-change guard possible: a restore only runs while the current state is
/// still what this app set.
/// </para>
/// </summary>
public sealed class HdrRestoreMemory
{
    public readonly record struct Entry(string Device, bool Before, bool After);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public IReadOnlyList<string> Keys => _entries.Keys.ToList();

    public void Remember(string key, string device, bool before, bool after)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            key = device;
        }

        if (string.IsNullOrWhiteSpace(key) || before == after)
        {
            return;
        }

        if (_entries.TryGetValue(key, out Entry remembered))
        {
            _entries[key] = remembered with { Device = device, After = after };
            return;
        }

        _entries[key] = new Entry(device, before, after);
    }

    public bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry);

    public bool ShouldRestore(string key, bool current, out Entry entry)
    {
        entry = default;

        if (!_entries.TryGetValue(key, out Entry remembered))
        {
            return false;
        }

        if (current != remembered.After)
        {
            return false;
        }

        entry = remembered;
        return true;
    }

    public void Forget(string key) => _entries.Remove(key);

    public IReadOnlyList<KeyValuePair<string, Entry>> Snapshot() => _entries.ToList();
}

/// <summary>
/// Applies, verifies and restores HDR on one screen, and remembers what it found.
/// <para>
/// Every hardware call is a delegate so the decisions can be tested without a
/// compositor. The production constructor wires the real <see cref="HardwareBrightness"/>
/// calls; tests supply a dictionary and a fake clock.
/// </para>
/// </summary>
public sealed class HdrControl
{
    private readonly Func<string, HdrReading> _read;
    private readonly Func<string, bool, HdrSetResult> _set;
    private readonly Func<DateTime> _clock;

    private readonly HdrRestoreMemory _memory = new();
    private readonly Dictionary<string, DateTime> _lastFlip = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);

    public HdrControl()
        : this(
            HardwareBrightness.ReadHdrState,
            (device, enable) =>
            {
                bool ok = HardwareBrightness.TrySetHdrState(device, enable, out string error);
                return new HdrSetResult(ok, error);
            },
            () => DateTime.UtcNow)
    {
    }

    public HdrControl(
        Func<string, HdrReading> read,
        Func<string, bool, HdrSetResult> set,
        Func<DateTime> clock)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public HdrRestoreMemory Memory => _memory;

    public bool Touched => _memory.Count > 0;

    public HdrReading Read(string device) =>
        string.IsNullOrWhiteSpace(device) ? HdrReading.Unknown : _read(device);

    /// <summary>
    /// Applies a slot's HDR request to one screen.
    /// <para>
    /// The order is fixed: read, decide, set, read back, remember only after the
    /// read-back agrees. A setter that returns success without changing anything
    /// is the normal failure mode of this API family, so the return code is never
    /// the answer.
    /// </para>
    /// </summary>
    public HdrApplyResult Apply(string device, string monitorKey, SlotHdrMode mode)
    {
        if (mode == SlotHdrMode.Leave)
        {
            return HdrApplyResult.NotRequested;
        }

        if (string.IsNullOrWhiteSpace(device))
        {
            return HdrApplyResult.SkippedBecause("Pick one screen for HDR");
        }

        string key = KeyFor(monitorKey, device);
        bool wanted = mode == SlotHdrMode.On;

        if (_inProgress.Contains(key))
        {
            return HdrApplyResult.SkippedBecause("HDR is already switching on this screen");
        }

        HdrReading before;
        try
        {
            before = _read(device);
        }
        catch (Exception ex)
        {
            TraceLog.Write("HDR read failed", ex);
            return HdrApplyResult.Failed("HDR could not be read on this screen");
        }

        if (!before.Known)
        {
            return HdrApplyResult.SkippedBecause("This screen did not report HDR");
        }

        if (wanted && !before.Supported)
        {
            return HdrApplyResult.SkippedBecause("This screen does not support HDR");
        }

        HdrFlipPlan plan = HdrFlipDecision.Decide(
            before.Active,
            wanted,
            LastFlip(key),
            _clock(),
            false);

        if (!plan.Flip)
        {
            return plan.IsNoOp
                ? new HdrApplyResult(false, true, string.Empty, before.IsLegacy)
                : HdrApplyResult.SkippedBecause(plan.Reason);
        }

        _inProgress.Add(key);

        try
        {
            HdrSetResult result = _set(device, wanted);

            if (!result.Ok)
            {
                // One retry, because a mode change can be refused while the
                // previous one is still settling. More than one would turn a
                // failure into a visible pause on the dispatcher.
                Thread.Sleep(120);
                result = _set(device, wanted);
            }

            if (!result.Ok)
            {
                TraceLog.Write("HDR set failed " + device + " <- " + result.Error);
                return HdrApplyResult.Failed("HDR did not switch on this screen");
            }

            HdrReading after = _read(device);

            if (!after.Known || after.Active != wanted)
            {
                Thread.Sleep(120);
                after = _read(device);
            }

            if (!after.Known || after.Active != wanted)
            {
                TraceLog.Write("HDR switch was not kept " + device);
                return HdrApplyResult.Failed("HDR did not switch on this screen");
            }

            _memory.Remember(key, device, before.Active, wanted);
            _lastFlip[key] = _clock();
            return HdrApplyResult.AppliedNow(after.IsLegacy || before.IsLegacy);
        }
        catch (Exception ex)
        {
            TraceLog.Write("HDR apply failed", ex);
            return HdrApplyResult.Failed("HDR failed on this screen");
        }
        finally
        {
            _inProgress.Remove(key);
        }
    }

    /// <summary>
    /// Waits until the screen reports the requested mode, then a short quiet
    /// period so a second mode change has landed. Used by Play before it starts
    /// a game, and by the gamma re-push after a manual flip.
    /// </summary>
    public async Task<bool> SettleAsync(string device, bool wanted, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return false;
        }

        DateTime deadline = DateTime.UtcNow + timeout;

        try
        {
            while (DateTime.UtcNow <= deadline)
            {
                HdrReading reading = _read(device);

                if (reading.Known && reading.Active == wanted)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    return true;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("HDR settle failed", ex);
        }

        return false;
    }

    /// <summary>
    /// Puts every remembered screen back, and forgets only what it verified.
    /// <para>
    /// A screen that is no longer attached is dropped: there is nothing to put
    /// back, and keeping the entry would make the next launch try to write to a
    /// name Windows may since have handed to different hardware. A screen the
    /// user changed by hand is dropped without a write, because their newer
    /// decision wins.
    /// </para>
    /// <para>
    /// Returns the warnings for the caller to log. Never throws: this runs on
    /// exit paths where an exception would take the rest of the restore with it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> RestoreAll(Func<string, string, string>? resolve = null)
    {
        List<string> warnings = new();

        foreach (KeyValuePair<string, HdrRestoreMemory.Entry> pair in _memory.Snapshot())
        {
            string key = pair.Key;
            HdrRestoreMemory.Entry entry = pair.Value;

            try
            {
                string device = resolve?.Invoke(key, entry.Device) ?? entry.Device;

                if (string.IsNullOrWhiteSpace(device)
                    || !device.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
                {
                    _memory.Forget(key);
                    warnings.Add("HDR was not restored: the screen is not connected");
                    continue;
                }

                if (_inProgress.Contains(key))
                {
                    warnings.Add("HDR was not restored: a switch is still in progress");
                    continue;
                }

                HdrReading current = _read(device);

                if (!current.Known)
                {
                    warnings.Add("HDR was not restored: the screen did not report its state");
                    continue;
                }

                if (current.Active != entry.After)
                {
                    _memory.Forget(key);
                    warnings.Add("HDR was left alone: it was changed by hand");
                    continue;
                }

                HdrSetResult result = _set(device, entry.Before);

                if (!result.Ok)
                {
                    warnings.Add("HDR did not go back: " + result.Error);
                    continue;
                }

                HdrReading after = _read(device);

                if (!after.Known || after.Active != entry.Before)
                {
                    warnings.Add("HDR did not go back");
                    continue;
                }

                _memory.Forget(key);
                _lastFlip[key] = _clock();
            }
            catch (Exception ex)
            {
                TraceLog.Write("HDR restore failed", ex);
                warnings.Add("HDR did not go back");
            }
        }

        return warnings;
    }

    /// <summary>The key a screen is remembered under: the panel, not the number.</summary>
    public static string KeyFor(string monitorKey, string device) =>
        string.IsNullOrWhiteSpace(monitorKey) ? device : monitorKey;

    private DateTime LastFlip(string key) =>
        _lastFlip.TryGetValue(key, out DateTime last) ? last : DateTime.MinValue;
}

/// <summary>
/// Whether a screen preset can do anything on a monitor, and what to say when it
/// cannot.
/// <para>
/// Windows documents gamma ramps as undefined in HDR and resets them on a mode
/// change. A slot that carries both a screen preset and HDR would otherwise look
/// like it did nothing, and the user would blame the app rather than the mode.
/// </para>
/// </summary>
public static class ScreenPresetEffective
{
    public static bool Applies(bool hdrActive, bool hasScreenPreset) =>
        hasScreenPreset && !hdrActive;

    public static string SlotNote(bool hdrActive, bool hasScreenPreset) =>
        hasScreenPreset && hdrActive
            ? "HDR is on, so this preset's brightness and gamma won't apply here"
            : string.Empty;
}

/// <summary>The Display-tab wording, kept here so the panel and the tests agree.</summary>
public static class HdrNotes
{
    public const string HdrOn =
        "HDR is on. Windows ignores gamma ramps in HDR, so brightness and gamma presets won't change this screen.";

    public const string Unknown =
        "Can't tell whether HDR is on. If these sliders do nothing, turn HDR off in Windows.";

    public const string SlotWithoutMonitor =
        "HDR is set on a slot with All screens. Pick one screen for that slot, or HDR will be skipped.";

    public const string Unsupported =
        "This screen does not report HDR support, so a slot that asks for it will be skipped.";
}
