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
/// The crosshairs that ship, which are a small library of settings rather than one
/// per shape.
/// <para>
/// The set used to be a catalogue of drawings: seven shapes, every one of them a two
/// pixel line, named after its own colour. That is a demo of the shape picker, not a
/// starting point, and a player who picked "Magenta X" learned nothing about why they
/// might want one. These are the configurations the research kept turning up - a two
/// pixel tactical cross, the same with a dot, a dot on its own, a wide tracking
/// frame, a T - plus the shapes no shooter draws, kept and named for what they are.
/// </para>
/// <para>
/// The values are pinned loosely: the tests describe each archetype's shape, not its
/// exact pixels, so retuning a number does not fail the build but flattening the
/// library back into seven identical crosses does.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairPresetLibraryTests
{
    private static readonly CrosshairPreset[] BuiltIns = CrosshairPreset.BuiltIns.ToArray();

    [Fact]
    public void Every_built_in_is_its_own_preset_with_a_name_of_its_own()
    {
        Assert.Equal(BuiltIns.Length, BuiltIns.Select(p => p.Id.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(BuiltIns.Length, BuiltIns.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(BuiltIns, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
        Assert.All(BuiltIns, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Every_built_in_is_already_in_range()
    {
        // A shipped preset that Sanitised would change is one that is clipped the
        // moment it is loaded, so the numbers on disk would never be the numbers on
        // the screen.
        foreach (CrosshairPreset preset in BuiltIns)
        {
            CrosshairPreset loaded = preset.Copy().Sanitised();

            Assert.True(preset.SameLook(loaded), preset.Id + " is changed by loading it");
            Assert.Equal(preset.CenterDotSize, loaded.CenterDotSize);
            Assert.Equal(preset.Id, loaded.Id);
            Assert.Equal(preset.Name, loaded.Name);
        }
    }

    [Fact]
    public void The_default_is_the_first_built_in_and_it_is_a_cross()
    {
        // BuiltIns[0] is what a new install opens on, and what an unknown preset id
        // falls back to, so it has to stay the plain, safe one.
        CrosshairPreset start = CrosshairPreset.Default();

        Assert.Equal("xh-classic", start.Id);
        Assert.Equal(CrosshairShape.Cross, start.ShapeKind);
        Assert.True(BuiltIns[0].SameLook(start));
    }

    [Fact]
    public void Every_shape_still_has_a_built_in_that_shows_it()
    {
        // The shapes no competitive shooter draws are still here on purpose: the
        // library is also how a user finds out the picker can draw an X.
        CrosshairShape[] drawn = Enum.GetValues<CrosshairShape>()
            .Where(s => s != CrosshairShape.Custom)
            .ToArray();

        Assert.All(drawn, shape => Assert.Contains(BuiltIns, p => p.ShapeKind == shape));
    }

    [Fact]
    public void The_small_tactical_cross_has_short_arms_and_a_thin_gap()
    {
        CrosshairPreset precision = Assert.Single(BuiltIns, p => p.Id == "xh-precision");

        Assert.Equal(CrosshairShape.Cross, precision.ShapeKind);
        Assert.InRange(ArmLength(precision), 1, 3);
        Assert.InRange(precision.Gap, 0, 1);

        // The outline is off on the smallest crosses: one pixel of black either side
        // doubles the width of a two pixel arm.
        Assert.False(precision.Outline);
    }

    [Fact]
    public void The_cross_with_a_dot_sizes_the_dot_on_its_own()
    {
        CrosshairPreset mixed = Assert.Single(BuiltIns, p => p.Id == "xh-crossdot");

        Assert.Equal(CrosshairShape.Cross, mixed.ShapeKind);
        Assert.True(mixed.CenterDot);
        Assert.True(mixed.HasOwnCenterDotSize);
        Assert.InRange(mixed.EffectiveCenterDotSize(), 1, 3);

        // And it is the dot's own size, not one inherited from the arm.
        Assert.NotEqual(Math.Max(2, mixed.Thickness + 1), mixed.CenterDotSize);
    }

    [Fact]
    public void The_tracking_cross_gives_the_middle_more_room_than_the_precision_one()
    {
        CrosshairPreset tracking = Assert.Single(BuiltIns, p => p.Id == "xh-tracking");
        CrosshairPreset precision = Assert.Single(BuiltIns, p => p.Id == "xh-precision");

        // Short arms around a wide hole: a frame rather than a sight, which is what
        // a hero shooter's tracking reticle is for.
        Assert.True(tracking.Gap > ArmLength(tracking), "the tracking cross is not mostly gap");
        Assert.True(tracking.Gap > precision.Gap);
        Assert.True(ArmLength(tracking) >= ArmLength(precision));
    }

    [Fact]
    public void There_is_a_dot_on_its_own_and_a_T_with_the_top_left_off()
    {
        CrosshairPreset dot = Assert.Single(BuiltIns, p => p.ShapeKind == CrosshairShape.Dot);
        Assert.True(dot.EffectiveCenterDotSize() <= 4, "a dot-only crosshair that is not a dot");

        CrosshairPreset t = Assert.Single(BuiltIns, p => p.ShapeKind == CrosshairShape.T);
        Assert.Equal(CrosshairShape.T, t.ShapeKind);
    }

    [Fact]
    public void Every_built_in_draws_something_inside_its_overlay_window()
    {
        foreach (CrosshairPreset preset in BuiltIns)
        {
            int side = CrosshairGeometry.WindowSide(preset);
            double room = side / 2.0;

            Assert.True(side >= 48, preset.Id + " asks for a window smaller than the floor");
            Assert.Equal(0, side % 2);

            if (preset.ShapeKind == CrosshairShape.Dot)
            {
                Assert.True(CrosshairGeometry.DotRadius(preset) * 2 <= room, preset.Id + " is clipped by its window");
                continue;
            }

            Geometry strokes = WpfTestHost.Invoke(() => CrosshairGeometry.Strokes(preset));
            Rect bounds = strokes.Bounds;
            Assert.False(bounds.IsEmpty, preset.Id + " draws nothing");
            Assert.True(bounds.Left >= -room && bounds.Right <= room, preset.Id + " reaches outside its window");
            Assert.True(bounds.Top >= -room && bounds.Bottom <= room, preset.Id + " reaches outside its window");

            // The dot sits on top of the shape, so it has to fit as well.
            Assert.True(CrosshairGeometry.CenterDotRadius(preset) <= room, preset.Id + " has a dot wider than its window");
        }
    }

    /// <summary>How far one arm reaches from the edge of the gap, in pixels.</summary>
    private static int ArmLength(CrosshairPreset preset)
    {
        int half = Math.Max(3, (int)Math.Round(preset.Size / 2.0));
        int gap = Math.Clamp(preset.Gap, 0, Math.Max(0, half - 2));
        return half - gap;
    }
}
