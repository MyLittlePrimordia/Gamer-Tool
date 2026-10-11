using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace GamerTool.UI;

/// <summary>
/// Stroke outlines for the small action buttons. Emoji were tried first and were
/// the wrong call: a handful of glyphs like the wastebasket fall back to a broken
/// box at button sizes, they are full colour images that cannot be tinted, and
/// they sit at whatever weight the system font felt like that day. Every glyph in
/// the app is a vector here, drawn on one 24 unit canvas, so the whole set shares
/// a weight, a corner treatment and an optical size, and picks up the tab accent.
/// </summary>
public static class Icons
{
    public static Geometry Plus { get; } = Freeze(
        "M 12 5.2 V 18.8 M 5.2 12 H 18.8");

    public static Geometry Pencil { get; } = Freeze(
        "M 4.2 19.8 L 4.8 16.2 L 15.6 5.4 A 2.1 2.1 0 0 1 18.6 8.4 L 7.8 19.2 Z " +
        "M 13.6 7.4 L 16.6 10.4");

    public static Geometry Trash { get; } = Freeze(
        "M 4 6.6 H 20 " +
        "M 9.6 6.6 V 4.9 A 1.3 1.3 0 0 1 10.9 3.6 H 13.1 A 1.3 1.3 0 0 1 14.4 4.9 V 6.6 " +
        "M 6.6 6.6 L 7.5 19.5 A 1.4 1.4 0 0 0 8.9 20.9 H 15.1 A 1.4 1.4 0 0 0 16.5 19.5 L 17.4 6.6 " +
        "M 10.1 10 V 17 M 13.9 10 V 17");

    public static Geometry Refresh { get; } = Freeze(
        "M 21.5 3.6 L 21.5 9.2 L 15.9 9.2 " +
        "M 19.1 14.4 A 8 8 0 1 1 17.2 6.1 L 21.5 9.2");

    public static Geometry Search { get; } = Freeze(
        "M 10.6 4.2 A 6.4 6.4 0 1 1 10.6 17 A 6.4 6.4 0 1 1 10.6 4.2 Z " +
        "M 15.4 15.4 L 20.4 20.4");

    public static Geometry Download { get; } = Freeze(
        "M 12 3.4 V 14.8 M 7.6 10.4 L 12 14.8 L 16.4 10.4 M 4 17.6 V 20.4 H 20 V 17.6");

    public static Geometry Restore { get; } = Freeze(
        "M 12 15.4 V 4 M 7.6 8.4 L 12 4 L 16.4 8.4 M 4 20.4 H 20");

    public static Geometry Save { get; } = Freeze(
        "M 4 5 H 15.6 L 20 9.4 V 19 H 4 Z " +
        "M 7.8 5 V 10.2 H 15.4 V 5 " +
        "M 7.8 19 V 13.6 H 16.2 V 19");

    /// <summary>
    /// Play and pause are drawn as outlines rather than taken as emoji. U+25B6 and
    /// U+23F8 are geometric glyphs with no colour to lose, and a stroke keeps them
    /// sharp at any size and pickable from the tab accent. The triangle is nudged
    /// to sit optically centred rather than mathematically centred: a right angle
    /// triangle reads as light on the left, so the bounding box centre leaves it
    /// looking pushed right at small sizes.
    /// </summary>
    public static Geometry Play { get; } = Freeze(
        "M 7.4 4.9 L 19.4 12 L 7.4 19.1 Z");

    /// <summary>
    /// The two bars are 2.8 wide with a 3.2 gap. They used to be 3.3 wide with a
    /// 2.2 gap, which is the one proportion that makes a pause mark read wrong:
    /// the gap has to be at least as wide as the bars, or the eye joins them into
    /// one heavy slab instead of two strokes. The bounding box is unchanged, so
    /// the centring below still lands where it did.
    /// </summary>
    public static Geometry Pause { get; } = Freeze(
        "M 7.6 5.2 H 10.4 V 18.8 H 7.6 Z M 13.6 5.2 H 16.4 V 18.8 H 13.6 Z");

