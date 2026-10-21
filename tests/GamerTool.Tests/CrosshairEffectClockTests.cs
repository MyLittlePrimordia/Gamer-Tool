using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The clock that makes an effect move, checked on a real element in a real window.
/// <para>
/// The arithmetic is checked elsewhere; what this holds to account is the part the
/// arithmetic cannot see - that a crosshair with an effect actually starts redrawing
/// sixty times a second, and that one without an effect holds no timer at all. That
/// second half is the promise the whole feature is built on: an animated crosshair
/// costs nothing while it is off the screen, and a still one costs nothing ever.
/// </para>
/// <para>
/// The window is put well off the screen so the suite does not flash a crosshair at
/// whoever is using the machine. It is never activated and never appears in the
/// taskbar, and it is closed before the test returns.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairEffectClockTests
{
    [Fact]
    public void An_effect_starts_the_clock_and_a_still_crosshair_stops_it()
    {
        WpfTestHost.Invoke(() =>
        {
            var view = new CrosshairView
            {
                Preset = new CrosshairPreset { EffectKind = CrosshairEffect.Spin, Id = "clock-test" }
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
                Pump(TimeSpan.FromMilliseconds(200));

                Assert.True(
                    Read<Stopwatch>(view, "_phaseClock").Elapsed.TotalSeconds > 0.05,
                    "the animation clock never started, so nothing would ever move");
                Assert.True(Read<DispatcherTimer>(view, "_clock")!.IsEnabled);

                view.Preset = new CrosshairPreset { Id = "clock-test" };
                Pump(TimeSpan.FromMilliseconds(80));

                Assert.False(
                    Read<DispatcherTimer>(view, "_clock")!.IsEnabled,
                    "a still crosshair should hold no timer at all");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Hiding_the_window_stops_the_clock_and_does_not_leave_it_running()
    {
        WpfTestHost.Invoke(() =>
        {
            var view = new CrosshairView
            {
                Preset = new CrosshairPreset { EffectKind = CrosshairEffect.Pulse, Id = "clock-test" }
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
                Assert.True(Read<DispatcherTimer>(view, "_clock")!.IsEnabled);

                // The overlay is a window that is shown and hidden, never closed: hidden
                // has to mean the timer stops, or a crosshair that is off the screen is
                // still costing a redraw every sixteen milliseconds.
                window.Hide();
                Pump(TimeSpan.FromMilliseconds(80));

                Assert.False(Read<DispatcherTimer>(view, "_clock")!.IsEnabled);
                Assert.Equal(0, Read<Stopwatch>(view, "_phaseClock").Elapsed.TotalSeconds);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_moving_crosshair_paints_differently_a_moment_later()
    {
        // The clock running is not the same thing as the picture changing. This is the
        // end of the chain: what is on the screen at one moment is not what is on it at
        // the next, and a still crosshair is.
        WpfTestHost.Invoke(() =>
        {
            var spinning = new CrosshairPreset
            {
                EffectKind = CrosshairEffect.Spin,
                EffectSpeed = CrosshairPreset.EffectSpeedMax,
                Size = 20,
                Thickness = 2,
                Id = "clock-test"
            };

            var view = new CrosshairView { Preset = spinning };
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
                Pump(TimeSpan.FromMilliseconds(120));
                byte[] first = Pixels(view);

                Pump(TimeSpan.FromMilliseconds(160));
                byte[] second = Pixels(view);

                Assert.False(first.SequenceEqual(second), "the crosshair did not move on the screen");

                view.Preset = new CrosshairPreset { Size = 20, Thickness = 2, Id = "clock-test" };
                Pump(TimeSpan.FromMilliseconds(80));

                byte[] stillOne = Pixels(view);
                Pump(TimeSpan.FromMilliseconds(160));
                byte[] stillTwo = Pixels(view);

                Assert.Equal(stillOne, stillTwo);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void An_effect_paints_with_the_glow_switch_off()
    {
        // The bug behind "glow pulse does nothing": the effect only changed the alpha of
        // a glow that is not drawn at all with the switch off, so on a crosshair tuned
        // without glow the effect was indistinguishable from no effect. Every effect now
        // puts marks of its own on the screen, whatever the switch says.
        WpfTestHost.Invoke(() =>
        {
            var plain = new CrosshairPreset { Size = 20, Thickness = 2, Outline = false, Glow = false, Id = "glow" };
            CrosshairPreset sparking = plain.Copy();
            sparking.EffectKind = CrosshairEffect.Sparks;
            sparking.EffectAmount = CrosshairPreset.EffectAmountMax;

            int stillLight = Lit(plain);
            int sparkLight = Lit(sparking);

            Assert.True(
                sparkLight > stillLight + 200,
                $"the sparks added {sparkLight - stillLight} of light with the switch off");
        });
    }

    [Fact]
    public void A_white_crosshair_paints_its_ripple_like_any_other_colour()
    {
        // The report this covers: with a white crosshair the ripple looked like it was
        // not on at all. Nothing about the colour stopped it being drawn - the wave was
        // white on a bright scene and simply could not be seen - but the day it is told
        // apart from a working ripple is by the pixels, so those are what this checks.
        WpfTestHost.Invoke(() =>
        {
            int still = PaintedWhileRunning(WaveCrosshair(CrosshairEffect.None, "#FFFFFF", "white-still"));
            int white = PaintedWhileRunning(WaveCrosshair(CrosshairEffect.Ripple, "#FFFFFF", "white-wave"));
            int green = PaintedWhileRunning(WaveCrosshair(CrosshairEffect.Ripple, "#3CFF5A", "green-wave"));

            Assert.True(
                white > still + 200,
                $"the white wave painted {white - still} of light, so it was not on the screen");
            Assert.True(
                white >= green * 0.8,
                $"the white wave ({white}) was a pale ghost of the green one ({green})");
        });
    }

    private static CrosshairPreset WaveCrosshair(CrosshairEffect effect, string colour, string id)
    {
        return new CrosshairPreset
        {
            ShapeKind = CrosshairShape.Cross,
            EffectKind = effect,
            EffectSpeed = 50,
            EffectAmount = 60,
            Size = 22,
            Thickness = 2,
            Color = colour,
            Id = id
        };
    }

    /// <summary>
    /// The alpha sum of a crosshair a few hundred milliseconds into its animation. The
    /// window is what starts the clock, so this is the only way to measure an effect that
    /// spends its first frame invisible - a ripple does, because a wave that appears at
    /// full strength out of nowhere is a flash.
    /// </summary>
    private static int PaintedWhileRunning(CrosshairPreset preset)
    {
        const int Side = 80;
        var view = new CrosshairView { Preset = preset, Magnification = 1.0 };
        var window = new Window
        {
            Width = Side,
            Height = Side,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = System.Windows.Media.Brushes.Black,
            Content = view
        };

        window.Show();
        try
        {
            Pump(TimeSpan.FromMilliseconds(520));

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Side, Side, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(view);

            var pixels = new byte[Side * Side * 4];
            bitmap.CopyPixels(pixels, Side * 4, 0);

            int total = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                total += pixels[i];
            }

            return total;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// All the alpha a crosshair puts on the screen. Summed over the whole element so
    /// the answer does not depend on where the antialiasing landed.
    /// </summary>
    private static int Lit(CrosshairPreset preset)
    {
        const int Side = 56;
        var view = new CrosshairView { Preset = preset, Magnification = 1.0 };
        view.Measure(new Size(Side, Side));
        view.Arrange(new Rect(0, 0, Side, Side));
        view.UpdateLayout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Side, Side, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(view);

        var pixels = new byte[Side * Side * 4];
        bitmap.CopyPixels(pixels, Side * 4, 0);

        int total = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            total += pixels[i];
        }

        return total;
    }

    /// <summary>The element's own pixels, at one crosshair pixel to one real pixel.</summary>
    private static byte[] Pixels(CrosshairView view)
    {
        view.Measure(new Size(48, 48));
        view.Arrange(new Rect(0, 0, 48, 48));
        view.UpdateLayout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            48, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(view);

        byte[] pixels = new byte[48 * 48 * 4];
        bitmap.CopyPixels(pixels, 48 * 4, 0);
        return pixels;
    }

    private static T Read<T>(object target, string field)
    {
        return (T)target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }

    /// <summary>Run the dispatcher for a while, so timer ticks actually happen.</summary>
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
