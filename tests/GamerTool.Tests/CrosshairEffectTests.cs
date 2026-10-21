using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The animation arithmetic on its own: no window, no clock, no dispatcher.
/// <para>
/// Two things matter enough to be checked hard. Nothing an effect draws may leave the
/// window the overlay gives it, or the crosshair is clipped and looks like a fault
/// rather than a setting. And a crosshair with no effect has to draw exactly what it
/// drew before any of this existed, because every preset saved until now is one.
/// </para>
/// </summary>
public class CrosshairEffectTests
{
    private static CrosshairPreset Preset(
        CrosshairEffect effect = CrosshairEffect.None,
        int amount = 50,
        int speed = 50,
        CrosshairShape shape = CrosshairShape.Cross,
        int size = 16,
        int thickness = 2,
        bool glow = false)
    {
        return new CrosshairPreset
        {
            ShapeKind = shape,
            EffectKind = effect,
            EffectAmount = amount,
            EffectSpeed = speed,
            Size = size,
            Thickness = thickness,
            Glow = glow
        };
    }

    /// <summary>Times across a few cycles, so every phase of every effect is visited.</summary>
    private static IEnumerable<double> Phases(double seconds = 5.4)
    {
        const int Steps = 120;
        for (int i = 0; i <= Steps; i++)
        {
            yield return i * seconds / Steps;
        }
    }

    // ---------------------------------------------------------------- still first

    [Fact]
    public void A_crosshair_with_no_effect_is_the_still_frame_at_every_time()
    {
        CrosshairPreset preset = Preset();

        foreach (double at in new[] { 0.0, 0.5, 3.2, 60.0 })
        {
            CrosshairFrame frame = CrosshairAnimation.At(preset, at);
            CrosshairFrame still = CrosshairFrame.Still;

            Assert.Equal(still.Opacity, frame.Opacity);
            Assert.Equal(still.Scale, frame.Scale);
            Assert.Equal(0, frame.Rotation);
            Assert.Equal(0, frame.Hue);
            Assert.Equal(0, frame.Sheen);
            Assert.Equal(0, frame.SheenAlpha);
            Assert.Empty(frame.Ripples);
            Assert.Empty(frame.Dots);
        }
    }

    [Fact]
    public void A_crosshair_saved_before_effects_existed_is_still()
    {
        // No Effect field on disk at all, which is what every preset written by an older
        // build looks like.
        var saved = new CrosshairPreset { Id = "old", Name = "Old", Size = 20, Thickness = 2 };

        Assert.Equal(string.Empty, saved.Effect);
        Assert.Equal(CrosshairEffect.None, saved.EffectKind);
        Assert.False(saved.Animated);
    }

    // ------------------------------------------------------------- window bounds

