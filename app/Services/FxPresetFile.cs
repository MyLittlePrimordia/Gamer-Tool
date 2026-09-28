using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Writes a FxSound preset file so the equaliser curve can be pushed across.
///
/// Per band gains and per band centre frequencies are not part of FxSound's
/// documented command line. They live only inside a preset file, and the
/// documented way to load a preset is --preset with its name. So applying an EQ
/// means writing the file and asking FxSound to select it, which is the only
/// route that actually carries a curve.
///
/// The file is line oriented text. Everything above the equaliser block is the
/// effect and master state, and that block is copied from a shipped template
/// untouched; only the trailing band section is regenerated.
/// </summary>
public static class FxPresetFile
{
    /// <summary>The preset name the app writes under and then selects.</summary>
    public const string PresetName = "GamerTool";

    /// <summary>Where FxSound keeps its presets.</summary>

    public static string PresetsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FxSound",
        "Presets");


    /// <summary>
    /// Writes the preset and returns the full path, or null when it could not be
    /// written. A failure here is not fatal: the scalar settings can still be
    /// pushed, only the curve is lost, so the caller carries on.
    /// </summary>
    public static string? Write(AudioPreset preset, IReadOnlyList<double> frequencies, bool effectsEnabled)
    {
        try
        {
            List<string> lines = ReadTemplate();
            if (lines.Count == 0)
            {
                return null;
            }

            int count = frequencies.Count > 0 ? frequencies.Count : AudioPreset.PresetBandCount;
            var gains = new double[count];
            for (int i = 0; i < count; i++)
            {
                gains[i] = Math.Clamp(preset.Band(i), AudioPreset.GainMin, AudioPreset.GainMax);
            }

            var freqs = new double[count];
            for (int i = 0; i < count; i++)
            {
                freqs[i] = frequencies.Count > i
                    ? frequencies[i]
                    : AudioPreset.BandFrequency(count, i);
            }

            RewriteEq(lines, freqs, gains, effectsEnabled);
            SetName(lines, PresetName);

            Directory.CreateDirectory(PresetsFolder);
            string path = Path.Combine(PresetsFolder, PresetName + ".fac");
            File.WriteAllLines(path, lines);
            return path;
        }
        catch (Exception ex)
        {
            TraceLog.Write("FAC WRITE", ex);
            return null;
        }
    }


    private static List<string> ReadTemplate()
    {
        return DisplayPreview.ReadAsset("fac-template.fac") is { } bytes && bytes.Length > 0
            ? System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')).ToList()
            : new List<string>();
    }


    private static void SetName(List<string> lines, string name)
    {
        // Line 0 is the class marker and line 1 the version, so the name is the
        // third line. It has to match what --preset is given, case sensitively.
        if (lines.Count > 2)
        {
            lines[2] = name;
        }
    }


    private static void RewriteEq(List<string> lines, IReadOnlyList<double> frequencies, IReadOnlyList<double> gains, bool effectsEnabled)
    {
        int start = lines.FindIndex(l => l.Contains("Number of EQ Bands", StringComparison.Ordinal));
        if (start < 0)
        {
            throw new InvalidDataException("template has no EQ block");
        }

        var rebuilt = new List<string>(lines.GetRange(0, start));
        rebuilt.Add(frequencies.Count + ": Number of EQ Bands");
        // The engine ignores this field when it loads a preset, so it does not
        // decide what the user hears. It is written to match the effect being
        // asked for anyway, so the file is not describing the opposite of what the
        // app just did to the engine, and so the two cannot drift apart unnoticed.
        //
        // 1 is on and 0 is off, which is the opposite of what the line reads
        // like. Every preset FxSound itself writes carries a 1, the shipped
        // template carries a 1, and the version of this app that predates the
        // bypass wrote a 1 unconditionally and had a working equaliser. So the
        // bypass writes a 0.
        //
        // The curve itself does not come from here. The band gains below travel
        // separately, in a --set_band_gain command of their own after the preset
        // has been selected, because the engine applies a selected preset's gains
        // after it has finished parsing the command line, so gains sent in the
        // same invocation are discarded.
        rebuilt.Add((effectsEnabled ? "1" : "0") + ": On/Off Flag");

        for (int i = 0; i < frequencies.Count; i++)
        {
            rebuilt.Add("Band " + (i + 1).ToString(CultureInfo.InvariantCulture));
            rebuilt.Add("   " + Fmt(frequencies[i]) + ": CF");
            rebuilt.Add("   " + Fmt(gains[i]) + ": Boost/Cut");
        }

        lines.Clear();
        lines.AddRange(rebuilt);
    }


    /// <summary>
    /// FxSound writes plain numbers, so a whole number must not pick up a decimal
    /// point, and the invariant culture keeps a comma decimal separator from
    /// turning 0.5 into "0,5", which this format cannot represent.
    /// </summary>
    private static string Fmt(double value)
    {
        double rounded = Math.Round(value, 2);
        return rounded == Math.Floor(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
