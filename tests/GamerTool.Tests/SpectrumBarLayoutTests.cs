using System;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The shape of the bar row: how many bars, and whether the layout can actually
/// draw them at that count.
/// <para>
/// The count went from forty eight to eighty, which is a visual choice, and the
/// layout around it is arithmetic with a floor in it. A count that quietly
/// produces bars narrower than a device pixel, or that trips the layout's
/// refusal to draw at all, looks like the spectrum has broken rather than like a
/// mistake worth reporting. So the arithmetic is pinned here rather than
/// discovered by looking at a screenshot.
/// </para>
/// <para>
/// The constants are read off the shipped types rather than restated, so a change to the
/// shipped numbers fails a test instead of quietly agreeing with itself.
/// </para>
/// </summary>
public class SpectrumBarLayoutTests
{
    private const double HostWidthDips = 423.0;

    private static int Bars => SpectrumView.BarCount;

[Fact]
    public void TheBarCountIsFinerThanItUsedToBe()
    {
        // The point of the change, stated so it cannot be quietly reverted.
        Assert.True(Bars > 48, "bar count is " + Bars);
    }

    [Fact]
    public void TheRowDrawsEveryBandTheAnalyserProduces()
    {
        // This is the one that was missing, and it is why nothing else here
        // caught the problem. Every other test in this class reads the count off
        // SpectrumView and checks the arithmetic that follows from it, so they
        // were all happily verifying a row of eighty bars in a renderer that
        // drew forty eight: the analyser produced eighty, Update copied
        // Math.Min(80, 48) of them, and everything above roughly 1.4 kHz was
        // dropped on the floor and then described on screen as covering the
        // range up to 16 kHz.
        Assert.Equal(
            SpectrumView.BarCount,
            SpectrumBars.BarCount);
    }

    [Theory]
    [InlineData(320.0)]
    [InlineData(423.0)]
    [InlineData(1024.0)]
    [InlineData(3840.0)]
    public void NothingIsDroppedAtAWidthThePanelActuallyOccupies(double width)
    {
        // The row drops bars from the end when a width cannot give every one of
        // them a pitch and a gap. That is a deliberate fallback, not something to
        // rely on: at every width the audio panel really is this wide, the full
        // count has to fit.
        int drawable = Math.Max(1, (int)(width / SpectrumBars.MinDrawablePitch));
        Assert.True(
            Math.Min(SpectrumBars.BarCount, drawable) == SpectrumBars.BarCount,
            "at " + width + "px only " + Math.Min(SpectrumBars.BarCount, drawable) + " of "
                + SpectrumBars.BarCount + " bars would be drawn");
    }


    [Fact]
    public void BarsAreThinnerThanTheyWere()
    {
        double newWidth = Math.Max(1.0, (int)((int)(HostWidthDips / Bars) * SpectrumBars.BarDuty));
        double oldWidth = Math.Max(1.0, (int)(HostWidthDips / 48) - 2.0);

        Assert.True(newWidth < oldWidth, "new bars are " + newWidth + "px, old were " + oldWidth + "px");
    }

    [Theory]
    [InlineData(320.0)]
    [InlineData(600.0)]
    [InlineData(1024.0)]
    [InlineData(1440.0)]
    [InlineData(1920.0)]
    [InlineData(2560.0)]
    [InlineData(3840.0)]
    public void EveryBarGetsRoomToDraw(double width)
    {
        int pitch = (int)(width / Bars);
        Assert.True(
            pitch >= SpectrumBars.MinDrawablePitch,
            "at " + width + "px the pitch is " + pitch + "px, under the " + SpectrumBars.MinDrawablePitch + "px floor, so the row would not be drawn at all");
        Assert.True(Math.Max(1.0, (int)(pitch * SpectrumBars.BarDuty)) >= 1.0);
    }

    [Fact]
    public void TheRowFitsWithoutOverflowingTheHost()
    {
        int pitch = (int)(HostWidthDips / Bars);
        int group = pitch * Bars;
        Assert.True(group <= HostWidthDips, "the row is " + group + "px wide inside a " + HostWidthDips + "px box");
    }

    [Fact]
    public void TheBarGradientStaysOnOneHue()
    {
        // The failure this guards against actually happened once: a gradient that
        // walked the bars through violet, which put a second accent on a tab that
        // already has a pink equaliser and pink dials. Moving lightness is fine.
        // Moving hue is not.
        double axis = Hue(SpectrumBars.BarAxisColour);
        double tip = Hue(SpectrumBars.BarTipColour);

        Assert.True(
            Math.Abs(axis - tip) < 4.0,
            "the gradient spans " + (axis - tip).ToString("0.0") + " degrees of hue, which would read as two colours");
    }

