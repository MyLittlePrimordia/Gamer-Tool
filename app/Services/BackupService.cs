using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class BackupFile
{
    /// <summary>
    /// Empty and zero by default, deliberately, and the writer sets both.
    /// <para>
    /// These used to default to the real tag and version so a backup could be
    /// built without naming them. That is exactly what made the file impossible
    /// to validate: any JSON object at all deserialises into a BackupFile, and
    /// with the tag and version pre-filled it then looked like a valid one. An
    /// unrelated file became an empty profile that the app would offer to
    /// restore over a real one. A tag that has to be written to be believed is
    /// the whole point of having a tag.
    /// </para>
    /// </summary>
    public string Format { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary>
    /// The whole configuration being backed up, and nothing else.
    /// <para>
    /// There used to be two more fields here: a creation timestamp and the
    /// computer's name. Neither was ever read - not by the importer, not by the
    /// UI, not by a test - so both were payload that only left the machine. The
    /// name was the sharper problem, because a backup is a file people are meant
    /// to hand to somebody else, and <c>AppLog.Sanitise</c> goes out of its way to
    /// redact the user name from every log line for exactly that reason. The same
    /// file was then stamped with the machine name on the way out of the export.
    /// </para>
    /// <para>
    /// The date is not lost by dropping the timestamp: the suggested file name
    /// carries it. Removing it also makes an export of unchanged settings
    /// byte-for-byte reproducible, which it was not while a wall clock was in
    /// there.
    /// </para>
    /// <para>
    /// Backups written by an older build still import. System.Text.Json ignores
    /// members it does not recognise, so the two removed fields are simply
    /// skipped on the way in.
    /// </para>
    /// </summary>
    public AppSettings? Settings { get; set; }
}

public sealed class RestoreReport
{
    public List<string> Notes { get; } = new();

    public int SlotsCleared { get; set; }

    public int SlotsRepointed { get; set; }

    public int SlotsLostScreen { get; set; }

    public int SlotsLostSound { get; set; }

    public int ScreensReset { get; set; }

    public int KeysCleared { get; set; }

    public bool SoundDeviceReset { get; set; }

    public bool FxSoundPathReset { get; set; }

    public int SlotCount { get; set; }

    public int ScreenPresetCount { get; set; }

    public int AudioPresetCount { get; set; }

    public bool AnythingToReport
    {
        get
        {
            return SlotsCleared > 0
                || SlotsRepointed > 0
                || SlotsLostScreen > 0
                || SlotsLostSound > 0
                || ScreensReset > 0
                || KeysCleared > 0
                || SoundDeviceReset
                || FxSoundPathReset;
        }
    }

    public string Headline
    {
        get
        {
            return SlotCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " SLOTS, "
                + ScreenPresetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " SCREEN, "
                + AudioPresetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " SOUND PRESETS";
        }
    }

    public string Summary
    {
        get
        {
            List<string> parts = new();
            Add(parts, SlotsRepointed, "GAME RE-FOUND");
            Add(parts, SlotsCleared, "GAME MISSING, TARGET CLEARED");
            Add(parts, KeysCleared, "DUPLICATE KEYS CLEARED");
            Add(parts, SlotsLostScreen + SlotsLostSound, "MISSING PRESET CLEARED");
            Add(parts, ScreensReset, "SCREEN RESET");
            if (SoundDeviceReset)
            {
                parts.Add("SOUND OUTPUT RESET");
            }

            if (FxSoundPathReset)
            {
                parts.Add("FXSOUND PATH RESET");
            }

            return parts.Count == 0 ? "EVERYTHING MATCHED" : string.Join(" + ", parts);
        }
    }

    private static void Add(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + label);
        }
    }
}

public sealed class BackupService
{
    public const string FormatTag = "GamerTool.Backup";

    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string SuggestedFileName()
    {
        return "GamerTool-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".json";
    }

