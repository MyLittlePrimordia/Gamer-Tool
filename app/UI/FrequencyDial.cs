using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GamerTool.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GamerTool.UI;

/// <summary>
/// The small ring dial that sets one band's centre frequency, drawn the way the
/// engine draws it: an open ring with a dot on it. Only the 5 and 10 band
/// layouts get one, because those are the only counts where the engine gives a
/// band a frequency window wide enough to move.
///
/// The control works in detents rather than hertz, and the detent count comes
/// from the band window, so a narrow band gets a short dial and a wide band gets
/// a long one. Both still take the same number of pixels to cross, which is what
/// keeps a 62 to 85 Hz band as easy to tune as a 6360 to 11760 Hz one.
///
/// Dragging follows the pointer around the ring, the way the engine's own dial
/// does, so the knob stays under the pointer instead of running away from it.
/// </summary>
public sealed class FrequencyDial : Control
{
    /// <summary>The dial is a 270 degree arc with the gap at the bottom, like the engine's own control.</summary>
    /// <summary>
    /// The white the value dot is drawn in, the same near-white the effect slider
    /// handles use, so a dial and a Dynamic Boost knob are visibly the same thing.
    /// </summary>
    private static readonly Brush KnobFill = Frozen(Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
    private const double StartAngle = 135.0;

    private const double SweepAngle = 270.0;

    /// <summary>Raised whenever the dial moves, so the page can retune and redraw.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Set when the dial is being dragged, so the window can tell the difference from a click.</summary>
    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(
        nameof(IsDragging), typeof(bool), typeof(FrequencyDial), new PropertyMetadata(false));

    public bool IsDragging
    {
        get => (bool)GetValue(IsDraggingProperty);
        set => SetValue(IsDraggingProperty, value);
    }

    /// <summary>The accent the arc and dot are painted in, normally the audio accent.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(FrequencyDial),
        new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>How many detents this dial has, set from the band window.</summary>
    public static readonly DependencyProperty StepsProperty = DependencyProperty.Register(
        nameof(Steps), typeof(double), typeof(FrequencyDial),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Steps
    {
        get => (double)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    /// <summary>Where the dial sits, from zero to <see cref="Steps"/>.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(FrequencyDial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>What a double click puts the dial back to, which is the band default.</summary>
    public static readonly DependencyProperty DefaultStepProperty = DependencyProperty.Register(
        nameof(DefaultStep), typeof(double), typeof(FrequencyDial), new PropertyMetadata(0.0));

    public double DefaultStep
    {
        get => (double)GetValue(DefaultStepProperty);
        set => SetValue(DefaultStepProperty, value);
    }

    private double _dragCentreX;
    private double _dragCentreY;

    /// <summary>
    /// The window this band sits in, held as hertz so the automation peer has a
    /// real range to report rather than a step count it would have to describe in
    /// words. Set once when the strip is built.
    /// </summary>
    private double _minimumHertz = 62.0;

    private double _maximumHertz = 85.0;

    /// <summary>Where the dial currently is, in hertz, for the read-back.</summary>
    private double _currentHertz;

    /// <summary>Names the band, so a screen reader says which band it is on.</summary>
    public void SetBand(string name, double minimumHertz, double maximumHertz, double currentHertz)
    {
        _minimumHertz = minimumHertz;
        _maximumHertz = maximumHertz;
        _currentHertz = currentHertz;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        TrackHertz();
    }

    /// <summary>Where the dial is now, in hertz, for a readout or a tooltip.</summary>
    public double CurrentHertz => _currentHertz;

    /// <summary>Moves the dial to a frequency rather than to a detent.</summary>
    public void SetStepFromHertz(double hertz)
    {
        double clamped = Math.Clamp(hertz, _minimumHertz, _maximumHertz);
        SetStep(AudioPreset.StepFromFrequency(new AudioPreset.BandWindow(_minimumHertz, _maximumHertz), clamped));
    }

    public FrequencyDial()
    {
        // A hand, because the value is turned rather than dragged on a line.
        Cursor = Cursors.Hand;
        Focusable = true;

        // A Control hit tests across its whole box, so the empty corners inside
        // the ring are still grabbable. A FrameworkElement would only hit the
        // painted ring itself and the dial would feel lumpy to drag.
        Background = Brushes.Transparent;
    }

    /// <summary>Moves the dial to a detent, raising <see cref="ValueChanged"/> only if it really moved.</summary>
    public void SetStep(double step)
    {
        double clamped = Math.Clamp(step, 0.0, Math.Max(Steps, 1.0));
        if (Math.Abs(clamped - Step) < 0.0001)
        {
            return;
        }

        SetValue(StepProperty, clamped);
        TrackHertz();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps the hertz the automation peer reports in step with the detent, since
    /// the two are related by the band window rather than by a fixed ratio.
    /// </summary>
    private void TrackHertz()
    {
        _currentHertz = AudioPreset.FrequencyFromStep(
            new AudioPreset.BandWindow(_minimumHertz, _maximumHertz), Step);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (size <= 4.0)
        {
            return;
        }

        double radius = (size / 2.0) - 3.0;
        if (radius <= 0.5)
        {
            return;
        }

        var centre = new Point(RenderSize.Width / 2.0, RenderSize.Height / 2.0);
        // About a tenth of the dial's width. Thinner than this and a 38 pixel dial is
    // a hairline that reads as a scratch on the panel; much thicker and the arc
    // becomes a solid wedge with a gap in it rather than a dial with a reading.
    double thickness = Math.Max(2.6, size * 0.14);
        double max = Math.Max(Steps, 1.0);
        double fraction = Math.Clamp(Step / max, 0.0, 1.0);

        // The empty ring depends only on the size, and the dot's position only on
        // the fraction, so both are cached. Dragging a dial repaints at mouse
        // rate, and rebuilding a path, two pens and a brush on every one of those
        // is a lot of garbage for a picture that is mostly the same shape.
  RingMetrics metrics = new(radius, thickness);
  if (_cachedRing is null || !_cachedRing.Metrics.Matches(metrics))
  {
  // The engine draws the empty part of its band dials in a dark maroon, the same
  // pink as the filled part but almost out of it, rather than in a neutral grey.
  // A grey ring on a black panel read as a separate control that happened to sit
  // under the fader; a maroon one reads as part of the same dial.
  // The unfilled part of the ring is the same grey the effect sliders use for their
  // grooves, so a dial and a Dynamic Boost handle read as the same family of
  // control. It was a dark maroon, which made the dial a second pink object
  // competing with the arc instead of a track with a value on it.
  var track = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)), thickness)
  {
  StartLineCap = PenLineCap.Round,
  EndLineCap = PenLineCap.Round
  };
  track.Freeze();


            _cachedRing = new CachedRing(metrics, track, Arc(centre, radius, StartAngle, SweepAngle));
        }

        // The empty part of the ring, so the dial reads as a range rather than a
        // dot on nothing.
        dc.DrawGeometry(null, _cachedRing.Pen, _cachedRing.Geometry);

        // How much of the ring is filled.
        if (fraction > 0.0001)
        {
            var active = new Pen(Accent, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            active.Freeze();
            dc.DrawGeometry(null, active, Arc(centre, radius, StartAngle, SweepAngle * fraction));
        }

        // The dot, sitting on the ring at the current angle.
        double angle = (StartAngle + (SweepAngle * fraction)) * Math.PI / 180.0;
        var dot = new Point(centre.X + (radius * Math.Cos(angle)), centre.Y + (radius * Math.Sin(angle)));
        double dotSize = Math.Max(3.0, size * 0.17);
        dc.DrawEllipse(KnobFill, null, dot, dotSize / 2.0, dotSize / 2.0);
    }

    private readonly record struct RingMetrics(double Radius, double Thickness)
    {
        public bool Matches(RingMetrics other) => Radius.Equals(other.Radius) && Thickness.Equals(other.Thickness);
    }

    private sealed record CachedRing(RingMetrics Metrics, Pen Pen, Geometry Geometry);

    private CachedRing? _cachedRing;

    /// <summary>Builds an arc as a path, so the ring can be drawn with round caps like the engine's.</summary>
    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry Arc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        double start = startDegrees * Math.PI / 180.0;
        double end = (startDegrees + sweepDegrees) * Math.PI / 180.0;

        var from = new Point(centre.X + (radius * Math.Cos(start)), centre.Y + (radius * Math.Sin(start)));
        var to = new Point(centre.X + (radius * Math.Cos(end)), centre.Y + (radius * Math.Sin(end)));

        bool large = Math.Abs(sweepDegrees) > 180.0;
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(to, new Size(radius, radius), 0.0, large, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        Point here = e.GetPosition(this);
        _dragCentreX = RenderSize.Width / 2.0;
        _dragCentreY = RenderSize.Height / 2.0;
        IsDragging = true;
        CaptureMouse();

        // Pressing straight on the knob jumps to that spot rather than waiting
        // for a drag, which is how the engine's dials behave.
        if (AngleAt(here.X - _dragCentreX, here.Y - _dragCentreY) is double pressed)
        {
            SetStep(StepForAngle(pressed));
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsDragging || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point here = e.GetPosition(this);
        double? angle = AngleAt(here.X - _dragCentreX, here.Y - _dragCentreY);
        if (angle is null)
        {
            return;
        }

        // The dial follows the pointer around the ring. The angle is measured
        // from the start of the sweep, so the same place on the circle always
        // means the same value and the knob cannot run away from the pointer.
        double target = StepForAngle(angle.Value);
        SetStep(target);
        e.Handled = true;
    }

    /// <summary>
    /// Where the pointer sits around the ring, as an angle measured from the
    /// start of the sweep. Null when the pointer is on the exact centre, where
    /// an angle would be meaningless.
    /// </summary>
    private static double? AngleAt(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001)
        {
            return null;
        }

        // Screen y runs downwards, so the angle comes out increasing clockwise
        // and already matches the direction the arc is drawn in.
        double degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double fromStart = degrees - StartAngle;

        // Wrap into the sweep. The gap at the bottom of the ring belongs to
        // whichever end is nearer, so the value never jumps across it.
        while (fromStart < 0.0)
        {
            fromStart += 360.0;
        }

        return fromStart > SweepAngle ? (double?)null : fromStart;
    }

    /// <summary>The detent that sits at a given angle around the ring.</summary>
    private double StepForAngle(double angleFromStart)
    {
        double fraction = Math.Clamp(angleFromStart / SweepAngle, 0.0, 1.0);
        return fraction * Math.Max(Steps, 1.0);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        IsDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SetStep(Step + Math.Sign(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // Puts the band back on its own default frequency.
        SetStep(DefaultStep);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // The dial was focusable but did nothing with a key, so a keyboard could
        // land on it and go nowhere. Arrows step one detent, Page steps ten, Home
        // and End go to the ends, which is what every other dial in Windows does.
        double step = 1.0;
        bool handled = true;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                SetStep(Step - step);
                break;

            case Key.Right:
            case Key.Up:
                SetStep(Step + step);
                break;

            case Key.PageDown:
                SetStep(Step - 10.0);
                break;

            case Key.PageUp:
                SetStep(Step + 10.0);
                break;

            case Key.Home:
                SetStep(0.0);
                break;

            case Key.End:
                SetStep(Steps);
                break;

            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    /// <summary>
    /// Reports the dial as a named, described control rather than an anonymous
    /// one.
    /// <para>
    /// It used to return a bare <see cref="FrameworkElementAutomationPeer"/>,
    /// which announces a control with no name and nothing else, so a screen reader
    /// said the word for the class and stopped. The name says which band, and the
    /// help text says what the keys do and what range the band is allowed to move
    /// in, which is the part someone tuning by ear actually needs.
    /// </para>
    /// <para>
    /// WPF has no range peer that will read from a plain control: its own
    /// RangeBaseAutomationPeer only reports on a real RangeBase, which this is
    /// not. Getting a live range would mean deriving from Slider and reworking
    /// the hit testing and the drag, which is a rewrite of the control players
    /// touch most, for a screen reader improvement. The keyboard support and the
    /// description are the parts worth having for that price.
    /// </para>
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new DialAutomationPeer(this);

    private sealed class DialAutomationPeer : FrameworkElementAutomationPeer
    {
        private readonly FrequencyDial _owner;

        public DialAutomationPeer(FrequencyDial owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.Slider;
        }

        protected override string GetClassNameCore()
        {
            return nameof(FrequencyDial);
        }

        protected override string GetNameCore()
        {
            string label = System.Windows.Automation.AutomationProperties.GetName(_owner);
            return string.IsNullOrWhiteSpace(label) ? "Band frequency" : label;
        }

        protected override string GetHelpTextCore()
        {
            string help = System.Windows.Automation.AutomationProperties.GetHelpText(_owner);
            if (!string.IsNullOrWhiteSpace(help))
            {
                return help;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "Between {0:0.##} and {1:0.##} hertz. Arrow keys step by one hertz, page keys by ten, home and end go to the ends.",
                _owner._minimumHertz,
                _owner._maximumHertz);
        }
    }

    /// <summary>Reads the dial's detent, used when saving a tune.</summary>
    public double CurrentStep => Step;

    /// <summary>A short description of the dial for screen readers and tooltips.</summary>
    public string Describe(double hertz) => hertz.ToString("0.##", CultureInfo.InvariantCulture) + " Hz";
}

