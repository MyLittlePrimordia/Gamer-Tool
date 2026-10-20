using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamerTool.Models;

namespace GamerTool.Services;

public enum PlayCheckState
{
    /// <summary>This part of the slot is set and available.</summary>
    Ready,

    /// <summary>The slot does not ask for this, which is not a problem.</summary>
    NotSet,

    /// <summary>The slot asks for this and it would not work.</summary>
    Warning,
}

public sealed class PlayCheck
{
    public PlayCheck(string label, string detail, PlayCheckState state)
    {
        Label = label;
        Detail = detail;
        State = state;
    }

    public string Label { get; }

    public string Detail { get; }

    public PlayCheckState State { get; }

    public bool IsProblem => State == PlayCheckState.Warning;
}

/// <summary>
/// What a Play click can actually do, worked out before anything is applied and
/// long before a game is started.
/// <para>
/// The checklist is deliberately one object rather than a handful of booleans
/// read in different places. The tooltip, the refusal message and the line the
/// OSD shows after a launch all describe the same answer, so they cannot drift
/// into saying three different things about one slot.
/// </para>
/// <para>
/// Missing presets and a missing FxSound are warnings rather than blockers: a
/// user can still start the game and get the parts of the profile that exist.
/// Missing or non-exact game targets are blockers, because there is nothing safe
/// to start. A folder and a wildcard are matches, not programs; launching either
/// would be guessing which executable the user meant.
/// </para>
/// </summary>
public sealed class PlayPreflight
{
    private PlayPreflight(
        IReadOnlyList<PlayCheck> checks,
        bool canLaunch,
        string blockedReason,
        string summary)
    {
        Checks = checks;
        CanLaunch = canLaunch;
        BlockedReason = blockedReason;
        Summary = summary;
    }

    public IReadOnlyList<PlayCheck> Checks { get; }

    public bool CanLaunch { get; }

    public string BlockedReason { get; }

    /// <summary>
    /// The parts of the profile that are ready, in a form that fits one OSD
    /// line. "screen + sound", or "no profile changes" when the slot is only a
    /// launcher.
    /// </summary>
    public string Summary { get; }

    public IReadOnlyList<PlayCheck> Warnings =>
        Checks.Where(c => c.IsProblem).ToList();

