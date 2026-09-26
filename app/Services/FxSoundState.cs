using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class FxBandState
{
    public int Index { get; set; }

    public double Frequency { get; set; }

    public double Gain { get; set; }

    public string FrequencyText => AudioPreset.FormatFrequency(Frequency);
}

public sealed class FxEffectsState
{
    public double Clarity { get; set; }

    public double Ambience { get; set; }

    public double Surround { get; set; }

    public double DynamicBoost { get; set; }

    public double Bass { get; set; }
}

public sealed class FxEqualizerState
{
    public int NumBands { get; set; } = 10;

    public double MasterGain { get; set; }

    public double VolumeLeveling { get; set; }

    public double FilterQ { get; set; } = 1.0;

    public double Balance { get; set; }

    public List<FxBandState> Bands { get; } = new();
}

public sealed class FxSoundState
{
    public string Version { get; set; } = string.Empty;

    public bool Power { get; set; }

    public string SelectedPreset { get; set; } = string.Empty;

    public string SelectedOutput { get; set; } = string.Empty;

    public List<string> OutputDevices { get; } = new();

    public List<string> UserPresets { get; } = new();

    public List<string> BuiltInPresets { get; } = new();

    public FxEqualizerState Equalizer { get; set; } = new();

    public FxEffectsState Effects { get; set; } = new();

    public DateTime ReadAt { get; set; } = DateTime.Now;

    public DateTime FileWrittenUtc { get; set; } = DateTime.MinValue;

    public bool IsFresh => FileWrittenUtc > DateTime.MinValue
        && (DateTime.UtcNow - FileWrittenUtc).TotalSeconds < 20.0;

    public static string StatusPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "FxSound", "status.json");
        }
    }

    public static FxSoundState? TryRead()
    {
        try
        {
            string path = StatusPath;
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            FxSoundState state = new() { FileWrittenUtc = File.GetLastWriteTimeUtc(path) };

            if (root.TryGetProperty("version", out JsonElement version))
            {
                state.Version = version.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("power", out JsonElement power) && power.ValueKind == JsonValueKind.True)
            {
                state.Power = true;
            }

            if (root.TryGetProperty("selected_preset", out JsonElement selected))
            {
                state.SelectedPreset = selected.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("selected_output", out JsonElement output))
            {
                state.SelectedOutput = output.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("output_devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement device in devices.EnumerateArray())
                {
                    string? name = device.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        state.OutputDevices.Add(name);
                    }
                }
            }

            if (root.TryGetProperty("presets", out JsonElement presets))
            {
                ReadPresetList(presets, "user_defined", state.UserPresets);
                ReadPresetList(presets, "built_in", state.BuiltInPresets);
            }

            if (root.TryGetProperty("equalizer", out JsonElement equalizer))
            {
                state.Equalizer.NumBands = ReadInt(equalizer, "num_bands", 10);
                state.Equalizer.MasterGain = ReadDouble(equalizer, "master_gain");
                state.Equalizer.VolumeLeveling = ReadDouble(equalizer, "volume_leveling");
                state.Equalizer.FilterQ = ReadDouble(equalizer, "filter_q", 1.0);
                state.Equalizer.Balance = ReadDouble(equalizer, "balance");

                if (equalizer.TryGetProperty("bands", out JsonElement bands) && bands.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement band in bands.EnumerateArray())
                    {
                        state.Equalizer.Bands.Add(new FxBandState
                        {
                            Index = ReadInt(band, "index", state.Equalizer.Bands.Count),
                            Frequency = ReadDouble(band, "frequency"),
                            Gain = ReadDouble(band, "gain")
                        });
                    }
                }
            }

            if (root.TryGetProperty("effects", out JsonElement effects))
            {
                state.Effects.Clarity = ReadDouble(effects, "clarity");
                state.Effects.Ambience = ReadDouble(effects, "ambience");
                state.Effects.Surround = ReadDouble(effects, "surround");
                state.Effects.DynamicBoost = ReadDouble(effects, "dynamicboost");
                state.Effects.Bass = ReadDouble(effects, "bass");
            }

            return state;
        }
        catch (Exception ex)
        {
            TraceLog.Write("STATE", ex);
            return null;
        }
    }

    private static void ReadPresetList(JsonElement presets, string name, List<string> target)
    {
        if (!presets.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                string? text = entry.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    target.Add(text);
                }

                continue;
            }

            if (entry.TryGetProperty("name", out JsonElement entryName))
            {
                string? text = entryName.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    target.Add(text);
                }
            }
        }
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }

        return fallback;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback = 0.0)
    {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return fallback;
    }

    public string Describe()
    {
        return "V" + Version + " POWER " + (Power ? "ON" : "OFF") + " PRESET " + SelectedPreset;
    }

    public string Frequencies()
    {
        List<string> parts = new();
        foreach (FxBandState band in Equalizer.Bands)
        {
            parts.Add(band.FrequencyText);
        }

        return string.Join(" ", parts);
    }
}
