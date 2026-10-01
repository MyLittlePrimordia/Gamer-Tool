using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GamerTool.UI;

/// <summary>
/// Draws a live spectrum of whatever Windows is playing.
/// <para>
/// This used to replay a table of magnitudes baked offline from one preview
/// track, and that was wrong in two ways that no test caught. The table was a
/// slice of a 30 dB window, so anything more than 30 dB down stored as a
/// literal zero: thirteen of the top fourteen bars were zero in every frame and
/// could never move. And there was one table, made from game.mp3, while the
/// player offers samples of 95, 156 and 223 seconds, so cycling to anything
/// longer than the baked length pinned the position past the end of the table
/// and froze the whole row.
/// </para>
/// <para>
/// Now it taps the render device with a WASAPI loopback, which is how FxSound's
/// own analyser hears system audio, and runs the numbers over whatever arrives.
/// So it reacts to games, music and video alike, the top of the spectrum is
/// present because the range is wide enough to show it, and there is no asset to
/// keep in step with a track that no baker in this repository can rebuild.
/// </para>
/// <para>
/// The equaliser's band gains are deliberately no longer folded into the heights.
/// The loopback tap is downstream of FxSound, so the curve the user has built is
/// already physically in the signal; adding it to the display a second time would
/// draw a correction on top of a correction, and the bars would disagree with the
/// sound they are meant to be showing.
/// </para>
/// </summary>
public sealed class SpectrumView
{
    /// <summary>
    /// How many bars the row is divided into.
    /// <para>
    /// Sixty seven percent more than the forty eight this started on, because
    /// the bars were too heavy to read as a spectrum and more of them look finer
    /// rather than blockier. At the usual host width that is a four pixel bar
    /// and a one pixel gap, against six and two before.
    /// </para>
    /// <para>
    /// Worth being straight about what the extra bars do and do not buy. The
    /// analysis window is 2048 samples, so at 44.1 kHz one FFT bin covers about
    /// 21 Hz. A bar below roughly 200 Hz is narrower than that no matter how many
    /// of them there are, and the analyser already widens the very lowest bands
    /// to two bins so they stop flickering. So the new bars are density and
    /// visual fineness, mostly in the mid and high end. Genuinely more detail
    /// down at the bass would need a longer window, which costs response time
    /// that was spent getting the display quick in the first place.
    /// </para>
    /// </summary>
    public const int BarCount = 80;

    /// <summary>Roughly a third of a second, comfortably over one analysis window.</summary>
    private const int SampleBuffer = 16384;

    private readonly Grid _host;
    private readonly SpectrumBars _bars;
    private readonly ISampleFeed _feed;
    private readonly DispatcherTimer _timer;

    /// <summary>Fresh from the analyser this frame. Never drawn directly.</summary>
    private readonly double[] _raw = new double[BarCount];

    private readonly SpectrumSmoother _smoother = new(BarCount);
    private readonly float[] _samples = new float[SampleBuffer];

    /// <summary>Stopwatch seconds of the previous tick, for frame rate independent smoothing.</summary>
    private double _lastTick;

    /// <summary>Set once the first capture has been logged, so it is not logged on every start.</summary>
    private bool _announced;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>, so starts cannot stack.</summary>
    private bool _running;

    public SpectrumView(Grid host, ISampleFeed feed)
    {
        _host = host;
        _feed = feed;

        _bars = new SpectrumBars(BarBrush());
        BuildBars();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // Sixty a second, so the smoothing reads the same whatever the machine
            // does, and so a bass hit shows up on the frame it lands rather than
            // on the next one.
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;

        // The columns are sized from the measured width, so they have to be laid
        // out again whenever that width changes: a window resize, a monitor change,
        // or a different display scale. The host is the one that hears about any
        // of those, and the guard inside makes it cheap to call often.
        _host.SizeChanged += (_, _) => LayoutColumns();
    }

    /// <summary>True while real audio is arriving.</summary>
    public bool IsLive => _feed.IsLive;