    [Theory]
    [InlineData(CrosshairEffect.Pulse)]
    [InlineData(CrosshairEffect.Spin)]
    [InlineData(CrosshairEffect.Breathe)]
    [InlineData(CrosshairEffect.Sheen)]
    [InlineData(CrosshairEffect.Sparks)]
    [InlineData(CrosshairEffect.Ripple)]
    [InlineData(CrosshairEffect.ColourCycle)]
    public void Nothing_an_effect_draws_leaves_the_window_it_is_drawn_in(CrosshairEffect effect)
    {
        // The overlay window is sized from the preset, and an effect that paints past it
        // is clipped. This walks the whole animation for the smallest and largest
        // crosshairs at the widest and narrowest amplitude.
        foreach (CrosshairShape shape in new[]
                 {
                     CrosshairShape.Cross, CrosshairShape.T, CrosshairShape.Circle,
                     CrosshairShape.Square, CrosshairShape.X, CrosshairShape.Dot
                 })
        {
            foreach (int size in new[] { CrosshairPreset.SizeMin, 16, CrosshairPreset.SizeMax })
            {
                foreach (int amount in new[] { CrosshairPreset.EffectAmountMin, 50, CrosshairPreset.EffectAmountMax })
                {
                    CrosshairPreset preset = Preset(effect, amount: amount, shape: shape, size: size, glow: true);
                    double half = CrosshairGeometry.WindowSide(preset) / 2.0;

                    foreach (double at in Phases())
                    {
                        CrosshairFrame frame = CrosshairAnimation.At(preset, at);
                        double reach = ReachOf(preset, frame) * frame.Scale;

                        Assert.True(
                            reach <= half,
                            $"{effect} on a {shape} of {size}px at {amount}% reaches {reach:0.##}px "
                            + $"but its window gives it {half:0.##}px, at t={at:0.###}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// How far from the middle anything this frame draws can paint: the shape's own
    /// bounds, turned by the frame's rotation, plus the widest thing the effect adds -
    /// a spark, a ring - plus the glow's reach past the line.
    /// </summary>
    private static double ReachOf(CrosshairPreset preset, CrosshairFrame frame)
    {
        // The glow passes reach five pixels past the line and the outline adds a little
        // more; both are inside the geometry's own margin, so five is the number the
        // shape contributes beyond its stroke.
        const double GlowReach = 5.0;

        // No effect moves the arms any more, so the shape is the preset's own.
        double reach;
        if (preset.ShapeKind == CrosshairShape.Dot)
        {
            reach = CrosshairGeometry.DotRadius(preset) + GlowReach;
        }
        else
        {
            Geometry shape = CrosshairGeometry.Strokes(preset);
            reach = RotatedReach(shape.Bounds, preset.Rotation + frame.Rotation) + GlowReach;
        }

        // A ripple's bands are the outermost thing on the screen while they are alive.
        // Measured off the geometry the view draws, so a band that is shaped like the
        // crosshair cannot claim a reach it does not have.
        foreach (CrosshairRipple ripple in frame.Ripples)
        {
            if (CrosshairGeometry.RippleBand(preset, ripple.Distance, ripple.Width) is not { } band)
            {
                continue;
            }

            Rect box = band.Bounds;
            reach = Math.Max(
                reach,
                Math.Max(
                    Math.Max(Math.Abs(box.Left), Math.Abs(box.Right)),
                    Math.Max(Math.Abs(box.Top), Math.Abs(box.Bottom))));
        }

        foreach (CrosshairDot dot in frame.Dots)
        {
            reach = Math.Max(reach, Math.Max(Math.Abs(dot.X), Math.Abs(dot.Y)) + dot.Radius);
        }

        // A sheen is clipped to the ink, so it cannot reach past the shape.
        return reach;
    }

    private static double RotatedReach(Rect bounds, double degrees)
    {
        double x = Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right));
        double y = Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom));

        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double reach = 0;
        foreach ((double px, double py) in new[] { (x, y), (x, -y), (-x, y), (-x, -y) })
        {
            reach = Math.Max(reach, Math.Max(Math.Abs((px * cos) - (py * sin)), Math.Abs((px * sin) + (py * cos))));
        }

        return reach;
    }

    [Fact]
    public void A_spinning_crosshair_is_sized_for_the_diagonal_it_turns_through()
    {
        CrosshairPreset still = Preset(size: 20);
        CrosshairPreset spinning = Preset(CrosshairEffect.Spin, size: 20);

        Assert.True(
            CrosshairGeometry.WindowSide(spinning) > CrosshairGeometry.WindowSide(still),
            "a spinning crosshair needs room for the corners it passes through");
    }

    [Fact]
    public void A_pulsing_crosshair_is_sized_for_the_largest_it_swells_to()
    {
        CrosshairPreset still = Preset(size: 20);
        CrosshairPreset pulsing = Preset(CrosshairEffect.Pulse, amount: 100, size: 20);

        Assert.True(CrosshairGeometry.WindowSide(pulsing) > CrosshairGeometry.WindowSide(still));
    }

    // ---------------------------------------------------------------- each effect

    [Fact]
    public void Breathe_only_changes_brightness_and_never_moves_anything()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Breathe, amount: 100);
        var frames = Phases(12).Select(at => CrosshairAnimation.At(preset, at)).ToList();

        Assert.True(frames.Max(f => f.Opacity) > 0.99, "it comes all the way back");
        // The whole complaint: the dimming was a tenth of an already partly transparent
        // crosshair on a dark scene, which is not a subtle effect, it is an invisible
        // one. A breath now goes to about half at full amount.
        Assert.True(frames.Min(f => f.Opacity) < 0.55, "and dims enough to be seen");
        Assert.True(frames.Min(f => f.Opacity) > 0.3, "without ever looking like it is off");

        // And it is visible at the amount the effect starts at, not only at a hundred.
        CrosshairPreset stock = Preset(
            CrosshairEffect.Breathe, amount: CrosshairAnimation.Defaults(CrosshairEffect.Breathe).Amount);
        double dips = Phases(12).Min(at => CrosshairAnimation.At(stock, at).Opacity);
        Assert.True(dips < 0.75, $"the default amount only dims to {dips:0.##}, which cannot be seen");

        Assert.All(frames, f =>
        {
            Assert.Equal(1, f.Scale);
            Assert.Equal(0, f.Rotation);
            Assert.Empty(f.Ripples);
            Assert.Empty(f.Dots);
        });
    }

