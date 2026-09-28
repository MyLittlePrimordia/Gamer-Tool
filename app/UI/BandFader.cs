using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace GamerTool.UI;

/// <summary>
/// A vertical fader that can draw its groove as a row of dashes instead of a
/// solid line, and can drop the small dash that marks the zero point.
///
/// The engine draws its equaliser bands as dashed lines with no marker at zero,
/// while its colour trims are solid and do mark the middle. Rather than keep two
/// near identical control templates in step, the one template asks this flag.
///
/// The dashes are real rectangles rather than a dashed stroke or a tiled brush.
/// A stroke needs a path with some width to run along, and a tiling brush was
/// measured rendering nothing at all here, so the only dependable way to get
/// evenly spaced marks down a track of unknown height is to lay them out.
/// </summary>
public sealed class BandFader : Slider
{
    /// <summary>How long each dash is, and the gap that follows it.</summary>
    private const double DashLength = 5.0;

    private const double DashGap = 6.0;

    public static readonly DependencyProperty DashedProperty = DependencyProperty.Register(
        nameof(Dashed),
        typeof(bool),
        typeof(BandFader),
        new FrameworkPropertyMetadata(false, OnDashedChanged));

    /// <summary>True for a dashed groove with no zero marker, as the engine draws its bands.</summary>
    public bool Dashed
    {
        get => (bool)GetValue(DashedProperty);
        set => SetValue(DashedProperty, value);
    }

