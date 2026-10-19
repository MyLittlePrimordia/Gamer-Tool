using System;

namespace GamerTool.Services;

/// <summary>
/// Scales a picture down into a square, on plain byte arrays.
/// <para>
/// Kept free of WPF on purpose. Drawing the picture into a <c>RenderTargetBitmap</c>
/// needs a thread that owns a dispatcher, and importing an animation touches dozens
/// of frames, so doing the work on raw pixels is what lets the import run on a
/// worker thread instead of freezing the window, and what lets a test check the
/// result without a screen.
/// </para>
/// <para>
/// Pixels are premultiplied BGRA throughout, the same as the rest of the crosshair
/// pipeline. Averaging premultiplied pixels is the correct way to shrink a picture
/// with transparency: averaging straight-alpha ones bleeds the colour of fully
/// transparent pixels into the edges and leaves a dark fringe round the shape.
/// </para>
/// </summary>
internal static class PixelResize
{
    /// <summary>
    /// Fit the picture inside a square no bigger than <paramref name="maxSide"/>,
    /// centred and transparent round the short side. A picture already small
    /// enough is never enlarged, so what is stored is never softer than what came in.
    /// </summary>
    /// <param name="side">The side of the square returned.</param>
    public static byte[] FitSquare(byte[] pixels, int width, int height, int maxSide, out int side)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (width < 1 || height < 1 || maxSide < 1 || pixels.Length < width * height * 4)
        {
            throw new ArgumentException("The picture has no pixels to scale.");
        }

        double scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        int scaledWidth = scale >= 1.0 ? width : Math.Max(1, (int)Math.Round(width * scale));
        int scaledHeight = scale >= 1.0 ? height : Math.Max(1, (int)Math.Round(height * scale));
        side = Math.Max(scaledWidth, scaledHeight);

        byte[] scaled = scale >= 1.0 ? pixels : Shrink(pixels, width, height, scaledWidth, scaledHeight);

        byte[] square = new byte[side * side * 4];
        int offsetX = (side - scaledWidth) / 2;
        int offsetY = (side - scaledHeight) / 2;
        for (int y = 0; y < scaledHeight; y++)
        {
            Buffer.BlockCopy(scaled, y * scaledWidth * 4, square, (((y + offsetY) * side) + offsetX) * 4, scaledWidth * 4);
        }

        return square;
    }

    /// <summary>
    /// Area average: each target pixel is the mean of the source pixels it covers,
    /// weighted by how much of each it covers. Slower than nearest neighbour and
    /// much better looking on a crosshair, whose whole job is thin clean lines.
    /// </summary>
    private static byte[] Shrink(byte[] source, int width, int height, int targetWidth, int targetHeight)
    {
        byte[] target = new byte[targetWidth * targetHeight * 4];
        double stepX = (double)width / targetWidth;
        double stepY = (double)height / targetHeight;

        for (int ty = 0; ty < targetHeight; ty++)
        {
            double top = ty * stepY;
            double bottom = top + stepY;
            int firstRow = (int)Math.Floor(top);
            int lastRow = Math.Min(height - 1, (int)Math.Ceiling(bottom) - 1);

            for (int tx = 0; tx < targetWidth; tx++)
            {
                double left = tx * stepX;
                double right = left + stepX;
                int firstColumn = (int)Math.Floor(left);
                int lastColumn = Math.Min(width - 1, (int)Math.Ceiling(right) - 1);

                double b = 0, g = 0, r = 0, a = 0, total = 0;
                for (int y = firstRow; y <= lastRow; y++)
                {
                    double weightY = Math.Min(bottom, y + 1) - Math.Max(top, y);
                    if (weightY <= 0)
                    {
                        continue;
                    }

                    for (int x = firstColumn; x <= lastColumn; x++)
                    {
                        double weightX = Math.Min(right, x + 1) - Math.Max(left, x);
                        if (weightX <= 0)
                        {
                            continue;
                        }

                        double weight = weightX * weightY;
                        int at = ((y * width) + x) * 4;
                        b += source[at] * weight;
                        g += source[at + 1] * weight;
                        r += source[at + 2] * weight;
                        a += source[at + 3] * weight;
                        total += weight;
                    }
                }

                if (total <= 0)
                {
                    continue;
                }

                int to = ((ty * targetWidth) + tx) * 4;
                target[to] = (byte)Math.Min(255, Math.Round(b / total));
                target[to + 1] = (byte)Math.Min(255, Math.Round(g / total));
                target[to + 2] = (byte)Math.Min(255, Math.Round(r / total));
                target[to + 3] = (byte)Math.Min(255, Math.Round(a / total));
            }
        }

        return target;
    }
}
