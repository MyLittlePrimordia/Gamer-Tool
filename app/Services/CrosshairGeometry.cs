using System;
using System.Windows;
using System.Windows.Media;
using GamerTool.Models;
using Point = System.Windows.Point;

namespace GamerTool.Services;

/// <summary>
/// The outlines of the six crosshair shapes, as geometry centred on the origin
/// and measured in screen pixels.
/// <para>
/// Pure and free of any window: the preview on the tab and the overlay on the
/// screen both draw what this returns, so the two cannot disagree about what a
/// shape is. Everything is rounded to whole pixels before it is drawn, because a
/// one pixel line whose end lands half way between two pixels is drawn as two
/// grey ones and a crosshair that looks soft is a crosshair nobody uses.
/// </para>
/// </summary>
public static class CrosshairGeometry
{
    /// <summary>Room left round the shape for the glow and the outline.</summary>
    private const int Margin = 12;

    /// <summary>
    /// How far a turned shape can reach, as a multiple of its upright reach: the
    /// diagonal of its square, which is the square root of two (1.414...), rounded
    /// up so the window is never a pixel short.
    /// </summary>
    private const double TurnedReach = 1.42;

    /// <summary>
    /// The side of the overlay window, in pixels. Always even, so the centre falls
    /// on a pixel boundary the same way the middle of an even-width monitor does.
    /// </summary>
    public static int WindowSide(CrosshairPreset preset)
    {
        // A turned crosshair reaches further from the middle than an upright one,
        // up to the corner of its square, so the window grows to hold it.
        double reach = preset.Rotation % 90 == 0 ? 1.0 : TurnedReach;
        int half = (int)Math.Ceiling(preset.Size / 2.0 * reach);
        int side = 2 * (half + preset.Thickness + Margin);
        if (side % 2 != 0)
        {
            side++;
        }

        return Math.Max(side, 48);
    }

    /// <summary>
    /// Stroke geometry for every shape except the dot. Draw it with a pen the
    /// width of <see cref="CrosshairPreset.Thickness"/> and no fill.
    /// </summary>
    public static Geometry Strokes(CrosshairPreset preset)
    {
        int t = preset.Thickness;
        int half = Math.Max(3, (int)Math.Round(preset.Size / 2.0));

        // Odd widths are centred half a pixel off so the line covers whole
        // pixels. Even widths already do.
        double o = t % 2 == 1 ? 0.5 : 0.0;

        Geometry geometry = preset.ShapeKind switch
        {
            CrosshairShape.X => Diagonals(half, preset.Gap),
            CrosshairShape.Circle => new EllipseGeometry(new Point(o, o), Math.Max(1.5, half - t / 2.0), Math.Max(1.5, half - t / 2.0)),
            CrosshairShape.Square => Box(half, t, o),
            CrosshairShape.Chevrons => Chevrons(half, preset.Gap),
            CrosshairShape.Triangle => Triangle(half),
            CrosshairShape.T => TShape(half, preset.Gap, o),
            _ => Arms(half, preset.Gap, o)
        };

        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    /// <summary>
    /// The filled disc for <see cref="CrosshairShape.Dot"/>, and the radius used
    /// by its glow and outline.
    /// </summary>
    public static double DotRadius(CrosshairPreset preset)
    {
        // Size alone decides it. Thickness means nothing to a disc, and the tab
        // greys the slider out for it.
        double diameter = Math.Max(2.0, Math.Round(preset.Size / 3.0));
        return diameter / 2.0;
    }

    /// <summary>The radius of the little dot that can sit at the middle of any crosshair.</summary>
    public static double CenterDotRadius(CrosshairPreset preset)
    {
        return Math.Max(1.0, (preset.Thickness + 1) / 2.0);
    }

    private static Geometry Arms(int half, int gap, double o)
    {
        // The gap can never swallow the arm: at least two pixels of line remain.
        int g = Math.Clamp(gap, 0, Math.Max(0, half - 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Segment(ctx, new Point(-half, o), new Point(-g, o));
            Segment(ctx, new Point(g, o), new Point(half, o));
            Segment(ctx, new Point(o, -half), new Point(o, -g));
            Segment(ctx, new Point(o, g), new Point(o, half));
        }

        return geometry;
    }

    private static Geometry TShape(int half, int gap, double o)
    {
        // Left, right and down. Leaving the top arm off is the whole point of the
        // shape: nothing is drawn over the head you are aiming at.
        int g = Math.Clamp(gap, 0, Math.Max(0, half - 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Segment(ctx, new Point(-half, o), new Point(-g, o));
            Segment(ctx, new Point(g, o), new Point(half, o));
            Segment(ctx, new Point(o, g), new Point(o, half));
        }

        return geometry;
    }

    private static Geometry Triangle(int half)
    {
        // Pointing up, an open outline, a little wider than it is tall so it reads
        // as a triangle and not as a spike. Its middle is close to the middle of
        // the window, which is where the eye expects the aim point to be.
        double apexY = -half;
        double baseY = Math.Round(half * 0.7);
        double reach = Math.Round(half * 0.95);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, apexY), false, true);
            ctx.LineTo(new Point(reach, baseY), true, true);
            ctx.LineTo(new Point(-reach, baseY), true, true);
        }

        return geometry;
    }

    private static Geometry Diagonals(int half, int gap)
    {
        // An X reaches less far along each axis than a cross of the same size,
        // or it looks larger than every other shape in the grid.
        int reach = Math.Max(3, (int)Math.Round(half * 0.8));
        int g = (int)Math.Round(gap * 0.7071);
        g = Math.Clamp(g, 0, Math.Max(0, reach - 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Segment(ctx, new Point(-g, -g), new Point(-reach, -reach));
            Segment(ctx, new Point(g, -g), new Point(reach, -reach));
            Segment(ctx, new Point(-g, g), new Point(-reach, reach));
            Segment(ctx, new Point(g, g), new Point(reach, reach));
        }

        return geometry;
    }

    private static Geometry Box(int half, int t, double o)
    {
        double side = Math.Max(3.0, Math.Round(half * 0.85));
        return new RectangleGeometry(new Rect(o - side, o - side, side * 2.0, side * 2.0));
    }

    private static Geometry Chevrons(int half, int gap)
    {
        // The tips point outward and the open sides face the middle: < >.
        // Size is the overall width, and the gap is the space between the
        // inner ends of the two arms.
        int g = Math.Clamp(gap, 0, Math.Max(0, half));
        int a = Math.Max(3, half - g / 2);
        double inner = g / 2.0;
        double tip = inner + a;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(-inner, -a), false, false);
            ctx.LineTo(new Point(-tip, 0), true, true);
            ctx.LineTo(new Point(-inner, a), true, true);

            ctx.BeginFigure(new Point(inner, -a), false, false);
            ctx.LineTo(new Point(tip, 0), true, true);
            ctx.LineTo(new Point(inner, a), true, true);
        }

        return geometry;
    }

    private static void Segment(StreamGeometryContext ctx, Point from, Point to)
    {
        ctx.BeginFigure(from, false, false);
        ctx.LineTo(to, true, false);
    }
}
