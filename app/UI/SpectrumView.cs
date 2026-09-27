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

namespace GamerTool.UI;

/// <summary>
/// Draws a live spectrum of the preview track into a plain Grid.
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
    private const int Bars = 48;
    private const double LowHz = 40.0;
    private const double HighHz = 16000.0;

    private readonly Grid _host;
    private readonly Border[] _bars = new Border[Bars];

    /// <summary>The row of bars, inset so the group can be centred on its own.</summary>
    private Grid _barGrid = null!;
    private readonly double[] _level = new double[Bars];
    private readonly double[] _barCentreHz = new double[Bars];
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

        for (int i = 0; i < Bars; i++)
        {
            double t = (i + 0.5) / Bars;
            _barCentreHz[i] = LowHz * Math.Pow(HighHz / LowHz, t);
        }

        Load();
        BuildBars();

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
        for (int i = 0; i < Bars; i++)
        {
            _level[i] = 0;
            _bars[i].Height = 4;
        }
    }

    public void Release()
    {
        _timer.Stop();
    }

    /// <summary>
    /// Reads the pre-computed magnitudes. Header layout, kept in lockstep with
    /// the generator that bakes Assets\spectrum.bin from the preview track:
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

            if (bars != Bars || hop <= 0 || rate <= 0 || length <= 0 || raw.Length < HeaderSize + length)
            {
                TraceLog.Write("SPECTRUM bad header bars=" + bars + " hop=" + hop + " len=" + length);
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

        // The bars live in their own grid rather than directly in the host, so
        // that the row of them can be inset to centre the group. The axis stays
        // in the host and keeps the full width, which is right: the zero line is
        // the width of the box, the bars are the width that divides evenly.
        _barGrid = new Grid();
        _barGrid.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_barGrid, 0);
        Grid.SetColumn(_barGrid, 0);
        _host.Children.Add(_barGrid);

        for (int i = 0; i < Bars; i++)
        {
            // Widths are filled in by LayoutColumns once the real width is known.
            _barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

            // Centred in the upper cell, so Height can simply be twice the level
            // and the bar reaches the same distance up as down. Width is centred
            // too rather than inset by a margin, so the gap between neighbours is
            // the same everywhere.
            Border bar = new()
            {
                CornerRadius = new CornerRadius(1.5),
                Background = BarBrush(i),
                Height = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(bar, i);
            Grid.SetRow(bar, 0);
            _bars[i] = bar;
            _barGrid.Children.Add(bar);
        }

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
        if (_barGrid is null || _barGrid.ColumnDefinitions.Count != Bars || _host.ActualWidth <= 0.0)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(_host).DpiScaleX;
        if (scale <= 0.0)
        {
            scale = 1.0;
        }

        int deviceWidth = (int)Math.Round(_host.ActualWidth * scale);
        int pitch = deviceWidth / Bars;

        // Below about three pixels of pitch there is no room for a bar and a gap,
        // and a sub pixel bar would only look like noise.
        if (pitch < 3)
        {
            return;
        }

        int groupWidth = pitch * Bars;
        int insetLeft = (deviceWidth - groupWidth) / 2;
        int insetRight = deviceWidth - groupWidth - insetLeft;

        _barGrid.Margin = new Thickness(insetLeft / scale, 0, insetRight / scale, 0);

        const double GapPixels = 2.0;
        for (int i = 0; i < Bars; i++)
        {
            _barGrid.ColumnDefinitions[i].Width = new GridLength(pitch / scale);
            if (_bars[i] is not null)
            {
                _bars[i].Width = Math.Max(1.0, (pitch - GapPixels) / scale);
            }
        }
    }


    private static Brush BarBrush(int index)
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

        int offset = first * Bars;
        int next = playing && first + 1 < _frameCount ? offset + Bars : offset;

        for (int bar = 0; bar < Bars; bar++)
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

            // Half the available height each way from the axis. With no floor at
            // all, silence is genuinely empty and only the zero line is left.
            _bars[bar].Height = playing || _level[bar] > 0.0
                ? Math.Max(1, Math.Round(_level[bar] * available) * 2)
                : 0.0;
        }
    }

    /// <summary>Centre frequency of a bar, exposed so the window can match EQ bands to bars.</summary>
    public static double BarCentre(int bar)
    {
        double t = (bar + 0.5) / Bars;
        return LowHz * Math.Pow(HighHz / LowHz, t);
    }
}
