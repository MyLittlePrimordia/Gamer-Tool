using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The cases where an effect used to be switched on and visibly do nothing, or do
/// something other than what its name says.
/// </summary>
[Collection("wpf")]
public sealed class CrosshairEffectHardeningTests
{
    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0xC8, 0xC8, 0xC8)]
    [InlineData(0x20, 0x20, 0x20)]
    public void The_colour_cycle_moves_a_white_or_grey_crosshair(byte r, byte g, byte b)
    {
        // Turning the hue of white gives white, which made the cycle do nothing at all on
        // the colour most crosshairs are set to.
        var seen = new HashSet<(byte, byte, byte)>();
        for (int degrees = -150; degrees <= 150; degrees += 30)
        {
            seen.Add(CrosshairAnimation.CycleHue(r, g, b, degrees));
        }

        Assert.True(seen.Count >= 5, "a grey crosshair should pass through several colours");
        Assert.All(seen, c => Assert.True(Math.Max(c.Item1, Math.Max(c.Item2, c.Item3)) >= 200, "and stay bright enough to read"));
    }

    [Fact]
    public void The_colour_cycle_leaves_a_coloured_crosshair_to_the_ordinary_hue_shift()
    {
        foreach (double degrees in new[] { -120.0, -30.0, 0.0, 45.0, 200.0 })
        {
            Assert.Equal(
                CrosshairAnimation.ShiftHue(0x3C, 0xFF, 0x5A, degrees),
                CrosshairAnimation.CycleHue(0x3C, 0xFF, 0x5A, degrees));
        }
    }

    [Fact]
    public void A_spin_that_cannot_be_seen_says_so_and_one_that_can_does_not()
    {
        Assert.NotEmpty(CrosshairAnimation.Caveat(CrosshairEffect.Spin, CrosshairShape.Dot, false));
        Assert.NotEmpty(CrosshairAnimation.Caveat(CrosshairEffect.Spin, CrosshairShape.Circle, false));
        Assert.Empty(CrosshairAnimation.Caveat(CrosshairEffect.Spin, CrosshairShape.Cross, false));
        Assert.Empty(CrosshairAnimation.Caveat(CrosshairEffect.Spin, CrosshairShape.Square, false));
        Assert.Empty(CrosshairAnimation.Caveat(CrosshairEffect.Pulse, CrosshairShape.Dot, false));
    }

    [Fact]
    public void A_picture_that_keeps_its_own_colours_says_the_cycle_only_reaches_its_glow()
    {
        Assert.NotEmpty(CrosshairAnimation.Caveat(CrosshairEffect.ColourCycle, CrosshairShape.Custom, true));
        Assert.Empty(CrosshairAnimation.Caveat(CrosshairEffect.ColourCycle, CrosshairShape.Custom, false));
        Assert.Empty(CrosshairAnimation.Caveat(CrosshairEffect.ColourCycle, CrosshairShape.Cross, true));
    }

    [Fact]
    public void The_sheen_at_its_starting_amount_is_strong_enough_to_see()
    {
        (int speed, int amount) = CrosshairAnimation.Defaults(CrosshairEffect.Sheen);
        var preset = new CrosshairPreset
        {
            EffectKind = CrosshairEffect.Sheen,
            EffectSpeed = speed,
            EffectAmount = amount
        };

        double peak = Enumerable.Range(0, 800)
            .Select(i => CrosshairAnimation.At(preset, i * 0.02).SheenAlpha)
            .Max();

        Assert.True(peak >= 0.4, $"the brightest the sheen gets at its default is {peak:0.##}, which is easy to miss");
        Assert.True(peak <= 1.0);
    }

    [Fact]
    public void An_animation_with_uneven_frame_times_is_not_ticked_at_the_pace_of_its_first_frame()
    {
        WpfTestHost.Invoke(() =>
        {
            BitmapSource Frame()
            {
                var pixels = new byte[4 * 4 * 4];
                BitmapSource bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, pixels, 16);
                bitmap.Freeze();
                return bitmap;
            }

            // The first frame is long and the rest are short. A timer interval taken from
            // the first frame would show every later frame hundreds of milliseconds late.
            var picture = new CrosshairPicture(new[] { Frame(), Frame(), Frame() }, new[] { 400, 20, 20 });
            var view = new CrosshairView
            {
                Preset = new CrosshairPreset { ShapeKind = CrosshairShape.Custom, CustomImage = "x.png" },
                Picture = picture
            };

            var window = new Window
            {
                Width = 64,
                Height = 64,
                Left = -4000,
                Top = -4000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = view
            };

            window.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(150));

                var clock = (DispatcherTimer)typeof(CrosshairView)
                    .GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(view)!;

                Assert.True(clock.IsEnabled);
                Assert.True(
                    clock.Interval.TotalMilliseconds <= AnimationPlan.MinDelayMs,
                    $"the clock ticks every {clock.Interval.TotalMilliseconds} ms, too slow for a {AnimationPlan.MinDelayMs} ms frame");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            frame.Continue = false;
        };

        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
