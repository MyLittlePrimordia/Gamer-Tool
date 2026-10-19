using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// bitmap effects and no per-frame work. WPF retains what was drawn and the
/// element only redraws when a setting changes, so a crosshair that is sitting on
/// the screen costs no CPU at all. The glow is a few wider translucent strokes
/// rather than a <c>DropShadowEffect</c>, which would put a software effect pass
/// on a window that is otherwise a handful of lines.
/// </para>
/// <para>
/// The one exception is an animated picture, which needs a clock. The clock exists
/// only while an animated picture is actually showing: it stops the moment the
/// element is hidden, unloaded or given anything else to draw, so the overlay
/// costs nothing while it is off the screen, and a still picture or a drawn shape
/// never starts one.
/// </para>
/// </summary>
public sealed class CrosshairView : FrameworkElement
{
    public static readonly DependencyProperty PresetProperty = DependencyProperty.Register(
        nameof(Preset),
        typeof(CrosshairPreset),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnClockInputChanged));

    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image),
        typeof(ImageSource),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(CrosshairPicture),
        typeof(CrosshairView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPictureChanged));

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

        // The clock follows visibility. A hidden overlay window never draws, but
        // nothing else would stop a timer that was already ticking.
        IsVisibleChanged += (s, e) => UpdateClock();
        Loaded += (s, e) => UpdateClock();
        Unloaded += (s, e) => UpdateClock();
    }

    private static void OnClockInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CrosshairView)d).UpdateClock();
    }

    private static void OnPictureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CrosshairView)d;
        view._frame = 0;
        view.UpdateClock();
    }

    /// <summary>What to draw. Set a new instance, or call <see cref="InvalidateVisual"/> after editing one.</summary>
    public CrosshairPreset? Preset
    {
        get => (CrosshairPreset?)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    /// <summary>A still picture for <see cref="CrosshairShape.Custom"/>. Ignored while <see cref="Picture"/> is set.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>
    /// The picture for <see cref="CrosshairShape.Custom"/>, still or animated. An
    /// animated one plays for as long as this element is on the screen.
    /// </summary>
    public CrosshairPicture? Picture
    {
        get => (CrosshairPicture?)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
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
        // Every redraw is a chance to notice the clock is running when it should
        // not be, or the other way round, after a setting changed what is showing.
        UpdateClock();

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

    /// <summary>Each frame of the picture after colour, thickness, gap, outline and glow, built the first time it is shown.</summary>
    private BitmapSource?[] _composedFrames = Array.Empty<BitmapSource?>();

    private double _composedSide;

    private string _composedKey = string.Empty;

    /// <summary>Which frame of an animation is showing.</summary>
    private int _frame;

    private DispatcherTimer? _clock;

    private void DrawImage(DrawingContext dc, CrosshairPreset preset, double magnification)
    {
        CrosshairPicture? picture = Picture;
        int frames = picture is null ? 1 : picture.Frames.Count;
        int index = Math.Clamp(_frame, 0, frames - 1);

        BitmapSource? source = picture is not null ? picture.Frames[index] : Image as BitmapSource;
        if (source is null)
        {
            return;
        }

        // Rebuilt only when something that shows in it has changed. OnRender runs
        // when a setting moves or an animation steps, so the key is what makes a
        // repeated redraw of the same frame free. Rotation and opacity are not in
        // it: they are applied as the picture is drawn and never change its pixels.
        string key = string.Join(
            "|",
            RuntimeHelpers.GetHashCode((object?)picture ?? Image),
            preset.Color,
            preset.Size,
            preset.Thickness,
            preset.PictureGap,
            preset.Outline,
            preset.Glow,
            magnification.ToString("0.###", CultureInfo.InvariantCulture));

        if (_composedFrames.Length != frames || !string.Equals(key, _composedKey, StringComparison.Ordinal))
        {
            _composedFrames = new BitmapSource?[frames];
            _composedKey = key;
        }

        // A frame is built when it first comes up, not all at once, so moving a
        // slider on an animation costs one frame's work per step instead of sixty.
        BitmapSource? composed = _composedFrames[index];
        if (composed is null)
        {
            composed = CrosshairImageComposer.Compose(source, preset, magnification, out double side);
            _composedFrames[index] = composed;
            if (composed is not null)
            {
                _composedSide = side;
            }
        }

        if (composed is null)
        {
            return;
        }

        double half = _composedSide / 2.0;
        dc.DrawImage(composed, new Rect(-half, -half, _composedSide, _composedSide));
    }

    /// <summary>
    /// Run the frame clock when, and only when, an animated picture is showing: a
    /// picture shape, with more than one frame, on an element that is loaded and
    /// visible. Anything else stops it, so a hidden overlay or a still picture holds
    /// no timer at all.
    /// </summary>
    private void UpdateClock()
    {
        bool needed = Picture is { IsAnimated: true }
            && Preset?.ShapeKind == CrosshairShape.Custom
            && IsLoaded
            && IsVisible;

        if (!needed)
        {
            _clock?.Stop();
            return;
        }

        if (_clock is null)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _clock.Tick += OnClockTick;
        }

        if (!_clock.IsEnabled)
        {
            _clock.Interval = FrameDelay(Picture!, _frame);
            _clock.Start();
        }
    }

    private static TimeSpan FrameDelay(CrosshairPicture picture, int frame)
    {
        int at = Math.Clamp(frame, 0, picture.DelaysMs.Count - 1);
        return TimeSpan.FromMilliseconds(Math.Max(AnimationPlan.MinDelayMs, picture.DelaysMs[at]));
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        CrosshairPicture? picture = Picture;
        if (picture is null || !picture.IsAnimated || _clock is null)
        {
            _clock?.Stop();
            return;
        }

        _frame = (_frame + 1) % picture.Frames.Count;
        _clock.Interval = FrameDelay(picture, _frame);
        InvalidateVisual();
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
