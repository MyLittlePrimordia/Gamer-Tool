using System;
using System.Collections.Generic;
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
/// <summary>
/// A point on the edge of a crosshair, and which way is outward from there.
/// <para>
/// What the effects that throw something off the crosshair need: sparks leave from these
/// and travel along <see cref="Degrees"/>, so they come off a cross's tips, a ring's rim
/// and a picture's border rather than out of the empty middle. Degrees is clockwise from
/// three o'clock, the same convention as the rotation slider.
/// </para>
/// </summary>
public readonly record struct CrosshairAnchor(Point At, double Degrees);

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
        // up to the corner of its square, so the window grows to hold it. A spinning
        // one is turned at every angle it will ever be drawn at, so it is always sized
        // for the diagonal rather than for the angle it happens to be resting on.
        bool turned = preset.Rotation % 90 != 0 || preset.EffectKind == CrosshairEffect.Spin;
        double reach = turned ? TurnedReach : 1.0;

        // A pulse draws the whole thing bigger than its size, and sparks and a ripple
        // reach past the shape, so the window is sized for the largest frame the
        // effect can produce and not for the one on screen now. An effect that paints
        // outside its window is clipped, and a clipped crosshair looks like a bug in
        // the app rather than a setting on the tab.
        double growth = 1.0 + CrosshairAnimation.ScaleGrowth(preset);
        int half = (int)Math.Ceiling(preset.Size / 2.0 * reach * growth);
        int side = 2 * (half + preset.Thickness + Margin + CrosshairAnimation.ReachPx(preset));
        if (side % 2 != 0)
        {
            side++;
        }

        return Math.Max(side, 48);
    }

    /// <summary>
    /// Stroke geometry for every shape except the dot. Draw it with a pen the width of
    /// <see cref="CrosshairPreset.Thickness"/> and no fill.
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
    /// The points an effect throws things from: the tips of a cross, the corners of a
    /// box, the vertices of a triangle, the rim of a ring or a dot, the border of a
    /// picture. Worked out from the same numbers the geometry is drawn from, so a spark
    /// leaves the pixel the crosshair actually ends on.
    /// </summary>
    public static IReadOnlyList<CrosshairAnchor> Anchors(CrosshairPreset preset)
    {
        var anchors = new List<CrosshairAnchor>();
        int t = preset.Thickness;
        int half = Math.Max(3, (int)Math.Round(preset.Size / 2.0));

        void Add(double x, double y)
        {
            anchors.Add(new CrosshairAnchor(new Point(x, y), Math.Atan2(y, x) * 180.0 / Math.PI));
        }

        switch (preset.ShapeKind)
        {
            case CrosshairShape.Cross:
                Add(-half, 0);
                Add(half, 0);
                Add(0, -half);
                Add(0, half);
                break;

            case CrosshairShape.T:
                Add(-half, 0);
                Add(half, 0);
                Add(0, half);
                break;

            case CrosshairShape.X:
                int reach = Math.Max(3, (int)Math.Round(half * 0.8));
                Add(-reach, -reach);
                Add(reach, -reach);
                Add(-reach, reach);
                Add(reach, reach);
                break;

            case CrosshairShape.Circle:
                OnRim(anchors, Math.Max(1.5, half - (t / 2.0)), 8);
                break;

            case CrosshairShape.Square:
                double side = Math.Max(3.0, Math.Round(half * 0.85));
                Add(-side, -side);
                Add(side, -side);
                Add(-side, side);
                Add(side, side);
                break;

            case CrosshairShape.Chevrons:
                int g = Math.Clamp(preset.Gap, 0, Math.Max(0, half));
                double inner = g / 2.0;
                double a = Math.Max(3, half - (g / 2));
                Add(-inner - a, 0);
                Add(inner + a, 0);
                Add(-inner, -a);
                Add(-inner, a);
                Add(inner, -a);
                Add(inner, a);
                break;

            case CrosshairShape.Triangle:
                double wide = Math.Round(half * 0.95);
                Add(0, -half);
                Add(wide, Math.Round(half * 0.7));
                Add(-wide, Math.Round(half * 0.7));
                break;

            case CrosshairShape.Dot:
                OnRim(anchors, DotRadius(preset), 8);
                break;

            default:
                // A picture has no geometry of its own, so its border is the edge of the
                // box it is drawn in - which is where a spark leaving it should start.
                double edge = Math.Max(4.0, half);
                OnRim(anchors, edge, 8);
                break;
        }

        return anchors;
    }

    /// <summary>
    /// One band of a ripple: the shape's own outline pushed outward by
    /// <paramref name="distance"/> and <paramref name="width"/> pixels, with the
    /// inside cut away.
    /// <para>
    /// Pushed outward and not scaled. A copy of a cross scaled up lies on the same two
    /// lines as the cross and is only seen as its arms getting longer, which is what
    /// made the first version of this look like nothing at all; a band offset from the
    /// ink is a band whatever the shape is, and it keeps the shape's form, so a cross
    /// sends out a cross and a box a box.
    /// </para>
    /// <para>
    /// Null for a picture, which has no geometry to push outward; the view grows a copy
    /// of the picture for that one instead.
    /// </para>
    /// </summary>
    public static Geometry? RippleBand(CrosshairPreset preset, double distance, double width)
    {
        if (preset.ShapeKind == CrosshairShape.Custom || distance < 0 || width <= 0)
        {
            return null;
        }

        bool dot = preset.ShapeKind == CrosshairShape.Dot;
        double radius = Math.Max(1.0, DotRadius(preset));

        // A stroke is centred on its path, so a line of thickness t already reaches t/2
        // either side of it; the band is measured from there.
        double ink = dot ? 0 : Math.Min(preset.Thickness, 8);

        Geometry path = dot
            ? new EllipseGeometry(new Point(0, 0), radius, radius)
            : Strokes(preset);

        var band = new GeometryGroup { FillRule = FillRule.EvenOdd };
        band.Children.Add(Widen(path, ink + (2 * (distance + width))));

        if (distance > 0.05)
        {
            // The hole. At zero there is nothing to cut away and an outline drawn over
            // the aim point is the one thing a ripple may not do.
            band.Children.Add(Widen(path, ink + (2 * distance)));
        }

        if (band.CanFreeze)
        {
            band.Freeze();
        }

        return band;
    }

    private static Geometry Widen(Geometry path, double thickness)
    {
        // Round caps and round joins, so the band is the ink pushed outward in every
        // direction. A flat cap would leave the ends of a cross's arms where they were
        // and only thicken them, which is an outline rather than a wave leaving.
        var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, Math.Max(0.1, thickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        return path.GetWidenedPathGeometry(pen);
    }

    private static void OnRim(List<CrosshairAnchor> anchors, double radius, int count)
    {
        for (int i = 0; i < count; i++)
        {
            double degrees = 360.0 / count * i;
            double radians = degrees * Math.PI / 180.0;
            anchors.Add(new CrosshairAnchor(
                new Point(Math.Cos(radians) * radius, Math.Sin(radians) * radius), degrees));
        }
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

    /// <summary>
    /// The radius of the little dot that can sit at the middle of any crosshair. Its
    /// own size when it has one, the line's otherwise; the preset decides, so the
    /// number on the slider is the number of pixels drawn.
    /// </summary>
    public static double CenterDotRadius(CrosshairPreset preset)
    {
        return preset.EffectiveCenterDotSize() / 2.0;
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
