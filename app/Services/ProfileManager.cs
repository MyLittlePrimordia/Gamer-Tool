using System;
using System.Collections.Generic;
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
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        return Normalize(new AppSettings());
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
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
