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

    /// <summary>
    /// Whether <see cref="Power"/> was in the file at all.
    /// <para>
    /// Added because <see cref="Power"/> cannot tell "the engine says the EQ is off"
    /// from "the engine did not say", and this app used to conflate them.
    /// </para>
    /// <para>
    /// <c>Power</c> is a plain bool defaulting to false, and status.json is another
    /// program's output that changes shape between versions. A file without a
    /// <c>power</c> key therefore reads as powered-off, which the status line then
    /// reported as fact - "EQ switched off" while the EQ was on, for a machine that
    /// was answering perfectly well.
    /// </para>
    /// <para>
    /// So the two cases are separated at the only place that can tell them apart.
    /// </para>
    /// </summary>
    public bool ReportsPower { get; set; }

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

            return Parse(ReadShared(path), File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex)
        {
            TraceLog.Write("STATE", ex);
            return null;
        }
    }

    /// <summary>
    /// Turns the engine's snapshot into a state object, or nothing if it is not
    /// usable at all.
    /// <para>
    /// Split from the reading so the part that is actually fragile - mapping
    /// somebody else's JSON onto this app's idea of the engine's state - can be
    /// tested directly. Reading the live file would mean writing to the real
    /// engine's status path, which is not a thing a test should be doing.
    /// </para>
    /// </summary>
    internal static FxSoundState? Parse(string json, DateTime fileWrittenUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            FxSoundState state = new() { FileWrittenUtc = fileWrittenUtc };

            state.Version = ReadString(root, "version");

if (root.TryGetProperty("power", out JsonElement power))
        {
            // Both facts recorded: what it said, and that it said it. Only the
            // second one makes the first trustworthy, because a missing key used to
            // be indistinguishable from a false one.
            state.ReportsPower = true;
            state.Power = power.ValueKind == JsonValueKind.True;
        }

            state.SelectedPreset = ReadString(root, "selected_preset");
            state.SelectedOutput = ReadString(root, "selected_output");

            if (root.TryGetProperty("output_devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement device in devices.EnumerateArray())
                {
                    // An entry that is not a string is skipped rather than thrown
                    // on. Every one of these helpers was written individually at
                    // some point, and three of them were left calling GetString
                    // without checking what they had, so one odd entry anywhere in
                    // the file discarded the whole snapshot - including the band
                    // frequencies the panel spends its time drawing.
                    string name = AsString(device);
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
            // Two shapes, because FxSound writes both: a bare name for some and
            // an object carrying a name for others.
            string text = entry.ValueKind == JsonValueKind.String
                ? AsString(entry)
                : ReadString(entry, "name");

            if (!string.IsNullOrWhiteSpace(text))
            {
                target.Add(text);
            }
        }
    }

    /// <summary>
    /// Reads the file with a sharing mode that tolerates another reader.
    /// <para>
    /// This file belongs to another process which rewrites it whenever anything
    /// about the audio changes, so it is genuinely being read and written at the
    /// same time. The default share mode of <c>File.ReadAllText</c> is
    /// <see cref="FileShare.Read"/>, which fails the moment the engine has it open
    /// for writing - and the timing of that is not ours to choose.
    /// </para>
    /// </summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A string property, or empty when it is absent or is not a string.
    /// <para>
    /// The "is not a string" half is the point. <c>GetString()</c> throws on
    /// anything else, and this whole file is read in one try, so a field that
    /// arrived as a number or an object used to throw away the entire snapshot -
    /// every band, every effect and the output list - over one value. The engine
    /// is another program's output and its shape is not this app's to assume.
    /// </para>
    /// </summary>
    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            ? AsString(value)
            : string.Empty;
    }

    /// <summary>The element itself as a string, or empty when it is not one.</summary>
    private static string AsString(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// An integer property, or the fallback when it is absent, is not a number, or
    /// does not fit.
    /// <para>
    /// Two ways to fall over that both read as plausible input: <c>GetInt32</c>
    /// throws on <c>10.0</c> because JSON numbers are not typed the way a command
    /// line argument is, and on anything outside the range of an <see cref="int"/>.
    /// Neither is a reason to discard a snapshot over one field.
    /// </para>
    /// </summary>
    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? ReadIntValue(value, fallback)
            : fallback;
    }

    private static int ReadIntValue(JsonElement value, int fallback)
    {
        return value.TryGetDouble(out double number)
            && number >= int.MinValue
            && number <= int.MaxValue
            ? (int)Math.Round(number)
            : fallback;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback = 0.0)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
                ? number
                : fallback;
    }
}
