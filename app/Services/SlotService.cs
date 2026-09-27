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

    public static List<HotkeySlot> Migrate(AppSettings settings)
    {
        List<HotkeySlot> slots = new();
        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeySlot slot in settings.Slots ?? new List<HotkeySlot>())
        {
            if (string.IsNullOrWhiteSpace(slot.Id))
            {
                slot.Id = AppProfileTools.NewId("slot");
            }

            if (string.IsNullOrWhiteSpace(slot.Name))
            {
                slot.Name = "SLOT";
            }

            if (!usedIds.Add(slot.Id))
            {
                continue;
            }

            slots.Add(slot);
        }

        foreach (ComboPreset combo in ComboPreset.Defaults)
        {
            string key = HotkeyKey(combo.Hotkey);
            if (slots.Any(s => string.Equals(HotkeyKey(s.Hotkey), key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string id = combo.Id.Replace("combo_", "slot_", StringComparison.Ordinal);
            if (usedIds.Contains(id))
            {
                continue;
            }

            usedIds.Add(id);
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

        foreach (ComboPreset combo in settings.CustomCombos ?? new List<ComboPreset>())
        {
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

        if (slots.Count == 0)
        {
            slots.AddRange(DefaultSlots());
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

    public static string HotkeyKey(string hotkey)
    {
        return HotkeyServiceNormalizer.Normalize(hotkey);
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

public static class HotkeyServiceNormalizer
{
    public static string Normalize(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return string.Empty;
        }

        return hotkey.Trim().ToUpperInvariant().Replace("CONTROL", "CTRL").Replace("WINDOWS", "WIN");
    }
}
