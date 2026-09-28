using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GamerTool.UI;

/// <summary>
/// Draws a live spectrum of the preview track.
///
/// The magnitudes are pre-computed offline from the embedded preview track and
/// shipped as a small table of bytes, because WPF's MediaPlayer will not hand up
/// samples to run an FFT on and decoding the track at runtime would mean pulling
/// in Media Foundation. Baking it once keeps the app dependency free and the
/// frame timing exact.
///
/// What makes this useful rather than decorative is the second half: each bar is
/// then weighted by the gain of the EQ band it sits in, so dragging a band fader
/// visibly grows or shrinks that slice of the spectrum. The bars therefore show
/// the curve the user is building, which is the thing they are trying to judge.
/// </summary>
public sealed class SpectrumView
{
    /// <summary>How many bars the row is divided into.</summary>
    public const int BarCount = 48;

    private const double LowHz = 40.0;
    private const double HighHz = 16000.0;

    /// <summary>The gap between bars, in whole device pixels.</summary>
    private const double GapPixels = 2.0;

    private readonly Grid _host;
    private readonly SpectrumBars _bars;
    private readonly double[] _level = new double[BarCount];
    private readonly double[] _barCentreHz = new double[BarCount];
    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _position;
    private readonly Func<bool> _playing;

    private byte[] _frames = Array.Empty<byte>();
    private int _frameCount;
    private double _secondsPerFrame = 1.0 / 22.0;

    private Func<int, double>? _bandGain;

    /// <summary>Stopwatch seconds of the previous tick, for frame rate independent smoothing.</summary>
    private double _lastTick;

    public SpectrumView(Grid host, Func<TimeSpan> position, Func<bool> playing)
    {
        _host = host;
        _position = position;
        _playing = playing;

        for (int i = 0; i < BarCount; i++)
        {
            double t = (i + 0.5) / BarCount;
            _barCentreHz[i] = LowHz * Math.Pow(HighHz / LowHz, t);
        }

        _bars = new SpectrumBars(BarBrush());
        BuildBars();
        Load();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // The baked table only has about twenty two frames a second, so a slow
            // tick would step the bars visibly. Ticking at sixty and blending
            // between the two nearest frames turns that staircase into motion.
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;

        // The columns are sized from the measured width, so they have to be laid
        // out again whenever that width changes: a window resize, a monitor change,
        // or a different display scale. The host is the one that hears about any
        // of those, and the guard inside makes it cheap to call often.
        _host.SizeChanged += (_, _) => LayoutColumns();
    }

    /// <summary>Supplies the gain in dB for one spectrum bar, from the live EQ.</summary>
    public void SetBandGain(Func<int, double>? bandGain)
    {
        _bandGain = bandGain;
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        Array.Clear(_level);
        _bars.SetIdle(true);
    }

    public void Release()
    {
        _timer.Stop();
    }

