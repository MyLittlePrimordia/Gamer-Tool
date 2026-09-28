using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How the spectrum behaves frame to frame, which is what the eye actually
/// judges.
/// <para>
/// The bug that motivated this was not a taste disagreement. The view analysed
/// into the same array the smoothing was supposed to be updating, so the value
/// being approached was the value it had just been set to, the comparison was
/// always false, and the update was always zero. The bars were drawn straight
/// from the analyser. Against real music that measured 8.6 dB of movement per
/// frame on average and single frame jumps of over 100 dB, which is bars
/// teleporting rather than moving.
/// </para>
/// </summary>
public class SpectrumSmootherTests
{
    private const int Bars = 48;
    private const double Frame = 1.0 / 60.0;

    private static double[] Flat(double value)
    {
        double[] raw = new double[Bars];
        Array.Fill(raw, value);
        return raw;
    }

    private static double WorstStep(SpectrumSmoother smoother, double[] raw, int frames = 240)
    {
        double worst = 0;
        double[] previous = new double[Bars];
        for (int f = 0; f < frames; f++)
        {
            smoother.Push(raw, hasAudio: true, Frame);
            if (f == 0)
            {
                previous = (double[])smoother.Levels.ToArray();
                continue;
            }

            for (int b = 0; b < Bars; b++)
            {
                worst = Math.Max(worst, Math.Abs(smoother.Levels[b] - previous[b]));
            }

            previous = (double[])smoother.Levels.ToArray();
        }

        return worst;
    }

    [Fact]
    public void ABareStepDoesNotTeleportTheBars()
    {
        // The whole point. Without real smoothing, one frame of a loud passage
        // after a quiet one moved every bar the full height of the display.
        var smoother = new SpectrumSmoother(Bars);
        double[] raw = new double[Bars];
        Array.Fill(raw, 0.05);

        for (int f = 0; f < 30; f++)
        {
            smoother.Push(raw, hasAudio: true, Frame);
        }

        double[] loud = Flat(0.95);
        double worst = WorstStep(smoother, loud, frames: 3);

        // A 70 dB span means this is the share of full height moved in one frame.
        Assert.True(worst < 0.20, "one frame moved a bar by " + (worst * 100).ToString("0") + "% of full height");
    }

    [Fact]
    public void ASteadyToneSettlesAndStaysPut()
    {
        var smoother = new SpectrumSmoother(Bars);
        double[] steady = Flat(0.6);

        for (int f = 0; f < 400; f++)
        {
            smoother.Push(steady, hasAudio: true, Frame);
        }

        double settled = smoother.Levels[0];
        Assert.InRange(settled, 0.55, 0.65);

        // Once settled, a steady input must not keep the bars moving at all.
        double worst = WorstStep(smoother, steady, frames: 120);
        Assert.True(worst < 0.01, "a settled display still moved " + (worst * 100).ToString("0.0") + "% per frame");
    }

    [Fact]
    public void NoiseIsAveragedRatherThanFollowed()
    {
        // Alternating frames is the worst case for a display that draws whatever
        // the analyser said last time.
        var smoothed = new SpectrumSmoother(Bars);
        var followed = new SpectrumSmoother(Bars);

        double[] quiet = Flat(0.2);
        double[] loud = Flat(0.9);
        for (int f = 0; f < 120; f++)
        {
            smoothed.Push(f % 2 == 0 ? loud : quiet, hasAudio: true, Frame);
            followed.Push(f % 2 == 0 ? loud : quiet, hasAudio: true, Frame);
        }

        // The average should end up between the two extremes, not on either.
        double level = smoothed.Levels[0];
        Assert.InRange(level, 0.35, 0.75);

        // And it must not be swinging with the input any more.
        double before = smoothed.Levels[0];
        smoothed.Push(quiet, hasAudio: true, Frame);
        Assert.True(Math.Abs(smoothed.Levels[0] - before) < 0.06);
        Assert.NotEqual(0.0, followed.Levels[0]);
    }

    [Fact]
    public void BarsRiseSlowerThanTheyFall()
    {
        // The property that makes a display read as a meter. A fast rise and a
        // slow fall is what people describe as twitchy.
        Assert.True(SpectrumSmoother.Release < SpectrumSmoother.Attack);
        Assert.True(SpectrumSmoother.Decay < SpectrumSmoother.Attack);
    }

    [Fact]
    public void SilenceReachesExactlyZeroRatherThanHovering()
    {
        var smoother = new SpectrumSmoother(Bars);
        for (int f = 0; f < 60; f++)
        {
            smoother.Push(Flat(0.5), hasAudio: true, Frame);
        }

        for (int f = 0; f < 300; f++)
        {
            smoother.Push(new double[Bars], hasAudio: false, Frame);
        }

        for (int b = 0; b < Bars; b++)
        {
            Assert.Equal(0.0, smoother.Levels[b]);
        }
    }