    /// <summary>
    /// The tallest level currently on its way to the screen, zero when the row
    /// has nothing to show.
    /// <para>
    /// Public because "the bars are stuck" is a question about the drawn
    /// spectrum rather than about any one stage of producing it, so a test that
    /// cannot see the drawn row has to be able to ask about the levels feeding
    /// it. The smoother is not asked directly: that is a step short of the thing
    /// the complaint was about.
    /// </para>
    /// </summary>
    public double PeakLevel
    {
        get
        {
            double peak = 0.0;
            ReadOnlySpan<double> levels = _smoother.Levels;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > peak)
                {
                    peak = levels[i];
                }
            }

            return peak;
        }
    }

    public void Start()
    {
        // Asked for on every window state change, every visibility change and
        // every page switch, so it arrives constantly while the tab is on screen.
        // Answering it every time used to reopen the capture underneath the
        // running one and reset the announcement flag, which is why the log said
        // the loopback had come live again every twenty seconds on a machine
        // where nothing about the audio had changed at all.
        if (_running)
        {
            return;
        }

        _running = true;
        _feed.Start();
        _announced = false;

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        _feed.Stop();
        _smoother.Reset();
        _bars.SetIdle(true);
    }

    public void Release()
    {
        _running = false;
        _timer.Stop();
        _feed.Stop();
    }

    private void BuildBars()
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();

        // A row for the bars, and a row for the axis they grow out of. The bars
        // are centred on the axis and grow both ways, the way a hardware analyser
        // does, so a loud passage reads as a thick symmetric band rather than a
        // row of spikes climbing to one side.
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _bars.HorizontalAlignment = HorizontalAlignment.Stretch;
        _bars.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_bars, 0);
        Grid.SetColumn(_bars, 0);
        _host.Children.Add(_bars);

        // The zero line the bars sit on, so the symmetry is visible even in
        // silence and it is obvious that silence is the middle, not the bottom.
        Border axis = new()
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, -1)
        };
        Grid.SetRow(axis, 1);
        Grid.SetColumn(axis, 0);
        _host.Children.Add(axis);
    }

    /// <summary>
    /// Gives every bar the same integer width, at an identical pitch.
    /// <para>
    /// The window sets SnapsToDevicePixels, so every edge lands on a whole device
    /// pixel whatever the layout asks for. With 48 bars across roughly 423 pixels
    /// that matters: 423 / 48 is 8.81, which cannot be drawn as equal whole pixel
    /// steps, and the leftover has to land somewhere. Star columns spread it
    /// across the columns, so the pitch came out alternating between 13 and 14
    /// physical pixels and the row read as unevenly spaced.
    /// </para>
    /// <para>
    /// So the pitch is chosen as a whole number of pixels that fits, and the
    /// leftover becomes an equal inset at each end instead of a variation in the
    /// gaps. Every gap is then identical, which is the thing the eye was reading.
    /// </para>
    /// </summary>
    private void LayoutColumns()
    {
        if (_host.ActualWidth <= 0.0)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(_host).DpiScaleX;
        _bars.Layout(_host.ActualWidth, scale);
    }

    private static Brush BarBrush()
    {
        // One flat colour for every bar, taken from the audio accent. An earlier
        // version walked the bars into violet, which put a second, lighter pink
        // on the same tab as the equaliser and the dials and made the whole page
        // look like it had two accents.
        //
        // The value itself was sampled out of a live FxSound window rather than
        // picked by eye, because eyeballing a pink is how you end up with a pink
        // that is nearly the same and not quite.
        SolidColorBrush brush = new(SpectrumBars.BarAxisColour);
        brush.Freeze();
        return brush;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Seconds since the last tick, so the display feels the same whatever the
        // timer ends up doing.
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double dt = _lastTick <= 0.0 ? 1.0 / 60.0 : Math.Clamp(now - _lastTick, 0.0, 0.1);
        _lastTick = now;

        Draw(dt);
    }

    /// <summary>
    /// One drawn frame. Separated from the timer so a test can step the display
    /// without waiting on a real clock.
    /// <para>
    /// Public because the freeze this reaches is a behaviour of the whole
    /// chain - feed, analyser, smoothing, draw - and none of the halves can show
    /// it on their own. The seam exists for that, not so the timer can be
    /// swapped out.
    /// </para>
    /// </summary>
    public void Draw(double dtSeconds)
    {
        double dt = Math.Clamp(dtSeconds, 0.0, 0.1);

        int got = _feed.Read(_samples);

        // A full window or nothing, which is what the analyser itself requires.
        // This check is only a cheap way of not calling it, and it is kept in step
        // with the analyser's own rule on purpose: it used to allow half a window
        // and disagree with it.
        bool analysed = got >= SpectrumAnalyser.WindowSize
            && SpectrumAnalyser.Analyse(
                _samples.AsSpan(0, got),
                _feed is LoopbackSampleFeed live ? live.SampleRate : 48000,
                BarCount,
                _raw.AsSpan());

        if (!_announced && _feed is LoopbackSampleFeed feed)
        {
            _announced = true;
            TraceLog.Write("SPECTRUM loopback "
                + (feed.IsLive ? "live at " + feed.SampleRate + " Hz" : "unavailable: " + (feed.LastFailure ?? "unknown")));
        }

        // The analysis lands in its own buffer and the smoother keeps a separate
        // one for what is drawn. They used to be the same array, which quietly
        // made the smoothing a no-op: the analyser overwrote the value the
        // smoothing was supposed to be approaching, so the bars were drawn from
        // raw frame to frame and looked like they were having a fight.
        _smoother.Push(_raw.AsSpan(), analysed, dt);
        _bars.Update(_smoother.Levels, Math.Max((_host.ActualHeight - 4) / 2.0, 1));

        // Once the bars have decayed to silence on their own, saying so is
        // clearer than leaving a flat row that could be read as a stalled
        // display. Only after the decay, so a pause between tracks does not
        // flicker the message in and out.
        if (!analysed && LevelsAreSilent(_smoother.Levels))
        {
            _bars.SetIdle(_feed.IsLive
                ? "Nothing playing"
                : "Audio capture unavailable");
        }
    }

    /// <summary>
    /// True when every drawn level has reached zero, which is the only moment a
    /// flat row is silence rather than a row that has not been told anything yet.
    /// </summary>
    private static bool LevelsAreSilent(ReadOnlySpan<double> levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] > 0.0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The row of bars, drawn in one pass rather than as one element each.