    /// <summary>
    /// Reads the pre-computed magnitudes. Header layout, kept in lockstep with the
    /// generator that bakes Assets\spectrum.bin from the preview track:
    /// 0 magic, 4 version, 8 bars, 12 frames, 16 rate, 20 hop, 24 fft, 28 length.
    /// </summary>
    private void Load()
    {
        try
        {
            byte[] raw = DisplayPreview.ReadAsset("spectrum.bin");
            const int HeaderSize = 32;
            if (raw.Length < HeaderSize)
            {
                TraceLog.Write("SPECTRUM asset too short: " + raw.Length);
                return;
            }

            int bars = BitConverter.ToInt32(raw, 8);
            _frameCount = BitConverter.ToInt32(raw, 12);
            int rate = BitConverter.ToInt32(raw, 16);
            int hop = BitConverter.ToInt32(raw, 20);
            int length = BitConverter.ToInt32(raw, 28);

            // The frame count has to agree with the byte count as well as with the
            // length, because the tick indexes straight into the table by frame. A
            // header claiming more frames than the table holds put an out of range
            // read inside a sixteen millisecond timer, and an exception in a timer
            // becomes a dialog per tick that cannot be dismissed. The asset is
            // embedded so this needs a broken build, but the check is a comparison.
            if (bars != BarCount
                || hop <= 0
                || rate <= 0
                || length <= 0
                || raw.Length < HeaderSize + length
                || _frameCount <= 0
                || (long)_frameCount * BarCount > length)
            {
                TraceLog.Write("SPECTRUM bad header bars=" + bars + " hop=" + hop + " len=" + length + " frames=" + _frameCount);
                _frameCount = 0;
                return;
            }

            _frames = new byte[length];
            Buffer.BlockCopy(raw, HeaderSize, _frames, 0, length);
            _secondsPerFrame = (double)hop / rate;
            TraceLog.Write("SPECTRUM ready frames=" + _frameCount + " step=" + _secondsPerFrame.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            TraceLog.Write("SPECTRUM", ex);
        }
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
        SolidColorBrush brush = new(Color.FromRgb(0xFF, 0x2D, 0x6F));
        brush.Freeze();
        return brush;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Length == 0 || _frameCount == 0)
        {
            return;
        }

        bool playing = _playing();
        double available = Math.Max((_host.ActualHeight - 4) / 2.0, 1);

        // Seconds since the last tick, so the smoothing feels the same whatever
        // the timer ends up doing. A fixed per tick fraction would attack and
        // release at three different speeds depending on the frame rate.
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double dt = _lastTick <= 0.0 ? 1.0 / 60.0 : Math.Clamp(now - _lastTick, 0.0, 0.1);
        _lastTick = now;

        // Framerate independent exponential approach. Attack is quick so a
        // transient shows up immediately, release is slower so the bars fall away
        // instead of flickering.
        double attack = 1.0 - Math.Exp(-28.0 * dt);
        double release = 1.0 - Math.Exp(-9.0 * dt);

        // Paused or stopped decays all the way to nothing, rather than holding a
        // dimmed frozen frame. A held frame reads as a stuck picture; FxSound's
        // analyser falls silent and so should this one.
        double decay = 1.0 - Math.Exp(-6.0 * dt);

        int first = 0;
        double blend = 0.0;
        if (playing)
        {
            double exact = _position().TotalSeconds / _secondsPerFrame;
            first = (int)Math.Floor(exact);
            blend = exact - first;

            if (first < 0)
            {
                first = 0;
                blend = 0.0;
            }
            else if (first >= _frameCount - 1)
            {
                first = _frameCount - 1;
                blend = 0.0;
            }
        }

        int offset = first * BarCount;
        int next = playing && first + 1 < _frameCount ? offset + BarCount : offset;

        for (int bar = 0; bar < BarCount; bar++)
        {
            double target = 0.0;

            if (playing)
            {
                double a = _frames[offset + bar] / 255.0;
                double b = _frames[next + bar] / 255.0;
                target = a + ((b - a) * blend);

                if (_bandGain is not null)
                {
                    // The baked magnitudes are a linear 0..1 slice of a 30 dB window,
                    // so a band's gain in dB maps onto the same scale as exactly
                    // gain / 30. Using the window width here is what keeps the bars
                    // honest: a +12 dB band really does grow them by 40 percent of
                    // the full height, and a -12 dB band shrinks them the same way.
                    target += _bandGain(bar) / 30.0;
                }

                target = Math.Clamp(target, 0.0, 1.0);
            }

            double k = !playing ? decay : (target > _level[bar] ? attack : release);
            _level[bar] += (target - _level[bar]) * k;

            if (!playing && _level[bar] < 0.004)
            {
                _level[bar] = 0.0;
            }
        }

        // One call, one redraw. This used to be forty-eight Height assignments,
        // which is forty-eight layout invalidations a tick, at sixty ticks a
        // second, for a picture that only changes height.
        _bars.Update(_level, available);
    }

    /// <summary>Centre frequency of a bar, exposed so the window can match EQ bands to bars.</summary>
    public static double BarCentre(int bar)
    {
        double t = (bar + 0.5) / BarCount;
        return LowHz * Math.Pow(HighHz / LowHz, t);
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
/// <see cref="SpectrumView.LayoutColumns"/>: 423 pixels across 48 bars is 8.81
/// each, which cannot be drawn as equal whole pixel steps, and letting the
/// leftover land in the gaps makes the row read as unevenly spaced.
/// </para>
/// </summary>
public sealed class SpectrumBars : FrameworkElement
{
    private const int Count = SpectrumView.BarCount;
    private const double GapPixels = 2.0;

    private readonly Brush _brush;
    private readonly double[] _level = new double[Count];

    private double _pitch = 8;
    private double _barWidth = 6;
    private double _inset;
    private double _available = 1;
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
    public void Update(IReadOnlyList<double> levels, double available)
    {
        _idle = false;

        int count = Math.Min(levels.Count, _level.Length);
        for (int i = 0; i < count; i++)
        {
            _level[i] = levels[i];
        }

        _available = Math.Max(available, 1);
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
        int pitch = deviceWidth / Count;

        // Below about three pixels of pitch there is no room for a bar and a gap,
        // and a sub pixel bar would only look like noise.
        if (pitch < 3)
        {
            return;
        }

        _pitch = pitch / scale;
        _barWidth = Math.Max(1.0, (pitch - GapPixels) / scale);
        _radius = Math.Min(1.5, _barWidth / 2.0);

        int groupWidth = pitch * Count;
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

        for (int i = 0; i < Count; i++)
        {
            // Doubled, because the bar is centred on the axis and grows both ways,
            // so a loud passage reads as a thick symmetric band rather than a row
            // of spikes climbing to one side.
            double height = Math.Max(1, Math.Round(_level[i] * _available) * 2);
            dc.DrawRoundedRectangle(_brush, null, new Rect(x, centreY - (height / 2.0), _barWidth, height), _radius, _radius);
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

        if (RenderSize.Width < 120)
        {
            return;
        }

        dc.DrawText(text, new Point(
            Math.Max(0, (RenderSize.Width - text.Width) / 2.0),
            centreY - (text.Height / 2.0)));
    }

    /// <summary>Measures as whatever it is given, since it draws rather than lays out.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return finalSize;
    }
}