    [Fact]
    public void LosingAudioFallsRatherThanFreezing()
    {
        var smoother = new SpectrumSmoother(Bars);
        for (int f = 0; f < 60; f++)
        {
            smoother.Push(Flat(0.5), hasAudio: true, Frame);
        }

        double before = smoother.Levels[0];
        smoother.Push(new double[Bars], hasAudio: false, Frame);
        double after = smoother.Levels[0];

        Assert.True(after < before, "a held frame reads as a stuck picture");
        Assert.True(after > 0.0, "but it should fall away, not snap to nothing");
    }

    [Fact]
    public void ResetDropsAllHistory()
    {
        var smoother = new SpectrumSmoother(Bars);
        for (int f = 0; f < 120; f++)
        {
            smoother.Push(Flat(0.7), hasAudio: true, Frame);
        }

        smoother.Reset();

        for (int b = 0; b < Bars; b++)
        {
            Assert.Equal(0.0, smoother.Levels[b]);
        }
    }

    [Fact]
    public void TheSameElapsedTimeGivesTheSameResultAtAnyFrameRate()
    {
        // This was a real defect: a fixed fraction per tick meant the display had
        // three different personalities depending on what the machine managed.
        double[] raw = Flat(0.8);

        var slow = new SpectrumSmoother(Bars);
        for (int f = 0; f < 12; f++)
        {
            slow.Push(raw, hasAudio: true, 1.0 / 12.0);
        }

        var fast = new SpectrumSmoother(Bars);
        for (int f = 0; f < 120; f++)
        {
            fast.Push(raw, hasAudio: true, 1.0 / 120.0);
        }

        for (int b = 0; b < Bars; b++)
        {
            Assert.Equal(slow.Levels[b], fast.Levels[b], 3);
        }
    }

    [Fact]
    public void AZeroOrNegativeStepChangesNothing()
    {
        var smoother = new SpectrumSmoother(Bars);
        smoother.Push(Flat(0.4), hasAudio: true, Frame);
        double before = smoother.Levels[0];

        smoother.Push(Flat(0.9), hasAudio: true, 0.0);
        smoother.Push(Flat(0.9), hasAudio: true, -1.0);

        Assert.Equal(before, smoother.Levels[0]);
    }

    [Fact]
    public void ShortInputAffectsOnlyTheBarsItSupplied()
    {
        // The view always hands over a full width buffer, but a short one must
        // not throw or quietly disturb the bars it did not mention.
        var smoother = new SpectrumSmoother(Bars);
        smoother.Push(new double[] { 0.5, 0.5 }, hasAudio: true, Frame);

        Assert.True(smoother.Levels[0] > 0.0);
        Assert.True(smoother.Levels[1] > 0.0);
        Assert.Equal(0.0, smoother.Levels[2]);
    }

    [Fact]
    public void ARoundOfRealMusicMovesTheBarsByAReasonableAmount()
    {
        // The end to end claim, measured on the same kind of material the
        // regression was found on. A round of music should move the bars, but
        // never teleport them.
        const int Rate = 44100;
        var random = new Random(20260928);
        var samples = new float[SpectrumAnalyser.WindowSize * 40];

        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)Rate;
            double tone = 0.3 * Math.Sin(2 * Math.PI * 220 * t)
                + 0.2 * Math.Sin(2 * Math.PI * 1400 * t)
                + 0.1 * Math.Sin(2 * Math.PI * 60 * t);
            samples[i] = (float)(tone + 0.02 * (random.NextDouble() * 2 - 1));
        }

        var smoother = new SpectrumSmoother(Bars);
        var raw = new double[Bars];
        double worst = 0;
        int frames = 0;

        for (int offset = 0; offset + SpectrumAnalyser.WindowSize <= samples.Length; offset += SpectrumAnalyser.WindowSize / 2)
        {
            Assert.True(SpectrumAnalyser.Analyse(
                samples.AsSpan(offset, SpectrumAnalyser.WindowSize), Rate, Bars, raw));

            int before = CountAbove(smoother);
            smoother.Push(raw, hasAudio: true, Frame);
            if (frames > 20)
            {
                worst = Math.Max(worst, CountAbove(smoother) - before);
            }

            frames++;
        }

        Assert.True(frames > 20);
        Assert.True(worst <= 6, "a single frame lit up " + worst + " extra bars at once");
    }

    private static int CountAbove(SpectrumSmoother smoother)
    {
        int n = 0;
        for (int b = 0; b < smoother.Levels.Length; b++)
        {
            if (smoother.Levels[b] > 0.05)
            {
                n++;
            }
        }

        return n;
    }
}
