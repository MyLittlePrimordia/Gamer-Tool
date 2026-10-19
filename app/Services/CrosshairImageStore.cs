using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GamerTool.Services;

/// <summary>
/// Keeps the pictures people import as crosshairs.
/// <para>
/// An import is never used as it came. The file is decoded, checked, scaled down
/// to fit a 256 pixel square and written back out as a fresh PNG, so what the
/// overlay later loads is something this app made: no metadata, no odd colour
/// profile, no decompression bomb, and a small file whatever was picked.
/// </para>
/// <para>
/// PNG, GIF, JPEG and BMP are taken, each checked against its own decoder so a
/// file merely renamed to look like one is refused. An animated GIF is stored as a
/// strip of its frames in one PNG, with a small timing file beside it, so the
/// overlay plays something this app made rather than re-reading the original. SVG
/// is left out on purpose, since an SVG is a document that can reference other
/// files, and drawing one would mean shipping a vector engine.
/// </para>
/// <para>
/// Presets refer to a picture by its file name inside this folder, never by a
/// path, so a hand edited or restored profile cannot make the app open a file
/// from somewhere else.
/// </para>
/// </summary>
public static class CrosshairImageStore
{
    /// <summary>The longest side the stored copy of a still keeps.</summary>
    public const int StoredSide = 256;

    /// <summary>
    /// The longest side of each frame of an animation. Smaller than a still's,
    /// because an animation is up to sixty of them side by side and the crosshair
    /// is never drawn bigger than 64 pixels, which even at 200 percent scaling is
    /// 128.
    /// </summary>
    public const int AnimatedSide = 128;

    /// <summary>The largest file taken, before decoding.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>The largest picture taken in either direction, before decoding it in full.</summary>
    public const int MaxPixels = 4096;

    /// <summary>Ends the name of the timing file beside an animation's strip.</summary>
    private const string TimingSuffix = ".anim.json";

    private static readonly Dictionary<string, CrosshairPicture> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder => Path.Combine(ProfileManager.AppDataFolder, "crosshairs");

    /// <summary>A problem worded for the user, thrown inside an import and turned into the message.</summary>
    private sealed class ImportProblem : Exception
    {
        public ImportProblem(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Import one picture. Returns the stored file name, or null with the reason
    /// in <paramref name="problem"/> worded for the user. Safe to call off the UI
    /// thread: it uses decoders and raw pixels and never draws.
    /// </summary>
    public static string? Import(string sourcePath, out string problem)
    {
        problem = string.Empty;

        try
        {
            PictureKind kind = CrosshairFileKinds.FromPath(sourcePath);
            if (kind == PictureKind.None)
            {
                problem = "Pick a PNG, GIF, JPG or BMP picture";
                return null;
            }

            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                problem = "That file is not there any more";
                return null;
            }

            if (info.Length > MaxFileBytes)
            {
                problem = "That picture is too big, keep it under 8 MB";
                return null;
            }

            // Held open for the whole import: the decoder may read frames lazily.
            using var stream = new MemoryStream(File.ReadAllBytes(sourcePath));
            BitmapDecoder decoder = OpenDecoder(stream, kind);

            Directory.CreateDirectory(Folder);
            string name = "custom-" + Guid.NewGuid().ToString("N")[..8] + ".png";

            if (decoder is GifBitmapDecoder gif && gif.Frames.Count > 1)
            {
                StoreAnimation(gif, name);
                return name;
            }

            BitmapSource still = decoder.Frames[0];
            if (still.PixelWidth < 4 || still.PixelHeight < 4)
            {
                problem = "That picture is too small to use";
                return null;
            }

            if (still.PixelWidth > MaxPixels || still.PixelHeight > MaxPixels)
            {
                problem = "That picture is too large, keep it under 4096 pixels";
                return null;
            }

            File.WriteAllBytes(Path.Combine(Folder, name), Normalise(still));
            return name;
        }
        catch (ImportProblem ex)
        {
            problem = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMPORT", ex);
            problem = "That file could not be read as a picture";
            return null;
        }
    }

    /// <summary>The stored picture, still or animated, or null if there is none by that name.</summary>
    public static CrosshairPicture? LoadPicture(string? fileName)
    {
        if (!IsSafeName(fileName))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(fileName!, out CrosshairPicture? cached))
            {
                return cached;
            }
        }

