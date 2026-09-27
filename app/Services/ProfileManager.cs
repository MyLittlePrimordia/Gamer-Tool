using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class ProfileManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string AppDataFolder
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "GamerTool");
        }
    }

    public string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public ProfileManager()
    {
        Directory.CreateDirectory(AppDataFolder);
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    return Normalize(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            // A settings file that will not read is the one failure in this app
            // that costs the user their work outright, so it goes to the log the
            // diagnostics button can actually surface. Debug.WriteLine is
            // compiled out of a Release build and was invisible where it counted.
            AppLog.Error("SETTINGS LOAD", ex);
            Quarantine();
        }

        return Normalize(new AppSettings());
    }


    /// <summary>
    /// Moves an unreadable settings file aside under a timestamped name, so the
    /// presets in it are still there to be recovered by hand and the next save
    /// cannot quietly destroy them.
    /// </summary>
    private void Quarantine()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(SettingsPath, SettingsPath + ".corrupt." + stamp + ".json", overwrite: true);
        }
        catch (Exception ex)
        {
            // The file stays where it is and the next save overwrites it, which is
            // the outcome this was meant to prevent. Failing here must not stop
            // the app from starting, so this is logged and nothing more.
            AppLog.Error("SETTINGS QUARANTINE", ex);
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);

            // Serialised in full first, so a failure here is a failure to
            // serialise rather than a half written file on disk.
            string json = JsonSerializer.Serialize(settings, Options);

            // Written beside the real file and swapped in, because Commit runs on
            // nearly every interaction and a plain overwrite leaves a window in
            // which a crash, a forced kill or a lost power cuts the file in half.
            // The result is that the next launch cannot read it, and a truncated
            // settings file takes every preset, slot and key with it.
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            AtomicSwap(temp, SettingsPath);
        }
        catch (Exception ex)
        {
            // Same reasoning as the load path, and kept under its own tag so a
            // failed save is distinguishable from a failed read in the log.
            AppLog.Error("SETTINGS SAVE", ex);
        }
    }


    /// <summary>
    /// Puts <paramref name="temp"/> where <paramref name="target"/> belongs, in
    /// one step, leaving the previous file alongside it as a backup.
    /// <para>
    /// Replace is the atomic swap on Windows, but it requires the target to exist
    /// already, which on a first run it does not. So the first write is a move and
    /// every write after that is a replace.
    /// </para>
    /// </summary>
    private static void AtomicSwap(string temp, string target)
    {
        if (File.Exists(target))
        {
            File.Replace(temp, target, target + ".bak", ignoreMetadataErrors: true);
            return;
        }

        File.Move(temp, target);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FxSoundPath))
        {
            settings.FxSoundPath = AudioService.DefaultFxSoundPath;
        }

        settings.UserHotkeys ??= new List<UserHotkey>();
        settings.CustomDisplayPresets ??= new List<DisplayPreset>();
        settings.CustomAudioPresets ??= new List<AudioPreset>();
        settings.CustomCombos ??= new List<ComboPreset>();
        settings.AppProfiles ??= new List<AppProfile>();
        settings.Slots = SlotService.Migrate(settings);

        foreach (AppProfile profile in settings.AppProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = AppProfileTools.NewId("app");
            }
        }

        foreach (AudioPreset preset in settings.CustomAudioPresets)
        {
            int wanted = preset.NumBands <= 0 ? AudioPreset.PresetBandCount : preset.NumBands;
            if (!AudioPreset.BandCounts.Contains(wanted))
            {
                wanted = AudioPreset.PresetBandCount;
            }

            preset.NumBands = wanted;
            if (preset.Bands is null || preset.Bands.Length != wanted)
            {
                double[] fixedBands = new double[wanted];
                if (preset.Bands is not null)
                {
                    for (int i = 0; i < preset.Bands.Length && i < fixedBands.Length; i++)
                    {
                        fixedBands[i] = preset.Bands[i];
                    }
                }

                preset.Bands = fixedBands;
            }
        }

        return settings;
    }
}