    /// <summary>
    /// The nudge that puts each glyph's ink in the middle of its button, in the
    /// same units as the button itself, so a correction here is the same number
    /// the measurement that produced it reported.
    /// <para>
    /// These are measured on the finished button, not on the raw geometry, and the
    /// difference matters. Measuring the geometry alone had put every icon about
    /// two pixels down and to the right, because the bounding box had been centred
    /// on the canvas while its origin was left where the shape happened to start,
    /// and the Viewbox centres the box rather than the ink inside it. That is fixed
    /// in <see cref="OriginAtZero"/>, and what is left here is only the part that
    /// is genuinely optical: the gap between the middle of a shape and the middle
    /// of the mass of its ink.
    /// </para>
    /// <para>
    /// A magnifier is a ring with a thin handle, so nearly all of its mass sits up
    /// and to the left and it was the worst of the set. A plus is symmetric and
    /// needs nothing. Every number here was read off a render of the real control
    /// with the real style, and the largest is under one pixel.
    /// </para>
    /// </summary>
    private static readonly Dictionary<Geometry, Point> OpticalOffsets = new(ReferenceEqualityComparer.Instance)
    {
        // Symmetric, and measured dead on. Nothing to correct.
        [Plus] = new Point(0.00, 0.00),

        // The point and the ferrule sit up and left of the shaft's middle.
        [Pencil] = new Point(0.28, 0.48),

        // The lid handle is thin and the body is not, so the mass sits low.
        [Trash] = new Point(0.00, 0.49),

        // The arrow head is heavier than the tail it sweeps from.
        [Refresh] = new Point(0.28, 0.93),

        // The ring carries nearly all the mass and the handle almost none, so the
        // mark wants to sit down and right of the middle of its own box.
        [Search] = new Point(0.82, 0.83),

        // The wide baseline stroke is a large fraction of the ink, all of it low.
        [Download] = new Point(0.38, -2.56),

        // Arrow head above, short baseline below.
        [Restore] = new Point(-0.01, -0.53),

        // The page corner pulls the mass up and to the left.
        [Save] = new Point(0.71, 0.13),

        // Play and pause are the two states of one button and get the same nudge
        // on purpose. Play's ink is 2.46 pixels left of centre, pause's only 0.44,
        // and nudging each to its own number would make the icon jump sideways
        // every time the user pressed play. Averaging them puts both within about
        // a pixel of true centre and, more importantly, keeps them still relative
        // to each other. A triangle two pixels heavy is a better trade than an
        // icon that hops on every click.
        [Play] = new Point(1.45, 0.43),
        [Pause] = new Point(1.45, 0.43),
    };
    /// <summary>
    /// The correction that puts <paramref name="geometry"/>'s visual mass in the
    /// middle of its bounding box, in canvas units. Zero for anything not in the
    /// table, and for a null geometry, which is the un-set case.
    /// </summary>
    public static Point OpticalOffset(Geometry? geometry)
    {
        if (geometry is null)
        {
            return default;
        }

        return OpticalOffsets.TryGetValue(geometry, out Point offset) ? offset : default;
    }

    private static Geometry Freeze(string figures)
    {
        Geometry geometry = Geometry.Parse(figures);
        geometry = OriginAtZero(geometry);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Moves a glyph so its own bounding box starts at the canvas origin.
    /// <para>
    /// This used to centre the box on the middle of a 24 unit canvas instead, and
    /// that was the cause of every icon sitting low and to the right in its button.
    /// A Viewbox centres the desired size box of what it draws, not the ink inside
    /// it, and the desired box starts wherever the geometry's coordinates start.
    /// Centring the box on twelve left its origin at five, so the geometry sat five
    /// units down and right inside the very box being centred, and the glyph came
    /// out roughly two pixels off at a 30 pixel button. That offset was identical
    /// on every glyph, which is what gave it away: optical centring never shifts
    /// everything the same way.
    /// </para>
    /// <para>
    /// Putting the origin at zero means the shape exactly fills the box it is
    /// measured in, so centring the box and centring the ink are the same act.
    /// </para>
    /// </summary>
    private static Geometry OriginAtZero(Geometry geometry)
    {
        Rect bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return geometry;
        }

        if (Math.Abs(bounds.X) < 0.01 && Math.Abs(bounds.Y) < 0.01)
        {
            return geometry;
        }

        var move = new TransformGroup();
        move.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));

        var group = new GeometryGroup();
        group.Children.Add(geometry);
        group.Transform = move;
        return group;
    }
}