        try
        {
            string path = Path.Combine(Folder, fileName!);
            if (!File.Exists(path))
            {
                return null;
            }

            CrosshairPicture picture = BuildPicture(Decode(File.ReadAllBytes(path)), TimingPathFor(path));
            lock (Cache)
            {
                Cache[fileName!] = picture;
            }

            return picture;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMAGE", ex);
            return null;
        }
    }

    /// <summary>The stored picture as one image: the first frame of an animation. Null if there is none by that name.</summary>
    public static BitmapSource? Load(string? fileName) => LoadPicture(fileName)?.First;

    /// <summary>Remove a stored picture and its timing file. Best effort, because a leftover file is harmless.</summary>
    public static void Delete(string? fileName)
    {
        if (!IsSafeName(fileName))
        {
            return;
        }

        lock (Cache)
        {
            Cache.Remove(fileName!);
        }

        try
        {
            string path = Path.Combine(Folder, fileName!);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string timing = TimingPathFor(path);
            if (File.Exists(timing))
            {
                File.Delete(timing);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMAGE DELETE", ex);
        }
    }

    /// <summary>
    /// Delete every stored picture no saved crosshair uses, and every timing file
    /// whose picture has gone. Imports that were tried and then abandoned would
    /// otherwise pile up for ever, and nothing else ever looks in this folder.
    /// Best effort.
    /// </summary>
    public static void PruneUnused(IEnumerable<string> keep)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            HashSet<string> used = new(keep.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(Folder, "custom-*.png"))
            {
                if (!used.Contains(Path.GetFileName(path)))
                {
                    Delete(Path.GetFileName(path));
                }
            }

            // A timing file written just before a crash, or left when its strip was
            // deleted by hand, has no picture to belong to.
            foreach (string path in Directory.GetFiles(Folder, "custom-*" + TimingSuffix))
            {
                string file = Path.GetFileName(path);
                string strip = file[..^TimingSuffix.Length] + ".png";
                if (!used.Contains(strip))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PRUNE", ex);
        }
    }

    /// <summary>
    /// A plain file name ending in .png, with no folder in it. Anything else is
    /// refused rather than resolved, which is what stops a profile from naming
    /// a file outside the crosshair folder. Every stored picture, animated or not,
    /// is named by a .png.
    /// </summary>
    public static bool IsSafeName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string TimingPathFor(string stripPath)
    {
        return stripPath[..^".png".Length] + TimingSuffix;
    }

    /// <summary>
    /// Open the file with whichever decoder its contents call for, and refuse it if
    /// that is not the one its name promised. The extension is only a hint: a
    /// program that trusted it would hand a TIFF or a webpage-shaped payload to the
    /// wrong code.
    /// </summary>
    private static BitmapDecoder OpenDecoder(Stream stream, PictureKind kind)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        bool matches = kind switch
        {
            PictureKind.Png => decoder is PngBitmapDecoder,
            PictureKind.Gif => decoder is GifBitmapDecoder,
            PictureKind.Jpeg => decoder is JpegBitmapDecoder,
            PictureKind.Bmp => decoder is BmpBitmapDecoder,
            _ => false
        };

        if (!matches || decoder.Frames.Count == 0)
        {
            throw new ImportProblem("That file is not the kind of picture its name says");
        }

        return decoder;
    }

    /// <summary>A stored still is always a PNG this app wrote, so only the PNG decoder is needed to read it back.</summary>
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        BitmapFrame frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// A strip with a timing file beside it is an animation: cut it into its
    /// frames. A strip with none is a still. A timing file that does not match its
    /// strip means one of the two is damaged, which is an error and not a guess.
    /// </summary>
    private static CrosshairPicture BuildPicture(BitmapSource strip, string timingPath)
    {
        if (!File.Exists(timingPath))
        {
            return new CrosshairPicture(new[] { strip }, new[] { 0 });
        }

        if (!AnimationInfo.TryParse(BoundedRead.AllText(timingPath), out AnimationInfo timing)
            || strip.PixelHeight != timing.Side
            || strip.PixelWidth != timing.Side * timing.Frames)
        {
            throw new InvalidDataException("The animation's timing file does not match its picture.");
        }

        var frames = new List<BitmapSource>(timing.Frames);
        for (int i = 0; i < timing.Frames; i++)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(i * timing.Side, 0, timing.Side, timing.Side));
            frame.Freeze();
            frames.Add(frame);
        }

        return new CrosshairPicture(frames, timing.DelaysMs);
    }

    /// <summary>Premultiplied BGRA pixels of any picture, whatever format it was stored in.</summary>
    private static byte[] ToPixels(BitmapSource source, out int width, out int height)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;

        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Fit a still inside a transparent square and encode it as a new PNG. Square,
    /// so Size on the tab means the same thing for every picture and a wide image
    /// is centred rather than stretched.
    /// </summary>
    private static byte[] Normalise(BitmapSource source)
    {
        byte[] pixels = ToPixels(source, out int width, out int height);
        byte[] square = PixelResize.FitSquare(pixels, width, height, StoredSide, out int side);
        return EncodePng(square, side, side);
    }

    // ------------------------------------------------------------ animation

    /// <summary>
    /// Read every frame of a GIF, build the picture each one means, thin the
    /// animation to the frame limit if it is longer, and write the frames side by
    /// side as one PNG with the timings in a file beside it.
    /// </summary>
    private static void StoreAnimation(GifBitmapDecoder decoder, string name)
    {
        int count = decoder.Frames.Count;
        (int canvasWidth, int canvasHeight) = LogicalScreen(decoder);

        if (canvasWidth < 4 || canvasHeight < 4)
        {
            throw new ImportProblem("That picture is too small to use");
        }

        // Checked before a single frame is decoded in full, because the cost is
        // frames times pixels and a tiny file can expand to a great deal of both.
        if (!AnimationPlan.WithinBudget(canvasWidth, canvasHeight, count))
        {
            throw new ImportProblem("That animation is too large, keep it under 1024 pixels and 60 frames or so");
        }

        List<int> delays = new(count);
        for (int i = 0; i < count; i++)
        {
            delays.Add(AnimationPlan.DelayMs(Query(decoder.Frames[i].Metadata as BitmapMetadata, "/grctlext/Delay", 0)));
        }

        List<(int Index, int DelayMs)> kept = AnimationPlan.Reduce(delays, AnimationPlan.MaxFrames);
        Dictionary<int, int> slotOf = new();
        for (int k = 0; k < kept.Count; k++)
        {
            slotOf[kept[k].Index] = k;
        }

        byte[]?[] squares = new byte[kept.Count][];
        int side = 0;

        // Every frame is composed, kept or not, because each is drawn on top of the
        // last; only the kept ones are shrunk and stored.
        GifFrameCompositor.Compose(
            canvasWidth,
            canvasHeight,
            count,
            i => ReadFrame(decoder.Frames[i]),
            (i, canvas) =>
            {
                if (slotOf.TryGetValue(i, out int slot))
                {
                    squares[slot] = PixelResize.FitSquare(canvas, canvasWidth, canvasHeight, AnimatedSide, out side);
                }
            });

        int stripWidth = side * kept.Count;
        byte[] strip = new byte[stripWidth * side * 4];
        for (int k = 0; k < kept.Count; k++)
        {
            byte[] square = squares[k] ?? throw new InvalidDataException("A frame of the animation could not be built.");
            for (int y = 0; y < side; y++)
            {
                Buffer.BlockCopy(square, y * side * 4, strip, ((y * stripWidth) + (k * side)) * 4, side * 4);
            }
        }

        string stripPath = Path.Combine(Folder, name);

        // Timing first and picture second: a crash between the two leaves a timing
        // file with no picture, which is pruned, rather than a strip with no timing,
        // which would show as one very wide still.
        File.WriteAllText(TimingPathFor(stripPath), new AnimationInfo(side, kept.Select(f => f.DelayMs).ToArray()).ToJson());
        File.WriteAllBytes(stripPath, EncodePng(strip, stripWidth, side));
    }

    private static GifRawFrame ReadFrame(BitmapFrame frame)
    {
        BitmapMetadata? metadata = frame.Metadata as BitmapMetadata;
        byte[] pixels = ToPixels(frame, out int width, out int height);

        return new GifRawFrame(
            Query(metadata, "/imgdesc/Left", 0),
            Query(metadata, "/imgdesc/Top", 0),
            width,
            height,
            pixels,
            Query(metadata, "/grctlext/Disposal", 0));
    }

    /// <summary>
    /// The size of the GIF's canvas. Taken from the file's own header; where that
    /// is missing or zero, from the furthest edge any frame reaches.
    /// </summary>
    private static (int Width, int Height) LogicalScreen(GifBitmapDecoder decoder)
    {
        int width = 0;
        int height = 0;

        try
        {
            if (decoder.Metadata is BitmapMetadata header)
            {
                width = Query(header, "/logscrdesc/Width", 0);
                height = Query(header, "/logscrdesc/Height", 0);
            }
        }
        catch (NotSupportedException)
        {
            // No header to read. The frames say how big the picture is.
        }

        if (width < 1 || height < 1)
        {
            foreach (BitmapFrame frame in decoder.Frames)
            {
                BitmapMetadata? metadata = frame.Metadata as BitmapMetadata;
                width = Math.Max(width, Query(metadata, "/imgdesc/Left", 0) + frame.PixelWidth);
                height = Math.Max(height, Query(metadata, "/imgdesc/Top", 0) + frame.PixelHeight);
            }
        }

        return (width, height);
    }

    /// <summary>
    /// One number out of a frame's metadata, or the fallback when it is not there.
    /// Metadata comes back as whatever width the codec chose (a byte for the
    /// disposal method, a ushort for sizes), and a GIF may simply not carry it.
    /// </summary>
    private static int Query(BitmapMetadata? metadata, string query, int fallback)
    {
        if (metadata is null)
        {
            return fallback;
        }

        try
        {
            if (!metadata.ContainsQuery(query))
            {
                return fallback;
            }

            return metadata.GetQuery(query) switch
            {
                ushort value => value,
                byte value => value,
                int value => value,
                _ => fallback
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return fallback;
        }
    }
}
