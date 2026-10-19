using System;

namespace GamerTool.Services;

/// <summary>
/// Where the crosshair sits on the tab's preview picture.
/// <para>
/// The preview pictures show a soldier standing in the middle of a corridor, and
/// a crosshair at the exact middle of the picture lands on his belt. Players do not
/// aim there. In a shooter the crosshair rests on the upper chest, the broad part
/// of the body that is easy to hold and that a shot to the head is a small flick
/// from, so that is where the preview puts it. The picture is a still, and the
/// real overlay on the screen is unaffected: it is always at the middle of the
/// monitor, as the game's own crosshair would be.
/// </para>
/// </summary>
internal static class CrosshairScene
{
    /// <summary>The side of the preview pictures, in pixels. Both are square.</summary>
    public const double PictureSide = 768.0;

    /// <summary>Across, as a fraction of the picture. The soldier is centred.</summary>
    public const double AimX = 384.0 / PictureSide;

    /// <summary>Down, as a fraction of the picture: the middle of his upper chest plate, between collar and ribs.</summary>
    public const double AimY = 335.0 / PictureSide;

    /// <summary>
    /// How far from the middle of the box the crosshair has to be drawn for it to
    /// land on the aim point. The picture fills the box and is cropped to do it
    /// (<c>UniformToFill</c>), so the point moves with the scale, not just with the
    /// box. Positive is right and down.
    /// </summary>
    public static (double Dx, double Dy) Offset(double boxWidth, double boxHeight)
    {
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            return (0, 0);
        }

        double drawn = PictureSide * Math.Max(boxWidth / PictureSide, boxHeight / PictureSide);
        double left = (boxWidth - drawn) / 2.0;
        double top = (boxHeight - drawn) / 2.0;

        return (
            left + (AimX * drawn) - (boxWidth / 2.0),
            top + (AimY * drawn) - (boxHeight / 2.0));
    }
}
