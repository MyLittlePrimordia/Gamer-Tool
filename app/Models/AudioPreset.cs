using System;
using System.Collections.Generic;

namespace GamerTool.Models;

public sealed class AudioPreset
{
    public const int PresetBandCount = 10;

    public const double GainMin = -12.0;

    public const double GainMax = 12.0;

    public const double EffectMin = 0.0;

    public const double EffectMax = 10.0;

    public const double MasterGainMin = -20.0;

    public const double MasterGainMax = 20.0;

    public const double LevelingMin = 0.0;

    public const double LevelingMax = 4.0;

    public const double FilterQMin = 1.0;

    public const double FilterQMax = 3.0;

    public static readonly int[] BandCounts = { 5, 10, 15, 20, 31 };

    public static readonly double[] DefaultBandFrequencies = { 31.25, 62.5, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 };

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public double[] Bands { get; set; } = new double[PresetBandCount];

    public int NumBands { get; set; } = PresetBandCount;

    public double Clarity { get; set; }

    public double Ambience { get; set; }

    public double Surround { get; set; }

    public double DynamicBoost { get; set; }

    public double BassBoost { get; set; }

    public double MasterGain { get; set; }

    public double VolumeLeveling { get; set; }

    public double FilterQ { get; set; } = 1.0;

    public double Balance { get; set; }

    public string ClarityText => Clarity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string AmbienceText => Ambience.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string SurroundText => Surround.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string DynamicBoostText => DynamicBoost.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string BassBoostText => BassBoost.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string MasterGainText => (MasterGain >= 0 ? "+" : string.Empty) + MasterGain.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public string SpecText => "CLARITY " + ClarityText + "   BASS " + BassBoostText;

    public string CompactSpec => "CLARITY " + ClarityText + "  ·  BASS " + BassBoostText;

    public double Band(int index)
    {
        if (index < 0 || index >= Bands.Length)
        {
            return 0.0;
        }

        return Bands[index];
    }

    public double[] GainList(int count)
    {
        double[] list = new double[count];
        for (int i = 0; i < count; i++)
        {
            list[i] = Math.Clamp(Band(i), GainMin, GainMax);
        }

        return list;
    }

    public AudioPreset Copy()
    {
        double[] copy = new double[Math.Max(Bands.Length, PresetBandCount)];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = i < Bands.Length ? Bands[i] : 0.0;
        }

