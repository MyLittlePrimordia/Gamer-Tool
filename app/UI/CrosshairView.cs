using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace GamerTool.UI;

/// <summary>
/// Draws one crosshair at the middle of itself, and nothing else.
/// <para>
/// It is the same element on the tab, where it sits over the preview picture, and
/// in the overlay window on the screen, so the preview is the real drawing code
/// and not an imitation of it.
/// </para>
/// <para>
/// This is what keeps the feature light. It is a single <c>OnRender</c>: no
/// timers, no animations, no bitmap effects, no per-frame work. WPF retains what
/// was drawn and the element only redraws when a setting changes, so a crosshair
/// that is sitting on the screen costs no CPU at all. The glow is a few wider
/// translucent strokes rather than a <c>DropShadowEffect</c>, which would put a
/// software effect pass on a window that is otherwise a handful of lines.
/// </para>
/// </summary>
public sealed class CrosshairView : FrameworkElement
{
    public static readonly DependencyProperty PresetProperty = DependencyProperty.Register(
        nameof(Preset),
        typeof(CrosshairPreset),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image),
        typeof(ImageSource),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MagnificationProperty = DependencyProperty.Register(
        nameof(Magnification),
        typeof(double),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public CrosshairView()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>What to draw. Set a new instance, or call <see cref="InvalidateVisual"/> after editing one.</summary>
    public CrosshairPreset? Preset
    {
        get => (CrosshairPreset?)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    /// <summary>The picture for <see cref="CrosshairShape.Custom"/>.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>
    /// Pretend the display is at this scale (1.0 is 96 dpi) instead of asking the
    /// window. Used only when the crosshair is drawn into a file, where there is
    /// no window to ask and the answer has to be the same on every machine. Zero,
    /// the normal case, means ask.
    /// </summary>
    public double AssumeDpi { get; set; }

    /// <summary>
    /// How many device pixels one crosshair pixel becomes. 1 on the screen, where
    /// a ten pixel arm has to be ten pixels. The tab's preview uses a larger
    /// number so the detail is visible in a small box.
    /// </summary>
    public double Magnification
    {
        get => (double)GetValue(MagnificationProperty);
        set => SetValue(MagnificationProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        CrosshairPreset? preset = Preset;
        if (preset is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Layout units are device independent and the crosshair is measured in
        // real pixels, so undo the window's DPI scale here. A 24 pixel crosshair
        // is 24 pixels on a 150 percent monitor as well.
        double dpi = AssumeDpi > 0 ? AssumeDpi : VisualTreeHelper.GetDpi(this).DpiScaleX;
        double magnification = Math.Max(0.1, Magnification);
        double scale = magnification / (dpi <= 0 ? 1.0 : dpi);

        dc.PushTransform(new TranslateTransform(ActualWidth / 2.0, ActualHeight / 2.0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushOpacity(Math.Clamp(preset.Opacity, CrosshairPreset.OpacityMin, CrosshairPreset.OpacityMax) / 100.0);

        // The turn applies to the shape or picture and not to the centre dot, which
        // sits on the pivot and looks the same whichever way it is turned.
        bool turned = preset.Rotation != 0;
        if (turned)
        {
            dc.PushTransform(new RotateTransform(preset.Rotation));
        }

        if (preset.ShapeKind == CrosshairShape.Custom)
        {
            DrawImage(dc, preset, magnification);
        }
        else if (preset.ShapeKind == CrosshairShape.Dot)
        {
            DrawDot(dc, preset, CrosshairGeometry.DotRadius(preset));
        }
        else
        {
            DrawStrokes(dc, preset);
        }

        if (turned)
        {
            dc.Pop();
        }

        if (preset.CenterDot && preset.ShapeKind != CrosshairShape.Dot)
        {
            DrawDot(dc, preset, CrosshairGeometry.CenterDotRadius(preset));
        }

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private BitmapSource? _composed;

    private double _composedSide;

    private string _composedKey = string.Empty;

    private void DrawImage(DrawingContext dc, CrosshairPreset preset, double magnification)
    {
        if (Image is not BitmapSource source)
        {
            return;
        }

        // Rebuilt only when something that shows in it has changed. OnRender runs
        // when a setting moves and not otherwise, so this is not a per-frame cost,
        // and the key makes a repeated redraw of the same crosshair free as well.
        string key = string.Join(
            "|",
            RuntimeHelpers.GetHashCode(source),
            preset.Color,
            preset.Size,
            preset.Thickness,
            preset.Outline,
            preset.Glow,
            magnification.ToString("0.###", CultureInfo.InvariantCulture));

        if (_composed is null || !string.Equals(key, _composedKey, StringComparison.Ordinal))
        {
            _composed = CrosshairImageComposer.Compose(source, preset, magnification, out _composedSide);
            _composedKey = key;
        }

        if (_composed is null)
        {
            return;
        }

        double half = _composedSide / 2.0;
        dc.DrawImage(_composed, new Rect(-half, -half, _composedSide, _composedSide));
    }

    private static void DrawDot(DrawingContext dc, CrosshairPreset preset, double r)
    {
        Color colour = ColourOf(preset);
        var origin = new Point(0, 0);

        if (preset.Glow)
        {
            foreach ((double extra, byte alpha) in GlowPasses)
            {
                dc.DrawEllipse(Frozen(Color.FromArgb(alpha, colour.R, colour.G, colour.B)), null, origin, r + extra, r + extra);
            }
        }

        if (preset.Outline)
        {
            dc.DrawEllipse(Frozen(Colors.Black), null, origin, r + OutlineWidth(preset), r + OutlineWidth(preset));
        }

        dc.DrawEllipse(Frozen(colour), null, origin, r, r);
    }

    private static void DrawStrokes(DrawingContext dc, CrosshairPreset preset)
    {
        Color colour = ColourOf(preset);
        Geometry shape = CrosshairGeometry.Strokes(preset);
        double t = preset.Thickness;

        if (preset.Glow)
        {
            foreach ((double extra, byte alpha) in GlowPasses)
            {
                dc.DrawGeometry(null, StrokePen(Color.FromArgb(alpha, colour.R, colour.G, colour.B), t + (extra * 2.0), PenLineCap.Round), shape);
            }
        }

        if (preset.Outline)
        {
            // Square caps push the black a little past the end of each arm, which
            // is what gives the tip an edge as well as the sides.
            dc.DrawGeometry(null, StrokePen(Colors.Black, t + (OutlineWidth(preset) * 2.0), PenLineCap.Square), shape);
        }

        dc.DrawGeometry(null, StrokePen(colour, t, PenLineCap.Flat), shape);
    }

    /// <summary>How far each glow pass reaches past the line, and how strong it is. Widest first.</summary>
    private static readonly (double Extra, byte Alpha)[] GlowPasses =
    {
        (5.0, 0x1C),
        (3.0, 0x2E),
        (1.5, 0x4A)
    };

    private static double OutlineWidth(CrosshairPreset preset) => preset.Thickness >= 4 ? 1.5 : 1.0;

    private static Color ColourOf(CrosshairPreset preset)
    {
        return CrosshairPreset.TryParseColor(preset.Color, out byte r, out byte g, out byte b)
            ? Color.FromRgb(r, g, b)
            : Color.FromRgb(0x3C, 0xFF, 0x5A);
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Pen StrokePen(Color colour, double width, PenLineCap cap)
    {
        var pen = new Pen(Frozen(colour), width)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = cap == PenLineCap.Round ? PenLineJoin.Round : PenLineJoin.Miter,
            MiterLimit = 4.0
        };

        pen.Freeze();
        return pen;
    }
}