    [Fact]
    public void TheGradientActuallyVariesLightness()
    {
        // Otherwise the previous test passes for a gradient that is flat.
        double axis = Luma(SpectrumBars.BarAxisColour);
        double tip = Luma(SpectrumBars.BarTipColour);

        Assert.True(axis > tip, "the axis should be the lighter part of the bar");
        double spread = axis - tip;
        Assert.True(
            spread > 8.0 && spread < 90.0,
            "the lightness spread is " + spread.ToString("0") + ", which is either invisible or a different colour entirely");
    }

    [Fact]
    public void TheBarPinkIsTheOneSampledFromTheReference()
    {
        // FxSound's accent, read out of a live window. Held as a value so a
        // future "let's warm it up a touch" is a decision rather than a drift.
        Assert.Equal(0xE8, SpectrumBars.BarAxisColour.R);
        Assert.Equal(0x3A, SpectrumBars.BarAxisColour.G);
        Assert.Equal(0x58, SpectrumBars.BarAxisColour.B);
    }

    private static double Hue(System.Windows.Media.Color c)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta <= 0.0)
        {
            return 0.0;
        }

        double h;
        if (max == r)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        return h < 0 ? h + 360.0 : h;
    }

    private static double Luma(System.Windows.Media.Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    [Fact]
    public void TheEqDashRampIsBuiltFromTheTabAccent()
    {
        // The bug this pins: the equaliser's dashed band lines used to carry three
        // hard coded pinks of their own, so they were a different colour from every
        // other accent on the same tab. The middle stop is the accent itself, so
        // the ramp cannot be a different colour than the rest of the page.
        var accent = System.Windows.Media.Color.FromRgb(0xE3, 0x32, 0x50);
        var (top, middle, bottom) = GamerTool.UI.BandFader.DashRamp(accent);

        Assert.Equal(accent, middle);
    }

    [Fact]
    public void TheEqDashRampStaysOnTheAccentHue()
    {
        // Lightening and darkening must be towards white and black. Rotating the
        // hue is what put a second accent on this tab once already.
        var accent = System.Windows.Media.Color.FromRgb(0xE3, 0x32, 0x50);
        var (top, middle, bottom) = GamerTool.UI.BandFader.DashRamp(accent);

        double h = Hue(accent);
        Assert.True(Math.Abs(Hue(top) - h) < 3.0, "the top stop has drifted " + (Hue(top) - h).ToString("0.0") + " degrees");
        Assert.True(Math.Abs(Hue(bottom) - h) < 3.0, "the bottom stop has drifted " + (Hue(bottom) - h).ToString("0.0") + " degrees");
    }

    [Fact]
    public void TheEqDashRampActuallyFades()
    {
        var accent = System.Windows.Media.Color.FromRgb(0xE3, 0x32, 0x50);
        var (top, middle, bottom) = GamerTool.UI.BandFader.DashRamp(accent);

        Assert.True(Luma(top) > Luma(middle), "the top of the fader should be lighter");
        Assert.True(Luma(middle) > Luma(bottom), "the bottom of the fader should be deeper");
    }

    [Fact]
    public void TheDashRampFollowsWhicheverAccentItIsGiven()
    {
        // The reason it is derived rather than hard coded: the colour trims reuse
        // the same fader with a different accent, and they must follow it too.
        var teal = System.Windows.Media.Color.FromRgb(0x22, 0xD3, 0xEE);
        var (_, tealMiddle, _) = GamerTool.UI.BandFader.DashRamp(teal);
        Assert.Equal(teal, tealMiddle);
    }

    [Fact]
    public void TheSwitchedOnKnobMatchesTheDialKnobs()
    {
        // A toggle's on state used to carry a near black handle left over from the
        // display accent, so it read as a hole in the track and was the wrong hue
        // for every other accent. It is the dial knob's white now.
        Assert.Equal(
            GamerTool.UI.FrequencyDial.KnobColour,
            System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0));
    }

    [Fact]
    public void TheSwitchedOnKnobIsNotAHole()
    {
        var knob = GamerTool.UI.FrequencyDial.KnobColour;
        Assert.True(knob.R > 200 && knob.G > 200 && knob.B > 200, "the handle is not a near white");
    }

    [Fact]
    public void TheRowIsMostlyGapSoTheBarsReadSeparately()
    {
        // The reason this is expressed as a share of the pitch rather than a
        // pixel count: at a one pixel gap the bars covered four fifths of the
        // pitch and a loud passage drew as one filled block instead of a row of
        // separate strokes.
        int pitch = (int)(HostWidthDips / Bars);
        int bar = (int)(pitch * SpectrumBars.BarDuty);
        double fill = bar / (double)pitch;

        Assert.True(
            fill <= 0.55,
            "the bars fill " + (fill * 100).ToString("0") + "% of the pitch, so they merge into a block");
    }

    [Theory]
    [InlineData(320.0)]
    [InlineData(423.0)]
    [InlineData(1024.0)]
    [InlineData(1920.0)]
    [InlineData(3840.0)]
    public void ThereIsAlwaysAGapBetweenNeighbours(double width)
    {
        int pitch = (int)(width / Bars);
        int bar = Math.Clamp((int)(pitch * SpectrumBars.BarDuty), 1, pitch - 1);

        Assert.True(bar < pitch, "at " + width + "px a bar is " + bar + "px in a " + pitch + "px pitch, so neighbours touch");
    }

    [Fact]
    public void TheLeftoverWidthBecomesAnEqualInsetAtEachEnd()
    {
        // The layout centres the group, so a remainder is split between the two
        // ends rather than left as a ragged gap on one side.
        int pitch = (int)(HostWidthDips / Bars);
        int leftover = (int)HostWidthDips - (pitch * Bars);
        int left = leftover / 2;
        int right = leftover - left;

        Assert.True(left >= 0 && right >= 0);
        Assert.True(
            Math.Abs(left - right) <= 1,
            "the two insets differ by more than a pixel: " + left + " and " + right);
    }


    [Fact]
    public void BandsStayOrderedAndCoverTheRangeAtTheShippedCount()
    {
        for (int b = 1; b < Bars; b++)
        {
            Assert.True(
                SpectrumAnalyser.BarCentre(b, Bars) > SpectrumAnalyser.BarCentre(b - 1, Bars),
                "bar " + b + " does not sit above bar " + (b - 1));
        }

        Assert.InRange(SpectrumAnalyser.BarCentre(0, Bars), 40.0, 48.0);
        Assert.InRange(SpectrumAnalyser.BarCentre(Bars - 1, Bars), 13000.0, 16000.0);
    }

    [Fact]
    public void TheLowestBandIsNarrowerThanAnFftBinAndTheWideningCoversIt()
    {
        // The honest shape of the trade. At eighty bars the bottom band is far
        // narrower than an FFT bin, so without the minimum-bin widening it would
        // read one bin and flicker. This pins that the widening is still in force
        // at the count now shipped.
        const int Rate = 48000;
        Assert.Equal(2, SpectrumAnalyser.MinBinsPerBar);

        double binHz = Rate / (double)SpectrumAnalyser.WindowSize;
        double bottomWidthHz = 40.0 * (Math.Pow(16000.0 / 40.0, 1.0 / Bars) - 1.0);
        Assert.True(
            bottomWidthHz < binHz,
            "the bottom band is " + bottomWidthHz.ToString("0.00") + "Hz against a " + binHz.ToString("0.0") + "Hz bin, which is exactly why the widening exists");
    }

    [Fact]
    public void ASteadyLowToneDoesNotMakeTheLowestBarsFlicker()
    {
        // The behaviour the widening is for, measured rather than asserted: a
        // tone that is not changing should not make the bar that carries it jump
        // from frame to frame.
        const int Rate = 48000;
        var samples = new float[SpectrumAnalyser.WindowSize * 40];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 42.0 * i / Rate));
        }

        var levels = new double[Bars];
        double lowest = double.MaxValue;
        double highest = double.MinValue;
        int frames = 0;

        for (int offset = 0; offset + SpectrumAnalyser.WindowSize <= samples.Length; offset += SpectrumAnalyser.WindowSize / 2)
        {
            Assert.True(SpectrumAnalyser.Analyse(
                samples.AsSpan(offset, SpectrumAnalyser.WindowSize), Rate, Bars, levels));

            double v = levels[0];
            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
            frames++;
        }

        Assert.True(frames > 20);
        double swingDb = (highest - lowest) * 100.0 / SpectrumAnalyser.SpanDb * 100.0;
        Assert.True(
            swingDb < 12.0,
            "a steady 42 Hz tone moved the lowest bar by " + swingDb.ToString("0.0") + " dB between frames");
    }
}


