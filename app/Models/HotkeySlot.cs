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
                || !string.IsNullOrWhiteSpace(AppName);
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
            IsAnyGameTarget = IsAnyGameTarget,
            MonitorDevice = MonitorDevice,
            OutputDeviceId = OutputDeviceId,
            Enabled = Enabled
        };
    }
}