/// <para>
/// They used to be forty-eight Borders whose Height was assigned on every tick.
/// Assigning Height invalidates layout, and the tick runs at sixty hertz, so that
/// was around two thousand nine hundred layout passes a second to draw a picture
/// that never changes shape, only height. A single element that overrides
/// <see cref="OnRender"/> draws the lot with no layout involved at all.
/// </para>
/// <para>
/// The pitch is still worked out in whole device pixels, for the reason given on
/// <see cref="SpectrumView.LayoutColumns"/>: 423 pixels across eighty bars is 5.28
/// each, which cannot be drawn as equal whole pixel steps, and letting the
/// leftover land in the gaps makes the row read as unevenly spaced.
/// </para>
/// <para>
/// The bar count is <see cref="SpectrumView.BarCount"/> rather than a number of
/// its own, and that is the whole point of writing it that way. It used to be a
/// literal forty eight sitting here while the analyser produced eighty bands, so
/// <c>Update</c> silently copied the first forty eight and threw the rest away.
/// The row then drew the bottom 1.4 kHz of an 80 band spread across the full
/// width, and everything from there to 16 kHz was never drawn at all - while the
/// class above it described the range as 40 Hz to 16 kHz, and the layout tests
/// went on checking the pitch arithmetic for eighty bars. Tying the two numbers
/// together means the next count change cannot be half made.
/// </para>
/// </summary>
public sealed class SpectrumBars : FrameworkElement
{
    /// <summary>
    /// How many bars the row draws, which is exactly as many as the analyser
    /// produces. Public because that equality is the thing worth asserting: the
    /// two numbers were once independent and the gap was invisible.
    /// </summary>
    public const int BarCount = SpectrumView.BarCount;
    /// <summary>
    /// Share of the pitch that a bar fills. The remainder is the gap between
    /// bars.
    /// <para>
    /// Just under a half, which is roughly what a well made reference does and
    /// is the difference between a row of separate strokes and a filled block.
    /// The earlier one pixel gap filled four fifths of the pitch.
    /// </para>
    /// </summary>
    public const double BarDuty = 0.45;

