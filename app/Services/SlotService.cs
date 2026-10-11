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
            // Paired with the film look rather than Standard. The sound half of this
            // slot was already doing the work - it is the Immersive curve, and a
            // game with no picture of its own was getting a neutral screen beside
            // a deliberately cinematic one. Standard is not wrong, it is just a
            // mismatch: the two halves of one slot are meant to be one picture.
            DisplayPresetId = "cinematic",
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

    /// <summary>
    /// A new slot that carries over everything worth keeping from another one.
    /// <para>
    /// Setting up a second slot for a game that is already there means pointing
    /// at the same screen, the same sound, the same monitor and the same game
    /// four times. Copying is the whole point of the operation.
    /// </para>
    /// <para>
    /// Three things are deliberately not carried over, and each for its own
    /// reason. The key has to be unique or <c>RegisterHotKey</c> fails and the
    /// slot is silently dead, so the copy starts unbound and the user presses a
    /// key for it. The target is not copied because two slots auto claiming one
    /// game is the ambiguity <see cref="MatchForeground"/> and the auto-claim
    /// resolver already have to unpick; a duplicate would double it on every
    /// launch. And the built-in flag goes, because a copy is the user's own slot
    /// and should be renameable and deletable like one.
    /// </para>
    /// </summary>
    public static HotkeySlot Duplicate(HotkeySlot source, int index)
    {
        HotkeySlot copy = NewSlot(index);

        copy.Name = string.IsNullOrWhiteSpace(source.Name) ? "SLOT" : source.Name + " copy";
        copy.DisplayPresetId = source.DisplayPresetId;
        copy.AudioPresetId = source.AudioPresetId;
        copy.MonitorDevice = source.MonitorDevice;
        copy.ApplyOnStart = source.ApplyOnStart;
        copy.Enabled = source.Enabled;

        // The wildcard is not carried over. A duplicate exists to give a second
        // key to the same look, and a copy of the one wildcard would mean two slots
        // claiming every unbound game - which ResolveAutoClaims then has to stand
        // one of down, so the duplicate silently differs from what it was a
        // duplicate of. The new slot starts untargeted instead, which is what the
        // user can then choose.
        //
        // ApplyOnStart is deliberately left alone. It looks like the same kind of
        // mode flag and is not treated like one: there is a test pinning that it
        // comes across, so a duplicate of a start-up slot is itself a start-up
        // slot. Two of them is ambiguous - the launch picks the first - but that is
        // pre-existing behaviour with a decision behind it, and quietly changing
        // it here would be a different change wearing this feature's clothes.
        copy.IsAnyGameTarget = false;

        return copy;
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
    public static List<HotkeySlot> Migrate(AppSettings settings, bool brandNew = true)
    {
        if (settings.Slots is null || settings.Slots.Count == 0)
        {
            // Only a profile that has never had slots is given the shipped six.
            //
            // This used to be "no slots means no slots yet", which is true of a fresh
            // profile and completely wrong of a used one. Delete every slot and the
            // empty list was written out as "Slots": [], and the next launch read
            // that as a first run and handed back six slots - along with their
            // default bindings, which were then registered with Windows, so keys the
            // user had deliberately unbound came back and started firing. The window
            // meanwhile showed "No slots" right up until the relaunch, which is what
            // made it look like the deletions had not stuck.
            //
            // An empty list on a profile that has been used is a decision somebody
            // made, and the app's job is to keep it rather than overrule it.
            return brandNew ? DefaultSlots() : new List<HotkeySlot>();
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

            // The three target modes are mutually exclusive, and a file can arrive
            // carrying more than one - a hand edit, or a build where the flags were
            // separate. Everything downstream reads them independently, so a slot
            // carrying two would arm two mechanisms and revert through whichever
            // happened to be checked first.
            //
            // On start wins over the wildcard, because it is the older mode and
            // the more specific claim: it applies at a known moment to a known
            // thing, where the wildcard is a fallback. Self over both, since it
            // does not compete for games at all.
            if (slot.IsSelfTarget)
            {
                slot.IsAnyGameTarget = false;
                slot.ApplyOnStart = true;
            }
            else if (slot.IsAnyGameTarget)
            {
                slot.ApplyOnStart = false;
                slot.AppExePath = null;
                slot.AppName = null;
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
            // Matched by name before minting an id, for the same reason the
            // profile pass below does and with the same bug behind it. A combo
            // whose Id does not begin with "slot_" had a fresh one generated on
            // every load, so each launch appended one more slot per combo and the
            // list grew without bound. CustomCombos is never cleared, so nothing
            // stopped it. The name is what the user gave the combo and is what the
            // slot it became is called, so it is also the only thing available to
            // recognise it by.
            bool already = slots.Any(s => string.Equals(s.Name, combo.Name, StringComparison.OrdinalIgnoreCase));
            if (already)
            {
                continue;
            }

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

        if (slot.IsAnyGameTarget)
        {
            return "any";
        }

        if (string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            // A folder rule is its own kind of target and gets its own key. Returning
            // the empty string here would make ResolveAutoClaims switch auto-activate
            // off for every folder-bound slot, which is the one thing the feature
            // cannot be for.
            return string.IsNullOrWhiteSpace(slot.TargetFolder)
                ? string.Empty
                : "folder:" + slot.TargetFolder.TrimEnd('\\', '/').ToLowerInvariant();
        }

        return AppProfileTools.ProcessNameOf(slot.AppExePath).ToLowerInvariant();
    }

    /// <summary>
    /// Whether an executable lives inside a folder.
    /// <para>
    /// The whole implementation is the trailing separator, and that is what this is
    /// for. A prefix comparison alone would let a slot bound to
    /// <c>D:\Games\Foo</c> claim every game on the drive that happens to start
    /// with those same letters, and a broad folder rule firing on the wrong game is
    /// the exact failure a specific binding is supposed to prevent.
    /// </para>
    /// <para>
    /// Text rather than <see cref="Path"/> arithmetic, deliberately. This is
    /// reached from the foreground tick with a string out of a hand-editable
    /// settings file, so it has to survive whatever that file says: a null, a
    /// stray NUL, a path with a character no path may contain. Resolving either
    /// side throws on all three, and an exception here takes the whole tick down.
    /// Every one of those answers false instead, which is the safe answer - a rule
    /// that cannot be understood does not match.
    /// </para>
    /// <para>
    /// A drive root is rejected rather than honoured. "Everything under C:\" is
    /// not a folder rule, it is the machine, and a profile carrying it would fire
    /// on everything the user ever runs - which for this app means a game loading
    /// its boost on every single launch.
    /// </para>
    /// </summary>
    /// <summary>
    /// The name to give a slot for a game the app noticed but was never told about.
    /// <para>
    /// The window title is the only one of the three strings that is ever the
    /// game's own name. A process called <c>launcher.exe</c> has a window that says
    /// what it is launching, and the exe name is just the name of the thing that
    /// starts it - so a slot called "launcher" is a slot the user has to open to
    /// find out what it does.
    /// </para>
    /// <para>
    /// Two things disqualify the title, and both are ordinary rather than rare.
    /// Games that draw their own chrome leave the title as the exe name, which is
    /// the same string twice; and a title can be a path, a URL or a line out of a
    /// log, none of which belongs on a one-line slot row. So an empty, silly-long,
    /// or merely-duplicate title falls back to the process name.
    /// </para>
    /// <para>
    /// Trimmed, because window titles carry trailing whitespace often enough that
    /// an untrimmed name would not match what the user types to find it again.
    /// </para>
    /// <para>
    /// Lives here rather than on the window, where it was written. Naming a slot
    /// from a window is slot logic, and having it in the window is what forced the
    /// caller that needed it - <see cref="AddCurrentGame"/> - to reach into the UI
    /// layer to get at a pure string function.
    /// </para>
    /// </summary>
    public static string NameForCurrentGame(WatchedWindow window)
    {
        // A slot row is one line high and the name is what the user reads on the
        // board, so this is a display limit rather than a storage one.
        const int MaxNameLength = 60;

        string process = window.ProcessName ?? string.Empty;
        string title = (window.Title ?? string.Empty).Trim();

        bool usable = title.Length > 0
            && title.Length <= MaxNameLength
            && !string.Equals(title, process, StringComparison.OrdinalIgnoreCase);

        return usable ? title : process;
    }


    /// <summary>
    /// Whether a path sits inside a directory, separator and all.
    /// <para>
    /// The separator is what stops "C:\Program Files (x86)" from counting as
    /// inside "C:\Program Files", which a plain StartsWith would happily report.
    /// Both sides are compared on their full form first, so a ".." that climbs
    /// out and one that does not are told apart - the comparison here is on the
    /// resolved paths, not on the text that was combined.
    /// </para>
    /// </summary>
    public static bool IsUnderFolder(string? exePath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        string root = TrimTrailingSeparators(folder);

        // "C:" and "C:\" both trim down to this, and it has to be recognised
        // before anything else, or a drive root becomes the prefix that claims
        // every path on that drive. Length is the test, not the colon: every
        // absolute path has a colon at index 1.
        if (root.Length <= 2)
        {
            return false;
        }

        string exe = TrimTrailingSeparators(exePath);
        if (exe.Length <= root.Length || !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Which also covers the folder itself: the rule is "runs out of here", and
        // a path equal to the folder is a directory rather than something that can
        // be executed.
        return exe[root.Length] == '\\' || exe[root.Length] == '/';
    }

    /// <summary>
    /// Drops the trailing directory separators, which a folder rule may or may not
    /// carry depending on whether the user typed them or a picker produced them.
    /// <para>
    /// Never returns an empty string: it stops at one character, so a drive root
    /// trims to "C:" rather than to nothing and then reads as a relative folder
    /// under the current directory.
    /// </para>
    /// </summary>
    private static string TrimTrailingSeparators(string path)
    {
        int end = path.Length;
        while (end > 1 && (path[end - 1] == '\\' || path[end - 1] == '/'))
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }

public static void ResolveAutoClaims(List<HotkeySlot> slots)
    {
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        // At most one wildcard, and the first one in the list keeps it.
        //
        // Two wildcards would both match every unbound fullscreen program, so
        // which one applied would come down to the order they happen to sit in the
        // list - which is not a rule anybody could state, let alone rely on. First
        // wins is the same rule every other conflict on this screen already uses.
        bool wildcardTaken = false;

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

            if (slot.IsAnyGameTarget)
            {
                if (wildcardTaken)
                {
                    slot.AutoActivate = false;
                    continue;
                }

                wildcardTaken = true;
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

    /// <summary>
    /// The output device a slot should actually send sound to.
    /// <para>
    /// The slot's own device when it names one the app currently knows about,
    /// and the app's own setting otherwise. That fallback is the whole reason
    /// this is a function and not a property read: a device that was unplugged,
    /// renamed by a driver update, or removed since the slot named it has to
    /// resolve to something real.
    /// </para>
    /// <para>
    /// Falling back rather than sending the stale name is what keeps this from
    /// reaching the output repair. The repair exists because the engine
    /// sometimes does not take a device, and it works by noticing a
    /// disagreement and putting a usable one back. Handing it a name that is
    /// already gone would be asking it to recover from something the app could
    /// have answered before the engine ever saw it, and the app's own
    /// configuration is a perfectly good answer.
    /// </para>
    /// <para>
    /// Matched case insensitively, and the match comes back rather than the slot's
    /// own spelling. These names come from a driver and the engine looks the
    /// device up by name, so sending back a hand edited lower case version of one
    /// that is present would be a device the engine does not recognise - which is
    /// the same failure as sending a device that is genuinely gone, arrived at by
    /// a different route.
    /// </para>
    /// </summary>
    public static string ResolveOutputDevice(
        HotkeySlot slot,
        IReadOnlyList<string> knownDevices,
        string appDevice)
    {
        if (!string.IsNullOrWhiteSpace(slot.OutputDeviceId))
        {
            foreach (string known in knownDevices)
            {
                if (string.Equals(known, slot.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }
        }

        return appDevice;
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

            // A wildcard names no process, so it contributes nothing here. That is
            // correct rather than an oversight: the watcher watches a fixed list of
            // names, and a wildcard works from the foreground window instead, which
            // is a different route entirely. See MatchWildcard.
            if (slot.IsSelfTarget || slot.IsAnyGameTarget)
            {
                continue;
            }

            // A folder rule for the same reason, and it is worth saying out loud
            // because it looks like an omission. A folder says "when something from
            // here is in the foreground", not "watch this folder for processes
            // starting", and the second is not a thing this app does on a timer -
            // there is no event for it and no cheap way to ask. So a folder-bound
            // slot is matched from the foreground window and nothing else.
            if (!string.IsNullOrWhiteSpace(slot.TargetFolder))
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

        // Last, and only after both specific bindings have been given their chance.
        // The order is the whole design and it is the same one MatchWildcard
        // follows: a folder rule is broader than a path, so letting list order
        // decide between them would make every specific binding on the machine
        // pointless the moment anyone added a wide folder rule.
        for (int i = 0; i < slots.Count; i++)
        {
            HotkeySlot slot = slots[i];
            if (!slot.Enabled || !slot.AutoActivate || !slot.HasWork)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(slot.TargetFolder)
                && IsUnderFolder(exePath, slot.TargetFolder))
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// The wildcard slot, if one is armed.
    /// <para>
    /// Kept separate from <see cref="MatchForeground"/> because the order is the
    /// whole design. A specific match always wins: a wildcard is a fallback for
    /// "I forgot to bind this", not a competitor. Checking for one first would
    /// mean every bound game was at the mercy of list order.
    /// </para>
    /// <para>
    /// The caller decides whether the window is a candidate at all - that is the
    /// fullscreen test, and it needs the window handle rather than the strings
    /// this class is handed. What is here is only "is there a wildcard, and is it
    /// armed".
    /// </para>
    /// </summary>
    public static HotkeySlot? MatchWildcard(IReadOnlyList<HotkeySlot> slots)
    {
        foreach (HotkeySlot slot in slots)
        {
            if (slot.Enabled && slot.AutoActivate && slot.HasWork && slot.IsAnyGameTarget)
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
