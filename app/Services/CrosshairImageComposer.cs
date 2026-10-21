using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Turns a picture into a crosshair that answers to the same controls as a drawn
/// one: colour, thickness, outline and glow.
/// <para>
/// The picture is scaled to its final size in real screen pixels first and the
/// work is done on those pixels, so an outline that is one pixel on the sliders
/// is one pixel on the screen, and the tab's preview and the overlay come out
/// identical apart from the preview's magnification. The result is one small
/// bitmap that is drawn once. It is rebuilt only when a setting changes, so a
/// picture that is simply sitting on the screen costs nothing, the same as a
/// drawn shape.
/// </para>
/// <para>
/// Size measures the picture's visible pixels, not its file. A picture is drawn
/// from the rectangle its art occupies, so a small crosshair in a large canvas
/// fills Size instead of sitting in the middle of it at a fraction of its size,
/// and the shape of the art is kept rather than squared off.
/// </para>
/// <list type="bullet">
/// <item><b>Colour</b> multiplies the picture by the chosen colour. A white
/// picture, which is what the pack is made of, becomes exactly that colour; a
/// coloured picture is tinted, and white leaves it as it came. A picture set to
/// keep its own colours skips the multiply entirely, so what was drawn is what
/// is seen.</item>
/// <item><b>Thickness</b> above 2 makes the lines of the picture bolder, by
/// growing its shape outward. At 2 and below the picture is untouched, so a
/// picture looks as drawn until the slider is moved.</item>
/// <item><b>Gap</b> clears a disc at the middle of the picture, the way it opens
/// the middle of a drawn cross.</item>
/// <item><b>Outline</b> is a one pixel black edge round the whole shape.</item>
/// <item><b>Glow</b> is a soft halo in the colour, made by blurring the shape.
/// It stays in the colour even when the picture keeps its own: the halo is drawn
/// round the picture rather than part of it, and a multi-coloured picture has no
/// single colour to blur in its place.</item>
/// </list>
/// </summary>
public static class CrosshairImageComposer
{
    /// <summary>How far the glow reaches past the shape, in crosshair pixels.</summary>
    private const double GlowReach = 6.0;

    /// <summary>
    /// The finished bitmap and the side of it in crosshair pixels, or null if the
    /// picture could not be processed. <paramref name="scale"/> is how many screen
    /// pixels one crosshair pixel is.
    /// </summary>
    public static BitmapSource? Compose(BitmapSource source, CrosshairPreset preset, double scale, out double sideUnits)
    {
        return Compose(source, preset, scale, default, out sideUnits);
    }

    /// <summary>
    /// The same, for a picture that knows which part of itself is drawn.
    /// <paramref name="content"/> is that part, in source pixels, or empty for all of
    /// it. Size then measures the crosshair rather than the transparent margin the
    /// file keeps round it: an imported animation is usually the size of its canvas,
    /// not of the art, and scaling the canvas made a small crosshair in a big file
    /// impossible to make any bigger.
    /// </summary>
    public static BitmapSource? Compose(
        BitmapSource source,
        CrosshairPreset preset,
        double scale,
        Int32Rect content,
        out double sideUnits)
    {
        sideUnits = 0;

        try
        {
            scale = Math.Max(0.1, scale);
            int side = Math.Max(4, (int)Math.Round(preset.Size * scale));
            int grow = (int)Math.Round(Math.Max(0, (preset.Thickness - 2) * 0.5) * scale);
            int outline = preset.Outline ? Math.Max(1, (int)Math.Round(scale)) : 0;
            int glow = preset.Glow ? (int)Math.Ceiling(GlowReach * scale) : 0;

            int pad = grow + outline + glow + 2;
            int width = side + (2 * pad);
            if (width % 2 != 0)
            {
                width++;
            }

            (BitmapSource picture, int drawWidth, int drawHeight) = FitToSize(source, content, side);
            byte[] image = Render(picture, drawWidth, drawHeight, width);

            // Skipped rather than multiplied by white, because a picture rarely has
            // pure white lines: multiplying a pale blue one by white is not the same
            // as leaving it alone, and pale pixels are exactly where that shows.
            if (!preset.KeepOwnColours)
            {
                Tint(image, preset.Color);
            }

            if (grow > 0)
            {
                image = Grow(image, width, grow);
            }

            // After the growing, so thicker lines cannot creep back into the hole,
            // and before the outline and glow, so both follow the edge of the hole
            // the way they follow the end of a drawn arm.
            if (preset.PictureGap > 0)
            {
                ClearMiddle(image, width, (int)Math.Round(preset.PictureGap * scale));
            }

            byte[] alpha = AlphaOf(image);
            byte[] edge = outline > 0 ? Dilate(alpha, width, outline) : Array.Empty<byte>();
            byte[] halo = glow > 0 ? Halo(alpha, width, glow) : Array.Empty<byte>();

            byte[] result = new byte[image.Length];
            TintOf(preset.Color, out byte tr, out byte tg, out byte tb);

            for (int i = 0, p = 0; i < alpha.Length; i++, p += 4)
            {
                // Premultiplied BGRA throughout: "over" is src + dst * (1 - srcAlpha).
                int b = 0, g = 0, r = 0, a = 0;

                if (glow > 0)
                {
                    int ga = halo[i];
                    b = tb * ga / 255;
                    g = tg * ga / 255;
                    r = tr * ga / 255;
                    a = ga;
                }

                if (outline > 0)
                {
                    int oa = edge[i];
                    int keep = 255 - oa;
                    b = b * keep / 255;
                    g = g * keep / 255;
                    r = r * keep / 255;
                    a = (a * keep / 255) + oa;
                }

                int ia = image[p + 3];
                int under = 255 - ia;
                b = image[p] + (b * under / 255);
                g = image[p + 1] + (g * under / 255);
                r = image[p + 2] + (r * under / 255);
                a = ia + (a * under / 255);

                result[p] = (byte)Math.Min(255, b);
                result[p + 1] = (byte)Math.Min(255, g);
                result[p + 2] = (byte)Math.Min(255, r);
                result[p + 3] = (byte)Math.Min(255, a);
            }

            BitmapSource bitmap = BitmapSource.Create(
                width, width, 96, 96, PixelFormats.Pbgra32, null, result, width * 4);
            bitmap.Freeze();

            sideUnits = width / scale;
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR COMPOSE", ex);
            return null;
        }
    }