    /// <summary>
    /// Below this pitch there is no room for a bar and a gap, and a sub pixel bar
    /// would only look like noise, so the row is not drawn at all.
    /// </summary>
    public const double MinDrawablePitch = 3;

    private readonly Brush _brush;
    private readonly double[] _level = new double[BarCount];

    /// <summary>
    /// How many of those bars the current width has room for. Equal to
    /// <see cref="BarCount"/> at any width the panel normally occupies, and fewer
    /// only on a box too narrow to give every bar a pixel of pitch and a gap.
    /// </summary>
    private int _drawable = BarCount;

    private double _pitch = 8;
    private double _barWidth = 6;
    private double _inset;
    private double _available = 1;

    /// <summary>Vertical gradient across the whole row, rebuilt only when the height moves.</summary>
    private Brush? _fill;

    private double _fillHeight = -1;

    /// <summary>Deeper end of a bar, at the tips.</summary>
    public static readonly Color BarTipColour = Color.FromRgb(0xC4, 0x27, 0x43);

    /// <summary>
    /// Brightest part of a bar, on the axis, and the flat colour everywhere else.
    /// <para>
    /// This is the Audio tab's accent, held here as a value so the bar fill
    /// cannot quietly become a second pink on a tab whose every other accent is
    /// keyed off <c>AccentAudio</c>. The spectrum tint, the equaliser curve, the
    /// dials and the sliders are all the same colour by design.
    /// </para>
    /// </summary>
    public static readonly Color BarAxisColour = Color.FromRgb(0xE8, 0x3A, 0x58);

