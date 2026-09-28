using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The arithmetic behind the live spectrum: a window, an FFT, and a mapping from
/// bins to the bars on screen.
/// <para>
/// All of it is pure maths over floats, so it is covered here rather than on a
/// machine with a sound card. That matters more than usual for this file, because
/// the version it replaces had a dead top half and nothing noticed: the bars were
/// zero in the asset, and no test ever looked at whether a bar could move at all.
/// </para>
/// </summary>
public class SpectrumAnalyserTests
{
    private const int Rate = 48000;
    private const int Bars = 48;

    private static double[] Levels(ReadOnlySpan<float> samples, int rate = Rate, int bars = Bars)
    {
        var levels = new double[bars];
        Assert.True(SpectrumAnalyser.Analyse(samples, rate, bars, levels));
        return levels;
    }

    private static float[] Tone(double hz, double amplitude = 0.5f, int length = SpectrumAnalyser.WindowSize * 2, int rate = Rate)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * hz * i / rate));
        }

        return samples;
    }

    [Fact]
    public void A_tone_lights_the_bar_for_its_frequency()
    {
        // 1 kHz.
        int bar = NearestBar(1000.0);

        double[] levels = Levels(Tone(1000.0f));

        Assert.True(levels[bar] > 0.7, $"expected a strong reading on bar {bar}, got {levels[bar]:0.00}");
    }

    [Fact]
    public void A_tone_does_not_light_the_whole_row()
    {
        // The check the old spectrum would have failed hardest: a pure tone must
        // be a spike, not a flat line. If this ever passes, the mapping has
        // stopped distinguishing frequencies at all.
        double[] levels = Levels(Tone(1000.0f));

        int bright = levels.Count(l => l > 0.5);
        Assert.True(bright <= 6, $"{bright} bars lit for a single tone, expected a spike");
    }

    [Fact]
    public void The_bar_for_a_tone_beats_its_neighbours()
    {
        double[] levels = Levels(Tone(1000.0f));
        int peak = Array.IndexOf(levels, levels.Max());

        Assert.True(peak > 0 && peak < Bars - 1, "the peak landed on an end bar");
        Assert.True(levels[peak] > levels[peak - 1], "the bar below the peak was as loud");
        Assert.True(levels[peak] > levels[peak + 1], "the bar above the peak was as loud");
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(250.0)]
    [InlineData(2000.0)]
    [InlineData(8000.0)]
    [InlineData(15000.0)]
    public void Every_octave_moves_its_own_bar(double hz)
    {
        // The regression the baked table had: 13 of the top 14 bars were zero in
        // every frame. Quiet but real content high in the spectrum has to produce
        // a visible bar, or the top half of the visual is dead again.
        //
        // The bar looked up is the NEAREST one, not the first inside a tolerance.
        // A tolerance wide enough to be forgiving at 8 kHz also swallows the bar
        // below it, and asking about the wrong bar makes this test fail while the
        // analyser is behaving perfectly.
        int bar = NearestBar(hz);
        Assert.True(
            Math.Abs(SpectrumAnalyser.BarCentre(bar, Bars) - hz) < hz * 0.15,
            $"no bar sits on {hz} Hz, got {SpectrumAnalyser.BarCentre(bar, Bars):0}");

        double[] levels = Levels(Tone(hz, amplitude: 0.02f));

        Assert.True(levels[bar] > 0.15, $"{hz} Hz at -34 dBFS produced {levels[bar]:0.00} on bar {bar}");
    }

    private static int NearestBar(double hz)
    {
        int best = 0;
        double distance = double.MaxValue;
        for (int b = 0; b < Bars; b++)
        {
            double d = Math.Abs(Math.Log(SpectrumAnalyser.BarCentre(b, Bars) / hz));
            if (d < distance)
            {
                distance = d;
                best = b;
            }
        }

        return best;
    }

    [Fact]
    public void Silence_reads_as_silence_everywhere()
    {
        double[] levels = Levels(new float[SpectrumAnalyser.WindowSize * 2]);

        Assert.All(levels, l => Assert.Equal(0.0, l, 3));
    }

    [Fact]
    public void Every_level_is_inside_the_range_the_renderer_expects()
    {
        // The view multiplies by the available height, so anything outside 0 to 1
        // draws outside the box or inverts.
        double[] loud = Levels(Tone(120.0f, amplitude: 4.0f));
        double[] quiet = Levels(Tone(120.0f, amplitude: 0.0001f));

        Assert.All(loud, l => Assert.InRange(l, 0.0, 1.0));
        Assert.All(quiet, l => Assert.InRange(l, 0.0, 1.0));
    }

    [Fact]
    public void Louder_audio_reads_higher()
    {
        double[] soft = Levels(Tone(440.0f, amplitude: 0.05f));
        double[] loud = Levels(Tone(440.0f, amplitude: 0.5f));

        Assert.True(loud.Max() > soft.Max(), "raising the level did not raise the bar");
    }

    [Fact]
    public void Too_little_audio_is_refused_rather_than_analysed()
    {
        // So the caller can hold the last picture instead of snapping to zero on
        // a dropped buffer.
        var levels = new double[Bars];
        Assert.False(SpectrumAnalyser.Analyse(new float[64], Rate, Bars, levels));
    }

    [Fact]
    public void An_impossible_bar_count_is_refused()
    {
        var none = Array.Empty<double>();
        Assert.False(SpectrumAnalyser.Analyse(new float[SpectrumAnalyser.WindowSize], Rate, 0, none));
        Assert.False(SpectrumAnalyser.Analyse(new float[SpectrumAnalyser.WindowSize], Rate, 8, new double[4]));
    }

    [Fact]
    public void The_bars_run_low_to_high()
    {
        // The visual grows left to right, so a mis-ordered mapping would put the
        // bass on the right and nobody would notice in a still.
        Assert.True(SpectrumAnalyser.BarCentre(0, Bars) < SpectrumAnalyser.BarCentre(10, Bars));
        Assert.True(SpectrumAnalyser.BarCentre(10, Bars) < SpectrumAnalyser.BarCentre(Bars - 1, Bars));
    }

    [Fact]
    public void The_bars_cover_40_hertz_to_16_kilohertz()
    {
        // Centres sit half a bar in from each end rather than exactly on it, so
        // the first bar is a little above 40 and the last a little under 16k.
        // That is deliberate: a centre exactly on the edge would put half of that
        // bar's width outside the range it is meant to describe.
        Assert.InRange(SpectrumAnalyser.BarCentre(0, Bars), 40.0, 48.0);
        Assert.InRange(SpectrumAnalyser.BarCentre(Bars - 1, Bars), 13000.0, 16000.0);
    }

    [Fact]
    public void The_window_is_a_power_of_two_the_transform_can_use()
    {
        Assert.Equal(0, SpectrumAnalyser.WindowSize & (SpectrumAnalyser.WindowSize - 1));
    }

    [Fact]
    public void The_floor_is_wide_enough_for_high_content_to_appear()
    {
        // The old asset used a 30 dB slice and everything below it stored as a
        // literal zero. This is the guard against going back to that.
        Assert.True(
            SpectrumAnalyser.FloorDb <= -60.0,
            "the floor is too high: quiet high frequency content will read as silence again");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1500)]
    [InlineData(2047)]
    public void A_short_read_is_refused_rather_than_throwing(int length)
    {
        // The crash this pins. The analyser used to accept anything over half a
        // window and then, because a short input is not sliced, computed a
        // negative offset and read before the start of the span. A capture that
        // had just been reopened hands back a short first read often enough to hit
        // it, and it surfaced as an IndexOutOfRangeException out of a dispatcher
        // tick with the spectrum simply dead.
        var samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.05);
        }

        var levels = new double[80];
        Assert.False(SpectrumAnalyser.Analyse(samples, Rate, 80, levels));
    }

    [Fact]
    public void A_full_window_is_still_analysed()
    {
        // The other half of the guard: refusing short input must not cost the
        // display its picture. Levels asserts the analyser accepted the input.
        double[] levels = Levels(Tone(1000.0f, amplitude: 0.5f));

        Assert.True(levels.Any(l => l > 0.0), "a loud tone produced nothing at all");
    }

    [Fact]
    public void Extra_samples_beyond_a_window_are_ignored()
    {
        // The other direction: a long read must analyse the newest window only,
        // and must not read past the end doing it.
        var samples = new float[SpectrumAnalyser.WindowSize * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        }

        var levels = new double[80];
        Assert.True(SpectrumAnalyser.Analyse(samples, Rate, 80, levels));
    }
}
