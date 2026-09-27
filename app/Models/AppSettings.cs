using System;
using System.Collections.Generic;
using GamerTool.Services;

namespace GamerTool.Models;

public sealed class UserHotkey
{
    public string TargetId { get; set; } = string.Empty;

    public string Hotkey { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    /// <summary>
    /// A fresh install opens on the two neutral presets, not on one of the
    /// game tunes. Starting on a preset that boosts shadows and lifts the low end
    /// would quietly alter the screen and the sound before the user has touched
    /// anything, which is a strange thing for a utility to do on first launch.
    /// </summary>
    public string ActiveDisplayPresetId { get; set; } = "flat";

    public string ActiveAudioPresetId { get; set; } = "flat";

    public string OutputDeviceId { get; set; } = string.Empty;

    public string OutputDeviceName { get; set; } = string.Empty;

    public string FxSoundPath { get; set; } = AudioService.DefaultFxSoundPath;

    public bool GammaLock { get; set; } = true;

    public bool ShowOsd { get; set; } = true;

    public bool StartHidden { get; set; } = false;

    public bool CloseToTray { get; set; } = false;

    public bool StartWithWindows { get; set; } = false;

    public bool AutoSwitch { get; set; } = false;

    /// <summary>
    /// Put the screen and sound back to neutral when a game that was auto loaded
    /// closes, instead of leaving the boost running for whatever comes next.
    /// <para>
    /// On by default because the failure it prevents is the common one: the app is
    /// left looking correct for a game that is no longer running, which reads as a
    /// bug in Gamer Tool rather than as a boost the user asked for and forgot
    /// about. It only ever undoes a slot the app applied by itself, so a tune the
    /// user loaded on purpose is never touched.
    /// </para>
    /// </summary>
    public bool AutoRevertOnExit { get; set; } = true;

    /// <summary>
    /// Auto preamp: trims master gain by the largest EQ boost so boosted bands
    /// cannot hit 0 dBFS and hard-clip.
    /// </summary>
    public bool AntiClip { get; set; } = true;

    /// <summary>
    /// Hardware backlight control over DDC/CI. Off by default and opt-in on
    /// purpose: it talks to the monitor over I2C, which is the one thing in this
    /// app that can upset hardware. The software gamma ramp is unaffected either
    /// way and remains the fallback everywhere.
    /// </summary>
    public bool HardwareBrightnessEnabled { get; set; }

    /// <summary>
    /// Displays that have been put off limits after misbehaving, by device name.
    /// They are skipped rather than probed, so one bad display cannot be asked
    /// again on every launch.
    /// </summary>
    public List<string> ExcludedDdcMonitors { get; set; } = new();

    /// <summary>
    /// The brightness each display was at before this app first touched it, so it
    /// can be put back if the app is killed. A monitor left at 5% because
    /// something crashed is a bad afternoon, and this is the cheapest guard
    /// against it.
    /// </summary>
    public Dictionary<string, uint> OriginalHardwareBrightness { get; set; } = new();

    /// <summary>0 = off, 1 = warm, 2 = extra warm.</summary>
    public int BlueLightFilter { get; set; }

    /// <summary>
    /// Set once the user has been offered the FxSound install on launch. Stops a
    /// machine with no FxSound from being nagged on every single start.
    /// </summary>
    public bool FxPromptSeen { get; set; }

    public List<AppProfile> AppProfiles { get; set; } = new();

    public List<HotkeySlot> Slots { get; set; } = new();

    public List<UserHotkey> UserHotkeys { get; set; } = new();

    public List<DisplayPreset> CustomDisplayPresets { get; set; } = new();

    public List<AudioPreset> CustomAudioPresets { get; set; } = new();

    public List<ComboPreset> CustomCombos { get; set; } = new();

    /// <summary>
    /// Bumped whenever a change has to be pushed into settings that already exist
    /// on disk. The property initialisers above only ever apply to a brand new
    /// profile, so anything that changes a default needs a matching step here or
    /// existing installs keep the old value forever.
    /// </summary>
    public int Schema { get; set; }

    /// <summary>The version this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>
    /// Brings settings written by an older build up to date.
    ///
    /// Schema 1 moved the opening tune from one of the game presets to the two
    /// neutral ones. Without this an existing install would carry on opening on
    /// the old tune, because a changed property initialiser says nothing about
    /// values that were already saved.
    /// </summary>
    public void Migrate()
    {
        if (Schema >= CurrentSchema)
        {
            return;
        }

        if (Schema < 1)
        {
            ActiveDisplayPresetId = "flat";
            ActiveAudioPresetId = "flat";
        }

        Schema = CurrentSchema;
    }

    public string? GetHotkey(string targetId)
    {
        for (int i = 0; i < UserHotkeys.Count; i++)
        {
            if (string.Equals(UserHotkeys[i].TargetId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                return UserHotkeys[i].Hotkey;
            }
        }

        return HotkeyDefaults.Get(targetId);
    }

    public void SetHotkey(string targetId, string hotkey)
    {
        for (int i = 0; i < UserHotkeys.Count; i++)
        {
            if (string.Equals(UserHotkeys[i].TargetId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                UserHotkeys[i].Hotkey = hotkey;
                return;
            }
        }

        UserHotkeys.Add(new UserHotkey { TargetId = targetId, Hotkey = hotkey });
    }
}

public static class HotkeyDefaults
{
    public static string Get(string targetId)
    {
        if (targetId.StartsWith("combo_", StringComparison.OrdinalIgnoreCase))
        {
            switch (targetId)
            {
                case "combo_tactical":
                    return "ALT+1";
                case "combo_royale":
                    return "ALT+2";
                case "combo_story":
                    return "ALT+3";
                case "combo_night":
                    return "ALT+4";
            }
        }

        if (targetId.StartsWith("display_", StringComparison.OrdinalIgnoreCase))
        {
            int number = NumberOf(targetId, DisplayPreset.Defaults);
            if (number > 0)
            {
                return "CTRL+ALT+" + number.ToString();
            }
        }

        if (targetId.StartsWith("audio_", StringComparison.OrdinalIgnoreCase))
        {
            int number = NumberOf(targetId, AudioPreset.Defaults);
            if (number > 0)
            {
                return "CTRL+SHIFT+" + number.ToString();
            }
        }

        return string.Empty;
    }

    public static string TargetId(string kind, string presetId)
    {
        return kind + "_" + presetId;
    }

    private static int NumberOf(string targetId, IReadOnlyList<DisplayPreset> presets)
    {
        for (int i = 0; i < presets.Count; i++)
        {
            if (string.Equals(TargetId("display", presets[i].Id), targetId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int NumberOf(string targetId, IReadOnlyList<AudioPreset> presets)
    {
        for (int i = 0; i < presets.Count; i++)
        {
            if (string.Equals(TargetId("audio", presets[i].Id), targetId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
