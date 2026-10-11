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

    /// <summary>
    /// The centre frequencies FxSound really uses for each band count, read back
    /// from the engine's own status output rather than guessed.
    ///
    /// 31 is not an arbitrary ceiling: it is the complete third-octave filter
    /// bank, 20Hz to 20kHz, which is the finest layout the engine offers. The
    /// coarser counts are subsets of that same bank, so switching band count
    /// redistributes the same frequency grid rather than changing what the EQ
    /// can reach. The irregular spacing at 10 and 20 is the engine's own
    /// rounding, reproduced here so the fader labels line up with what is
    /// actually applied.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, double[]> BandFrequencies = new Dictionary<int, double[]>
    {
        [5] = new[] { 62.0, 250.0, 1000.0, 4000.0, 16000.0 },
        [10] = new[] { 62.0, 116.0, 214.0, 397.0, 735.0, 1361.0, 2520.0, 4666.0, 8640.0, 16000.0 },
        [15] = new[] { 25.0, 40.0, 63.0, 100.0, 160.0, 250.0, 400.0, 630.0, 1000.0, 1600.0, 2500.0, 4000.0, 6300.0, 10000.0, 16000.0 },
        [20] = new[] { 20.0, 32.0, 40.0, 63.0, 80.0, 125.0, 160.0, 250.0, 315.0, 500.0, 630.0, 1000.0, 1250.0, 2000.0, 2500.0, 4000.0, 5000.0, 8000.0, 10000.0, 16000.0 },
        [31] = new[]
        {
            20.0, 25.0, 32.0, 40.0, 50.0, 63.0, 80.0, 100.0, 125.0, 160.0, 200.0, 250.0, 315.0, 400.0, 500.0,
            630.0, 800.0, 1000.0, 1250.0, 1600.0, 2000.0, 2500.0, 3150.0, 4000.0, 5000.0, 6300.0, 8000.0,
            10000.0, 12500.0, 16000.0, 20000.0
        }
    };

    public static readonly double[] DefaultBandFrequencies = { 31.25, 62.5, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 };


    /// <summary>
    /// The window one band's centre frequency is allowed to move inside. The
    /// engine gives every band its own window and the windows sit next to each
    /// other without overlapping, which is what stops two bands crossing over and
    /// fighting for the same part of the spectrum. Clamping to the window is
    /// therefore all the guard rail a frequency control needs.
    /// </summary>
    public readonly record struct BandWindow(double Min, double Max);

    /// <summary>
    /// Per band frequency windows, read off the engine's own dial ranges. Only the
    /// counts whose dials the engine exposes are listed; a count that is missing
    /// is treated as having a single legal frequency, so a control for it is
    /// never built.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, BandWindow[]> BandWindows = new Dictionary<int, BandWindow[]>
    {
        [5] = new[]
        {
            new BandWindow(62.0, 125.0),
            new BandWindow(125.0, 500.0),
            new BandWindow(501.0, 2000.0),
            new BandWindow(2001.0, 8000.0),
            new BandWindow(8001.0, 16000.0)
        },
        [10] = new[]
        {
            new BandWindow(62.0, 85.0),
            new BandWindow(86.0, 157.0),
            new BandWindow(158.0, 292.0),
            new BandWindow(293.0, 540.0),
            new BandWindow(541.0, 1000.0),
            new BandWindow(1010.0, 1850.0),
            new BandWindow(1860.0, 3430.0),
            new BandWindow(3440.0, 6350.0),
            new BandWindow(6360.0, 11760.0),
            new BandWindow(11770.0, 16000.0)
        }
    };

    /// <summary>How many detents a frequency dial has. Finer than the engine needs, so it never fights a real step.</summary>
    public const int FrequencySteps = 512;

    /// <summary>
    /// How many usable detents one band's window can actually be divided into
    /// without two detents landing on the same whole hertz value. The engine only
    /// stores whole hertz, so a narrow window has to get fewer detents or the dial
    /// goes dead in places: 62 to 85 Hz is 23 hertz wide and cannot carry 512
    /// distinct stops. The log step is solved for directly, which keeps the dial
    /// feeling the same everywhere while guaranteeing every detent moves the
    /// frequency by at least one hertz.
    /// </summary>
    public static int Steps(BandWindow window)
    {
        if (window.Max <= window.Min)
        {
            return 1;
        }

        double lo = Math.Round(window.Min);
        double ratio = window.Max / window.Min;
        if (ratio <= 1.0)
        {
            return 1;
        }

        // The smallest useful log step is the one that advances a whole hertz at
        // the bottom of the window, where the hertz are largest in relative terms.
        double perStep = Math.Log(1.0 + (1.0 / lo));
        double steps = Math.Log(ratio) / perStep;
        return (int)Math.Clamp(Math.Floor(steps), 1, FrequencySteps);
    }

    /// <summary>The window for one band, or a fixed point when the count has no dials.</summary>
    public static BandWindow Window(int count, int index)
    {
        if (BandWindows.TryGetValue(count, out BandWindow[]? windows) && index >= 0 && index < windows.Length)
        {
            return windows[index];
        }

        double fixedHz = BandFrequency(count, index);
        return new BandWindow(fixedHz, fixedHz);
    }

    /// <summary>True when the engine lets this band's centre frequency move at all.</summary>
    public static bool HasFrequencyDial(int count)
    {
        if (!BandWindows.TryGetValue(count, out BandWindow[]? windows))
        {
            return false;
        }

        foreach (BandWindow window in windows)
        {
            if (window.Max > window.Min)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps a dial position onto a frequency. The move is logarithmic because the
    /// windows are wildly different sizes: the lowest is 23 Hz wide and the highest
    /// is 4230 Hz wide, so a linear dial would give the bottom band no usable
    /// travel at all. In log space every band covers a comparable fraction of a
    /// turn, which is what makes the dials feel the same.
    /// </summary>
    public static double FrequencyFromStep(BandWindow window, double step)
    {
        int steps = Steps(window);
        double t = Math.Clamp(step, 0.0, steps) / steps;
        double hz = window.Min * Math.Pow(window.Max / window.Min, t);

        // The engine stores whole hertz, so settle on whole hertz here too.
        double rounded = Math.Round(hz);
        return Math.Clamp(rounded, Math.Round(window.Min), Math.Round(window.Max));
    }

    /// <summary>Where a frequency sits on the dial, so a stored tuning opens in the right place.</summary>
    public static double StepFromFrequency(BandWindow window, double hz)
    {
        int steps = Steps(window);
        if (window.Max <= window.Min)
        {
            return 0.0;
        }

        double clamped = Math.Clamp(hz, window.Min, window.Max);
        double t = Math.Log(clamped / window.Min) / Math.Log(window.Max / window.Min);
        return t * steps;
    }

    /// <summary>Centre frequency of one fader at the given band count.</summary>
    public static double BandFrequency(int count, int index)
    {
        if (BandFrequencies.TryGetValue(count, out double[]? table) && index >= 0 && index < table.Length)
        {
            return table[index];
        }

        return DefaultBandFrequencies[Math.Clamp(index, 0, DefaultBandFrequencies.Length - 1)];
    }


    /// <summary>
    /// The centre frequency for one fader, preferring the preset's own tuning when
    /// it has one. A preset with no stored frequencies falls back to the engine's
    /// measured layout for the band count in play.
    /// </summary>
    public static double BandFrequency(int count, IReadOnlyList<double>? frequencies, int index)
    {
        if (frequencies is not null && index >= 0 && index < frequencies.Count && frequencies[index] > 0.0)
        {
            return frequencies[index];
        }

        return BandFrequency(count, index);
    }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public double[] Bands { get; set; } = new double[PresetBandCount];

    /// <summary>
    /// Per band centre frequencies, when this preset has been retuned away from
    /// the engine's own band layout. Empty means "use the standard layout for this
    /// band count", which is what every built in preset uses.
    /// </summary>
    public double[] Frequencies { get; set; } = Array.Empty<double>();

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

    public double Band(int index)
    {
        if (index < 0 || index >= Bands.Length)
        {
            return 0.0;
        }

        return Bands[index];
    }

    /// <summary>
    /// Pulls every value back inside the range the engine and the sliders accept,
    /// and drops a stored centre frequency that is not legal for its band.
    /// <para>
    /// Same reasoning as <see cref="DisplayPreset.Clamp"/>. The frequency case
    /// matters more than it looks: a frequency outside its band's window is
    /// ignored by the engine without a word, so a bad value in a restored file
    /// produces a fader that looks set and does nothing, and the only clue is
    /// that the dial and the label disagree.
    /// </para>
    /// </summary>
    public AudioPreset Clamp()
    {
        int count = NumBands <= 0 ? PresetBandCount : NumBands;

        for (int i = 0; i < Bands.Length; i++)
        {
            Bands[i] = double.IsFinite(Bands[i]) ? Math.Clamp(Bands[i], GainMin, GainMax) : 0.0;
        }

        Clarity = ClampEffect(Clarity);
        Ambience = ClampEffect(Ambience);
        Surround = ClampEffect(Surround);
        DynamicBoost = ClampEffect(DynamicBoost);
        BassBoost = ClampEffect(BassBoost);
        MasterGain = ClampRange(MasterGain, MasterGainMin, MasterGainMax, 0.0);
        VolumeLeveling = ClampRange(VolumeLeveling, LevelingMin, LevelingMax, 0.0);
        FilterQ = ClampRange(FilterQ, FilterQMin, FilterQMax, 1.0);
        Balance = ClampRange(Balance, -20.0, 20.0, 0.0);

        if (!HasFrequencyDial(count))
        {
            // No dials at this count, so a band has exactly one legal frequency
            // and anything stored is either that or nothing.
            if (Frequencies.Length > 0)
            {
                Frequencies = Array.Empty<double>();
            }

            return this;
        }

        for (int i = 0; i < Frequencies.Length; i++)
        {
            BandWindow window = Window(count, i);
            Frequencies[i] = double.IsFinite(Frequencies[i])
                ? Math.Clamp(Frequencies[i], window.Min, window.Max)
                : BandFrequency(count, i);
        }

        return this;
    }

    private static double ClampEffect(double value) => ClampRange(value, EffectMin, EffectMax, 0.0);

    private static double ClampRange(double value, double min, double max, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
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
            Frequencies = (double[])Frequencies.Clone(),
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

    /// <summary>
    /// The no-op tune: every band at 0 dB and every effect off. This is a real
    /// built-in rather than a hidden value, so it sits at the top of the dropdown
    /// where every other app puts its flat setting, and the reset button simply
    /// selects it. The id is stable, so saved slots pointing at "flat" keep
    /// resolving.
    ///
    /// The band count is a parameter because a reset should not also quietly
    /// change the filter layout: someone who set 31 bands and pressed reset
    /// expects the gains zeroed, not the layout dropped back to ten.
    /// </summary>
    public static AudioPreset Flat(int bandCount = PresetBandCount)
    {
        // Bounded against BandCounts as well as against zero. The count arrives
        // from two places that are not the app: another program's status.json and
        // a hand-edited settings file, and neither bounds it. A count of two
        // billion sized a double array of sixteen gigabytes from here. The engine
        // has a fixed set of layouts, so anything outside them is not a layout at
        // all and the default is the honest answer rather than a clamp.
        int bands = bandCount <= 0 || !BandCounts.Contains(bandCount)
            ? PresetBandCount
            : bandCount;
        return new AudioPreset
        {
            Id = "flat",
            Name = "Flat",
            Tag = "No change",
            Bands = new double[bands],
            Frequencies = Array.Empty<double>(),
            NumBands = bands,
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

    /// <summary>
    /// A band centre for a label under a fader.
    /// <para>
    /// Whole hertz below a kilohertz, then one decimal place of kilohertz, then
    /// whole kilohertz. The old rule switched to two decimals below 10 kHz, which
    /// put five bare integers in a row and then four values like "1.36K" and
    /// "4.67K" beside them, so one row of ten labels read as two different kinds
    /// of number. It also implied a precision the band grid does not have: nobody
    /// tunes a 4666 Hz band to two decimal places of kilohertz.
    /// </para>
    /// </summary>
    public static string FormatFrequency(double hz)
    {
        if (hz >= 1000.0)
        {
            double k = hz / 1000.0;
            return k.ToString(k >= 10.0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "K";
        }

        return hz.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Ten band curves at 31Hz..16kHz. The recurring trick in competitive shooters
    // is to trim the sub bass that masks positional cues, lift the 1-4kHz range
    // where footstep transients and gunfire actually live, and leave the top end
    // alone because boosting 16kHz only adds hiss. Music and film use a different
    // shape: a low shelf for weight with the mid dip kept shallow. Every preset
    // has a different curve, so moving between them is an audible change rather
    // than a nudge.
    //
    // +/-12 dB per band is the practical distortion free limit; past that the
    // boost starts folding harmonics on loud passages. Ids are kept stable so
    // existing saved presets and slots keep pointing at the same entry.
    public static IReadOnlyList<AudioPreset> Defaults { get; } = new List<AudioPreset>
    {
        // First on purpose: Flat is the untouched reference and it is what the
        // reset button lands on.
        Flat(),

        // Movement cues. Explosions live under about 150Hz, so that is cut hard,
        // and the presence bands come up so footsteps, reloads and a defuse read
        // first. Master gain is up a little because the curve only removes energy
        // from the bottom rather than adding any - and the headroom that boost
        // needs is taken off automatically, so the figure here is an offset and
        // not the level that reaches the engine.
        //
        // No ambience and no surround, and this is the reason the preset exists in
        // this shape rather than the one it shipped in. Games in this bracket -
        // Counter-Strike, Valorant, Apex, Warzone - already output binaural audio
        // with real interaural timing and head-related filtering, and adding a
        // second spatialiser on top of it does not widen anything: the two stage
        // models disagree about where a sound is, the summed signal has comb
        // filtering across the whole midrange, and a transient as short as a
        // footstep smears and detaches from its own direction. The EQ is enough.
        new AudioPreset
        {
            Id = "footstep",
            Name = "Footsteps",
            Tag = "Movement",
            Bands = new[] { -7.0, -4.0, -1.0, 1.0, 2.0, 4.0, 6.0, 3.0, 0.0, -3.0 },
            NumBands = 10,
            // Capped from 6.0. Clarity is a harmonic exciter, so it manufactures
            // the upper midrange content that gunshot cracks and whistled voice
            // already have most of - and at this level it is ear fatigue bought
            // with detail nobody asked for.
            Clarity = 3.0,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 3.0,
            BassBoost = 0.0,
            MasterGain = 3.0,
            VolumeLeveling = 3.0,
            FilterQ = 1.5,
            Balance = 0.0
        },

        // Battle royale. A gentler low cut than Footsteps, because the loud thing
        // in these games is an air strike rather than a footstep, and the stage is
        // opened up so a direction still reads across a wider field of view.
        //
        // The same reasoning as Footsteps on the ambience and the surround, and
        // for a stronger reason here: the whole job is hearing a distant contact
        // and knowing where it was. A second spatialiser does not add distance
        // information to a signal that already carries it, it smears the arrival
        // that the distance was being read from.
        new AudioPreset
        {
            Id = "royale",
            Name = "Royale",
            Tag = "Battle royale",
            Bands = new[] { -5.0, -2.0, 0.0, 1.0, 2.0, 3.0, 5.0, 3.0, 1.0, -1.0 },
            NumBands = 10,
            Clarity = 3.0,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 3.0,
            BassBoost = 1.0,
            MasterGain = 2.0,
            VolumeLeveling = 3.5,
            FilterQ = 1.5,
            Balance = 0.0
        },

        // Multiplayer combat. Weapons carry their weight in the low mids, and the
        // top end is opened so a hitmarker or a ping cuts through the fight. The
        // filter is the tightest here, which keeps impacts from smearing.
        //
        // Ambience and surround stay on here, unlike the two competitive presets
        // above, because this one is aimed at games that mix for a stage rather
        // than for headphones: Battlefield, Helldivers. There is no binaural
        // master to fight, so the spatial cues are adding information rather than
        // duplicating it.
        new AudioPreset
        {
            Id = "explosion",
            Name = "Shooter",
            Tag = "Combat",
            Bands = new[] { 3.0, 4.0, 1.0, -1.0, 0.0, 2.0, 3.5, 4.0, 2.0, 0.0 },
            NumBands = 10,
            // 5.0 to 4.0, which was not on the list to change and had to be. This
            // preset had the highest clarity of the three combat curves and sat
            // above the cap the others were pulled under; a gunshot crack is the
            // loudest thing in the game it is aimed at.
            Clarity = 4.0,
            Ambience = 1.0,
            Surround = 2.0,
            DynamicBoost = 2.5,
            BassBoost = 2.0,
            MasterGain = 1.0,
            VolumeLeveling = 2.0,
            FilterQ = 1.2,
            Balance = 0.0
        },

        // Sim racing. Engine and exhaust need weight in the low mids and the top
        // end is opened for tyre grip, which is the opposite shape to Footsteps.
        // A wider filter keeps the mechanical detail rather than ringing it.
        new AudioPreset
        {
            Id = "racing",
            Name = "Racing",
            Tag = "Engines",
            Bands = new[] { 5.0, 6.0, 3.0, 0.0, -1.0, 1.0, 3.0, 4.0, 3.0, 1.0 },
            NumBands = 10,
            Clarity = 4.0,
            Ambience = 1.5,
            Surround = 3.5,
            DynamicBoost = 3.0,
            BassBoost = 4.0,
            MasterGain = 0.0,
            VolumeLeveling = 1.5,
            FilterQ = 1.4,
            Balance = 0.0
        },

        // Single player RPG. Sub bass for weight and a wide stage, with the
        // ambience doing the work rather than the equaliser. Master gain is back
        // a little to leave headroom under all that low end.
        new AudioPreset
        {
            Id = "arcade",
            Name = "Immersive",
            Tag = "Story",
            Bands = new[] { 4.0, 3.0, 1.0, 0.0, 1.0, 2.0, 3.0, 4.0, 4.5, 3.0 },
            NumBands = 10,
            Clarity = 3.0,
            Ambience = 5.0,
            Surround = 6.0,
            DynamicBoost = 4.0,
            BassBoost = 5.0,
            MasterGain = -1.0,
            VolumeLeveling = 0.0,
            FilterQ = 1.0,
            Balance = 0.0
        },

        // Comms. The deepest low cut of any preset here, with the speech range
        // lifted, so a teammate is audible without the user touching the volume.
        // This is the flattest curve in the list on purpose.
        new AudioPreset
        {
            Id = "moba",
            Name = "Voice Chat",
            Tag = "Comms",
            Bands = new[] { -9.0, -6.0, -1.0, 2.0, 4.0, 5.0, 3.0, 0.0, -4.0, -8.0 },
            NumBands = 10,
            // The highest Clarity in the list until now, at 7.0, and the least
            // appropriate place for it: this is the preset people leave on while
            // they are on voice comms, so an exciter adding 7 dB of harmonic
            // energy in the sibilant band is the exact ear-fatigue complaint the
            // cap exists for.
            Clarity = 4.0,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 1.0,
            BassBoost = 0.0,
            MasterGain = 1.0,
            VolumeLeveling = 4.0,
            FilterQ = 1.5,
            Balance = 0.0
        },

        // Heavy low end for music. The deepest band boost in the list, with
        // master gain pulled well back so the shelf adds weight rather than
        // simply making everything louder.
        new AudioPreset
        {
            Id = "basshead",
            Name = "Basshead",
            Tag = "Sub bass",
            Bands = new[] { 8.0, 6.0, 3.0, 1.0, 0.0, 0.0, 1.0, 2.0, 3.0, 2.0 },
            NumBands = 10,
            Clarity = 2.0,
            Ambience = 1.0,
            Surround = 1.5,
            DynamicBoost = 3.5,
            BassBoost = 7.5,
            MasterGain = -3.0,
            VolumeLeveling = 0.0,
            FilterQ = 1.0,
            Balance = 0.0
        },

        // Orchestral and acoustic. Air across the top two octaves and nothing in
        // the bass, so strings and cymbals stay separate instead of the low end
        // smearing over them.
        new AudioPreset
        {
            Id = "soundtrack",
            Name = "Soundtrack",
            Tag = "Orchestral",
            Bands = new[] { 2.0, 1.0, 0.0, -0.5, 0.0, 1.0, 2.0, 3.0, 4.0, 4.0 },
            NumBands = 10,
            Clarity = 3.5,
            Ambience = 3.0,
            Surround = 3.0,
            DynamicBoost = 2.0,
            BassBoost = 0.0,
            MasterGain = -1.0,
            VolumeLeveling = 0.0,
            FilterQ = 1.0,
            Balance = 0.0
        },

        // Warm and smooth for long background listening. Treble rolled right off
        // and the lower mids filled in, which is the inverse of Soundtrack, with
        // nothing in the chain that asks for attention at any volume.
        new AudioPreset
        {
            Id = "lofi",
            Name = "Lo-Fi",
            Tag = "Warm",
            Bands = new[] { 3.0, 4.0, 2.0, 1.0, 0.0, -1.0, -2.0, -3.0, -5.0, -7.0 },
            NumBands = 10,
            Clarity = 0.0,
            Ambience = 1.5,
            Surround = 1.0,
            DynamicBoost = 0.0,
            BassBoost = 2.0,
            MasterGain = 0.0,
            VolumeLeveling = 0.0,
            FilterQ = 1.0,
            Balance = 0.0
        },

        // Film. A V shape: low shelf for weight, a shallow dip through the mids,
        // air restored at the top. The widest stage here so it works on a
        // surround mix, with enough clarity left for dialogue.
        new AudioPreset
        {
            Id = "cinematic",
            Name = "Cinema",
            Tag = "Film",
            Bands = new[] { 6.0, 4.0, 1.0, -1.0, 1.0, 2.0, 3.0, 3.5, 4.0, 3.0 },
            NumBands = 10,
            Clarity = 4.0,
            Ambience = 4.0,
            Surround = 7.0,
            DynamicBoost = 3.5,
            BassBoost = 4.5,
            MasterGain = 0.0,
            VolumeLeveling = 3.0,
            FilterQ = 1.0,
            Balance = 0.0
        },

        // Late night. The whole point is dynamic control rather than tone, so the
        // levelling sits at the top of what this app allows and the low end is
        // pulled down to stop an explosion waking the house. The design this came
        // from asked for 8 dB of levelling, which is past the end of the slider.
        new AudioPreset
        {
            Id = "latenight",
            Name = "Late Night",
            Tag = "Quiet",
            Bands = new[] { -8.0, -5.0, -2.0, 2.0, 3.0, 4.0, 3.0, 1.0, -1.0, -4.0 },
            NumBands = 10,
            // 4.5, which is the cap and is left there. This is the one preset where
            // the highest clarity in the set is defensible: the material is quiet by
            // design and mostly speech, so there is nothing loud enough to be
            // fatiguing and the top end is what makes a murmur audible.
            Clarity = 4.5,
            Ambience = 0.0,
            Surround = 0.0,
            DynamicBoost = 1.0,
            BassBoost = 0.0,
            MasterGain = -2.0,
            VolumeLeveling = 4.0,
            FilterQ = 1.0,
            Balance = 0.0
        }
    };
}