        return new AudioPreset
        {
            Id = Id,
            Name = Name,
            Tag = Tag,
            Bands = copy,
            NumBands = NumBands,
            Clarity = Clarity,
            Ambience = Ambience,
            Surround = Surround,
            DynamicBoost = DynamicBoost,
            BassBoost = BassBoost,
            MasterGain = MasterGain,
            VolumeLeveling = VolumeLeveling,
            FilterQ = FilterQ,
            Balance = Balance
        };
    }

    public static AudioPreset Flat()
    {
        return new AudioPreset
        {
            Id = "flat",
            Name = "FLAT SOUND",
            Tag = "NO CHANGE",
            Bands = new double[PresetBandCount],
            NumBands = PresetBandCount,
            Clarity = 0.0,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 0.0,
            BassBoost = 0.0,
            MasterGain = 0.0,
            VolumeLeveling = 0.0,
            FilterQ = 1.0,
            Balance = 0.0
        };
    }

    public static string FormatFrequency(double hz)
    {
        if (hz >= 1000.0)
        {
            double k = hz / 1000.0;
            string text = k.ToString(k >= 10.0 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
            return text + "K";
        }

        return hz.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // 10-band curves (31Hz..16kHz) follow common competitive practice: trim the
    // sub-bass that masks cues, lift 1-4kHz where footstep transients live, and
    // leave 16kHz flat. +/-12 dB per band is the practical distortion-free limit.
    public static IReadOnlyList<AudioPreset> Defaults { get; } = new List<AudioPreset>
    {
        new AudioPreset
        {
            Id = "footstep",
            Name = "COMPETITIVE FPS",
            Tag = "FOOTSTEPS",
            Bands = new[] { -6.0, -4.0, -2.0, 0.0, 0.0, 2.0, 4.5, 5.0, 2.5, 0.0 },
            NumBands = 10,
            Clarity = 6.0,
            Ambience = 1.0,
            Surround = 2.0,
            DynamicBoost = 4.0,
            BassBoost = 2.0,
            MasterGain = 4.0,
            VolumeLeveling = 2.0,
            FilterQ = 1.5,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "explosion",
            Name = "EAR COMFORT",
            Tag = "SAFE HOURS",
            Bands = new[] { -8.0, -6.0, -4.0, -1.0, 0.0, 1.0, 2.0, 3.0, 1.0, -1.0 },
            NumBands = 10,
            Clarity = 4.0,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 6.0,
            BassBoost = 1.0,
            MasterGain = -2.0,
            VolumeLeveling = 1.0,
            FilterQ = 1.5,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "royale",
            Name = "BATTLE ROYALE",
            Tag = "WIDE STAGE",
            Bands = new[] { -8.0, -5.0, -3.0, 0.0, 0.0, 2.0, 4.0, 5.0, 3.0, 0.0 },
            NumBands = 10,
            Clarity = 7.0,
            Ambience = 5.0,
            Surround = 6.0,
            DynamicBoost = 3.0,
            BassBoost = 3.0,
            MasterGain = 3.0,
            VolumeLeveling = 2.0,
            FilterQ = 1.5,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "cinematic",
            Name = "MOVIE / CINEMA",
            Tag = "V SHAPE",
            Bands = new[] { 5.0, 4.0, 0.0, -2.0, -1.0, 0.0, 2.0, 3.0, 2.0, 1.0 },
            NumBands = 10,
            Clarity = 5.0,
            Ambience = 6.0,
            Surround = 5.0,
            DynamicBoost = 2.0,
            BassBoost = 6.0,
            MasterGain = 2.0,
            VolumeLeveling = 1.5,
            FilterQ = 1.0,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "racing",
            Name = "RACING / ENGINE",
            Tag = "LOW RUMBLE",
            Bands = new[] { 4.0, 3.0, 0.0, -2.0, -1.0, 0.0, 2.0, 3.0, 2.0, 0.0 },
            NumBands = 10,
            Clarity = 3.0,
            Ambience = 2.0,
            Surround = 3.0,
            DynamicBoost = 3.0,
            BassBoost = 7.0,
            MasterGain = 2.0,
            VolumeLeveling = 1.5,
            FilterQ = 1.0,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "arcade",
            Name = "ARCADE / FIGHTERS",
            Tag = "BIG HITS",
            Bands = new[] { 5.0, 4.0, 0.0, -1.0, -2.0, 0.0, 3.0, 4.0, 3.0, 0.0 },
            NumBands = 10,
            Clarity = 4.0,
            Ambience = 2.0,
            Surround = 3.0,
            DynamicBoost = 4.0,
            BassBoost = 5.0,
            MasterGain = 1.0,
            VolumeLeveling = 1.0,
            FilterQ = 1.0,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "moba",
            Name = "PODCAST / VOICE",
            Tag = "CLEAR WORDS",
            Bands = new[] { -6.0, -4.0, -3.0, 2.0, 3.0, 4.0, 3.0, 1.0, 0.0, 0.0 },
            NumBands = 10,
            Clarity = 6.0,
            Ambience = 0.0,
            Surround = 1.0,
            DynamicBoost = 5.0,
            BassBoost = 2.0,
            MasterGain = 3.0,
            VolumeLeveling = 2.5,
            FilterQ = 1.5,
            Balance = 0.0
        },
        new AudioPreset
        {
            Id = "lofi",
            Name = "MUSIC / WARM",
            Tag = "EASY LISTEN",
            Bands = new[] { 4.0, 3.0, 0.0, -1.0, 0.0, 0.0, 1.0, 2.0, 0.0, 0.0 },
            NumBands = 10,
            Clarity = 1.0,
            Ambience = 5.0,
            Surround = 2.0,
            DynamicBoost = 1.0,
            BassBoost = 4.0,
            MasterGain = 0.0,
            VolumeLeveling = 1.0,
            FilterQ = 1.0,
            Balance = 0.0
        }
    };
}