    /// <summary>
    /// The part of the picture that is drawn, and the size to draw it at: the content
    /// rectangle cropped out of the source, with its longest side made exactly
    /// <paramref name="side"/> and the other side kept in proportion, so a wide
    /// crosshair is not squashed into a square.
    /// </summary>
    private static (BitmapSource Picture, int Width, int Height) FitToSize(
        BitmapSource source,
        Int32Rect content,
        int side)
    {
        BitmapSource picture = source;
        int width = source.PixelWidth;
        int height = source.PixelHeight;

        if (content.Width > 0
            && content.Height > 0
            && content.X >= 0
            && content.Y >= 0
            && content.X + content.Width <= width
            && content.Y + content.Height <= height
            && (content.Width != width || content.Height != height))
        {
            var cropped = new CroppedBitmap(source, content);
            cropped.Freeze();
            picture = cropped;
            width = content.Width;
            height = content.Height;
        }

        if (width < 1 || height < 1)
        {
            return (picture, side, side);
        }

        return width >= height
            ? (picture, side, Math.Max(1, (int)Math.Round((double)side * height / width)))
            : (picture, Math.Max(1, (int)Math.Round((double)side * width / height)), side);
    }

    /// <summary>Draw the picture, scaled, in the middle of a transparent square and read the pixels back.</summary>
    private static byte[] Render(BitmapSource source, int drawWidth, int drawHeight, int width)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(
                (width - drawWidth) / 2.0,
                (width - drawHeight) / 2.0,
                drawWidth,
                drawHeight));
        }

        var target = new RenderTargetBitmap(width, width, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        byte[] pixels = new byte[width * width * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void TintOf(string colour, out byte r, out byte g, out byte b)
    {
        if (!CrosshairPreset.TryParseColor(colour, out r, out g, out b))
        {
            r = 0x3C;
            g = 0xFF;
            b = 0x5A;
        }
    }

    /// <summary>
    /// Make a disc at the middle of the picture transparent. That is what Gap does
    /// to a drawn cross, which starts its arms that far out from the centre, and it
    /// does the same for a picture of one. The picture is turned afterwards, and a
    /// disc looks the same whichever way it is turned.
    /// </summary>
    private static void ClearMiddle(byte[] pixels, int width, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        // The width is even, so the middle falls on the edge between two pixels and
        // each pixel is judged by its own centre.
        double middle = width / 2.0;
        double limit = (double)radius * radius;

        for (int y = 0; y < width; y++)
        {
            double dy = y + 0.5 - middle;
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - middle;
                if ((dx * dx) + (dy * dy) < limit)
                {
                    Array.Clear(pixels, ((y * width) + x) * 4, 4);
                }
            }
        }
    }

    /// <summary>Multiply every pixel by the colour. Premultiplied pixels scale the same way as plain ones.</summary>
    private static void Tint(byte[] pixels, string colour)
    {
        TintOf(colour, out byte r, out byte g, out byte b);
        if (r == 255 && g == 255 && b == 255)
        {
            return;
        }

        for (int p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = (byte)(pixels[p] * b / 255);
            pixels[p + 1] = (byte)(pixels[p + 1] * g / 255);
            pixels[p + 2] = (byte)(pixels[p + 2] * r / 255);
        }
    }

    private static byte[] AlphaOf(byte[] pixels)
    {
        byte[] alpha = new byte[pixels.Length / 4];
        for (int i = 0, p = 3; i < alpha.Length; i++, p += 4)
        {
            alpha[i] = pixels[p];
        }

        return alpha;
    }

    /// <summary>The offsets inside a disc of the given radius.</summary>
    private static (int Dx, int Dy)[] Disc(int radius)
    {
        var offsets = new System.Collections.Generic.List<(int, int)>();
        int limit = (radius * radius) + radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= limit)
                {
                    offsets.Add((dx, dy));
                }
            }
        }

        return offsets.ToArray();
    }

    /// <summary>
    /// Grow the coloured shape outward: each pixel becomes whichever neighbour in
    /// the disc is most opaque, colour and all. That thickens the lines of a
    /// picture without changing what colour they are.
    /// </summary>
    private static byte[] Grow(byte[] pixels, int width, int radius)
    {
        (int Dx, int Dy)[] disc = Disc(radius);
        byte[] result = new byte[pixels.Length];

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int best = -1;
                int bestAlpha = -1;

                foreach ((int dx, int dy) in disc)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width)
                    {
                        continue;
                    }

                    int at = ((ny * width) + nx) * 4;
                    if (pixels[at + 3] > bestAlpha)
                    {
                        bestAlpha = pixels[at + 3];
                        best = at;
                    }
                }

                if (best >= 0)
                {
                    int to = ((y * width) + x) * 4;
                    result[to] = pixels[best];
                    result[to + 1] = pixels[best + 1];
                    result[to + 2] = pixels[best + 2];
                    result[to + 3] = pixels[best + 3];
                }
            }
        }

        return result;
    }

    /// <summary>The alpha plane grown by a radius: the largest value in the disc round each pixel.</summary>
    private static byte[] Dilate(byte[] alpha, int width, int radius)
    {
        (int Dx, int Dy)[] disc = Disc(radius);
        byte[] result = new byte[alpha.Length];

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte best = 0;
                foreach ((int dx, int dy) in disc)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= width)
                    {
                        continue;
                    }

                    byte value = alpha[(ny * width) + nx];
                    if (value > best)
                    {
                        best = value;
                    }
                }

                result[(y * width) + x] = best;
            }
        }

        return result;
    }

    /// <summary>A soft halo: the shape blurred twice with a box filter, then lifted so it reads.</summary>
    private static byte[] Halo(byte[] alpha, int width, int reach)
    {
        int radius = Math.Max(1, reach / 2);
        byte[] blurred = BoxBlur(BoxBlur(alpha, width, radius), width, radius);

        for (int i = 0; i < blurred.Length; i++)
        {
            // Lifted, because a blur spreads the shape thin, then held below
            // half strength so the halo never competes with the crosshair.
            int lifted = Math.Min(255, blurred[i] * 3);
            blurred[i] = (byte)(lifted * 55 / 100);
        }

        return blurred;
    }

    private static byte[] BoxBlur(byte[] source, int width, int radius)
    {
        byte[] horizontal = new byte[source.Length];
        int span = (radius * 2) + 1;

        for (int y = 0; y < width; y++)
        {
            int row = y * width;
            int sum = 0;
            for (int x = -radius; x <= radius; x++)
            {
                if (x >= 0 && x < width)
                {
                    sum += source[row + x];
                }
            }

            for (int x = 0; x < width; x++)
            {
                horizontal[row + x] = (byte)(sum / span);
                int leaving = x - radius;
                int arriving = x + radius + 1;
                if (leaving >= 0)
                {
                    sum -= source[row + leaving];
                }

                if (arriving < width)
                {
                    sum += source[row + arriving];
                }
            }
        }

        byte[] result = new byte[source.Length];
        for (int x = 0; x < width; x++)
        {
            int sum = 0;
            for (int y = -radius; y <= radius; y++)
            {
                if (y >= 0 && y < width)
                {
                    sum += horizontal[(y * width) + x];
                }
            }

            for (int y = 0; y < width; y++)
            {
                result[(y * width) + x] = (byte)(sum / span);
                int leaving = y - radius;
                int arriving = y + radius + 1;
                if (leaving >= 0)
                {
                    sum -= horizontal[(leaving * width) + x];
                }

                if (arriving < width)
                {
                    sum += horizontal[(arriving * width) + x];
                }
            }
        }

        return result;
    }
}
