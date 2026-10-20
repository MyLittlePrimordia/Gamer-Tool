using System;
using System.Collections.Generic;

namespace GamerTool.Models;

/// <summary>
/// What a slot asks the compositor to do with HDR on its screen.
/// <para>
/// Leave is the default and the only value an older profile can carry. It is
/// deliberately not "Off": a slot made before HDR existed must not turn it off
/// on the first load, and a slot with no screen must not guess which panel the
/// user meant.
/// </para>
/// </summary>
public enum SlotHdrMode
{
    /// <summary>Leave the screen's HDR state exactly as it is.</summary>
    Leave,

    /// <summary>Switch HDR on for this slot.</summary>
    On,

    /// <summary>Switch HDR off for this slot.</summary>
    Off,
}

public sealed class HotkeySlot
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// True for the slots the app ships with. Those keep their names, because the
    /// names are how they are recognised; a slot the user added can be named so
    /// the toast that says a slot loaded actually identifies it.
    /// </summary>
    public bool BuiltIn { get; set; }

    public string? DisplayPresetId { get; set; }

    public string? AudioPresetId { get; set; }

    /// <summary>
    /// The crosshair this slot puts up, by id, or null for none. Part of the slot
    /// the same way the screen and the sound are: loading the slot shows it and
    /// standing the slot down takes it away again.
    /// </summary>
    public string? CrosshairPresetId { get; set; }

    public string Hotkey { get; set; } = string.Empty;

    public string? AppExePath { get; set; }

    /// <summary>
    /// Bind this slot to anything running out of this folder, or empty.
    /// <para>
    /// The third way to say what a slot is for, after an exact exe path and a
    /// process name. It exists for the installs where neither of those works:
    /// Ubisoft Connect and the EA app describe a game as a folder rather than an
    /// executable, and a game behind a launcher has its binary somewhere no scan
    /// looks and under a name nothing predicts.
    /// </para>
    /// <para>
    /// Only ever consulted in the foreground. It deliberately contributes nothing
    /// to the launch and exit scan, which stays name-based: "when something from
    /// here is in the foreground" is not "watch this drive for processes starting",
    /// and the second is not a thing this app does on a timer.
    /// </para>
    /// </summary>
    public string? TargetFolder { get; set; }

    public string? AppName { get; set; }

    /// <summary>
    /// The launcher URI Play hands to the shell for this game, or empty.
    /// <para>
    /// Copied from the candidate the user picked, and only for a game that came
    /// out of Steam or Epic. It exists because the exe underneath a launcher is a
    /// guess: Steam games relaunch themselves through Steam, and an Epic game
    /// usually refuses to start except through its launcher. The watcher is
    /// unaffected - it still matches the process the exe names - so this changes
    /// how a game is started and nothing about how it is recognised.
    /// </para>
    /// <para>
    /// Never trusted: checked against <c>LaunchUriPolicy</c> at launch time and
    /// again on import, because a profile is a file somebody else can write.
    /// </para>
    /// </summary>
    public string? LaunchUri { get; set; }

    public bool AutoActivate { get; set; }

    public bool ApplyOnStart { get; set; }

    public string MonitorDevice { get; set; } = string.Empty;

    /// <summary>
    /// The panel this slot is bound to, by PnP id, or empty.
    /// <para>
    /// <see cref="MonitorDevice"/> is <c>\\.\DISPLAYn</c>, which Windows
    /// renumbers when a cable moves or a laptop is undocked. A slot keyed on that
    /// alone follows the slot number rather than the monitor: the same key can
    /// load a preset onto a different screen, and the screen the user chose goes
    /// back to whatever Windows put there. The key is what identifies the panel;
    /// the device name is kept as the fallback for a profile written before keys
    /// existed and for an adapter that reports no monitor id.
    /// </para>
    /// </summary>
    public string MonitorKey { get; set; } = string.Empty;

    /// <summary>
    /// The output device this slot plays through, or empty for whatever the app
    /// is set to.
    /// <para>
    /// Empty rather than a copied default, so a slot made before a device was
    /// chosen, or made while the app was on the system default, keeps following
    /// the app's own setting instead of pinning itself to whatever it happened
    /// to be looking at when the slot was created. A slot that pins itself
    /// silently is a slot that surprises somebody months later.
    /// </para>
    /// <para>
    /// There is no default value, which is the point. It is a route, not a sound
    /// setting, and it belongs to the slot for the same reason the monitor does.
    /// </para>
    /// </summary>
    public string OutputDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// The Windows playback endpoint this slot makes the default, or empty to
    /// leave the default alone.
    /// <para>
    /// This is separate from <see cref="OutputDeviceId"/> on purpose. That one is
    /// FxSound's own output, and it only matters while FxSound is in the chain.
    /// This one is the endpoint Windows hands to every program that does not have
    /// an output picker of its own. Conflating them would either move the Windows
    /// default when the user only meant to move FxSound, or leave the game on the
    /// previous speakers when they meant to move Windows.
    /// </para>
    /// </summary>
    public string WindowsPlaybackDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// The Windows recording endpoint this slot makes the default microphone, or
    /// empty to leave it alone.
    /// </summary>
    public string WindowsRecordingDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Also make the slot's playback device the Windows communications device,
    /// which is the endpoint voice chat picks by default.
    /// </summary>
    public bool WindowsPlaybackCommunications { get; set; }

    /// <summary>
    /// Also make the slot's microphone the Windows communications microphone.
    /// </summary>
    public bool WindowsRecordingCommunications { get; set; }

    /// <summary>
    /// True only when the user has explicitly allowed this slot to point the
    /// Windows default playback at a physical endpoint while FxSound is running.
    /// <para>
    /// FxSound works by being the Windows default and forwarding the processed
    /// sound to its own output. Switching the default to a physical headset while
    /// FxSound is running therefore turns the equalizer off for every program that
    /// follows the default. That is a legitimate choice for a slot, and it is a
    /// terrible surprise when it happens without one. This flag is the choice.
    /// </para>
    /// </summary>
    public bool WindowsAudioBypassFxSound { get; set; }

    /// <summary>
    /// Whether this slot switches HDR on its monitor, or leaves it alone.
    /// <para>
    /// Applied before the gamma ramp, because Windows discards the ramp when the
    /// display mode changes. Restored when the slot is stood down for a real
    /// reason, never when the user only alt-tabbed away.
    /// </para>
    /// </summary>
    public SlotHdrMode HdrMode { get; set; } = SlotHdrMode.Leave;

    /// <summary>
    /// True once the user has bound this slot's game on this machine, or has
    /// looked at the full path and told Play to go ahead.
    /// <para>
    /// Until Play existed a slot's path was only ever matched against running
    /// processes; nothing executed it. Play executes it, and a path can arrive
    /// from a backup somebody else wrote - the same untrusted file the FxSound
    /// path check exists for. This is the one question that closes that gap:
    /// has a person on this PC seen this exact path and said yes to it.
    /// </para>
    /// <para>
    /// Cleared by a backup import, because the file cannot vouch for it. Not a
    /// warning on every Play - only the first one for a slot whose path has never
    /// been confirmed here.
    /// </para>
    /// </summary>
    public bool LaunchConfirmed { get; set; }

    /// <summary>
    /// Whether Play should show the exact target and ask before starting this
    /// slot.
    /// <para>
    /// Only an exact executable or one of the two allowed launcher URIs is ever
    /// started, so the two special targets and a folder never need asking: Play
    /// refuses to start them at all. A URI counts because it is imported content
    /// too - the case this covers is a backup whose target is a protocol call.
    /// </para>
    /// </summary>
    public bool NeedsLaunchConfirmation =>
        !LaunchConfirmed
        && !IsSelfTarget
        && !IsAnyGameTarget
        && (!string.IsNullOrWhiteSpace(AppExePath) || !string.IsNullOrWhiteSpace(LaunchUri));

    public bool HasWindowsAudioRouting =>
        !string.IsNullOrWhiteSpace(WindowsPlaybackDeviceId)
        || !string.IsNullOrWhiteSpace(WindowsRecordingDeviceId);

    public bool IsSelfTarget { get; set; }

    /// <summary>
    /// This slot is not bound to a game. It applies to any fullscreen program that
    /// no other slot claims.
    /// <para>
    /// The case it covers is forgetting to bind something: a new game, a launcher
    /// that starts a different executable, or a game that updates itself and
    /// changes path. Binding by path is exact and that is what makes it safe -
    /// and it is also what makes it fail silently when the path changes, which is
    /// the one thing a slot exists to prevent.
    /// </para>
    /// <para>
    /// Fullscreen is the qualifier that makes this safe rather than maddening.
    /// Without it, alt-tabbing to a browser would apply the slot, and the app
    /// would take over the picture of every window the user visits. A window
    /// covering its whole monitor is the one shape that reliably means "a game is
    /// running", and it is the same test the gamma lock already needs to decide
    /// not to fight something.
    /// </para>
    /// <para>
    /// At most one wildcard is armed. Two would both match every unmatched
    /// program and the order between them would be the order they happen to sit in
    /// the list, which is not a rule anybody could state.
    /// </para>
    /// </summary>
    public bool IsAnyGameTarget { get; set; }

    public bool Enabled { get; set; } = true;

    public bool HasTarget
    {
        get
        {
            return IsSelfTarget
                || IsAnyGameTarget
                || !string.IsNullOrWhiteSpace(AppExePath)
                || !string.IsNullOrWhiteSpace(AppName)
                || !string.IsNullOrWhiteSpace(TargetFolder);
        }
    }

    public bool HasWork
    {
        get
        {
            return !string.IsNullOrWhiteSpace(DisplayPresetId)
                || !string.IsNullOrWhiteSpace(AudioPresetId)
                || !string.IsNullOrWhiteSpace(CrosshairPresetId)
                || HdrMode != SlotHdrMode.Leave
                || HasWindowsAudioRouting;
        }
    }

    public string TargetText
    {
        get
        {
            if (IsSelfTarget)
            {
                return "GAMER TOOL";
            }

            // Ahead of the HasTarget check, which would otherwise report NO GAME
            // for a wildcard: it has no path and no name, so it looks untargeted
            // to everything that has not been taught about it.
            if (IsAnyGameTarget)
            {
                return "ANY GAME";
            }

            if (!HasTarget)
            {
                return "NO GAME";
            }

            if (!string.IsNullOrWhiteSpace(TargetFolder)
                && string.IsNullOrWhiteSpace(AppExePath)
                && string.IsNullOrWhiteSpace(AppName))
            {
                // Only the leaf, because the whole path is several lines wide on a
                // slot row and the drive letter tells the user nothing they did not
                // already pick.
                string leaf = TargetFolder.TrimEnd('\\', '/');
                int cut = leaf.LastIndexOfAny(new[] { '\\', '/' });
                return (cut >= 0 && cut < leaf.Length - 1 ? leaf[(cut + 1)..] : leaf).ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(AppName) ? AppProfileTools.ProcessNameOf(AppExePath ?? string.Empty).ToUpperInvariant() : AppName.ToUpperInvariant();
        }
    }

    public string WorkText
    {
        get
        {
            bool hasScreen = !string.IsNullOrWhiteSpace(DisplayPresetId);
            bool hasSound = !string.IsNullOrWhiteSpace(AudioPresetId);
            bool hasCrosshair = !string.IsNullOrWhiteSpace(CrosshairPresetId);
            bool hasHdr = HdrMode != SlotHdrMode.Leave;
            bool hasRoute = HasWindowsAudioRouting;

            // One word per thing the slot does, joined. The original three
            // phrases are unchanged when there is no crosshair, so nothing that
            // matched on them is affected.
            List<string> parts = new();
            if (hasScreen)
            {
                parts.Add("SCREEN");
            }

            if (hasHdr)
            {
                parts.Add("HDR");
            }

            if (hasSound)
            {
                parts.Add("SOUND");
            }

            if (hasRoute)
            {
                parts.Add("ROUTE");
            }

            if (hasCrosshair)
            {
                parts.Add("CROSSHAIR");
            }

            if (parts.Count == 0)
            {
                return "NOTHING SET";
            }

            return parts.Count == 1 ? parts[0] + " ONLY" : string.Join(" + ", parts);
        }
    }

    public HotkeySlot Copy()
    {
        return new HotkeySlot
        {
            Id = Id,
            Name = Name,
            BuiltIn = BuiltIn,
            DisplayPresetId = DisplayPresetId,
            AudioPresetId = AudioPresetId,
            CrosshairPresetId = CrosshairPresetId,
            Hotkey = Hotkey,
            AppExePath = AppExePath,
            AppName = AppName,
            LaunchUri = LaunchUri,
            TargetFolder = TargetFolder,
            AutoActivate = AutoActivate,
            ApplyOnStart = ApplyOnStart,
            IsSelfTarget = IsSelfTarget,
            IsAnyGameTarget = IsAnyGameTarget,
            MonitorDevice = MonitorDevice,
            MonitorKey = MonitorKey,
            OutputDeviceId = OutputDeviceId,
            WindowsPlaybackDeviceId = WindowsPlaybackDeviceId,
            WindowsRecordingDeviceId = WindowsRecordingDeviceId,
            WindowsPlaybackCommunications = WindowsPlaybackCommunications,
            WindowsRecordingCommunications = WindowsRecordingCommunications,
            WindowsAudioBypassFxSound = WindowsAudioBypassFxSound,
            HdrMode = HdrMode,
            LaunchConfirmed = LaunchConfirmed,
            Enabled = Enabled
        };
    }
}