    public void Export(AppSettings settings, string path)
    {
        BackupFile file = new()
        {
            Format = FormatTag,
            Version = FormatVersion,
            Settings = settings
        };

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // Serialised fully in memory first, written to a temporary file, then swapped
        // into place - the same three steps ProfileManager.Save takes for
        // settings.json, and for the same reason. This was a single
        // File.WriteAllText: a crash, a full disk or a killed process part way
        // through left the user with a half-written backup and no other copy of
        // their configuration, which is the one artefact in this app that exists
        // nowhere else. It did not keep a ".bak" the way the profile does, because
        // the whole point of a backup is that it is somewhere else.
        // Under the profile's own gate, exactly as ProfileManager.Save does it and for
        // the same reason. Save's comment names the hazard: the backlight worker
        // adds to the exclusion list and to the remembered brightness from a pool
        // thread while the UI thread serialises, and enumerating a collection while
        // another thread is adding to it throws. Taking the same lock here is what
        // stops the backup being the one path that does not.
        //
        // It is not redundant with anything. The worker holds BacklightService's
        // gate while it makes those writes, not settings.Gate, so the two locks do
        // not exclude each other and only settings.Gate is common to both sides.
        string json;
        lock (settings.Gate)
        {
            json = JsonSerializer.Serialize(file, Options);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        ProfileManager.AtomicSwap(temp, path);
    }

    public AppSettings? Import(string path, out RestoreReport report, out string error)
    {
        report = new RestoreReport();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                error = "FILE NOT FOUND";
                return null;
            }

            string json = BoundedRead.AllText(path);
            AppSettings? settings = Read(json);
            if (settings is null)
            {
                error = "NOT A GAMER TOOL BACKUP";
                return null;
            }

            // brandNew is false, and this is the reason the parameter exists. A backup is a
        // profile somebody actually used, so an empty slot list in one is a decision
        // they made and are getting back, not a first run that has yet to be given
        // the shipped slots. Restoring an empty profile used to hand back six.
        return ProfileManager.Normalize(settings, brandNew: false);
        }
        catch (Exception ex)
        {
            TraceLog.Write("BACKUP", ex);
            error = "FILE COULD NOT BE READ";
            return null;
        }
    }

    /// <summary>
    /// Members whose presence proves a JSON object is one of this app's own
    /// profile files rather than some other file that happens to parse.
    /// <para>
    /// A settings.json written by a build that predates the backup wrapper is
    /// still a genuine profile and still worth restoring, so it is accepted
    /// without a tag. But that path cannot simply trust the shape either, for
    /// the same reason the tagged path cannot: any JSON object deserialises into
    /// an empty profile and looks like a real one. So the object has to be shown
    /// to carry at least one name this app actually writes.
    /// </para>
    /// </summary>
    /// <summary>
    /// Property names that mark a blob as one of this app's own profiles.
    /// <para>
    /// "UserHotkeys" has no property behind it any more. It is kept because
    /// profiles written before hotkeys moved into slots still carry the key, and
    /// dropping the marker would stop those files being recognised as something
    /// worth offering to restore. It is a name in a file, not a live field, which
    /// is why looking for it in the model finds nothing.
    /// </para>
    /// </summary>
    private static readonly string[] ProfileMarkers =
    {
        "Schema", "Slots", "UserHotkeys", "ActiveDisplayPresetId", "ActiveAudioPresetId",
        "CustomDisplayPresets", "CustomAudioPresets", "AppProfiles", "CustomCombos",
        "GammaLock", "ShowOsd", "StartHidden", "CloseToTray", "AntiClip", "BlueLightFilter"
    };

    private static AppSettings? Read(string json)
    {
        try
        {
            BackupFile? file = JsonSerializer.Deserialize<BackupFile>(json, Options);
            if (file is not null
                && string.Equals(file.Format, FormatTag, StringComparison.Ordinal)
                && file.Version > 0
                && file.Version <= FormatVersion
                && file.Settings is not null
                && file.Settings.Slots is not null)
            {
                return file.Settings;
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            AppSettings? bare = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return bare is not null && CarriesProfileMarkers(json) ? bare : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the JSON holds at least one name this app writes.</summary>
    private static bool CarriesProfileMarkers(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                foreach (string marker in ProfileMarkers)
                {
                    if (string.Equals(property.Name, marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Repair(
        AppSettings settings,
        IReadOnlyList<string> validMonitors,
        IReadOnlyList<string> validSoundDevices,
        IReadOnlyList<AppCandidate> knownApps,
        string? workingFxSoundPath,
        RestoreReport report)
    {
        report.SlotCount = settings.Slots.Count;
        report.ScreenPresetCount = settings.CustomDisplayPresets.Count;
        report.AudioPresetCount = settings.CustomAudioPresets.Count;

        HashSet<string> screens = new(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            screens.Add(preset.Id);
        }

        foreach (DisplayPreset preset in settings.CustomDisplayPresets)
        {
            screens.Add(preset.Id);
        }

        HashSet<string> sounds = new(StringComparer.OrdinalIgnoreCase);
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            sounds.Add(preset.Id);
        }

        foreach (AudioPreset preset in settings.CustomAudioPresets)
        {
            sounds.Add(preset.Id);
        }

        HashSet<string> monitorSet = new(validMonitors, StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeySlot slot in settings.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.DisplayPresetId) && !screens.Contains(slot.DisplayPresetId))
            {
                slot.DisplayPresetId = null;
                report.SlotsLostScreen++;
            }

            if (!string.IsNullOrWhiteSpace(slot.AudioPresetId) && !sounds.Contains(slot.AudioPresetId))
            {
                slot.AudioPresetId = null;
                report.SlotsLostSound++;
            }

            if (slot.MonitorDevice.Length > 0 && !monitorSet.Contains(slot.MonitorDevice))
            {
                slot.MonitorDevice = string.Empty;
                report.ScreensReset++;
            }

            RepairTarget(slot, knownApps, report);

            if (slot.Hotkey.Length > 0 && !usedKeys.Add(slot.Hotkey))
            {
                slot.Hotkey = string.Empty;
                report.KeysCleared++;
            }
        }

        foreach (AppProfile profile in settings.AppProfiles)
        {
            if (profile.Enabled && profile.ExePath.Length > 0 && !File.Exists(profile.ExePath))
            {
                string? found = FindByName(knownApps, AppProfileTools.ProcessNameOf(profile.ExePath));
                if (found is not null)
                {
                    profile.ExePath = found;
                }
            }
        }

        if (settings.OutputDeviceId.Length > 0 && !validSoundDevices.Contains(settings.OutputDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            settings.OutputDeviceId = string.Empty;
            settings.OutputDeviceName = string.Empty;
            report.SoundDeviceReset = true;
        }

        // A saved engine path belongs to the machine that wrote it. Restoring a profile
        // is the main way a path arrives from somewhere else, and it is not
        // automatically wrong: FxSound installed in a custom folder is still the
        // engine, and the app already looks in four places for it. So the working
        // path is preferred and the imported one is only kept when it points at
        // something that is actually called FxSound.exe.
        //
        // The old rule replaced the path only when the imported one did not exist,
        // which left a path from another machine in place whenever it did. That is
        // the shape of a problem rather than a nuisance: the path is the FileName
        // of a process the app launches, and a profile is the sort of thing people
        // swap, so a backup carrying someone else's path would have run whatever
        // it named, as this user, on every engine call.
        if (!string.IsNullOrWhiteSpace(workingFxSoundPath)
            && !AudioService.IsPlausibleFxSoundPath(settings.FxSoundPath))
        {
            settings.FxSoundPath = workingFxSoundPath;
            report.FxSoundPathReset = true;
        }

        if (report.AnythingToReport)
        {
            BuildNotes(report);
        }
    }

    private static void RepairTarget(HotkeySlot slot, IReadOnlyList<AppCandidate> knownApps, RestoreReport report)
    {
        if (slot.IsSelfTarget)
        {
            slot.AppExePath = null;
            slot.AppName = null;
            return;
        }

        // A wildcard is already consistent - it names no file, so there is
        // nothing for the check below to fail on. Without this early return it
        // would fall into the empty-path branch and disarm the slot, which would
        // silently drop a wildcard from every restored backup. That is the
        // failure mode this repair exists to prevent, happening to the one target
        // shape it does not know about.
        if (slot.IsAnyGameTarget)
        {
            slot.AppExePath = null;
            slot.AppName = null;
            slot.IsSelfTarget = false;
            slot.ApplyOnStart = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            slot.AutoActivate = false;
            slot.ApplyOnStart = false;
            return;
        }

        if (File.Exists(slot.AppExePath))
        {
            return;
        }

        string? found = FindByName(knownApps, AppProfileTools.ProcessNameOf(slot.AppExePath));
        if (found is not null)
        {
            slot.AppExePath = found;
            slot.AppName = AppProfileTools.ProcessNameOf(found);
            report.SlotsRepointed++;
            return;
        }

        slot.AppExePath = null;
        slot.AppName = null;
        slot.AutoActivate = false;
        slot.ApplyOnStart = false;
        report.SlotsCleared++;
    }

    private static string? FindByName(IReadOnlyList<AppCandidate> knownApps, string processName)
    {
        if (processName.Length == 0)
        {
            return null;
        }

        foreach (AppCandidate candidate in knownApps)
        {
            if (string.Equals(candidate.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                && candidate.ExePath.Length > 0)
            {
                return candidate.ExePath;
            }
        }

        foreach (AppCandidate candidate in knownApps)
        {
            if (string.Equals(AppProfileTools.ProcessNameOf(candidate.ExePath), processName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ExePath;
            }
        }

        return null;
    }

    private static void BuildNotes(RestoreReport report)
    {
        if (report.SlotsCleared > 0)
        {
            report.Notes.Add("A slot's game is not on this PC, so its trigger is now not set and auto start is off.");
        }

        if (report.SlotsRepointed > 0)
        {
            report.Notes.Add("A game moved to a new folder, so the slot was pointed at the new one.");
        }

        if (report.KeysCleared > 0)
        {
            report.Notes.Add("Two slots shared the same key, so the extra one was left empty.");
        }

        if (report.ScreensReset > 0)
        {
            report.Notes.Add("A slot pointed at a screen this PC does not have, so it now covers all screens.");
        }

        if (report.SlotsLostScreen + report.SlotsLostSound > 0)
        {
            report.Notes.Add("A slot used a preset that was not in the file, so that half is empty.");
        }

        if (report.SoundDeviceReset)
        {
            report.Notes.Add("The saved sound output is not on this PC, so it is back to system default.");
        }

        if (report.FxSoundPathReset)
        {
            report.Notes.Add("The saved FxSound folder was not one this app would use, so the one found on this PC is used instead.");
        }
    }
}
