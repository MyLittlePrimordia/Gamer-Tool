using System;
using System.Collections.Generic;

namespace GamerTool.Models;

public sealed class HotkeySlot
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? DisplayPresetId { get; set; }

    public string? AudioPresetId { get; set; }

    public string Hotkey { get; set; } = string.Empty;

    public string? AppExePath { get; set; }

    public string? AppName { get; set; }

    public bool AutoActivate { get; set; }

    public bool ApplyOnStart { get; set; }

    public string MonitorDevice { get; set; } = string.Empty;

    public bool IsSelfTarget { get; set; }

    public bool Enabled { get; set; } = true;

    public bool HasTarget
    {
        get
        {
            return IsSelfTarget || !string.IsNullOrWhiteSpace(AppExePath) || !string.IsNullOrWhiteSpace(AppName);
        }
    }

    public string ScopeText
    {
        get
        {
            return MonitorDevice.Length == 0 ? "ALL SCREENS" : MonitorDevice.ToUpperInvariant();
        }
    }

    public bool HasWork
    {
        get
        {
            return !string.IsNullOrWhiteSpace(DisplayPresetId) || !string.IsNullOrWhiteSpace(AudioPresetId);
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

            if (!HasTarget)
            {
                return "NO GAME";
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
            if (hasScreen && hasSound)
            {
                return "SCREEN + SOUND";
            }

            if (hasScreen)
            {
                return "SCREEN ONLY";
            }

            if (hasSound)
            {
                return "SOUND ONLY";
            }

            return "NOTHING SET";
        }
    }

    public HotkeySlot Copy()
    {
        return new HotkeySlot
        {
            Id = Id,
            Name = Name,
            DisplayPresetId = DisplayPresetId,
            AudioPresetId = AudioPresetId,
            Hotkey = Hotkey,
            AppExePath = AppExePath,
            AppName = AppName,
            AutoActivate = AutoActivate,
            ApplyOnStart = ApplyOnStart,
            IsSelfTarget = IsSelfTarget,
            MonitorDevice = MonitorDevice,
            Enabled = Enabled
        };
    }
}
