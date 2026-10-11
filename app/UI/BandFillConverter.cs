using System;
using System.Globalization;
using System.Windows.Data;

namespace GamerTool.UI;

/// <summary>
/// Works out how long the coloured part of a bipolar fader should be, measured out
/// from the 0 line along whichever axis it runs. A value pushed up or right lights
/// the space past centre, a value pushed down or left lights the space before it,
/// and a control at rest shows nothing.
/// <para>
/// This runs in the template so it is correct from the first layout pass rather
/// than needing a code-behind nudge once the control has been measured.
/// </para>
/// <para>
/// The size handed in is the whole track, not the half this converter is drawing,
/// so the result is halved here. Reading the templated parent's size is
/// deliberate: binding to an ancestor Grid by type reaches whichever Grid happens
/// to be nearest, which in the vertical template is the half sized wrapper rather
/// than the track, and the fill then lands at the wrong length.
/// </para>
/// </summary>
public sealed class BandFillConverter : IMultiValueConverter
{
    /// <summary>
    /// True for the half above or right of the 0 line, false for the half below
    /// or left of it.
    /// </summary>
    public bool Above { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not double value
            || values[1] is not double minimum
            || values[2] is not double maximum
            || values[3] is not double height)
        {
            return 0.0;
        }

        if (height <= 0)
        {
            return 0.0;
        }

        // The zero line sits in the middle of the travel, which is the middle of
        // the range for both the EQ bands and the colour trims.
        double centre = (minimum + maximum) / 2.0;
        double span = Above ? maximum - centre : centre - minimum;
        if (span <= 0)
        {
            return 0.0;
        }

        double travel = Above ? value - centre : centre - value;
        double fraction = Math.Clamp(travel / span, 0.0, 1.0);
        return fraction * (height / 2.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return Array.Empty<object>();
    }
}
