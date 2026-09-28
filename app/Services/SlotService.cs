using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class SlotService
{
    // The five shipped slots pair a screen preset with a sound preset that are
    // meant to be used together, and a sixth unassigned baseline so there is
    // always a way back to neutral from the slot list. Ids are kept stable so
    // saved presets and existing slots keep pointing at the same entry.
    public static IReadOnlyList<HotkeySlot> FactorySlots { get; } = new List<HotkeySlot>
    {
        new HotkeySlot
        {
            Id = "slot_tactical",
            Name = "Tactical FPS",
            DisplayPresetId = "camper",
            AudioPresetId = "footstep",
            Hotkey = "SHIFT+1",
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        },
        new HotkeySlot
        {
            Id = "slot_royale",
            Name = "Battle Royale",
            DisplayPresetId = "racing",
            AudioPresetId = "royale",
            Hotkey = "SHIFT+2",
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        },
        new HotkeySlot
        {
            Id = "slot_story",
            Name = "Story and RPG",
            DisplayPresetId = "flat",
            AudioPresetId = "arcade",
            Hotkey = "SHIFT+3",
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        },
        new HotkeySlot
        {
            Id = "slot_cinema",
            Name = "Cinema",
            DisplayPresetId = "cinematic",
            AudioPresetId = "cinematic",
            Hotkey = "ALT+1",
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        },
        new HotkeySlot
        {
            Id = "slot_night",
            Name = "Late Night",
            DisplayPresetId = "oled",
            AudioPresetId = "latenight",
            Hotkey = "ALT+2",
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        },
        // Deliberately left without a key. It is the way back to neutral without
        // hunting for a reset button, and it does not fight the other five for a
        // binding the user has not chosen yet.
        new HotkeySlot
        {
            Id = "slot_desktop",
            Name = "Desktop",
            DisplayPresetId = "flat",
            AudioPresetId = "flat",
            Hotkey = string.Empty,
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        }
    };

    public static HotkeySlot NewSlot(int index)
    {
        return new HotkeySlot
        {
            Id = AppProfileTools.NewId("slot"),
            Name = "SLOT " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayPresetId = null,
            AudioPresetId = null,
            Hotkey = string.Empty,
            AppExePath = null,
            AppName = null,
            AutoActivate = false,
            Enabled = true
        };
    }

    public static List<HotkeySlot> DefaultSlots()
    {
        List<HotkeySlot> slots = new();
        foreach (HotkeySlot slot in FactorySlots)
        {
            HotkeySlot copy = slot.Copy();
            copy.BuiltIn = true;
            slots.Add(copy);
        }

        return slots;
    }

    /// <summary>
    /// Brings a saved slot list up to date, and supplies the shipped one when
    /// there is nothing to bring up.
    /// <para>
    /// The emptiness check is at the top on purpose. It used to sit at the
    /// bottom, after the legacy combo and app profile passes had each contributed
    /// slots of their own, which meant a brand new profile came out with four
    /// legacy combos rather than the six shipped slots, never got the neutral
    /// Desktop slot at all, and then had those four written to disk, so every
    /// later launch loaded them too. A restore went the same way and came back
    /// with four slots the user had never asked for.
    /// </para>
    /// <para>
    /// The combo pass is gone rather than ported. Its four entries pair up
    /// different presets from the shipped slots under the same names, so
    /// carrying them across would change what an existing install loads; and
    /// their ids collide with the shipped ones, so there is nowhere to put them
    /// without a mapping table. <see cref="ComboPreset"/> is kept only so a
    /// settings file that still carries custom combos can be read and ignored.
    /// </para>
    /// </summary>
    public static List<HotkeySlot> Migrate(AppSettings settings)
    {
        if (settings.Slots is null || settings.Slots.Count == 0)
        {
            return DefaultSlots();
        }

        List<HotkeySlot> slots = new();
        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeySlot slot in settings.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Id))
            {
                slot.Id = AppProfileTools.NewId("slot");
            }

            if (string.IsNullOrWhiteSpace(slot.Name))
            {
                slot.Name = "SLOT";
            }

            // Drop a stored binding that names a key nobody can press. Builds
            // before the Alt fix could save "ALT+SYSTEM", which looked like a
            // real chord on screen and never fired once. Left alone it would sit
            // there forever looking assigned, and the user would have no way to
            // tell which of their slots were real.
            if (!string.IsNullOrWhiteSpace(slot.Hotkey) && !HotkeyService.IsBindable(slot.Hotkey))
            {
                slot.Hotkey = string.Empty;
            }


            if (!usedIds.Add(slot.Id))
            {
                continue;
            }

            slots.Add(slot);
        }

        foreach (ComboPreset combo in settings.CustomCombos ?? new List<ComboPreset>())
        {
            // A custom combo from a build that had them becomes a slot, so
            // nothing a user made is silently dropped on upgrade.
            string id = combo.Id.StartsWith("slot_", StringComparison.Ordinal) ? combo.Id : AppProfileTools.NewId("slot");
            if (!usedIds.Add(id))
            {
                continue;
            }

            slots.Add(new HotkeySlot
            {
                Id = id,
                Name = combo.Name,
                DisplayPresetId = combo.DisplayPresetId,
                AudioPresetId = combo.AudioPresetId,
                Hotkey = combo.Hotkey,
                AutoActivate = false,
                Enabled = true
            });
        }

        foreach (AppProfile profile in settings.AppProfiles ?? new List<AppProfile>())
        {
            // A profile that already has a slot pointing at it is left alone.
            // This pass used to run unconditionally and mint a fresh id every
            // time, so each launch added one more slot per profile and the list
            // grew without bound.
            bool already = slots.Any(s => !string.IsNullOrWhiteSpace(profile.ExePath)
                && string.Equals(s.AppExePath, profile.ExePath, StringComparison.OrdinalIgnoreCase));

            if (already)
            {
                continue;
            }

            HotkeySlot slot = new()
            {
                Id = AppProfileTools.NewId("slot"),
                Name = profile.Name,
                DisplayPresetId = profile.DisplayPresetId,
                AudioPresetId = profile.AudioPresetId,
                Hotkey = string.Empty,
                AppExePath = profile.ExePath,
                AppName = profile.Name,
                AutoActivate = profile.Enabled,
                Enabled = true
            };

            if (slots.Any(s => string.Equals(s.Name, slot.Name, StringComparison.OrdinalIgnoreCase)))
            {
                slot.Name = slot.Name + " AUTO";
            }

            usedIds.Add(slot.Id);
            slots.Add(slot);
        }

        foreach (HotkeySlot slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Hotkey))
            {
                continue;
            }

            if (!usedKeys.Add(HotkeyKey(slot.Hotkey)))
            {
                slot.Hotkey = string.Empty;
            }
        }

        ResolveAutoClaims(slots);
        return slots;
    }

    public static string TargetKey(HotkeySlot slot)
    {
        if (slot.IsSelfTarget)
        {
            return "self";
        }

        if (string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            return string.Empty;
        }

        return AppProfileTools.ProcessNameOf(slot.AppExePath).ToLowerInvariant();
    }

    public static void ResolveAutoClaims(List<HotkeySlot> slots)
    {
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeySlot slot in slots)
        {
            if (!slot.AutoActivate)
            {
                continue;
            }

            if (!slot.HasWork)
            {
                slot.AutoActivate = false;
                continue;
            }

            string key = TargetKey(slot);
            if (key.Length == 0)
            {
                slot.AutoActivate = false;
                continue;
            }

            if (!claimed.Add(key))
            {
                slot.AutoActivate = false;
            }
        }
    }

    /// <summary>
    /// The one key identity, delegated to the one canonicaliser. See
    /// <see cref="HotkeyService.Normalise"/> for why it has to be that and not a
    /// local string cleanup.
    /// </summary>
    public static string HotkeyKey(string hotkey)
    {
        return HotkeyService.Normalise(hotkey);
    }

    /// <summary>
    /// True when the slot's own presets are the ones currently loaded, which is
    /// what makes a slot key a toggle.
    /// <para>
    /// A half that the slot does not set counts as matching. A sound-only slot
    /// has no screen preset, so comparing that half against whatever screen is
    /// loaded could never succeed and the key could only ever turn the slot on.
    /// The same went for a screen-only slot and for the empty preset id a
    /// deleted slot leaves behind. Such a slot does not own that half of the
    /// tune, so it cannot be the thing that tells on from off.
    /// </para>
    /// </summary>
    public static bool IsLoaded(HotkeySlot slot, string activeDisplayId, string activeAudioId)
    {
        if (!slot.HasWork)
        {
            return false;
        }

        bool screen = string.IsNullOrWhiteSpace(slot.DisplayPresetId)
            || string.Equals(slot.DisplayPresetId, activeDisplayId, StringComparison.OrdinalIgnoreCase);

        bool sound = string.IsNullOrWhiteSpace(slot.AudioPresetId)
            || string.Equals(slot.AudioPresetId, activeAudioId, StringComparison.OrdinalIgnoreCase);

        return screen && sound;
    }

    public static HashSet<string> TargetProcessNames(IEnumerable<HotkeySlot> slots)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (HotkeySlot slot in slots)
        {
            if (!slot.Enabled || !slot.AutoActivate)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(slot.AppName))
            {
                names.Add(slot.AppName);
            }

            if (!string.IsNullOrWhiteSpace(slot.AppExePath))
            {
                names.Add(AppProfileTools.ProcessNameOf(slot.AppExePath));
            }
        }

        names.Remove(string.Empty);
        return names;
    }

    public static HotkeySlot? MatchForeground(IReadOnlyList<HotkeySlot> slots, string exePath, string processName)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            HotkeySlot slot = slots[i];
            if (!slot.Enabled || !slot.AutoActivate || !slot.HasWork)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(slot.AppExePath)
                && string.Equals(slot.AppExePath, exePath, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            HotkeySlot slot = slots[i];
            if (!slot.Enabled || !slot.AutoActivate || !slot.HasWork)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(processName)
                && !string.IsNullOrWhiteSpace(slot.AppExePath)
                && string.Equals(AppProfileTools.ProcessNameOf(slot.AppExePath), processName, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        return null;
    }

    public static HotkeySlot? MatchProcessName(IReadOnlyList<HotkeySlot> slots, string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            HotkeySlot slot = slots[i];
            if (!slot.Enabled || !slot.AutoActivate || !slot.HasWork)
            {
                continue;
            }

            if (string.Equals(AppProfileTools.ProcessNameOf(slot.AppExePath ?? string.Empty), processName, StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        return null;
    }
}
