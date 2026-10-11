using System;

namespace GamerTool.Services;

/// <summary>
/// A stream of mono audio for the spectrum to look at.
/// <para>
/// The seam exists so the half of this that is arithmetic can be tested without an
/// audio device, which is the only kind of machine CI has. Everything below this
/// interface is pure maths over floats; everything above it is Windows.
/// </para>
/// </summary>
public interface ISampleFeed
{
    /// <summary>
    /// True while audio is genuinely arriving. False when capture is not
    /// running, and also false when the device has gone, because a feed that has
    /// quietly stopped is worse than one that admits it.
    /// </summary>
    bool IsLive { get; }

    /// <summary>Latest mono samples, oldest first. Returns how many were written.</summary>
    int Read(Span<float> into);

    void Start();

    void Stop();
}


/// <summary>
/// Turns a block of audio into one level per spectrum bar.
/// <para>
/// This is a self contained radix-2 FFT rather than a library one, for two
/// reasons. It keeps the whole of the arithmetic testable with no audio device
/// and no audio library involved, and it keeps the band mapping, which is where
/// the visual character actually comes from, in this repository where it can be
/// read and pinned down by a test.
/// </para>
/// </summary>
public static class SpectrumAnalyser
{
    /// <summary>FFT size. A power of two, and the window length.</summary>
    public const int WindowSize = 2048;

    /// <summary>
    /// Fewest FFT bins any one bar may be built from. Two is the point where a
    /// band stops depending on a single bin's luck.
    /// </summary>
    public const int MinBinsPerBar = 2;

    /// <summary>
    /// Where the bottom of the range sits, in dB below full scale. Everything
    /// quieter than this reads as silence.
    /// <para>
    /// This number is the whole reason the old spectrum had a dead top half. The
    /// baked table it replaced was a slice of a 30 dB window, so anything more
    /// than 30 dB down was stored as a literal zero, and music above about 3 kHz
    /// lives down there permanently. Thirteen of the top fourteen bars were zero
    /// in every frame and could never move. 76 dB leaves the noise floor still
    /// visible but out of the way.
    /// </para>
    /// </summary>
    public const double FloorDb = -76.0;

    /// <summary>Span shown above the floor, in dB. Roughly what a good analyser shows.</summary>
    public const double SpanDb = 70.0;

    private static readonly double[] CosTable = new double[WindowSize / 2];
    private static readonly double[] SinTable = new double[WindowSize / 2];

    static SpectrumAnalyser()
    {
        for (int i = 0; i < CosTable.Length; i++)
        {
            double angle = -2.0 * Math.PI * i / WindowSize;
            CosTable[i] = Math.Cos(angle);
            SinTable[i] = Math.Sin(angle);
        }
    }

    /// <summary>
    /// Centre frequency of a bar, log spaced from 40 Hz to 16 kHz.
    /// <para>
    /// Exposed rather than duplicated because the equaliser uses the same
    /// mapping to decide which band a bar belongs to.
    /// </para>
    /// </summary>
    public static double BarCentre(int bar, int barCount) =>
        40.0 * Math.Pow(16000.0 / 40.0, (bar + 0.5) / barCount);

