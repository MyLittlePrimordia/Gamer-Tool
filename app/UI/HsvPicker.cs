using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GamerTool.UI;

/// <summary>
/// A small colour picker in the usual shape: a square where the left edge is
/// white and the bottom edge is black, with the chosen hue filling the top right,
/// and a rainbow strip underneath that picks the hue.
/// <para>
/// Drawn by hand in one <c>OnRender</c> and nothing else, so it is a couple of
/// gradients and two small markers. There is no dialog, nothing to open and no
/// OK button: the colour changes as the mouse moves and the owner reads it from
/// <see cref="ColorChanged"/>.
/// </para>
/// </summary>
public sealed class HsvPicker : FrameworkElement
{
    private const double PickerWidth = 248.0;
    private const double SquareHeight = 156.0;
    private const double Gap = 12.0;
    private const double StripHeight = 20.0;
    private const double Corner = 6.0;

    private static readonly Brush HueBrush = BuildHueBrush();

    private enum Drag
    {
        None,
        Square,
        Strip
    }

    private Drag _drag;

    private double _hue;

    private double _saturation = 1.0;

    private double _value = 1.0;

    public HsvPicker()
    {
        Width = PickerWidth;
        Height = SquareHeight + Gap + StripHeight;
        Cursor = Cursors.Cross;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>Raised when the user moves the markers. Not raised by <see cref="SetColor"/>.</summary>
    public event EventHandler? ColorChanged;

    /// <summary>The colour now picked.</summary>
    public Color Color
    {
        get
        {
            HsvToRgb(_hue, _saturation, _value, out byte r, out byte g, out byte b);
            return Color.FromRgb(r, g, b);
        }
    }

    /// <summary>Put the markers where a colour is, without raising <see cref="ColorChanged"/>.</summary>
    public void SetColor(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out double h, out double s, out double v);

        // A grey has no hue of its own, and snapping the strip to red every time
        // one is set would throw away where the user had it.
        if (s > 0.001 && v > 0.001)
        {
            _hue = h;
        }

        _saturation = s;
        _value = v;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(PickerWidth, SquareHeight + Gap + StripHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The square: the hue at full strength, then white fading in from the left
        // and black fading in from the bottom. The two gradients over the base give
        // every saturation and brightness of that hue.
        var square = new Rect(0, 0, PickerWidth, SquareHeight);
        HsvToRgb(_hue, 1.0, 1.0, out byte hr, out byte hg, out byte hb);

        dc.PushClip(new RectangleGeometry(square, Corner, Corner));
        dc.DrawRectangle(Frozen(Color.FromRgb(hr, hg, hb)), null, square);
        dc.DrawRectangle(
            Gradient(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
            null,
            square);
        dc.DrawRectangle(
            Gradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
            null,
            square);
        dc.Pop();

        var strip = new Rect(0, SquareHeight + Gap, PickerWidth, StripHeight);
        dc.DrawRoundedRectangle(HueBrush, null, strip, Corner, Corner);

        // The square's marker: a ring that shows on light and dark alike.
        var mark = new Point(_saturation * PickerWidth, (1.0 - _value) * SquareHeight);
        dc.DrawEllipse(null, new Pen(Frozen(Colors.Black), 3.0), mark, 7.0, 7.0);
        dc.DrawEllipse(null, new Pen(Frozen(Colors.White), 1.5), mark, 7.0, 7.0);

        // The strip's marker: a pill standing across the strip.
        double x = Math.Clamp(_hue / 360.0, 0.0, 1.0) * PickerWidth;
        var pill = new Rect(x - 3.0, strip.Top - 2.0, 6.0, StripHeight + 4.0);
        dc.DrawRoundedRectangle(Frozen(Colors.White), new Pen(Frozen(Colors.Black), 1.0), pill, 3.0, 3.0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point at = e.GetPosition(this);
        _drag = at.Y <= SquareHeight + (Gap / 2.0) ? Drag.Square : Drag.Strip;
        CaptureMouse();
        Update(at);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag != Drag.None && e.LeftButton == MouseButtonState.Pressed)
        {
            Update(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = Drag.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Update(Point at)
    {
        // Clamped, so dragging out past an edge holds the marker on the edge
        // rather than losing it.
        double x = Math.Clamp(at.X, 0.0, PickerWidth);

        if (_drag == Drag.Square)
        {
            _saturation = x / PickerWidth;
            _value = 1.0 - (Math.Clamp(at.Y, 0.0, SquareHeight) / SquareHeight);
        }
        else if (_drag == Drag.Strip)
        {
            _hue = Math.Min(359.999, x / PickerWidth * 360.0);
        }

        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color from, Color to, Point start, Point end)
    {
        var brush = new LinearGradientBrush(from, to, start, end);
        brush.Freeze();
        return brush;
    }

    private static Brush BuildHueBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
        {
            HsvToRgb(i * 60.0 % 360.0, 1.0, 1.0, out byte r, out byte g, out byte b);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>Hue in degrees, saturation and value from 0 to 1.</summary>
    public static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        double c = v * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = v - c;

        double rr, gg, bb;
        if (h < 60.0) { rr = c; gg = x; bb = 0; }
        else if (h < 120.0) { rr = x; gg = c; bb = 0; }
        else if (h < 180.0) { rr = 0; gg = c; bb = x; }
        else if (h < 240.0) { rr = 0; gg = x; bb = c; }
        else if (h < 300.0) { rr = x; gg = 0; bb = c; }
        else { rr = c; gg = 0; bb = x; }

        r = (byte)Math.Round((rr + m) * 255.0);
        g = (byte)Math.Round((gg + m) * 255.0);
        b = (byte)Math.Round((bb + m) * 255.0);
    }

    public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rr = r / 255.0;
        double gg = g / 255.0;
        double bb = b / 255.0;
        double max = Math.Max(rr, Math.Max(gg, bb));
        double min = Math.Min(rr, Math.Min(gg, bb));
        double delta = max - min;

        v = max;
        s = max <= 0.0 ? 0.0 : delta / max;

        if (delta <= 0.0)
        {
            h = 0.0;
        }
        else if (max == rr)
        {
            h = 60.0 * (((gg - bb) / delta) % 6.0);
        }
        else if (max == gg)
        {
            h = 60.0 * (((bb - rr) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((rr - gg) / delta) + 4.0);
        }

        if (h < 0.0)
        {
            h += 360.0;
        }
    }
}
