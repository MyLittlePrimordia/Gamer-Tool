using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class BackupFile
{
    public string Format { get; set; } = BackupService.FormatTag;

    public int Version { get; set; } = BackupService.FormatVersion;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string CreatedOn { get; set; } = string.Empty;

    public AppSettings Settings { get; set; } = new();
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
            CreatedOn = Environment.MachineName,
            Settings = settings
        };

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
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

            string json = File.ReadAllText(path);
            AppSettings? settings = Read(json);
            if (settings is null)
            {
                error = "NOT A GAMER TOOL BACKUP";
                return null;
            }

            return ProfileManager.Normalize(settings);
        }
        catch (Exception ex)
        {
            TraceLog.Write("BACKUP", ex);
            error = "FILE COULD NOT BE READ";
            return null;
        }
    }

    private static AppSettings? Read(string json)
    {
        try
        {
            BackupFile? file = JsonSerializer.Deserialize<BackupFile>(json, Options);
            if (file is not null && file.Settings is not null && file.Settings.Slots is not null)
            {
                return file.Settings;
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (JsonException)
        {
            return null;
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

        if (!string.IsNullOrWhiteSpace(workingFxSoundPath)
            && settings.FxSoundPath.Length > 0
            && !File.Exists(settings.FxSoundPath)
            && File.Exists(workingFxSoundPath))
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
            report.Notes.Add("The saved FxSound folder was not here, so the one found on this PC is used.");
        }
    }
}