    private static void OnDashedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((BandFader)d).ApplyBandStyle();
        ((BandFader)d).LayoutDashes();
    }

    private Canvas? _dashHost;

    private Brush? _dashGradient;

    private double _dashGradientHeight = -1.0;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _dashHost = GetTemplateChild("DashHost") as Canvas;
        if (_dashHost is not null)
        {
            _dashHost.SizeChanged += (_, _) => LayoutDashes();
        }

        ApplyBandStyle();
        LayoutDashes();
    }

    /// <summary>
    /// Where the knob actually is, in this element's own coordinates, or null
    /// before the template has been applied.
    /// <para>
    /// This exists because working the knob's position out from the value is
    /// wrong. A vertical Track keeps the thumb's centre half a thumb inside each
    /// end, so scaling the value across the control's full height puts the curve
    /// up to eleven pixels from the knob, and the gap is worst at +12 and -12,
    /// which is where the eye checks first. The knob cannot be moved out of that
    /// inset without shrinking it to nothing and losing the drag handle, so the
    /// curve is moved to meet the knob instead.
    /// </para>
    /// </summary>
    public Point? KnobCentre()
    {
        if (GetTemplateChild("BandThumb") is not Thumb knob || knob.ActualHeight <= 0.0)
        {
            return null;
        }

        return knob.TranslatePoint(
            new Point(knob.ActualWidth / 2.0, knob.ActualHeight / 2.0),
            this);
    }
    /// <summary>
    /// Swaps the solid groove, the zero marker and the coloured bars for the
    /// dashed line the engine draws its bands with.
    ///
    /// This is done in code rather than with a template trigger because the
    /// trigger was only taking effect on some of the elements it named: the fills
    /// collapsed but the groove behind the dashes stayed, which left a solid bar
    /// showing through the gaps. Owning all four elements in one place means they
    /// change together or not at all.
    /// </summary>
    private void ApplyBandStyle()
    {
        bool band = Dashed;
        SetVisibility("Groove", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("CentreTick", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("UpperFill", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("LowerFill", band ? Visibility.Collapsed : Visibility.Visible);

        // A band fader keeps its thumb. Hiding it so the window could paint a knob
        // instead was a mistake: the thumb is the slider's drag handle, and a
        // hidden or collapsed one is not hit tested, so the bands stopped
        // responding to the mouse altogether. The knob is where WPF puts it and
        // the curve is moved to meet it, not the other way round.
        if (_dashHost is not null)
        {
            _dashHost.Visibility = band ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetVisibility(string name, Visibility visibility)
    {
        if (GetTemplateChild(name) is FrameworkElement element)
        {
            element.Visibility = visibility;
        }
    }

    /// <summary>
    /// Fills the host with the run of dashes that fits the current track height.
    /// <para>
    /// They are one Path rather than one Rectangle each, and that is not a
    /// tidiness choice. A gradient brush with Absolute mapping is resolved in the
    /// space of whatever shape it is painted on, and a Rectangle inside a Canvas
    /// has its own origin: Canvas.Top moves the child without moving its coordinate
    /// space. So a gradient spanning the whole track, shared by forty little
    /// rectangles, gave every dash the first two percent of the ramp and the
    /// column came out flat white. One Path for the whole column has one local
    /// space, the ramp runs the full height, and the dashes fade from white at the
    /// top to pink at the bottom the way the engine draws them.
    /// </para>
    /// </summary>
    private void LayoutDashes()
    {
        if (_dashHost is null)
        {
            return;
        }

        _dashHost.Children.Clear();

        if (!Dashed)
        {
            return;
        }

        double height = _dashHost.ActualHeight;
        double width = _dashHost.ActualWidth;
        if (height <= 0.0 || width <= 0.0)
        {
            // The track has not been measured yet. The size change will call back.
            return;
        }

        var geometry = new PathGeometry();
        double step = DashLength + DashGap;
        for (double y = 0.0; y < height; y += step)
        {
            double dash = Math.Min(DashLength, height - y);
            var figure = new PathFigure
            {
                StartPoint = new Point(0.0, y),
                IsClosed = true,
                IsFilled = true,
            };

            figure.Segments.Add(new LineSegment(new Point(width, y), true));
            figure.Segments.Add(new LineSegment(new Point(width, y + dash), true));
            figure.Segments.Add(new LineSegment(new Point(0.0, y + dash), true));
            figure.Segments.Add(new LineSegment(new Point(0.0, y), true));
            geometry.Figures.Add(figure);
        }

        geometry.Freeze();

        _dashHost.Children.Add(new Path
        {
            Data = geometry,
            Fill = DashBrushFor(height),
            IsHitTestVisible = false,
        });
    }

    /// <summary>
    /// The three stops of the dash ramp, derived from the control's own accent.
    /// <para>
    /// This used to be three hard coded pinks, which is how the equaliser's
    /// dashed bands ended up a different colour from the accent on the same tab:
    /// a near white at the top, a light pink in the middle and a mid pink at the
    /// bottom, none of which was the audio accent anything else on the page used.
    /// Deriving them means the fader follows whatever accent it is handed, so it
    /// cannot drift out of step with the rest of the tab again.
    /// </para>
    /// <para>
    /// The lightening and darkening are towards white and black, which keeps the
    /// hue exactly where it was. Rotating the hue instead would put a second
    /// accent on the page, which is the mistake an earlier version of this made.
    /// </para>
    /// </summary>
    public static (Color Top, Color Middle, Color Bottom) DashRamp(Color accent)
    {
        return (Mix(accent, Colors.White, 0.28), accent, Mix(accent, Colors.Black, 0.24));
    }

    private static Color Mix(Color from, Color towards, double amount)
    {
        byte Blend(byte a, byte b) => (byte)Math.Round(a + ((b - a) * amount));
        return Color.FromRgb(Blend(from.R, towards.R), Blend(from.G, towards.G), Blend(from.B, towards.B));
    }

    /// <summary>
    /// The fade down the column: the accent lightened towards white at the top
    /// and deepened towards black at the bottom. One brush for the whole column,
    /// cached, because the track height is the only thing that changes it.
    /// </summary>
    private Brush DashBrushFor(double height)
    {
        if (_dashGradient is not null && Math.Abs(_dashGradientHeight - height) < 0.5)
        {
            return _dashGradient;
        }

        Color accent = Foreground is SolidColorBrush solid ? solid.Color : Color.FromRgb(0xE3, 0x32, 0x50);
        (Color top, Color middle, Color bottom) = DashRamp(accent);

        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(top, 0.0),
                new GradientStop(middle, 0.5),
                new GradientStop(bottom, 1.0),
            },
            new Point(0.0, 0.0),
            new Point(0.0, Math.Max(height, 1.0)))
        {
            MappingMode = BrushMappingMode.Absolute,
        };

        brush.Freeze();
        _dashGradient = brush;
        _dashGradientHeight = height;
        return brush;
    }

}
