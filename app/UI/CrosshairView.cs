using System;
using System.Diagnostics;
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

        // Everything an effect does to this frame, worked out from the preset and the
        // clock and from nothing else. Still is the identity, so a crosshair with no
        // effect takes exactly the path it took before effects existed.
        CrosshairFrame frame = CrosshairAnimation.At(preset, PhaseSeconds);

        Color colour = ColourOf(preset);
        Color dotColour = CenterDotColourOf(preset);
        _cycledTint = null;

        // Asked of the effect and not of the hue: a frame whose hue happens to be exactly
        // zero is still a frame of the cycle, and for a white crosshair it is the
        // difference between white and the red the cycle starts from.
        if (preset.EffectKind == CrosshairEffect.ColourCycle)
        {
            double hue = frame.Hue;
            if (preset.ShapeKind == CrosshairShape.Custom)
            {
                // A picture has to be rebuilt for every colour it is drawn in, so its
                // colour moves in steps rather than at all of the sixty a second.
                hue = Math.Round(hue / HueStepDegrees) * HueStepDegrees;
            }

            colour = Cycled(colour, hue);
            dotColour = Cycled(dotColour, hue);

            // A picture that keeps its own colours only has its glow to paint, so it
            // is only worth rebuilding when there is one.
            if (preset.ShapeKind == CrosshairShape.Custom && (!preset.KeepOwnColours || preset.Glow))
            {
                _cycledTint = colour;
            }
        }

        dc.PushTransform(new TranslateTransform(ActualWidth / 2.0, ActualHeight / 2.0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushOpacity(
            Math.Clamp(preset.Opacity, CrosshairPreset.OpacityMin, CrosshairPreset.OpacityMax) / 100.0
            * frame.Opacity);

        bool swollen = Math.Abs(frame.Scale - 1) > 0.0001;
        if (swollen)
        {
            dc.PushTransform(new ScaleTransform(frame.Scale, frame.Scale));
        }

        // Behind the shape: the bands a ripple sends out, drawn first so the crosshair
        // itself sits on top of them and the aim point stays the clearest thing here.
        if (frame.Ripples.Count > 0)
        {
            DrawRipples(dc, preset, colour, frame, magnification);
        }

        // The turn applies to the shape or picture and not to the centre dot, which
        // sits on the pivot and looks the same whichever way it is turned.
        double turn = preset.Rotation + frame.Rotation;
        bool turned = Math.Abs(turn) > 0.001;
        if (turned)
        {
            dc.PushTransform(new RotateTransform(turn));
        }

        DrawShape(dc, preset, colour, magnification);

        // The sheen is over the ink, so it reads as light on the shape rather than as
        // something behind it, and under the sparks.
        if (frame.SheenAlpha > 0)
        {
            DrawSheen(dc, preset, frame, magnification);
        }

        if (frame.Dots.Count > 0)
        {
            DrawDots(dc, colour, frame);
        }

        if (turned)
        {
            dc.Pop();
        }

        if (preset.CenterDot && preset.ShapeKind != CrosshairShape.Dot)
        {
            // Its own colour when it has one. The two are separate settings because
            // the dot is the part that marks the exact pixel you are aiming at, and a
            // dot lost in a crosshair of the same colour is the whole reason to want it
            // another one.
            DrawDot(dc, preset, CrosshairGeometry.CenterDotRadius(preset), dotColour);
        }

        if (swollen)
        {
            dc.Pop();
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

    /// <summary>
    /// The colour a picture is composed in this frame, when a colour cycle is moving it
    /// off the preset's own. Null means the preset's colour.
    /// </summary>
    private Color? _cycledTint;

    /// <summary>How far apart the colours a cycling picture is rebuilt in are, in degrees of hue.</summary>
    private const double HueStepDegrees = 12.0;

    private DispatcherTimer? _clock;

    /// <summary>
    /// Runs while anything is moving, and is the clock both the effects and an
    /// animated picture read. A stopwatch rather than a counter: a timer that fires
    /// late - which a DispatcherTimer does the moment the UI is busy - must not make
    /// an effect run slow, only late.
    /// </summary>
    private readonly Stopwatch _phaseClock = new();

    /// <summary>When the next frame of an animated picture is due, in milliseconds on <see cref="_phaseClock"/>.</summary>
    private double _frameDueAt;

    /// <summary>
    /// Seconds into the animation. Zero whenever nothing is moving, including while the
    /// clock is stopped, so a crosshair that is off the screen always starts again from
    /// its first frame rather than from wherever it was left.
    /// </summary>
    private double PhaseSeconds => _phaseClock.IsRunning ? _phaseClock.Elapsed.TotalSeconds : 0;

    private void DrawImage(DrawingContext dc, CrosshairPreset preset, double magnification)
    {
        if (!TryCompose(preset, magnification, out BitmapSource composed, out double side))
        {
            return;
        }

        double half = side / 2.0;
        dc.DrawImage(composed, new Rect(-half, -half, side, side));
    }

    /// <summary>
    /// The composed picture for the frame that is showing, built and cached here rather
    /// than in every effect that needs to draw it. False when there is nothing to draw.
    /// </summary>
    private bool TryCompose(
        CrosshairPreset preset, double magnification, out BitmapSource composed, out double side)
    {
        composed = null!;
        side = 0;

        CrosshairPicture? picture = Picture;
        int frames = picture is null ? 1 : picture.Frames.Count;
        int index = Math.Clamp(_frame, 0, frames - 1);

        BitmapSource? source = picture is not null ? picture.Frames[index] : Image as BitmapSource;
        if (source is null)
        {
            return false;
        }

        // Rebuilt only when something that shows in it has changed. OnRender runs
        // when a setting moves or an animation steps, so the key is what makes a
        // repeated redraw of the same frame free. Rotation and opacity are not in
        // it: they are applied as the picture is drawn and never change its pixels.
        string colourKey = _cycledTint is { } tint
            ? "#" + tint.R.ToString("X2", CultureInfo.InvariantCulture)
                + tint.G.ToString("X2", CultureInfo.InvariantCulture)
                + tint.B.ToString("X2", CultureInfo.InvariantCulture)
            : preset.Color;

        string key = string.Join(
            "|",
            RuntimeHelpers.GetHashCode((object?)picture ?? Image),
            colourKey,
            preset.Size,
            preset.Thickness,
            preset.PictureGap,
            preset.Outline,
            preset.Glow,
            preset.KeepOwnColours,
            magnification.ToString("0.###", CultureInfo.InvariantCulture));

        if (_composedFrames.Length != frames || !string.Equals(key, _composedKey, StringComparison.Ordinal))
        {
            _composedFrames = new BitmapSource?[frames];
            _composedKey = key;
        }

        // A frame is built when it first comes up, not all at once, so moving a
        // slider on an animation costs one frame's work per step instead of sixty.
        BitmapSource? built = _composedFrames[index];
        if (built is null)
        {
            // The composer reads the colour from the preset, so a colour the cycle has
            // moved it to is put on a copy and the preset on the tab is left alone.
            CrosshairPreset composing = preset;
            if (_cycledTint is not null)
            {
                composing = preset.Copy();
                composing.Color = colourKey;
            }

            built = CrosshairImageComposer.Compose(
                source,
                composing,
                magnification,
                picture?.Content ?? default,
                out double builtSide);
            _composedFrames[index] = built;
            if (built is not null)
            {
                _composedSide = builtSide;
            }
        }

        if (built is null)
        {
            return false;
        }

        composed = built;
        side = _composedSide;
        return true;
    }

    /// <summary>
    /// Run the frame clock when, and only when, something is actually moving: an
    /// animated picture, or a crosshair with an effect, on an element that is loaded
    /// and visible. Anything else stops it, so a hidden overlay, a still picture and a
    /// still crosshair hold no timer at all - which is what lets an effect be free
    /// while it is off the screen.
    /// </summary>
    private void UpdateClock()
    {
        CrosshairPreset? preset = Preset;
        bool effect = preset?.Animated == true;
        bool picture = Picture is { IsAnimated: true } && preset?.ShapeKind == CrosshairShape.Custom;

        if ((!effect && !picture) || !IsLoaded || !IsVisible)
        {
            StopClock();
            return;
        }

        if (_clock is null)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _clock.Tick += OnClockTick;
        }

        if (!_clock.IsEnabled)
        {
            _phaseClock.Restart();
            _frameDueAt = 0;

            // One rate whatever is moving. The interval used to be the first frame's own
            // delay for a picture, and a timer's interval is fixed once it is running, so
            // an animation whose frames last different times was played at the pace of
            // whichever came first. A frame is shown when its own delay is up, which the
            // tick checks; the tick itself only has to be often enough to catch it.
            _clock.Interval = EffectInterval;
            _clock.Start();
        }
    }

    /// <summary>
    /// Sixty a second, which is as fast as anything here needs to be drawn and slower
    /// than a monitor that will show it. A crosshair is a few dozen pixels in a window
    /// under a hundred on a side: measured here, a still frame takes about a quarter of
    /// a millisecond and a ripple frame about half a millisecond, because its bands have
    /// to be widened into outlines first. That is a couple of percent of one core while
    /// an effect is running and nothing at all when one is not, rather than the expense
    /// a full screen overlay would be.
    /// </summary>
    private static readonly TimeSpan EffectInterval = TimeSpan.FromMilliseconds(16);

    private void StopClock()
    {
        _clock?.Stop();
        _phaseClock.Reset();
        _frameDueAt = 0;
    }

    private static TimeSpan FrameDelay(CrosshairPicture picture, int frame)
    {
        int at = Math.Clamp(frame, 0, picture.DelaysMs.Count - 1);
        return TimeSpan.FromMilliseconds(Math.Max(AnimationPlan.MinDelayMs, picture.DelaysMs[at]));
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        CrosshairPreset? preset = Preset;
        if (preset is null || _clock is null)
        {
            StopClock();
            return;
        }

        bool effect = preset.Animated;
        CrosshairPicture? picture = Picture is { IsAnimated: true } && preset.ShapeKind == CrosshairShape.Custom
            ? Picture
            : null;

        if (!effect && picture is null)
        {
            StopClock();
            return;
        }

        // An effect redraws every tick. A picture only moves on when its own delay is
        // up, which is a different rate from the effect's and is why the two share a
        // clock instead of one interval driving both.
        double now = _phaseClock.Elapsed.TotalMilliseconds;
        bool advanced = false;
        if (picture is not null)
        {
            if (_frameDueAt <= 0)
            {
                _frameDueAt = now + FrameDelay(picture, _frame).TotalMilliseconds;
            }
            else if (now >= _frameDueAt)
            {
                _frame = (_frame + 1) % picture.Frames.Count;
                _frameDueAt = now + FrameDelay(picture, _frame).TotalMilliseconds;
                advanced = true;
            }
        }

        // An effect changes every tick; a picture only when its frame has changed, so a
        // slow animation does not redraw sixty times a second to show the same image.
        if (effect || advanced)
        {
            InvalidateVisual();
        }
    }

    private static void DrawDot(DrawingContext dc, CrosshairPreset preset, double r, Color colour)
    {
        var origin = new Point(0, 0);

        if (preset.Glow)
        {
            foreach ((double extra, byte alpha) in GlowPasses)
            {
                dc.DrawEllipse(
                    Frozen(Color.FromArgb(alpha, colour.R, colour.G, colour.B)),
                    null,
                    origin,
                    r + extra,
                    r + extra);
            }
        }

        if (preset.Outline)
        {
            dc.DrawEllipse(Frozen(Colors.Black), null, origin, r + OutlineWidth(preset), r + OutlineWidth(preset));
        }

        dc.DrawEllipse(Frozen(colour), null, origin, r, r);
    }

    private static void DrawStrokes(DrawingContext dc, CrosshairPreset preset, Color colour)
    {
        Geometry shape = CrosshairGeometry.Strokes(preset);
        double t = preset.Thickness;

        if (preset.Glow)
        {
            foreach ((double extra, byte alpha) in GlowPasses)
            {
                dc.DrawGeometry(
                    null,
                    StrokePen(Color.FromArgb(alpha, colour.R, colour.G, colour.B), t + (extra * 2.0), PenLineCap.Round),
                    shape);
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

    /// <summary>
    /// The small circles a frame draws: sparks on their way out, and the lights
    /// twinkling on the ends of the shape. One loop, because they are the same mark in
    /// two places.
    /// <para>
    /// <see cref="CrosshairDot.Lift"/> pulls a dot towards white. A light sitting on the
    /// crosshair in the crosshair's own colour would be invisible against the ink it is
    /// sitting on.
    /// </para>
    /// </summary>
    private static void DrawDots(DrawingContext dc, Color colour, CrosshairFrame frame)
    {
        foreach (CrosshairDot dot in frame.Dots)
        {
            byte alpha = (byte)Math.Clamp(Math.Round(dot.Alpha * 255), 0, 255);
            if (alpha == 0 || dot.Radius <= 0)
            {
                continue;
            }

            double lift = Math.Clamp(dot.Lift, 0, 1);
            Color lit = Color.FromArgb(
                alpha,
                (byte)Math.Round(colour.R + ((255 - colour.R) * lift)),
                (byte)Math.Round(colour.G + ((255 - colour.G) * lift)),
                (byte)Math.Round(colour.B + ((255 - colour.B) * lift)));

            // A travelling light gets a halo, so it reads as light even on a crosshair
            // drawn in the same colour: a flat dot on a line is just more line.
            if (lift > 0.01 && dot.Radius >= 0.8)
            {
                var halo = Color.FromArgb((byte)(alpha * 0.16), lit.R, lit.G, lit.B);
                dc.DrawEllipse(Frozen(halo), null, new Point(dot.X, dot.Y), dot.Radius * 2.1, dot.Radius * 2.1);
            }

            dc.DrawEllipse(Frozen(lit), null, new Point(dot.X, dot.Y), dot.Radius, dot.Radius);
        }
    }

    /// <summary>The shape, picture or dot the preset is.</summary>
    private void DrawShape(DrawingContext dc, CrosshairPreset preset, Color colour, double magnification)
    {
        if (preset.ShapeKind == CrosshairShape.Custom)
        {
            DrawImage(dc, preset, magnification);
        }
        else if (preset.ShapeKind == CrosshairShape.Dot)
        {
            DrawDot(dc, preset, CrosshairGeometry.DotRadius(preset), colour);
        }
        else
        {
            DrawStrokes(dc, preset, colour);
        }
    }

    /// <summary>
    /// A soft highlight crossing the shape: a gradient band clipped to the ink, so only
    /// the crosshair catches the light as it passes. A picture is masked by its own
    /// pixels, so the transparent parts of a PNG stay transparent.
    /// <para>
    /// The band is white on a dark crosshair and dark on a white one. A white sheen on
    /// white ink is no sheen at all, and white is one of the colours in the row.
    /// </para>
    /// </summary>
    private void DrawSheen(
        DrawingContext dc, CrosshairPreset preset, CrosshairFrame frame, double magnification)
    {
        double half = (preset.Size / 2.0) + 10;
        double width = Math.Max(9.0, preset.Size * 0.4);
        var band = new Rect((frame.Sheen * half) - (width / 2.0), -half, width, half * 2.0);
        Brush brush = SheenBrush(ColourOf(preset));

        dc.PushOpacity(Math.Clamp(frame.SheenAlpha, 0, 1));

        if (preset.ShapeKind == CrosshairShape.Custom)
        {
            if (TryCompose(preset, magnification, out BitmapSource composed, out double side))
            {
                var mask = new ImageBrush(composed)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, composed.PixelWidth, composed.PixelHeight),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(-side / 2.0, -side / 2.0, side, side)
                };

                dc.PushOpacityMask(mask);
                dc.DrawRectangle(brush, null, band);
                dc.Pop();
            }
        }
        else
        {
            // The ink is the clip, widened by a pixel and a half either side. Without
            // that the highlight is only as tall as a two pixel line and there is
            // almost nothing of it to see; with it, the light sits on the line rather
            // than inside it, which is what a glint looks like.
            // Built once for the shape and kept: widening a path is the expensive part
            // of a frame, and the sheen is on screen for a third of every cycle.
            string inkKey = string.Join(
                '|', preset.ShapeKind, preset.Size, preset.Thickness, preset.Gap);

            if (_sheenInk is null || !string.Equals(inkKey, _sheenInkKey, StringComparison.Ordinal))
            {
                double radius = CrosshairGeometry.DotRadius(preset) + SheenSpread;
                Geometry built = preset.ShapeKind == CrosshairShape.Dot
                    ? new EllipseGeometry(new Point(0, 0), radius, radius)
                    : CrosshairGeometry.Strokes(preset).GetWidenedPathGeometry(
                        new Pen(System.Windows.Media.Brushes.Black, preset.Thickness + SheenSpread));

                if (built.CanFreeze)
                {
                    built.Freeze();
                }

                _sheenInk = built;
                _sheenInkKey = inkKey;
            }

            Geometry ink = _sheenInk;

            dc.PushClip(ink);
            dc.DrawRectangle(brush, null, band);
            dc.Pop();
        }

        dc.Pop();
    }

    /// <summary>How far the sheen reaches past the line it is lighting, in pixels.</summary>
    private const double SheenSpread = 3.0;

    private Geometry? _sheenInk;

    private string _sheenInkKey = string.Empty;

    /// <summary>A band that fades in and out across its own width, so the highlight has no hard edges.</summary>
    private static Brush SheenBrush(Color colour)
    {
        // Perceived brightness: above this the ink is light enough that a white sheen
        // would vanish, so the light becomes a soft shadow instead.
        bool light = ((0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B)) > 200;
        return light ? SheenOnLight : SheenOnDark;
    }

    private static readonly Brush SheenOnDark = BuildSheenBrush(Colors.White, 220);

    private static readonly Brush SheenOnLight = BuildSheenBrush(Colors.Black, 110);

    private static Brush BuildSheenBrush(Color tint, byte peak)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, tint.R, tint.G, tint.B), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The bands a ripple is sending out, in the crosshair's own colour so they belong
    /// to it. Each is the shape's outline pushed outward by the frame's distance, which
    /// is what makes the wave take the crosshair's form instead of being a circle drawn
    /// round whatever happens to be there.
    /// <para>
    /// A picture has no outline to push outward, so it ripples as a grown copy of
    /// itself. That is the one case where a scaled copy works: a photograph or an
    /// animation has detail inside it, so the copy is visible, where a scaled cross was
    /// only its own arms drawn again on top of themselves.
    /// </para>
    /// </summary>
    private void DrawRipples(
        DrawingContext dc, CrosshairPreset preset, Color colour, CrosshairFrame frame, double magnification)
    {
        foreach (CrosshairRipple ripple in frame.Ripples)
        {
            if (ripple.Alpha <= 0)
            {
                continue;
            }

            dc.PushOpacity(Math.Clamp(ripple.Alpha, 0, 1));

            if (preset.ShapeKind == CrosshairShape.Custom)
            {
                double half = Math.Max(1.0, preset.Size / 2.0);
                double grown = 1 + (ripple.Distance / half);

                dc.PushTransform(new ScaleTransform(grown, grown));
                DrawImage(dc, preset, magnification);
                dc.Pop();
            }
            else
            {
                // A wave in a very light colour disappears into a light scene, and a
                // wave nobody can see is the same as no effect at all. Those colours get
                // a soft edge under the band, so the wave reads as a light line with a
                // shadow whatever is behind it.
                if (RippleEdge(colour) is { } edge
                    && BandOf(preset, ripple.Distance - 1.0, ripple.Width + 1.8) is { } under)
                {
                    dc.DrawGeometry(Frozen(edge), null, under);
                }

                if (BandOf(preset, ripple.Distance, ripple.Width) is { } band)
                {
                    dc.DrawGeometry(Frozen(colour), null, band);
                }
            }

            dc.Pop();
        }
    }

    /// <summary>
    /// The edge colour for a wave, when the colour is light enough to need one. The rule
    /// itself is in <see cref="CrosshairAnimation.WaveEdge"/>; this only puts it on the
    /// screen, at the opacity a shadow wants.
    /// </summary>
    private static Color? RippleEdge(Color colour)
    {
        if (CrosshairAnimation.WaveEdge(colour.R, colour.G, colour.B) is not { } edge)
        {
            return null;
        }

        return Color.FromArgb(0x66, edge.R, edge.G, edge.B);
    }

    /// <summary>
    /// The bands already built for this element, by the shape that made them and the
    /// distance they sit at.
    /// </summary>
    private readonly Dictionary<string, Geometry?> _rippleBands = new(StringComparer.Ordinal);

    /// <summary>
    /// A ripple band, built once per distance rather than once per frame. Widening a path
    /// into an outline is the one expensive thing an effect does: a ripple frame measured
    /// at about half a millisecond against a quarter for a still one, and most of that
    /// half was rebuilding the same bands sixty times a second. The distance is quantised
    /// to half a pixel, which is finer than the band moves in a frame and invisible on a
    /// two pixel line, so a wave of a few dozen steps reuses its bands instead of
    /// rebuilding them every sixteenth of a second.
    /// </summary>
    private Geometry? BandOf(CrosshairPreset preset, double distance, double width)
    {
        distance = Math.Round(distance * 2) / 2;

        // The shape's own numbers are in the key as well, so a crosshair edited on the
        // tab cannot draw a band made for the size it used to be.
        string key = string.Join(
            '|',
            preset.ShapeKind,
            preset.Size,
            preset.Thickness,
            preset.Gap,
            distance.ToString("0.#", CultureInfo.InvariantCulture),
            width.ToString("0.##", CultureInfo.InvariantCulture));

        if (_rippleBands.TryGetValue(key, out Geometry? cached))
        {
            return cached;
        }

        Geometry? band = distance < 0
            ? null
            : CrosshairGeometry.RippleBand(preset, distance, width);

        // A different shape or size makes every entry nonsense; the cap keeps a long
        // tuning session from growing this without end.
        if (_rippleBands.Count > 256)
        {
            _rippleBands.Clear();
        }

        _rippleBands[key] = band;
        return band;
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

    private static Color Cycled(Color colour, double degrees)
    {
        (byte r, byte g, byte b) = CrosshairAnimation.CycleHue(colour.R, colour.G, colour.B, degrees);
        return Color.FromRgb(r, g, b);
    }

    private static Color CenterDotColourOf(CrosshairPreset preset)
    {
        return CrosshairPreset.TryParseColor(preset.EffectiveCenterDotColor(), out byte r, out byte g, out byte b)
            ? Color.FromRgb(r, g, b)
            : ColourOf(preset);
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
