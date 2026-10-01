using System;
using System.Collections.Generic;

namespace GamerTool.Models;

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

    public string Hotkey { get; set; } = string.Empty;

    public string? AppExePath { get; set; }

    public string? AppName { get; set; }

    public bool AutoActivate { get; set; }

    public bool ApplyOnStart { get; set; }

    public string MonitorDevice { get; set; } = string.Empty;

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

    public bool IsSelfTarget { get; set; }

    public bool Enabled { get; set; } = true;

    public bool HasTarget
    {
        get
        {
            return IsSelfTarget || !string.IsNullOrWhiteSpace(AppExePath) || !string.IsNullOrWhiteSpace(AppName);
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
            BuiltIn = BuiltIn,
            DisplayPresetId = DisplayPresetId,
            AudioPresetId = AudioPresetId,
            Hotkey = Hotkey,
            AppExePath = AppExePath,
            AppName = AppName,
            AutoActivate = AutoActivate,
            ApplyOnStart = ApplyOnStart,
            IsSelfTarget = IsSelfTarget,
            MonitorDevice = MonitorDevice,
            OutputDeviceId = OutputDeviceId,
            Enabled = Enabled
        };
    }
}
