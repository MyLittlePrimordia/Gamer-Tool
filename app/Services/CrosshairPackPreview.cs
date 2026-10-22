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
/// The pack grid used to show the first frame of every animation, so a pack full of
/// them read as a grid of stills and the only way to see what one did was to choose
/// it and look at the crosshair. Playing them in the tiles means decoding the
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
    /// not a GIF or a WebP, one frame only, too large to decode, or something that
    /// would not decode at all. Null is the normal answer for most of the pack, so
    /// nothing is logged for it.
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
            PictureKind kind = CrosshairFileKinds.FromPath(path);
            if (kind is not (PictureKind.Gif or PictureKind.WebP))
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

            // One reader each, because the two formats arrive in different shapes:
            // a GIF stores pieces of a canvas, a WebP stores whole frames.
            return kind == PictureKind.WebP
                ? BuildWebp(bytes, side, tint)
                : BuildGif(bytes, side, tint);
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK PREVIEW", ex);
            return null;
        }
    }

    private static CrosshairPackPreview? BuildGif(byte[] bytes, int side, Color? tint)
    {
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
                if (slotOf.TryGetValue(i, out int slot))
                {
                    frames[slot] = FrameBitmap(canvas, canvasWidth, canvasHeight, side, tint);
                }
            });

        return Assemble(frames, kept);
    }

    /// <summary>
    /// The same preview out of a WebP. Its frames are already whole pictures, so
    /// there is nothing to compose - only frames to shrink, and the ones the plan
    /// kept to keep.
    /// </summary>
    private static CrosshairPackPreview? BuildWebp(byte[] bytes, int side, Color? tint)
    {
        using WebpReader? reader = WebpReader.Open(bytes);
        if (reader is null || reader.FrameCount < 2)
        {
            return null;
        }

        if (!AnimationPlan.WithinBudget(reader.Width, reader.Height, reader.FrameCount))
        {
            return null;
        }

        List<(int Index, int DelayMs)> kept = AnimationPlan.Reduce(reader.DelaysMs, MaxFrames);
        var slotOf = new Dictionary<int, int>(kept.Count);
        for (int k = 0; k < kept.Count; k++)
        {
            slotOf[kept[k].Index] = k;
        }

        var frames = new BitmapSource?[kept.Count];

        // Read in order, kept or not: the decoder keeps its place, so a skipped
        // frame would only mean rewinding to it later.
        for (int i = 0; i < reader.FrameCount; i++)
        {
            byte[]? canvas = reader.Frame(i);
            if (canvas is null)
            {
                return null;
            }

            if (slotOf.TryGetValue(i, out int slot))
            {
                frames[slot] = FrameBitmap(canvas, reader.Width, reader.Height, side, tint);
            }
        }

        return Assemble(frames, kept);
    }

    /// <summary>
    /// The frames in the order the plan kept them, with the times that go with them.
    /// Null when any of them failed to build, because a tile that played a gap would
    /// be worse than one that stays on its first frame.
    /// </summary>
    private static CrosshairPackPreview? Assemble(BitmapSource?[] frames, IReadOnlyList<(int Index, int DelayMs)> kept)
    {
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

    /// <summary>
    /// One finished frame of an animation as the tile draws it: shrunk into its
    /// square, in the chosen colour when it is a mask, and ready for a thread that is
    /// not the one that made it.
    /// </summary>
    private static BitmapSource FrameBitmap(byte[] canvas, int canvasWidth, int canvasHeight, int side, Color? tint)
    {
        byte[] square = PixelResize.FitSquare(canvas, canvasWidth, canvasHeight, side, out int frameSide);
        if (tint is { } colour)
        {
            Multiply(square, colour);
        }

        BitmapSource bitmap = BitmapSource.Create(
            frameSide, frameSide, 96, 96, PixelFormats.Pbgra32, null, square, frameSide * 4);
        bitmap.Freeze();
        return bitmap;
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