    /// <summary>
    /// Writes one level per bar into <paramref name="levels"/>, each 0 to 1.
    /// <para>
    /// Returns false when there was not enough audio to analyse, so the caller
    /// can hold the last picture rather than snapping to zero on a single
    /// dropped buffer.
    /// </para>
    /// </summary>
    public static bool Analyse(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int barCount,
        Span<double> levels)
    {
        if (levels.Length < barCount || barCount <= 0)
        {
            return false;
        }

        // A full window, or nothing. This used to accept anything over half a
        // window and then index before the start of the span: the window is not
        // sliced when the input is short, so the offset came out negative and the
        // Hann loop read window[-548] and threw. A capture that had just been
        // reopened hands back a short first read often enough to hit it, which is
        // how it turned up: IndexOutOfRangeException out of a dispatcher tick,
        // with the spectrum simply stopping.
        // <para>
        // Returning false is the honest answer anyway. Half a window is not a
        // spectrum, it is a guess with a wider error bar than the display has
        // room to draw.
        // </para>
        if (samples.Length < WindowSize)
        {
            return false;
        }

        // Take the most recent WindowSize samples, so a long read does not
        // analyse a window that has already scrolled off. Unconditional now that
        // short input has been turned away above.
        ReadOnlySpan<float> window = samples.Slice(samples.Length - WindowSize, WindowSize);

        Span<double> re = stackalloc double[WindowSize];
        Span<double> im = stackalloc double[WindowSize];
        Span<double> magnitude = stackalloc double[WindowSize / 2 + 1];
        Span<double> hann = stackalloc double[WindowSize];

        // Hann window. Without one, each bar reads energy from its neighbours and
        // the whole row turns into a smear of the loudest band in the spectrum.
        for (int i = 0; i < WindowSize; i++)
        {
            double w = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (WindowSize - 1)));
            hann[i] = w;
            re[i] = window[i] * w;
            im[i] = 0.0;
        }

        Transform(re, im);

        int bins = WindowSize / 2;
        for (int i = 0; i <= bins; i++)
        {
            magnitude[i] = Math.Sqrt((re[i] * re[i]) + (im[i] * im[i])) / (WindowSize / 4.0);
        }

        double binHz = (double)sampleRate / WindowSize;

        for (int bar = 0; bar < barCount; bar++)
        {
            double centre = BarCentre(bar, barCount);

            // The band runs from the geometric mean of this bar and its
            // neighbours down to the same, which is the usual way to get band
            // edges that do not leave gaps between log spaced bars.
            double low = bar == 0
                ? 40.0
                : Math.Sqrt(BarCentre(bar - 1, barCount) * centre);
            double high = bar == barCount - 1
                ? 16000.0
                : Math.Sqrt(centre * BarCentre(bar + 1, barCount));

            int lo = (int)Math.Round(low / binHz);
            int hi = (int)Math.Round(high / binHz);

            lo = Math.Clamp(lo, 1, bins);
            hi = Math.Clamp(hi, lo, bins);

            // A band narrower than the transform can resolve is not a
            // measurement, it is a coin toss. At 48 kHz with a 2048 window one
            // bin is about 21 Hz, while the bottom bar spans roughly 5 Hz, so it
            // collapsed to a single bin whose content is completely different
            // from one frame to the next. That is the bass snapping.
            // <para>
            // Widening to a minimum of two bins gives up the ability to tell
            // 40 Hz from 45 Hz, which this window never had in the first place,
            // so nothing real is lost. The low bands end up slightly overlapping,
            // which is the honest picture at this resolution.
            // </para>
            if (hi - lo + 1 < MinBinsPerBar)
            {
                int centreBin = Math.Clamp((int)Math.Round(centre / binHz), 1, bins);
                lo = centreBin - (MinBinsPerBar / 2);
                hi = lo + MinBinsPerBar - 1;
                lo = Math.Clamp(lo, 1, bins);
                hi = Math.Clamp(hi, lo, bins);
            }

            // Max rather than mean. Mean across a band that is mostly empty
            // bottom sits under the floor and a bass hit moves nothing, which is
            // the difference between an analyser that dances and one that sits.
            double peak = 0.0;
            for (int i = lo; i <= hi; i++)
            {
                if (magnitude[i] > peak)
                {
                    peak = magnitude[i];
                }
            }

            double db = 20.0 * Math.Log10(peak + 1e-12);
            levels[bar] = Math.Clamp((db - FloorDb) / SpanDb, 0.0, 1.0);
        }

        return true;
    }

    /// <summary>
    /// In place radix-2 Cooley-Tukey, tables precomputed in the static
    /// constructor. Bit reversal is folded into the first pass.
    /// </summary>
    private static void Transform(Span<double> re, Span<double> im)
    {
        int n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;

            for (int i = 0; i < n; i += len)
            {
                int k = 0;
                for (int j = 0; j < half; j++)
                {
                    double wr = CosTable[k];
                    double wi = SinTable[k];

                    int a = i + j;
                    int b = a + half;

                    double tr = (re[b] * wr) - (im[b] * wi);
                    double ti = (re[b] * wi) + (im[b] * wr);

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;

                    k += step;
                }
            }
        }
    }
}
