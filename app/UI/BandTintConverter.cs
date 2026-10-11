using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace GamerTool.UI;

/// <summary>
/// Gives a fader's fill an opacity that follows the value, the way FxSound's
/// equaliser does: a band pushed to +12 is solid, one at rest is middling, and
/// one pulled to -12 is faint.
///
/// It is opacity on the fill only, so the groove, the handle and the zero tick
/// stay equally legible at every value. Without it a deep cut and a full boost
/// look equally heavy, and the eye cannot tell at a glance which end of the
/// curve is doing the work.
/// </summary>
public sealed class BandTintConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[1] is not double value
            || values[2] is not double minimum
            || values[3] is not double maximum)
        {
            return Brushes.Transparent;
        }

        // The template hands over the control's Foreground, which is a Brush and
        // not a Color. Taking a Color here instead would quietly return
        // transparent for every fader and the progress bar would never show at
        // all, so a solid brush is unwrapped and anything else is passed through
        // untouched.
        if (!TryColour(values[0], out Color colour))
        {
            return values[0] as Brush ?? Brushes.Transparent;
        }

        double span = maximum - minimum;
        if (span <= 0)
        {
            return Frozen(colour, 1.0);
        }

        // Where the value sits across the travel, 0 at the minimum and 1 at the
        // maximum. A boost lands near 1 and a cut near 0, which is the direction
        // the opacity has to follow.
        double fraction = Math.Clamp((value - minimum) / span, 0.0, 1.0);
        double alpha = 0.22 + (0.78 * fraction);
        return Frozen(colour, alpha);
    }

    /// <summary>Pulls a colour out of whatever the template supplied.</summary>
    private static bool TryColour(object? source, out Color colour)
    {
        switch (source)
        {
            case Color direct:
                colour = direct;
                return true;

            case SolidColorBrush solid:
                colour = solid.Color;
                return true;

            default:
                colour = Colors.White;
                return false;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return Array.Empty<object>();
    }

    private static Brush Frozen(Color colour, double alpha)
    {
        SolidColorBrush brush = new(Color.FromArgb((byte)Math.Round(255.0 * Math.Clamp(alpha, 0.0, 1.0)), colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
