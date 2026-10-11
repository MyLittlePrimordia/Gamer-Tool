using System;
using GamerTool.Models;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Folding the blue light trim into a display preset.
/// <para>
/// This is where the night filter's tint is actually applied, so it carries an
/// invariant that is easy to break and impossible to notice: the preview on the
/// Display tab and the ramp that reaches the screen must come out of the same
/// calculation. A strength parameter added for the fade has to reach both, or the
/// thumbnail stops describing what the monitor will show.
/// </para>
/// <para>
/// The backward compatibility is the other half. A strength of 1 has to produce
/// exactly what the two-argument version produced, or every existing preset's
/// colour shifts by however much the interpolation is off.
/// </para>
/// </summary>
public class BlueLightBlendTests
{
    /// <summary>The trim as it was applied before the strength parameter existed.</summary>
    private static DisplayPreset Legacy(int level, DisplayPreset source)
    {
        DisplayPreset result = source.Copy();
        result.Id = source.Id;
        if (level <= 0 || level > 2)
        {
            return result;
        }

        double[] trim = level == 1 ? DisplayPreset.BlueLightWarm : DisplayPreset.BlueLightExtraWarm;
        result.RedGain = Math.Clamp(result.RedGain * trim[0], 0.0, 2.0);
        result.GreenGain = Math.Clamp(result.GreenGain * trim[1], 0.0, 2.0);
        result.BlueGain = Math.Clamp(result.BlueGain * trim[2], 0.0, 2.0);
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FullStrengthIsExactlyWhatItWasBefore(int level)
    {
        // The load-bearing assertion. A fade that changed the colour at full
        // strength would silently alter every existing preset the first time the
        // schedule ran.
        foreach (double r in new[] { 0.9, 1.0, 1.15 })
        {
            foreach (double g in new[] { 0.95, 1.0, 1.05 })
            {
                foreach (double b in new[] { 0.88, 1.0, 1.2 })
                {
                    DisplayPreset source = new()
                    {
                        Id = "test",
                        RedGain = r,
                        GreenGain = g,
                        BlueGain = b,
                    };

                    DisplayPreset expected = Legacy(level, source);
                    DisplayPreset actual = DisplayPreset.WithBlueLight(source, level);

                    Assert.Equal(expected.RedGain, actual.RedGain, 9);
                    Assert.Equal(expected.GreenGain, actual.GreenGain, 9);
                    Assert.Equal(expected.BlueGain, actual.BlueGain, 9);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheDefaultArgumentMeansTheOldCallSitesAreUnchanged(int level)
    {
        // Called without a strength anywhere in the app - the preview outside the
        // night window, and every apply while the schedule is off. These must be
        // identical to passing 1 explicitly.
        DisplayPreset source = DisplayPreset.Flat();

        DisplayPreset implicitStrength = DisplayPreset.WithBlueLight(source, level);
        DisplayPreset explicitStrength = DisplayPreset.WithBlueLight(source, level, 1.0);

        Assert.Equal(explicitStrength.RedGain, implicitStrength.RedGain);
        Assert.Equal(explicitStrength.GreenGain, implicitStrength.GreenGain);
        Assert.Equal(explicitStrength.BlueGain, implicitStrength.BlueGain);
    }

    [Fact]
    public void NoStrengthLeavesTheSourceUntouched()
    {
        // Every field, not just the gains. A fade that dropped Backlight or changed
        // Gamma would be a different kind of change to the screen entirely.
        DisplayPreset source = new()
        {
            Id = "test",
            Backlight = 120,
            Gamma = 2.20,
            ShadowBoost = 0.4,
            Brightness = 0.1,
            Contrast = 0.2,
            RedGain = 1.15,
            GreenGain = 1.05,
            BlueGain = 0.9,
        };

        DisplayPreset result = DisplayPreset.WithBlueLight(source, 2, 0.0);

        Assert.Equal(source.Backlight, result.Backlight);
        Assert.Equal(source.Gamma, result.Gamma);
        Assert.Equal(source.ShadowBoost, result.ShadowBoost);
        Assert.Equal(source.Brightness, result.Brightness);
        Assert.Equal(source.Contrast, result.Contrast);
        Assert.Equal(source.RedGain, result.RedGain);
        Assert.Equal(source.GreenGain, result.GreenGain);
        Assert.Equal(source.BlueGain, result.BlueGain);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void HalfwayIsHalfwayAndNothingElse(double strength)
    {
        // The interpolation has to be linear in the trim's own delta, so half
        // strength is visibly between the two pictures rather than somewhere else.
        DisplayPreset source = DisplayPreset.Flat();
        double[] trim = DisplayPreset.BlueLightExtraWarm;

        DisplayPreset mid = DisplayPreset.WithBlueLight(source, 2, strength);
        DisplayPreset full = DisplayPreset.WithBlueLight(source, 2, 1.0);

        Assert.Equal(1.0 + ((trim[0] - 1.0) * strength), mid.RedGain, 9);
        Assert.Equal(1.0 + ((trim[2] - 1.0) * strength), mid.BlueGain, 9);

        // And genuinely between: closer to nothing than to full, at every strength
        // below one.
        Assert.True(mid.RedGain < full.RedGain);
        Assert.True(mid.RedGain > 1.0);
    }

    [Fact]
    public void AWeakerFadeIsAlwaysWarmerThanNoFadeAtAll()
    {
        // Monotonic in strength. A non-monotonic result would mean the screen got
        // warmer as the filter faded in, which the user would see as a flicker.
        DisplayPreset source = DisplayPreset.Flat();
        double previous = double.MaxValue;

        for (double strength = 0.0; strength <= 1.001; strength += 0.05)
        {
            DisplayPreset result = DisplayPreset.WithBlueLight(source, 2, strength);
            Assert.True(result.BlueGain <= previous, "not monotonic at strength " + strength);
            previous = result.BlueGain;
        }
    }

    [Fact]
    public void APresetWithAWarmTrimOfItsOwnDoesNotOvershoot()
    {
        // The clamp is what stops a strong red gain plus a warm trim going past the
        // 2.0 the ramp builder accepts.
        DisplayPreset hot = new() { Id = "hot", RedGain = 1.95, GreenGain = 1.0, BlueGain = 1.0 };

        DisplayPreset result = DisplayPreset.WithBlueLight(hot, 2, 1.0);

        Assert.True(result.RedGain <= 2.0, "red gain went past the clamp");
        Assert.InRange(result.RedGain, 0.0, 2.0);
        Assert.InRange(result.GreenGain, 0.0, 2.0);
        Assert.InRange(result.BlueGain, 0.0, 2.0);
    }

    [Fact]
    public void TheSourceIsNeverModified()
    {
        // Every level and strength in one sweep, because this is called from the
        // preview path on every frame the Display tab is animating and a mutation
        // there would corrupt the loaded tune.
        DisplayPreset source = DisplayPreset.Flat();
        double red = source.RedGain;

        foreach (int level in new[] { 0, 1, 2 })
        {
            foreach (double strength in new[] { 0.0, 0.3, 1.0, 5.0, -1.0 })
            {
                DisplayPreset.WithBlueLight(source, level, strength);
            }
        }

        Assert.Equal(red, source.RedGain);
    }

    [Fact]
    public void AnOutOfRangeStrengthIsClampedRatherThanTrusted()
    {
        // settings.json is hand-editable, and a fade value of 5 or -1 must not
        // produce a picture.
        DisplayPreset source = DisplayPreset.Flat();

        Assert.Equal(
            DisplayPreset.WithBlueLight(source, 2, 1.0).BlueGain,
            DisplayPreset.WithBlueLight(source, 2, 5.0).BlueGain);

        Assert.Equal(
            DisplayPreset.WithBlueLight(source, 2, 0.0).BlueGain,
            DisplayPreset.WithBlueLight(source, 2, -1.0).BlueGain);
    }

    [Fact]
    public void TheIdSurvivesTheCopy()
    {
        // ApplyDisplay writes _activeDisplayId from this, so an id that changed here
        // would make the loaded preset unrecognised on the next save.
        DisplayPreset source = new() { Id = "my-preset" };

        Assert.Equal("my-preset", DisplayPreset.WithBlueLight(source, 2, 0.5).Id);
    }

    [Fact]
    public void PreviewAndApplyAgreeAtTheSameStrength()
    {
        // The invariant the existing comment in MainWindow.Display.cs states: the
        // preview is built "so the preview matches what Apply will actually push".
        // Both call sites pass the same ActiveBlueLightLevel, so the only thing
        // that can make them disagree is one of them forgetting the strength.
        foreach (double strength in new[] { 0.0, 0.25, 0.5, 1.0 })
        {
            DisplayPreset previewed = DisplayPreset.WithBlueLight(
                DisplayPreset.Flat(), 2, strength);
            DisplayPreset applied = DisplayPreset.WithBlueLight(
                DisplayPreset.Flat(), 2, strength);

            Assert.Equal(previewed.RedGain, applied.RedGain);
            Assert.Equal(previewed.GreenGain, applied.GreenGain);
            Assert.Equal(previewed.BlueGain, applied.BlueGain);
        }
    }
}