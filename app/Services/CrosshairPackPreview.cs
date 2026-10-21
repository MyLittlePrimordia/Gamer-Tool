using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace GamerTool.Services;

/// <summary>
/// A pack tile's animation, decoded small and ready to play in the browser.
/// <para>
/// The pack grid used to show the first frame of every GIF, so a pack full of
/// animations read as a grid of stills and the only way to see what one did was to
/// choose it and look at the crosshair. Playing them in the tiles means decoding the
/// frames, which is too much work to do while the grid is being filled, so this is
/// built on a worker thread after the tile is already on screen showing its first
/// frame.
/// </para>
/// <para>
/// Everything here is bounded on purpose. Frames are thinned to
/// <see cref="MaxFrames" /> by the importer's own rule, so a long animation plays at
/// its real speed rather than in slow motion, and a picture too big to be a crosshair
/// is refused outright instead of taking the machine down for a preview.
/// </para>
/// </summary>
internal sealed class CrosshairPackPreview
{
    /// <summary>Most frames a tile keeps. Enough to read the motion, small enough for dozens of tiles.</summary>
    public const int MaxFrames = 20;

    private CrosshairPackPreview(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysMs)
    {
        Frames = frames;
        DelaysMs = delaysMs;
    }

    public IReadOnlyList<BitmapSource> Frames { get; }

    public IReadOnlyList<int> DelaysMs { get; }

    /// <summary>
    /// Build the preview, or null when the file is not an animation this can play:
    /// not a GIF, one frame only, too large to decode, or something that would not
    /// decode at all. Null is the normal answer for most of the pack, so nothing is
    /// logged for it.
    /// </summary>
    /// <param name="path">The picture in the pack folder.</param>
    /// <param name="side">How wide each frame is drawn, in pixels.</param>
    /// <param name="tint">
    /// The colour to multiply every frame by, for a picture that is drawn in the
    /// chosen colour, or null to leave the frames as they came.
    /// </param>
    public static CrosshairPackPreview? Build(string path, int side, Color? tint)
    {
        side = Math.Clamp(side, 8, 96);

        try
        {
            if (CrosshairFileKinds.FromPath(path) != PictureKind.Gif)
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked since the grid was read: a tile with no animation.
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var decoder = new GifBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            int count = decoder.Frames.Count;
            if (count < 2)
            {
                return null;
            }

            (int canvasWidth, int canvasHeight) = CrosshairImageStore.GifCanvas(decoder);

            // The same ceiling the importer applies. A preview is not worth decoding
            // something that would not be accepted as a crosshair anyway.
            if (!AnimationPlan.WithinBudget(canvasWidth, canvasHeight, count))
            {
                return null;
            }

            List<int> delays = new(count);
            for (int i = 0; i < count; i++)
            {
                delays.Add(CrosshairImageStore.GifFrameDelay(decoder.Frames[i]));
            }

            List<(int Index, int DelayMs)> kept = AnimationPlan.Reduce(delays, MaxFrames);
            var slotOf = new Dictionary<int, int>(kept.Count);
            for (int k = 0; k < kept.Count; k++)
            {
                slotOf[kept[k].Index] = k;
            }

            var frames = new BitmapSource?[kept.Count];
            GifFrameCompositor.Compose(
                canvasWidth,
                canvasHeight,
                count,
                i => CrosshairImageStore.GifFrame(decoder.Frames[i]),
                (i, canvas) =>
                {
                    if (!slotOf.TryGetValue(i, out int slot))
                    {
                        return;
                    }

                    byte[] square = PixelResize.FitSquare(canvas, canvasWidth, canvasHeight, side, out int frameSide);
                    if (tint is { } colour)
                    {
                        Multiply(square, colour);
                    }

                    BitmapSource bitmap = BitmapSource.Create(
                        frameSide, frameSide, 96, 96, PixelFormats.Pbgra32, null, square, frameSide * 4);
                    bitmap.Freeze();
                    frames[slot] = bitmap;
                });

            var built = new BitmapSource[kept.Count];
            var timings = new int[kept.Count];
            for (int k = 0; k < kept.Count; k++)
            {
                BitmapSource? frame = frames[k];
                if (frame is null)
                {
                    return null;
                }

                built[k] = frame;
                timings[k] = kept[k].DelayMs;
            }

            return new CrosshairPackPreview(built, timings);
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK PREVIEW", ex);
            return null;
        }
    }

    /// <summary>
    /// Multiply premultiplied BGRA by a colour, which is what the composer does to a
    /// picture that answers to the swatches. Done here rather than by the tile's brush
    /// because a tile that changes frame cannot keep an opacity mask in step.
    /// </summary>
    private static void Multiply(byte[] pixels, Color colour)
    {
        if (colour.R == 255 && colour.G == 255 && colour.B == 255)
        {
            return;
        }

        for (int p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = (byte)(pixels[p] * colour.B / 255);
            pixels[p + 1] = (byte)(pixels[p + 1] * colour.G / 255);
            pixels[p + 2] = (byte)(pixels[p + 2] * colour.R / 255);
        }
    }
}
