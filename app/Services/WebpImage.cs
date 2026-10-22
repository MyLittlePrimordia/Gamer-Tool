using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GamerTool.Services;

/// <summary>
/// A WebP file open for reading: its size, how many frames it holds and how long
/// each one lasts.
/// <para>
/// WebP is the one picture format Windows cannot decode out of the box. The rest go
/// through WIC, which every Windows install has; WebP needs a codec from the Store
/// that may or may not be there. So these frames come from Skia, which ships with
/// the program, and arrive in the one form the rest of the crosshair pipeline
/// speaks: premultiplied BGRA, the same as <see cref="PixelResize"/> and
/// <see cref="CrosshairImageStore"/> expect.
/// </para>
/// <para>
/// Plain arrays and no WPF, so an import can run on a worker thread without ever
/// touching the UI thread's decoder. Frames are read in order: the decoder keeps
/// its place, so walking an animation once costs one pass rather than a rewind per
/// frame, and a caller that skips frames still gets the right picture.
/// </para>
/// </summary>
internal sealed class WebpReader : IDisposable
{
    /// <summary>
    /// The largest file taken, before decoding. Above any crosshair anyone needs and
    /// below anything that would make reading the pack folder expensive.
    /// </summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Longest animation taken. The importer keeps 60 frames and thins anything
    /// longer, so this only refuses the absurd: ten seconds at sixty frames a
    /// second is a great deal more animation than a crosshair holds.
    /// </summary>
    public const int MaxFrames = 600;

    private readonly SKData _data;

    private readonly SKCodec _codec;

    private readonly SKImageInfo _info;

    private WebpReader(SKData data, SKCodec codec, SKImageInfo info, int frameCount, int[] delaysMs)
    {
        _data = data;
        _codec = codec;
        _info = info;
        Width = info.Width;
        Height = info.Height;
        FrameCount = frameCount;
        DelaysMs = delaysMs;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Frames in the file. One for a still, which is an animation of one frame.</summary>
    public int FrameCount { get; }

    /// <summary>How long each frame stays up, in milliseconds. Empty delay for a still.</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    /// <summary>
    /// Open a WebP from its bytes, or null when they are not a WebP this can read:
    /// empty, another format wearing the extension, or too large to take.
    /// </summary>
    public static WebpReader? Open(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxFileBytes || !IsWebp(bytes))
        {
            return null;
        }

        SKData? data = null;
        SKCodec? codec = null;

        try
        {
            data = SKData.CreateCopy(bytes);

            // Null, rather than an exception, for a file that is not a picture this
            // build can decode - which is the normal answer for a renamed file.
            codec = SKCodec.Create(data);
            if (codec is null)
            {
                data.Dispose();
                return null;
            }

            if (codec.Info.Width < 1 || codec.Info.Height < 1)
            {
                codec.Dispose();
                data.Dispose();
                return null;
            }

            // A still reports no frames at all rather than one, and is a picture
            // nobody plays.
            int frameCount = Math.Max(1, codec.FrameCount);
            if (frameCount > MaxFrames)
            {
                codec.Dispose();
                data.Dispose();
                return null;
            }

            int[] delays = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                delays[i] = frameCount == 1 ? 0 : DurationMs(codec.FrameInfo[i].Duration);
            }

            var info = new SKImageInfo(
                codec.Info.Width,
                codec.Info.Height,
                SKColorType.Bgra8888,
                codec.Info.AlphaType);

            return new WebpReader(data, codec, info, frameCount, delays);
        }
        catch (Exception ex)
        {
            codec?.Dispose();
            data?.Dispose();
            AppLog.Error("WEBP OPEN", ex);
            return null;
        }
    }

    /// <summary>
    /// One frame, premultiplied BGRA, <c>Width * Height * 4</c> bytes, or null when
    /// that frame will not decode. Frames are asked for in order; asking out of
    /// order is allowed and simply costs the decoder a rewind.
    /// </summary>
    public byte[]? Frame(int index)
    {
        if (index < 0 || index >= FrameCount)
        {
            return null;
        }

        try
        {
            using var bitmap = new SKBitmap(_info);
            if (bitmap.GetPixels() == IntPtr.Zero)
            {
                return null;
            }

            SKCodecResult result = _codec.GetPixels(_info, bitmap.GetPixels(), new SKCodecOptions(index));

            // Incomplete is a truncated file that still had most of its picture,
            // which is worth showing; anything else is one no viewer would draw.
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                return null;
            }

            int rowBytes = Width * 4;
            byte[] pixels = new byte[rowBytes * Height];
            if (bitmap.RowBytes == rowBytes)
            {
                Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), pixels, y * rowBytes, rowBytes);
                }
            }

            Normalise(pixels);
            return pixels;
        }
        catch (Exception ex)
        {
            AppLog.Error("WEBP FRAME", ex);
            return null;
        }
    }

    /// <summary>
    /// The first frame of a WebP file, for a thumbnail, or null when the file is not
    /// there, is not a WebP, or is too large. Never throws: one unreadable picture in
    /// the pack folder must not empty the browser.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? ReadFirstFrame(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes)
            {
                return null;
            }

            using WebpReader? reader = Open(File.ReadAllBytes(path));
            if (reader is null)
            {
                return null;
            }

            byte[]? first = reader.Frame(0);
            return first is null ? null : (first, reader.Width, reader.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Deleted, locked or not ours to read since the grid was listed.
            return null;
        }
    }

    public void Dispose()
    {
        _codec.Dispose();
        _data.Dispose();
    }

    /// <summary>
    /// Whether the bytes are a WebP by their own header. Skia decodes more than WebP,
    /// so asking it alone would let a PNG renamed to .webp in - and an importer that
    /// trusts a name over the file's contents is the thing this app refuses to do
    /// everywhere else.
    /// </summary>
    private static bool IsWebp(byte[] bytes)
    {
        return bytes.Length >= 12
            && bytes[0] == (byte)'R'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W'
            && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B'
            && bytes[11] == (byte)'P';
    }

    /// <summary>
    /// A WebP frame's duration, by the animation rules the rest of the tab uses: a
    /// file that says nothing useful gets the fallback every browser shows instead.
    /// </summary>
    private static int DurationMs(int milliseconds)
    {
        return Math.Clamp(
            milliseconds <= 10 ? AnimationPlan.FallbackDelayMs : milliseconds,
            AnimationPlan.MinDelayMs,
            AnimationPlan.MaxDelayMs);
    }

    /// <summary>
    /// Put the pixels in the form the rest of the pipeline speaks. A decoder that
    /// hands back straight alpha would otherwise leave the soft edge of a crosshair
    /// too bright once it is drawn as premultiplied, and an opaque picture's alpha
    /// channel is not something the decoder promises to fill in.
    /// </summary>
    private void Normalise(byte[] pixels)
    {
        if (_info.AlphaType == SKAlphaType.Opaque)
        {
            for (int p = 3; p < pixels.Length; p += 4)
            {
                pixels[p] = 255;
            }

            return;
        }

        if (_info.AlphaType != SKAlphaType.Unpremul)
        {
            return;
        }

        for (int p = 0; p + 3 < pixels.Length; p += 4)
        {
            int alpha = pixels[p + 3];
            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                pixels[p] = 0;
                pixels[p + 1] = 0;
                pixels[p + 2] = 0;
                continue;
            }

            pixels[p] = (byte)((pixels[p] * alpha + 127) / 255);
            pixels[p + 1] = (byte)((pixels[p + 1] * alpha + 127) / 255);
            pixels[p + 2] = (byte)((pixels[p + 2] * alpha + 127) / 255);
        }
    }
}