    [Fact]
    public void Pulse_changes_size_only()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Pulse, amount: 100);
        var frames = Phases().Select(at => CrosshairAnimation.At(preset, at)).ToList();

        Assert.True(frames.Max(f => f.Scale) > 1.25);
        Assert.True(frames.Min(f => f.Scale) < 0.75);

        // Brightness belongs to the breathe now, so the two cannot look alike.
        Assert.All(frames, f => Assert.Equal(1, f.Opacity));
    }

    [Fact]
    public void Sheen_crosses_the_shape_once_and_waits_between_passes()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Sheen, amount: 100);
        var frames = Phases(14).Select(at => CrosshairAnimation.At(preset, at)).ToList();

        var lit = frames.Where(f => f.SheenAlpha > 0).ToList();
        Assert.NotEmpty(lit);
        Assert.True(frames.Count(f => f.SheenAlpha == 0) > lit.Count, "most of the cycle is quiet");

        // It travels across, rather than appearing and disappearing in one place.
        Assert.True(lit.Min(f => f.Sheen) < -0.8);
        Assert.True(lit.Max(f => f.Sheen) > 0.8);
        Assert.All(lit, f =>
        {
            Assert.Equal(1, f.Scale);
            Assert.Equal(1, f.Opacity);
            Assert.Empty(f.Dots);
        });
    }

    [Fact]
    public void Spin_turns_a_full_circle_and_stays_between_zero_and_a_full_turn()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Spin, speed: 100);

        var turns = Phases().Select(at => CrosshairAnimation.At(preset, at).Rotation).ToList();

        Assert.All(turns, r => Assert.InRange(r, 0, 359.999));
        Assert.True(turns.Zip(turns.Skip(1), (a, b) => a != b).All(d => d), "it should never stop");
    }

    [Fact]
    public void Sparks_are_the_same_shower_every_time_and_stay_within_the_burst()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Sparks, amount: 100);
        CrosshairFrame first = CrosshairAnimation.At(preset, 0.7);
        CrosshairFrame again = CrosshairAnimation.At(preset, 0.7);

        Assert.Equal(first.Dots.Count, again.Dots.Count);
        for (int i = 0; i < first.Dots.Count; i++)
        {
            Assert.Equal(first.Dots[i], again.Dots[i]);
        }

        // Sparks start on the shape's own edge and travel one burst past it, so the
        // farthest any of them can get is half the crosshair plus the burst.
        Assert.InRange(first.Dots.Count, 4, 16);
        Assert.All(first.Dots, s =>
        {
            double radius = Math.Sqrt((s.X * s.X) + (s.Y * s.Y));
            Assert.True(radius <= 30, $"a spark got {radius:0.#}px from the middle");
            Assert.InRange(s.Alpha, 0, 1);
        });
    }

    [Fact]
    public void Ripple_sends_two_growing_fading_bands_outward()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Ripple, amount: 100, size: 20, speed: 100);
        var frames = Phases(3).Select(at => CrosshairAnimation.At(preset, at)).ToList();

        Assert.All(frames, f => Assert.InRange(f.Ripples.Count, 1, 2));
        Assert.True(
            frames.Count(f => f.Ripples.Count == 2) > frames.Count / 2,
            "a single band would make the ripple a pulse");

        // One is further out than the other, and both travel while they fade, so the
        // pair reads as something on its way outwards rather than one band blinking.
        Assert.All(frames, f =>
        {
            Assert.Equal(1, f.Scale);
            Assert.Equal(1, f.Opacity);
            Assert.Empty(f.Dots);

            if (f.Ripples.Count == 2)
            {
                CrosshairRipple near = f.Ripples.OrderBy(r => r.Distance).First();
                CrosshairRipple far = f.Ripples.OrderBy(r => r.Distance).Last();
                Assert.True(far.Distance > near.Distance + 2, "a band sat on top of the other one");
            }
        });

        // Follow one wave round: it only ever gets further from the ink, and only ever
        // fainter, all the way out.
        var wave = new[] { 0.12, 0.4, 0.68 }
            .Select(at => CrosshairAnimation.At(preset, at).Ripples.OrderBy(r => r.Distance).First())
            .ToList();

        Assert.True(wave[0].Distance < wave[1].Distance && wave[1].Distance < wave[2].Distance, "it should travel");
        Assert.True(wave[0].Alpha > wave[1].Alpha && wave[1].Alpha > wave[2].Alpha, "and fade as it goes");

        // It has to leave the crosshair, or it is a band drawn on top of the shape.
        Assert.True(
            frames.SelectMany(f => f.Ripples).Max(r => r.Distance) > 8,
            "the wave should reach clearly outside the shape");
    }

    [Fact]
    public void The_ripple_takes_the_shape_of_the_crosshair_and_not_a_circle()
    {
        // What a circle round a square is not: a wave of the shape itself. A band at
        // distance d is paint out along the arms of a cross and nothing at all on the
        // diagonal, where a circular ring of the same reach would be. Checked on the
        // geometry the view draws, so it is the drawn pixels being described.
        const double Distance = 6;
        const double Width = 2;

        CrosshairPreset cross = Preset(CrosshairEffect.Ripple, size: 20, shape: CrosshairShape.Cross);
        Geometry band = Assert.IsAssignableFrom<Geometry>(CrosshairGeometry.RippleBand(cross, Distance, Width));

        // Straight out past the right arm's tip, in the middle of the band's own width:
        // paint. The band is the ink pushed outward, so it lives at the outer edge of
        // what was drawn whatever the arithmetic works out to.
        double mid = band.Bounds.Right - (Width / 2.0);
        Assert.True(band.FillContains(new Point(mid, 0)), "the band should run past the arm's tip");

        // On the diagonal at the same distance from the middle: nothing there, because
        // the cross has no ink along it. A circle of the same reach would be lit there.
        double diagonal = mid / Math.Sqrt(2);
        Assert.False(
            band.FillContains(new Point(diagonal, diagonal)),
            "a circular ripple would light the diagonal of a cross; a cross-shaped one cannot");

        // A ring's own band does reach the diagonal, because a ring is round.
        CrosshairPreset circle = Preset(CrosshairEffect.Ripple, size: 20, shape: CrosshairShape.Circle);
        Geometry round = Assert.IsAssignableFrom<Geometry>(CrosshairGeometry.RippleBand(circle, Distance, Width));
        double roundMid = round.Bounds.Right - (Width / 2.0);
        Assert.True(round.FillContains(new Point(roundMid, 0)));
        Assert.True(
            round.FillContains(new Point(roundMid / Math.Sqrt(2), roundMid / Math.Sqrt(2))),
            "a ring should ripple as a ring, which means paint on its diagonal too");

        // A picture has no outline to push outward, so it is the view's grown copy and
        // the geometry says so rather than inventing one.
        CrosshairPreset picture = Preset(CrosshairEffect.Ripple, shape: CrosshairShape.Custom);
        Assert.Null(CrosshairGeometry.RippleBand(picture, Distance, Width));
    }

    [Fact]
    public void Sparks_leave_the_edges_of_the_shape_rather_than_its_middle()
    {
        // The complaint this answers: sparks that all radiated from the centre looked
        // like they belonged to the gap rather than to the crosshair, and on a picture
        // or a ring they were nowhere near the drawn shape.
        CrosshairPreset cross = Preset(CrosshairEffect.Sparks, amount: 100, size: 20);

        foreach (double at in Phases(3))
        {
            foreach (CrosshairDot spark in CrosshairAnimation.At(cross, at).Dots)
            {
                double radius = Math.Sqrt((spark.X * spark.X) + (spark.Y * spark.Y));
                Assert.True(radius >= 9, $"a spark started at {radius:0.#}px, inside a cross that ends at 10px");
            }
        }
    }

    [Fact]
    public void Sparks_leave_a_ring_from_round_its_rim()
    {
        CrosshairPreset ring = Preset(CrosshairEffect.Sparks, amount: 100, shape: CrosshairShape.Circle, size: 30);

        foreach (CrosshairDot spark in CrosshairAnimation.At(ring, 0.5).Dots)
        {
            double radius = Math.Sqrt((spark.X * spark.X) + (spark.Y * spark.Y));
            Assert.True(radius >= 13, "a spark from a ring should start on the ring, not in its middle");
        }
    }

    [Fact]
    public void Every_shape_has_anchors_on_its_own_edge()
    {
        foreach (CrosshairShape shape in Enum.GetValues<CrosshairShape>())
        {
            CrosshairPreset preset = Preset(shape: shape, size: 24);
            var anchors = CrosshairGeometry.Anchors(preset);

            Assert.NotEmpty(anchors);
            Assert.All(anchors, a => Assert.True(
                Math.Sqrt((a.At.X * a.At.X) + (a.At.Y * a.At.Y)) >= 2.0,
                $"{shape} has an anchor at the middle"));
        }

        // A cross has four tips, a box four corners, a triangle three points.
        Assert.Equal(4, CrosshairGeometry.Anchors(Preset(shape: CrosshairShape.Cross)).Count);
        Assert.Equal(4, CrosshairGeometry.Anchors(Preset(shape: CrosshairShape.Square)).Count);
        Assert.Equal(3, CrosshairGeometry.Anchors(Preset(shape: CrosshairShape.Triangle)).Count);
        Assert.Equal(8, CrosshairGeometry.Anchors(Preset(shape: CrosshairShape.Circle)).Count);
    }

    [Fact]
    public void A_cross_anchor_sits_on_the_tip_that_is_drawn()
    {
        CrosshairPreset cross = Preset(shape: CrosshairShape.Cross, size: 20);
        var anchors = CrosshairGeometry.Anchors(cross);

        Assert.Contains(anchors, a => Math.Abs(a.At.X - 10) < 0.001 && Math.Abs(a.At.Y) < 0.001);
        Assert.Contains(anchors, a => Math.Abs(a.At.Y + 10) < 0.001 && Math.Abs(a.At.X) < 0.001);

        // And the direction points out of the arm it is on, not along it.
        Assert.Contains(anchors, a => Math.Abs(a.Degrees) < 0.001);
        Assert.Contains(anchors, a => Math.Abs(a.Degrees + 90) < 0.001);
    }

    [Fact]
    public void Colour_cycle_drifts_and_comes_back_rather_than_snapping()
    {
        // The slowest cycle is twenty four seconds, so the window has to be longer than
        // one of them for the wheel to be seen going round at all.
        CrosshairPreset preset = Preset(CrosshairEffect.ColourCycle, amount: 100, speed: 50);
        var hues = Phases(16).Select(at => CrosshairAnimation.At(preset, at).Hue).ToList();

        Assert.True(hues.Max() - hues.Min() > 180, "the whole wheel should be in reach");
        Assert.All(hues, h => Assert.InRange(h, -180, 180));
    }

    // ------------------------------------------------------------------- colours

    [Fact]
    public void A_hue_shift_keeps_the_brightness_of_the_colour()
    {
        // Value, not hue: a crosshair that dimmed on its way round the wheel would read
        // as a flicker, which is the one thing a moving crosshair must not do.
        foreach (double degrees in new[] { 15.0, 90.0, 180.0, 270.0, 345.0 })
        {
            (byte r, byte g, byte b) = CrosshairAnimation.ShiftHue(0x3C, 0xFF, 0x5A, degrees);
            Assert.True(Math.Max(r, Math.Max(g, b)) >= 0xFA, "the colour should still be as bright");
        }
    }

    [Fact]
    public void A_hue_shift_of_nothing_is_the_colour_it_started_with()
    {
        Assert.Equal((0x1F, 0xE0, 0xFF), CrosshairAnimation.ShiftHue(0x1F, 0xE0, 0xFF, 0));
        Assert.Equal((0x1F, 0xE0, 0xFF), CrosshairAnimation.ShiftHue(0x1F, 0xE0, 0xFF, 360));
    }

    [Fact]
    public void Red_turned_a_third_of_the_way_round_is_green()
    {
        (byte r, byte g, byte b) = CrosshairAnimation.ShiftHue(0xFF, 0, 0, 120);

        Assert.True(g > r, "red plus a third of a turn is green");
        Assert.InRange(r, 0, 2);
        Assert.InRange(b, 0, 2);
    }

    // --------------------------------------------------------------- persistence

    [Fact]
    public void An_effect_survives_being_saved_and_read_back()
    {
        var saved = new CrosshairPreset
        {
            EffectKind = CrosshairEffect.Sparks,
            EffectSpeed = 80,
            EffectAmount = 20
        }.Sanitised();

        Assert.Equal("Sparks", saved.Effect);
        Assert.Equal(CrosshairEffect.Sparks, saved.EffectKind);
        Assert.Equal(80, saved.EffectSpeed);
        Assert.Equal(20, saved.EffectAmount);
    }

    [Fact]
    public void An_unreadable_effect_becomes_still_rather_than_wrong()
    {
        var saved = new CrosshairPreset { Effect = "wobble" }.Sanitised();

        Assert.Equal(string.Empty, saved.Effect);
        Assert.Equal(CrosshairEffect.None, saved.EffectKind);
    }

    [Fact]
    public void The_effect_numbers_are_pulled_into_range()
    {
        var high = new CrosshairPreset { EffectKind = CrosshairEffect.Spin, EffectSpeed = 4000, EffectAmount = 4000 }.Sanitised();
        var low = new CrosshairPreset { EffectKind = CrosshairEffect.Spin, EffectSpeed = -20, EffectAmount = -20 }.Sanitised();

        Assert.Equal(CrosshairPreset.EffectSpeedMax, high.EffectSpeed);
        Assert.Equal(CrosshairPreset.EffectAmountMax, high.EffectAmount);
        Assert.Equal(CrosshairPreset.EffectSpeedMin, low.EffectSpeed);
        Assert.Equal(CrosshairPreset.EffectAmountMin, low.EffectAmount);
    }

    [Fact]
    public void A_copy_keeps_the_effect_and_Reset_takes_it_away()
    {
        CrosshairPreset original = Preset(CrosshairEffect.Spin, speed: 90);
        CrosshairPreset copy = original.Copy();

        Assert.Equal(CrosshairEffect.Spin, copy.EffectKind);
        Assert.Equal(90, copy.EffectSpeed);

        copy.ResetTuning();

        Assert.Equal(CrosshairEffect.None, copy.EffectKind);
        Assert.Equal(string.Empty, copy.Effect);
    }

    [Fact]
    public void Two_crosshairs_differ_when_only_their_effect_does()
    {
        CrosshairPreset still = Preset();
        CrosshairPreset spinning = Preset(CrosshairEffect.Spin);

        Assert.False(still.SameLook(spinning));
        Assert.True(still.SameLook(Preset()));
    }

    [Fact]
    public void Every_effect_in_the_menu_has_a_label_and_a_hint()
    {
        foreach (CrosshairEffect effect in Enum.GetValues<CrosshairEffect>())
        {
            Assert.Contains(effect, CrosshairAnimation.Menu);
            Assert.False(string.IsNullOrWhiteSpace(CrosshairAnimation.Label(effect)));
            Assert.False(string.IsNullOrWhiteSpace(CrosshairAnimation.Hint(effect)));
        }

        Assert.Equal(CrosshairEffect.None, CrosshairAnimation.Menu[0]);
    }

    [Fact]
    public void A_very_light_wave_is_given_a_shadow_and_the_rest_are_left_alone()
    {
        // The report this covers: a white crosshair with Ripple looked like it did
        // nothing, because a white wave at a few tenths of its opacity is the same
        // colour as a bright scene. White and pale yellow now send out a shadow under
        // the wave; every other colour in the row carries itself and is untouched.
        (byte R, byte G, byte B)? white = CrosshairAnimation.WaveEdge(0xFF, 0xFF, 0xFF);
        Assert.NotNull(white);
        Assert.InRange(white!.Value.R, 0x3C, 0x46);
        Assert.Equal(white.Value.R, white.Value.G);
        Assert.Equal(white.Value.G, white.Value.B);

        (byte R, byte G, byte B)? yellow = CrosshairAnimation.WaveEdge(0xFF, 0xE6, 0x33);
        Assert.NotNull(yellow);
        Assert.True(
            yellow!.Value.R > yellow.Value.G && yellow.Value.G > yellow.Value.B,
            "the shadow keeps the colour's own hue rather than turning it grey");

        Assert.Null(CrosshairAnimation.WaveEdge(0x3C, 0xFF, 0x5A));
        Assert.Null(CrosshairAnimation.WaveEdge(0x1F, 0xE0, 0xFF));
        Assert.Null(CrosshairAnimation.WaveEdge(0xFF, 0x2B, 0xB0));
        Assert.Null(CrosshairAnimation.WaveEdge(0xFF, 0x3B, 0x3B));

        // And which colours in the palette that is, so a colour added to the row has to
        // answer this question instead of the rule quietly not applying to it.
        string[] shaded = CrosshairPreset.Palette
            .Where(hex => CrosshairPreset.TryParseColor(hex, out byte r, out byte g, out byte b)
                && CrosshairAnimation.WaveEdge(r, g, b) is not null)
            .ToArray();

        Assert.Equal(new[] { "#FFE633", "#FFFFFF" }, shaded.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void The_labels_are_short_enough_for_the_dropdown()
    {
        // "Color cycle" was longer than the box beside the heading and had its end cut
        // off, so one short word per effect is a rule here and not a preference.
        Assert.Equal("Cycle", CrosshairAnimation.Label(CrosshairEffect.ColourCycle));

        foreach (CrosshairEffect effect in CrosshairAnimation.Menu)
        {
            string label = CrosshairAnimation.Label(effect);
            Assert.True(label.Length <= 10, $"\"{label}\" is too long for the effect box");
        }
    }

    [Fact]
    public void The_amount_slider_is_only_offered_for_effects_that_have_one()
    {
        Assert.False(CrosshairAnimation.UsesAmount(CrosshairEffect.None));
        Assert.False(CrosshairAnimation.UsesAmount(CrosshairEffect.Spin));

        foreach (CrosshairEffect effect in CrosshairAnimation.Menu.Where(e => e is not (CrosshairEffect.None or CrosshairEffect.Spin)))
        {
            Assert.True(CrosshairAnimation.UsesAmount(effect), effect.ToString());
        }
    }

    [Fact]
    public void Speed_changes_the_rate_and_never_the_shape()
    {
        CrosshairPreset slow = Preset(CrosshairEffect.Spin, speed: CrosshairPreset.EffectSpeedMin);
        CrosshairPreset fast = Preset(CrosshairEffect.Spin, speed: CrosshairPreset.EffectSpeedMax);

        // Over the same five seconds the fast one has been round more often, so at
        // almost any instant the two are at a different angle.
        var disagreements = Phases().Count(at => Math.Abs(
            CrosshairAnimation.At(slow, at).Rotation - CrosshairAnimation.At(fast, at).Rotation) > 0.001);

        Assert.True(disagreements > 20, "speed 1 and speed 100 should not look the same");
    }

    [Fact]
    public void Negative_time_draws_the_first_frame_rather_than_something_arbitrary()
    {
        CrosshairPreset preset = Preset(CrosshairEffect.Ripple, amount: 100);

        Assert.Equal(
            CrosshairAnimation.At(preset, 0).Ripples.First().Distance,
            CrosshairAnimation.At(preset, -5).Ripples.First().Distance);
    }
}
