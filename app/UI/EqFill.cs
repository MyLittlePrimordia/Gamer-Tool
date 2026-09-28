using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace GamerTool.UI;

/// <summary>
/// The filled area behind the equaliser curve.
/// <para>
/// This is split out of the window because it is the part of the equaliser that
/// is hardest to judge from code. It used to be built inline, which meant the only
/// way to see whether it looked right was to launch the app, and the app needs
/// more width than a laptop screen has. Here it can be rendered on its own and
/// looked at.
/// </para>
/// <para>
/// The shape is the region between the curve and the bottom of the band area, and
/// the fade runs down the full height of that region: strongest at the top, gone
/// by the bottom. Both halves matter. Filling only down to the zero line loses the
/// tall glow under a deep cut, and fading from the zero line upward puts the
/// brightest colour exactly where the curve is doing nothing. The area above the
/// curve is never filled, which is what stops it reading as a wall of colour
/// behind the top of the graph.
/// </para>
/// </summary>
public static class EqFill
{
    /// <summary>
    /// Alpha at the top of the band area, 92 of 255, which is 36 percent.
    /// <para>
    /// It was 212, which is 83 percent, and that turned the area under a boost into
    /// a solid slab of pink with the curve line drawn on top of it. The point of
    /// the wash is to sit behind the curve and give it something to read against,
    /// not to be the loudest thing in the panel. At 36 percent a +12 band is
    /// clearly tinted and a -12 band is barely there, which is the right way round
    /// and is close to how the engine draws it.
    /// </para>
    /// </summary>
    private const byte TopAlpha = 0x5C;

    /// <summary>The pink the equalizer uses everywhere else.</summary>
    private static readonly Color Tint = Color.FromRgb(0xFF, 0x2D, 0x6F);

    /// <summary>
    /// The closed outline of the fill, or null when there is nothing to fill.
    /// </summary>
    /// <param name="points">The curve, left to right.</param>
    /// <param name="bandTop">Top of the band area, in the same space as the points.</param>
    /// <param name="bandBottom">Bottom of the band area.</param>
    /// <param name="zeroY">Where the flat curve sits, used only to decide if there is a fill at all.</param>
    public static Geometry? BuildOutline(IReadOnlyList<Point> points, double bandTop, double bandBottom, double zeroY)
    {
        if (points.Count < 2)
        {
            return null;
        }

        // A flat curve has nothing between it and the zero line, so a fill would
        // be a straight edge with no area under it. The engine shows a flat band
        // as a dashed line and no fill, and so does this.
        bool offZero = false;
        foreach (Point point in points)
        {
            if (Math.Abs(point.Y - zeroY) > 0.5)
            {
                offZero = true;
                break;
            }
        }

        if (!offZero)
        {
            return null;
        }

        var figure = new PathFigure { IsClosed = true, IsFilled = true, StartPoint = points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            figure.Segments.Add(new LineSegment(points[i], true));
        }

        // Down to the bottom of the band area and back along it, so the fill is
        // everything under the curve rather than only what is above the zero line.
        figure.Segments.Add(new LineSegment(new Point(points[^1].X, bandBottom), true));
        figure.Segments.Add(new LineSegment(new Point(points[0].X, bandBottom), true));
        figure.Segments.Add(new LineSegment(points[0], true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The fade for <see cref="BuildOutline"/>. Absolute mapping, anchored to the
    /// band area, so it stays put while the bands move. A relative brush would
    /// measure its stops against the fill's own bounding box, which changes shape
    /// as the curve moves, and the fade would crawl with it.
    /// </summary>
    public static Brush BuildBrush(double bandTop, double bandBottom)
    {
        if (bandBottom - bandTop < 1.0)
        {
            // A band area with no height would divide by nothing and come out flat.
            return new SolidColorBrush(Color.FromArgb(0, Tint.R, Tint.G, Tint.B));
        }

        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(TopAlpha, Tint.R, Tint.G, Tint.B), 0.0),

                // A touch above a straight line, so the upper half carries more of
                // it. That is where the eye is, and where the curve usually is.
                new GradientStop(Color.FromArgb(0x2C, Tint.R, Tint.G, Tint.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, Tint.R, Tint.G, Tint.B), 1.0),
            },
            new Point(0, bandTop),
            new Point(0, bandBottom))
        {
            MappingMode = BrushMappingMode.Absolute,
        };

        brush.Freeze();
        return brush;
    }
}