    /// <summary>
    /// The bar fill: the same pink, very slightly lighter where the bar is
    /// densest and a shade deeper at the tips.
    /// <para>
    /// The hue is deliberately held. The version that was rejected walked the
    /// bars through violet, which on a tab that already has a pink equaliser and
    /// pink dials put a second accent on screen. This only moves lightness, by
    /// about a tenth, so the row reads as one colour with some depth in it.
    /// </para>
    /// <para>
    /// One brush serves the whole row rather than one per bar, because every bar
    /// is centred on the same axis. A gradient mapped to the control rather than
    /// to each rectangle means a short bar near the axis gets the bright middle
    /// and a tall one reaches out into the deeper ends, which is the effect
    /// wanted, for the cost of a single object.
    /// </para>
    /// </summary>
    private static Brush BarGradient(double height)
    {
        LinearGradientBrush brush = new()
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, Math.Max(1.0, height)),
            GradientStops =
            {
                new GradientStop(BarTipColour, 0.0),
                new GradientStop(BarAxisColour, 0.5),
                new GradientStop(BarTipColour, 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }
    private double _radius = 1.5;
    private bool _idle = true;
    private string _idleText = "Play a sample to hear this preset";

    public SpectrumBars(Brush brush)
    {
        _brush = brush;
        SnapsToDevicePixels = true;
    }

    /// <summary>
    /// The line shown while nothing is playing. Without it the hero was a large
    /// black rectangle with a thirty pixel play button in the corner of it, which
    /// is the same footprint as the Display tab's preview and none of its
    /// content.
    /// </summary>
    public void SetIdle(string text)
    {
        _idleText = text;
        _idle = true;
        InvalidateVisual();
    }

    public void SetIdle(bool idle)
    {
        _idle = idle;
        InvalidateVisual();
    }

    /// <summary>Takes the current levels and asks for a single redraw.</summary>
    public void Update(ReadOnlySpan<double> levels, double available)
    {
        _idle = false;

        int count = Math.Min(levels.Length, _level.Length);
        for (int i = 0; i < count; i++)
        {
            _level[i] = levels[i];
        }

        _available = Math.Max(available, 1);

        // Rebuilt only when the height actually moves. A fresh brush every frame
        // would be eighty frozen objects a tick for no visible gain.
        if (Math.Abs(_fillHeight - (_available * 2.0)) > 0.5)
        {
            _fillHeight = _available * 2.0;
            _fill = BarGradient(_fillHeight);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Re-lays the bars out for a new width. Whole device pixels throughout, so
    /// the pitch is identical everywhere along the row and the leftover becomes
    /// an equal inset at each end.
    /// </summary>
    public void Layout(double actualWidth, double dpiScale)
    {
        double scale = dpiScale > 0 ? dpiScale : 1.0;
        int deviceWidth = (int)Math.Round(actualWidth * scale);

        // Fewer device pixels than there are bars. Rather than keeping the last
        // layout and drawing the new count at the old pitch, which would run the
        // row off the right edge of its own box, the bars are dropped from the
        // end until there is room for them. Losing the top of the spectrum is a
        // better answer than a row drawn on top of whatever sits next to it, and
        // it only happens at widths the panel does not normally occupy.
        int drawable = Math.Max(1, (int)(deviceWidth / MinDrawablePitch));
        int bars = Math.Min(BarCount, drawable);
        _drawable = bars;

        int pitch = deviceWidth / bars;
        _pitch = Math.Max(1, pitch) / scale;

        // The bar is a share of the pitch, and the rest is gap, rather than a
        // fixed number of pixels. Two reasons.
        //
        // It is what makes the row read as separate bars instead of one filled
        // shape. At a one pixel gap the bars covered four fifths of the pitch and
        // a loud passage became a solid block, which is the opposite of the
        // reference this was being compared against.
        //
        // And a fixed gap is the wrong rule anyway: it makes the density depend
        // on how wide the window happens to be, so resizing quietly changed the
        // character of the display. A share of the pitch looks the same at every
        // width, and stays crisp because it is still rounded to whole device
        // pixels.
        int deviceBar = (int)(pitch * BarDuty);

        // Always leave at least one pixel of gap, or the row fuses into a band.
        deviceBar = Math.Clamp(deviceBar, 1, Math.Max(1, pitch - 1));
        _barWidth = Math.Max(1.0, deviceBar / scale);

        // Rounded, but only just. The cap used to be half the bar width, which on
        // a thin bar is a lozenge rather than a bar, and a row of lozenges reads
        // as beads on a string instead of as a spectrum.
        _radius = Math.Min(1.25, _barWidth / 3.0);

        // The leftover becomes an equal inset at each end, so the row is centred
        // and the gaps stay even rather than the remainder all landing on one
        // side.
        int groupWidth = pitch * bars;
        int insetLeft = (deviceWidth - groupWidth) / 2;
        _inset = insetLeft / scale;

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (_idle)
        {
            DrawIdleState(dc);
            return;
        }

        double centreY = RenderSize.Height / 2.0;
        double x = _inset;

        for (int i = 0; i < _drawable; i++)
        {
            // Doubled, because the bar is centred on the axis and grows both ways,
            // so a loud passage reads as a thick symmetric band rather than a row
            // of spikes climbing to one side.
            double height = Math.Max(1, Math.Round(_level[i] * _available) * 2);
            dc.DrawRoundedRectangle(_fill ?? _brush, null, new Rect(x, centreY - (height / 2.0), _barWidth, height), _radius, _radius);
            x += _pitch;
        }
    }

    /// <summary>
    /// The resting state: the zero line, and one line saying what to press.
    /// </summary>
    private void DrawIdleState(DrawingContext dc)
    {
        double centreY = RenderSize.Height / 2.0;

        // The zero line is still drawn, so the box reads as an analyser at rest
        // rather than as an empty panel.
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            null,
            new Rect(0, centreY - 0.5, RenderSize.Width, 1));

        if (string.IsNullOrWhiteSpace(_idleText) || RenderSize.Width < 120)
        {
            return;
        }

        FormattedText text = new(
            _idleText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Inter, system-ui, sans-serif"),
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal),
            11,
            new SolidColorBrush(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF)),
            1.25);

        dc.DrawText(text, new Point(
            Math.Max(0, (RenderSize.Width - text.Width) / 2.0),
            centreY - (text.Height / 2.0)));
    }

    /// <summary>Measures as whatever it is given, since it draws rather than lays out.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            System.Double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            System.Double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return finalSize;
    }
}
