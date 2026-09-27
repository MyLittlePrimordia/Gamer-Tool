using System;
using System;
using System.Windows;
using System.Windows.Media;

namespace GamerTool.UI;

/// <summary>
/// Stroke outlines for the small action buttons. Emoji are great for the big
/// identity marks (the nav tabs, the option rows, play and pause) but a handful
/// of glyphs like the wastebasket fall back to a broken box at button sizes, and
/// a vector stays crisp and pickable from the tab accent colour.
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
    /// sharp at any size and pickable from the tab accent.
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

    /// <summary>The canvas every glyph is authored on, and the box they are centred in.</summary>
    private const double Canvas = 24.0;

    private static Geometry Freeze(string figures)
    {
        Geometry geometry = Geometry.Parse(figures);
        geometry = CentreOnCanvas(geometry);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Nudges a glyph so its own bounding box sits in the middle of the canvas.
    /// Hand drawn outlines rarely have symmetric bounds: the refresh arrow runs
    /// up and to the right, the pencil hangs low and left, and the play triangle
    /// is lopsided by nature. A button centres the bounding box rather than the
    /// shape, so without this the odd ones sit visibly off inside their outline.
    /// </summary>
    private static Geometry CentreOnCanvas(Geometry geometry)
    {
        Rect bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return geometry;
        }

        double dx = (Canvas / 2.0) - (bounds.X + (bounds.Width / 2.0));
        double dy = (Canvas / 2.0) - (bounds.Y + (bounds.Height / 2.0));
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            return geometry;
        }

        var move = new TransformGroup();
        move.Children.Add(new TranslateTransform(dx, dy));

        var group = new GeometryGroup();
        group.Children.Add(geometry);
        group.Transform = move;
        return group;
    }
}
