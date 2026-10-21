using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GamerTool.UI;

/// <summary>
/// The rainbow disc on the custom-colour swatch.
/// <para>
/// It is a bitmap rather than a brush because WPF has gradients that run in a
/// straight line and gradients that run out from a middle, and none that run round
/// a circle. Hue is the angle, saturation is the distance from the middle, so the
/// wheel is built once here, frozen, and shared by every swatch that shows it.
/// </para>
/// <para>
/// Sixty-four pixels across is enough: the swatch draws it at twenty-two, and the
/// edge is eased to transparent so scaling it up on a high-DPI display leaves no
/// hard staircase on the rim.
/// </para>
/// </summary>
public static class ColorWheel
{
    private const int Side = 64;

    /// <summary>The wheel itself, as a square image whose corners are transparent.</summary>
    public static ImageSource Wheel { get; } = Build();

    private static ImageSource Build()
    {
        var pixels = new byte[Side * Side * 4];
        double centre = (Side - 1) / 2.0;

        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                double dx = x - centre;
                double dy = y - centre;
                double radius = Math.Sqrt((dx * dx) + (dy * dy)) / centre;
                if (radius > 1.0)
                {
                    // Outside the disc. The ellipse it fills clips these anyway, but
                    // leaving them transparent keeps the ImageBrush honest if it is
                    // ever drawn somewhere the clip does not apply.
                    continue;
                }

                double hue = ((Math.Atan2(dy, dx) * 180.0 / Math.PI) + 360.0) % 360.0;
                HsvPicker.HsvToRgb(hue, Math.Min(1.0, radius), 1.0, out byte r, out byte g, out byte b);

                // Full strength inside, fading out over the last pixel of radius.
                double alpha = Math.Clamp((1.0 - radius) * centre, 0.0, 1.0);

                int at = ((y * Side) + x) * 4;
                pixels[at] = b;
                pixels[at + 1] = g;
                pixels[at + 2] = r;
                pixels[at + 3] = (byte)Math.Round(alpha * 255.0);
            }
        }

        BitmapSource bitmap = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Bgra32, null, pixels, Side * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
