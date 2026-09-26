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
    public string ActiveDisplayPresetId { get; set; } = "camper";

    public string ActiveAudioPresetId { get; set; } = "footstep";

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
    /// Auto preamp: trims master gain by the largest EQ boost so boosted bands
    /// cannot hit 0 dBFS and hard-clip.
    /// </summary>
    public bool AntiClip { get; set; } = true;

    /// <summary>0 = off, 1 = warm, 2 = extra warm.</summary>
    public int BlueLightFilter { get; set; }

    public List<AppProfile> AppProfiles { get; set; } = new();

    public List<HotkeySlot> Slots { get; set; } = new();

    public List<UserHotkey> UserHotkeys { get; set; } = new();

    public List<DisplayPreset> CustomDisplayPresets { get; set; } = new();

    public List<AudioPreset> CustomAudioPresets { get; set; } = new();

    public List<ComboPreset> CustomCombos { get; set; } = new();

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