    public string Tooltip
    {
        get
        {
            List<string> lines = new()
            {
                "PLAY WITH PROFILE",
            };

            foreach (PlayCheck check in Checks)
            {
                string mark = check.State switch
                {
                    PlayCheckState.Ready => "[x]",
                    PlayCheckState.Warning => "[!]",
                    _ => "[ ]",
                };

                lines.Add(mark + " " + check.Label.PadRight(9) + check.Detail);
            }

            lines.Add(string.Empty);
            lines.Add(CanLaunch
                ? "Click to apply this profile and start the game."
                : BlockedReason);

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Whether this slot names one exact executable, as opposed to a folder, a
    /// process name or the two special targets.
    /// </summary>
    public static bool TryTarget(HotkeySlot? slot, out string exePath)
    {
        exePath = string.Empty;

        if (slot is null
            || slot.IsSelfTarget
            || slot.IsAnyGameTarget
            || !string.IsNullOrWhiteSpace(slot.TargetFolder)
            || string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            return false;
        }

        string path = slot.AppExePath.Trim().Trim('"');

        if (path.Length == 0
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        exePath = path;
        return true;
    }

    public static bool TargetExists(HotkeySlot? slot) =>
        TryTarget(slot, out string path) && File.Exists(path);

    /// <summary>
    /// How Play would start this slot, and the URI to use when that is a
    /// launcher.
    /// <para>
    /// Read from the slot every time rather than remembered from when the target
    /// was picked. The field is persisted and can arrive from a backup, so the
    /// only safe moment to decide whether it may be handed to the shell is the
    /// moment before it is.
    /// </para>
    /// </summary>
    public static LaunchVia ViaFor(HotkeySlot? slot, out string launchUri)
    {
        launchUri = string.Empty;

        if (slot is null)
        {
            return LaunchVia.None;
        }

        LaunchVia via = LaunchUriPolicy.ViaOf(slot.LaunchUri);

        // A link is only usable when there is still a path beside it. The path is
        // what the watcher matches and what the launch watchdog waits for, so a
        // slot that could start a game it can never recognise would apply its
        // profile and have nothing to put it back with. The link is not lost -
        // it stays in the slot - but Play treats the slot as having no target.
        if (via != LaunchVia.None && TryTarget(slot, out _))
        {
            launchUri = slot.LaunchUri!;
            return via;
        }

        return LaunchVia.None;
    }

    /// <summary>
    /// Whether Play has anything safe to start: an allowed launcher URI, or one
    /// exact executable.
    /// </summary>
    public static bool TryLaunchTarget(HotkeySlot? slot, out string launchUri, out string exePath, out LaunchVia via)
    {
        launchUri = string.Empty;
        exePath = string.Empty;

        via = ViaFor(slot, out launchUri);
        if (via != LaunchVia.None)
        {
            TryTarget(slot, out exePath);
            return true;
        }

        return TryTarget(slot, out exePath);
    }

    /// <summary>
    /// The string the one-time confirmation shows: exactly what Play is about to
    /// hand to the system, never a guess at it.
    /// </summary>
    public static string ConfirmationTarget(HotkeySlot slot, string launchUri)
    {
        return launchUri.Length > 0 ? launchUri : (TryTarget(slot, out string exePath) ? exePath : string.Empty);
    }

    public static PlayPreflight Build(
        HotkeySlot slot,
        bool targetExists,
        bool displayExists,
        bool audioExists,
        bool crosshairExists,
        bool audioInstalled,
        bool monitorResolved)
    {
        List<PlayCheck> checks = new();

        checks.Add(slot.HasWork
            ? new PlayCheck("PROFILE", slot.WorkText, PlayCheckState.Ready)
            : new PlayCheck("PROFILE", "nothing set", PlayCheckState.Warning));

        LaunchVia via = ViaFor(slot, out string launchUri);
        TryTarget(slot, out string exePath);
        bool exactTarget = exePath.Length > 0;

        // A launcher URI is the better target when there is one: the exe under a
        // Steam or Epic install is a guess at the same game, and a wrong guess is
        // a launch of the wrong program. So the file check does not apply to it -
        // the launcher is the authority, and it can start a game whose guessed exe
        // this scan got wrong.
        bool launches = via != LaunchVia.None || (exactTarget && targetExists);

        string gameDetail = via != LaunchVia.None
            ? slot.TargetText + " (" + LaunchUriPolicy.Describe(via) + ")"
            : exactTarget
                ? (targetExists ? slot.TargetText : "file not found")
                : TargetProblem(slot);

        checks.Add(new PlayCheck(
            "GAME",
            gameDetail,
            launches ? PlayCheckState.Ready : PlayCheckState.Warning));

        checks.Add(LaunchCheck(slot, via, exactTarget));
        checks.Add(DisplayCheck(slot, displayExists, monitorResolved));
        checks.Add(HdrCheck(slot));
        checks.Add(AudioCheck(slot, audioExists, audioInstalled));
        checks.Add(RouteCheck(slot));
        checks.Add(CrosshairCheck(slot, crosshairExists));

        string blocked = string.Empty;
        if (!slot.HasWork)
        {
            blocked = "Nothing set on " + DisplayName(slot);
        }
        else if (via == LaunchVia.None && !exactTarget)
        {
            blocked = TargetProblem(slot);
        }
        else if (via == LaunchVia.None && !targetExists)
        {
            blocked = "The game file is not at " + exePath;
        }

        bool canLaunch = blocked.Length == 0;

        // Not part of the profile summary: how Play starts the game is not one of
        // the things the slot changes about the machine.
        List<string> ready = checks
            .Where(c => c.Label is not ("PROFILE" or "GAME" or "LAUNCH") && c.State == PlayCheckState.Ready)
            .Select(c => c.Label.ToLowerInvariant())
            .ToList();

        return new PlayPreflight(
            checks,
            canLaunch,
            blocked,
            ready.Count == 0 ? "no profile changes" : string.Join(" + ", ready));
    }

    /// <summary>
    /// How the game will be started, as its own row.
    /// <para>
    /// Separate from GAME because the two answer different questions and can
    /// disagree: GAME says which title it is, LAUNCH says whether Play runs an
    /// executable or asks a store to start it. A refused URI is a warning here
    /// rather than a blocker, because the executable underneath it is still a
    /// perfectly good answer.
    /// </para>
    /// </summary>
    private static PlayCheck LaunchCheck(HotkeySlot slot, LaunchVia via, bool exactTarget)
    {
        if (via != LaunchVia.None)
        {
            return new PlayCheck("LAUNCH", LaunchUriPolicy.Describe(via), PlayCheckState.Ready);
        }

        if (!string.IsNullOrWhiteSpace(slot.LaunchUri))
        {
            return new PlayCheck("LAUNCH", "launcher refused", PlayCheckState.Warning);
        }

        return exactTarget
            ? new PlayCheck("LAUNCH", LaunchUriPolicy.Describe(LaunchVia.None), PlayCheckState.Ready)
            : new PlayCheck("LAUNCH", "no target", PlayCheckState.Warning);
    }

    private static PlayCheck DisplayCheck(HotkeySlot slot, bool exists, bool monitorResolved)
    {
        if (string.IsNullOrWhiteSpace(slot.DisplayPresetId))
        {
            return new PlayCheck("SCREEN", "not set", PlayCheckState.NotSet);
        }

        if (!exists)
        {
            return new PlayCheck("SCREEN", "preset missing", PlayCheckState.Warning);
        }

        if (!monitorResolved)
        {
            return new PlayCheck("SCREEN", "monitor not connected", PlayCheckState.Warning);
        }

        return new PlayCheck("SCREEN", "ready", PlayCheckState.Ready);
    }

    /// <summary>
    /// The HDR half of the screen, kept separate from SCREEN because the two can
    /// disagree: a slot can load a screen preset and ask for HDR, and the preset
    /// will be ignored on that monitor while HDR is on. The row says what will be
    /// attempted; the Display tab explains why it may look inert.
    /// </summary>
    private static PlayCheck HdrCheck(HotkeySlot slot)
    {
        if (slot.HdrMode == SlotHdrMode.Leave)
        {
            return new PlayCheck("HDR", "not set", PlayCheckState.NotSet);
        }

        if (slot.IsAnyGameTarget)
        {
            // A wildcard would flip HDR for a video player or a browser game.
            // The slot still loads everything else; this part is refused.
            return new PlayCheck("HDR", "not on Any game", PlayCheckState.Warning);
        }

        if (string.IsNullOrWhiteSpace(slot.MonitorKey) && string.IsNullOrWhiteSpace(slot.MonitorDevice))
        {
            return new PlayCheck("HDR", "pick one screen", PlayCheckState.Warning);
        }

        // The two halves can contradict each other: Windows ignores the gamma
        // ramp in HDR, so a slot with a screen preset and HDR On will load a
        // preset that cannot be seen on that monitor. Still Ready - both parts
        // are available - but the detail says why the picture will not change.
        string detail = slot.HdrMode switch
        {
            SlotHdrMode.On when !string.IsNullOrWhiteSpace(slot.DisplayPresetId) =>
                "on (screen preset won't apply)",
            SlotHdrMode.On => "on",
            _ => "off",
        };

        return new PlayCheck("HDR", detail, PlayCheckState.Ready);
    }

    private static PlayCheck AudioCheck(HotkeySlot slot, bool exists, bool installed)
    {
        if (string.IsNullOrWhiteSpace(slot.AudioPresetId))
        {
            return new PlayCheck("SOUND", "not set", PlayCheckState.NotSet);
        }

        if (!exists)
        {
            return new PlayCheck("SOUND", "preset missing", PlayCheckState.Warning);
        }

        if (!installed)
        {
            return new PlayCheck("SOUND", "FxSound not installed", PlayCheckState.Warning);
        }

        return new PlayCheck("SOUND", "ready", PlayCheckState.Ready);
    }

    private static PlayCheck RouteCheck(HotkeySlot slot)
    {
        if (!slot.HasWindowsAudioRouting)
        {
            return new PlayCheck("ROUTE", "not set", PlayCheckState.NotSet);
        }

        List<string> parts = new();
        if (!string.IsNullOrWhiteSpace(slot.WindowsPlaybackDeviceId))
        {
            parts.Add("playback");
        }

        if (!string.IsNullOrWhiteSpace(slot.WindowsRecordingDeviceId))
        {
            parts.Add("microphone");
        }

        if (slot.WindowsPlaybackCommunications || slot.WindowsRecordingCommunications)
        {
            parts.Add("voice chat");
        }

        return new PlayCheck("ROUTE", string.Join(" + ", parts), PlayCheckState.Ready);
    }

    private static PlayCheck CrosshairCheck(HotkeySlot slot, bool exists)
    {
        if (string.IsNullOrWhiteSpace(slot.CrosshairPresetId))
        {
            return new PlayCheck("CROSSHAIR", "not set", PlayCheckState.NotSet);
        }

        return exists
            ? new PlayCheck("CROSSHAIR", "ready", PlayCheckState.Ready)
            : new PlayCheck("CROSSHAIR", "preset missing", PlayCheckState.Warning);
    }

    private static string TargetProblem(HotkeySlot slot)
    {
        if (slot.IsSelfTarget)
        {
            return "self target cannot be launched";
        }

        if (slot.IsAnyGameTarget)
        {
            return "any-game target cannot be launched";
        }

        if (!string.IsNullOrWhiteSpace(slot.TargetFolder))
        {
            return "folder target cannot be launched";
        }

        if (!string.IsNullOrWhiteSpace(slot.AppName) && string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            return "process target cannot be launched";
        }

        return "point this slot at an exact .exe";
    }

    private static string DisplayName(HotkeySlot slot) =>
        string.IsNullOrWhiteSpace(slot.Name) ? "this slot" : slot.Name;
}
