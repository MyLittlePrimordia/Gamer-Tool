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
/// PNG, GIF, WebP, JPEG and BMP are taken, each checked against its own decoder so
/// a file merely renamed to look like one is refused. An animation, GIF or WebP, is
/// stored as a strip of its frames in one PNG, with a small timing file beside it,
/// so the overlay plays something this app made rather than re-reading the
/// original. SVG is left out on purpose, since an SVG is a document that can
/// reference other files, and drawing one would mean shipping a vector engine.
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

    /// <summary>
    /// Largest stored file a backup may carry, in bytes.
    /// <para>
    /// The stored copy is already shrunk - 256 pixels for a still, 128 a frame for
    /// an animation - so this is far above anything this app writes and far below
    /// anything that would make a backup unreasonable to open. It is a ceiling on
    /// what a file is allowed to claim, not a size anything real reaches.
    /// </para>
    /// </summary>
    public const int MaxBackupBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Widest a stored picture can be: every frame of the longest animation the
    /// importer keeps, side by side.
    /// </summary>
    public const int MaxStoredWidth = AnimatedSide * AnimationPlan.MaxFrames;

    /// <summary>Tallest a stored picture can be: the side of a still's square.</summary>
    public const int MaxStoredHeight = StoredSide;

    /// <summary>Ends the name of the timing file beside an animation's strip.</summary>
    private const string TimingSuffix = ".anim.json";

    private static readonly Dictionary<string, CrosshairPicture> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string? _redirectedFolder;

    /// <summary>Where stored pictures live: the profile's folder, or wherever a test sent them.</summary>
    public static string Folder => _redirectedFolder ?? Path.Combine(ProfileManager.AppDataFolder, "crosshairs");

    /// <summary>
    /// Send the store somewhere else, and forget everything read from the old place.
    /// Used by the test suite, the way <see cref="AppLog.RedirectTo" /> is: the
    /// Crosshair tab reads pictures from this folder by name, so a test that stands
    /// the tab up would otherwise draw from - and prune - the user's own crosshairs.
    /// Null, the normal case, means the profile's folder.
    /// </summary>
    internal static void RedirectTo(string? folder)
    {
        lock (Cache)
        {
            Cache.Clear();
        }

        _redirectedFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>
    /// A picture that made it into the folder, and whether it brought colours of its
    /// own. The second half is what lets an import arrive looking like the thing that
    /// was imported: a multi-coloured animation is not a mask, and multiplying it by
    /// one colour on the way in is how it used to come out single-coloured.
    /// </summary>
    public sealed record StoredPicture(string Name, bool OwnColours);

    /// <summary>A problem worded for the user, thrown inside an import and turned into the message.</summary>
    private sealed class ImportProblem : Exception
    {
        public ImportProblem(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Import one picture. Returns the stored name and whether it kept colours of its
    /// own, or null with the reason in <paramref name="problem"/> worded for the user.
    /// Safe to call off the UI thread: it uses decoders and raw pixels and never draws.
    /// </summary>
    public static StoredPicture? Import(string sourcePath, out string problem)
    {
        return Import(sourcePath, Folder, out problem);
    }

    /// <summary>
    /// The same import into a given folder. <paramref name="folder"/> exists so a test
    /// can take a picture in somewhere disposable, the way <see cref="ProfileManager"/> and
    /// <see cref="BackupService"/> take one, instead of into the user's own crosshairs.
    /// </summary>
    internal static StoredPicture? Import(string sourcePath, string folder, out string problem)
    {
        problem = string.Empty;

        try
        {
            PictureKind kind = CrosshairFileKinds.FromPath(sourcePath);
            if (kind == PictureKind.None)
            {
                problem = "Pick a PNG, GIF, WebP, JPG or BMP picture";
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

            byte[] file = File.ReadAllBytes(sourcePath);

            Directory.CreateDirectory(folder);
            string name = "custom-" + Guid.NewGuid().ToString("N")[..8] + ".png";

            // WebP is the one kind Windows cannot decode, so it takes its own route
            // and its own decoder. Everything below this is WIC.
            if (kind == PictureKind.WebP)
            {
                return StoreWebp(file, name, folder);
            }

            // Held open for the whole import: the decoder may read frames lazily.
            using var stream = new MemoryStream(file);
            BitmapDecoder decoder = OpenDecoder(stream, kind);

            if (decoder is GifBitmapDecoder gif && gif.Frames.Count > 1)
            {
                return StoreAnimation(gif, name, folder);
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

            bool ownColours = Normalise(still, kind, out byte[] square);
            File.WriteAllBytes(Path.Combine(folder, name), square);
            return new StoredPicture(name, ownColours);
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

        CrosshairPicture? picture = ReadPicture(fileName!, Folder);
        if (picture is not null)
        {
            lock (Cache)
            {
                Cache[fileName!] = picture;
            }
        }

        return picture;
    }

    /// <summary>
    /// The same, out of the given folder and never from the cache. The folder is a
    /// parameter so a test can read a picture it imported into a temp folder, the
    /// way the rest of this store takes one; the cache is keyed by name alone and a
    /// temp folder must not seed it.
    /// </summary>
    internal static CrosshairPicture? LoadPicture(string? fileName, string folder)
    {
        return IsSafeName(fileName) ? ReadPicture(fileName!, folder) : null;
    }

    /// <summary>
    /// Whether a stored picture has colours of its own, judged by its pixels. Used
    /// when the tab puts a picture back to its defaults, which has no import result
    /// left to consult: by then the picture is this app's own PNG, whatever it was
    /// imported from.
    /// </summary>
    public static bool PictureHasOwnColours(string? fileName) => PictureHasOwnColours(fileName, Folder);

    internal static bool PictureHasOwnColours(string? fileName, string folder)
    {
        CrosshairPicture? picture = LoadPicture(fileName, folder);
        if (picture is null)
        {
            return false;
        }

        foreach (BitmapSource frame in picture.Frames)
        {
            if (HasOwnColours(ToPixels(frame, out _, out _)))
            {
                return true;
            }
        }

        return false;
    }

    private static CrosshairPicture? ReadPicture(string fileName, string folder)
    {
        try
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return BuildPicture(Decode(File.ReadAllBytes(path)), TimingPathFor(path));
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
    /// A stored picture and its timing file, for a backup to carry. Null when there
    /// is no file by that name, or when the name is not one this app would have
    /// written.
    /// <para>
    /// The folder is a parameter rather than <see cref="Folder"/> so a backup taken
    /// by a test can read somewhere disposable, the same reason
    /// <see cref="ProfileManager"/> takes one.
    /// </para>
    /// </summary>
    internal static (byte[] Png, string? Timing)? ReadForBackup(string? fileName, string folder)
    {
        if (!IsSafeName(fileName))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(folder, fileName!);
            if (!File.Exists(path))
            {
                return null;
            }

            string timingPath = TimingPathFor(path);
            string? timing = File.Exists(timingPath) ? BoundedRead.AllText(timingPath) : null;
            return (File.ReadAllBytes(path), timing);
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR BACKUP READ", ex);
            return null;
        }
    }

    /// <summary>
    /// Put a picture out of a backup into <paramref name="folder"/>, replacing any
    /// file of the same name. False, with a reason worded for the log, when the
    /// bytes are not a picture this app could have written.
    /// <para>
    /// A backup is a file from somewhere else, so nothing in it is trusted: the
    /// name has to be one this app writes, the size and the dimensions have to be
    /// ones it produces, the pixels have to decode, and a timing file has to agree
    /// with the strip it claims to describe. Anything that fails is refused whole,
    /// because half a picture - or a strip with a timing file that does not match it
    /// - is a crosshair that silently refuses to load.
    /// </para>
    /// </summary>
    internal static bool WriteFromBackup(
        string? fileName,
        byte[]? png,
        string? timing,
        string folder,
        out string problem)
    {
        problem = string.Empty;

        if (!IsSafeName(fileName))
        {
            problem = "the name is not one this app writes";
            return false;
        }

        if (png is null || png.Length == 0)
        {
            problem = "there is no picture data";
            return false;
        }

        if (png.Length > MaxBackupBytes)
        {
            problem = "the picture is larger than this app ever stores";
            return false;
        }

        int width;
        int height;
        try
        {
            (width, height) = MeasureAndDecode(png);
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR BACKUP WRITE", ex);
            problem = "the picture is not a PNG this app could read";
            return false;
        }

        if (width < 4 || height < 4 || width > MaxStoredWidth || height > MaxStoredHeight)
        {
            problem = "the picture is not a size this app stores";
            return false;
        }

        // A still is square, and a strip is a whole number of square frames. A file
        // that is neither is one this app could not have written, and drawing it as
        // a still would show a whole animation as one very wide smear.
        string? accepted = null;
        if (!string.IsNullOrWhiteSpace(timing))
        {
            if (!AnimationInfo.TryParse(timing, out AnimationInfo info)
                || info.Side != height
                || info.Side * info.Frames != width)
            {
                problem = "the animation's timings do not match its picture";
                return false;
            }

            // Re-serialised from the parsed value rather than copied, so anything
            // else the file happened to carry is dropped.
            accepted = info.ToJson();
        }
        else if (width != height)
        {
            problem = "the picture is wider than it is tall but carries no timings";
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, fileName!);
            string timingPath = TimingPathFor(path);

            // Timings first and picture second, the same order StoreAnimation
            // writes them in and for the same reason: a crash between the two
            // leaves an orphan timing file, which is pruned, rather than a strip
            // with no timings. A still clears any timing file of that name, or the
            // old animation's timings would be glued to a new still.
            if (accepted is null)
            {
                if (File.Exists(timingPath))
                {
                    File.Delete(timingPath);
                }
            }
            else
            {
                File.WriteAllText(timingPath, accepted);
            }

            File.WriteAllBytes(path, png);

            // Whatever was cached under this name is the picture it replaced.
            lock (Cache)
            {
                Cache.Remove(fileName!);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR BACKUP WRITE", ex);
            problem = "the picture could not be written";
            return false;
        }
    }

    /// <summary>
    /// The stored size of a PNG, and a full decode to prove the pixels are there.
    /// <para>
    /// The size is read from the header with a delayed decoder, so a file claiming
    /// enormous dimensions is refused before anything allocates for it; the decode
    /// afterwards is what catches a header that survived a truncated file.
    /// </para>
    /// </summary>
    private static (int Width, int Height) MeasureAndDecode(byte[] png)
    {
        int width;
        int height;

        using (var header = new MemoryStream(png))
        {
            var decoder = new PngBitmapDecoder(
                header,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("The file has no picture in it.");
            }

            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }

        Decode(png);
        return (width, height);
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

    /// <summary>
    /// A stored still is always a PNG this app wrote, so only the PNG decoder is
    /// needed to read it back.
    /// <para>
    /// The pixels are copied into a bitmap of their own rather than the frame being
    /// handed on. A decoded frame keeps a reference to the decoder that made it, and
    /// a decoder belongs to the thread that opened it - even after the frame is
    /// frozen, cropping it on another thread throws and the drawing code, which is
    /// not allowed to fail loudly on a redraw, quietly draws nothing. A copy has
    /// nothing left to ask, so a picture can be loaded by one thread and drawn by
    /// another.
    /// </para>
    /// </summary>
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        return Detached(decoder.Frames[0]);
    }

    /// <summary>Premultiplied BGRA pixels of any picture in a fresh, frozen bitmap of their own.</summary>
    private static BitmapSource Detached(BitmapSource source)
    {
        byte[] pixels = ToPixels(source, out int width, out int height);
        BitmapSource detached = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        detached.Freeze();
        return detached;
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
            BitmapSource[] still = { strip };
            return new CrosshairPicture(still, new[] { 0 }, ContentBounds(still));
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

        return new CrosshairPicture(frames, timing.DelaysMs, ContentBounds(frames));
    }

    /// <summary>
    /// The rectangle every frame has something drawn in, or empty when nothing is
    /// drawn at all. Read once when the picture is loaded, because scanning a whole
    /// animation is cheap there and would not be on every redraw.
    /// <para>
    /// Pixels below four levels of alpha are ignored. A stored picture is re-encoded
    /// by this app and the edge of its art carries a fringe of almost invisible
    /// pixels; counting those would make the rectangle as big as the frame and
    /// defeat the point, which is that Size measures the crosshair and not the empty
    /// space the file happens to keep round it.
    /// </para>
    /// </summary>
    private static Int32Rect ContentBounds(IReadOnlyList<BitmapSource> frames)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = -1;
        int bottom = -1;

        foreach (BitmapSource frame in frames)
        {
            byte[] pixels = ToPixels(frame, out int width, out int height);
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[row + (x * 4) + 3] < 4)
                    {
                        continue;
                    }

                    if (x < left)
                    {
                        left = x;
                    }

                    if (x > right)
                    {
                        right = x;
                    }

                    if (y < top)
                    {
                        top = y;
                    }

                    if (y > bottom)
                    {
                        bottom = y;
                    }
                }
            }
        }

        return right < left || bottom < top
            ? default
            : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
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
    /// is centred rather than stretched. Also says whether the picture brought
    /// colours of its own, which is decided on the pixels as imported rather than on
    /// the shrunk copy, so a picture that is coloured in one corner cannot lose its
    /// colour to the resize before it is looked at.
    /// </summary>
    private static bool Normalise(BitmapSource source, PictureKind kind, out byte[] png)
    {
        byte[] pixels = ToPixels(source, out int width, out int height);
        bool ownColours = KeepOwnColours(kind, pixels);
        byte[] square = PixelResize.FitSquare(pixels, width, height, StoredSide, out int side);
        png = EncodePng(square, side, side);
        return ownColours;
    }

    /// <summary>
    /// Whether a picture should be drawn in its own colours rather than tinted.
    /// <para>
    /// A JPEG or a BMP is a photograph or a drawn bitmap, never a white mask, so it
    /// is always shown as it came - which is what the old rule did in the clumsy way,
    /// by forcing the colour to white on import. A PNG or GIF is white line art more
    /// often than not, so it keeps answering to the colour swatches unless it actually
    /// carries colour.
    /// </para>
    /// </summary>
    internal static bool KeepOwnColours(PictureKind kind, byte[] rgba)
    {
        return kind is PictureKind.Jpeg or PictureKind.Bmp || HasOwnColours(rgba);
    }

    /// <summary>
    /// How far apart the strongest and weakest of red, green and blue have to be for
    /// a pixel to count as coloured rather than grey. Well above the few levels that
    /// antialiasing and GIF palettes add to a white line, and well below anything that
    /// was meant as a colour.
    /// </summary>
    internal const int OwnColourChroma = 24;

    /// <summary>
    /// How many coloured pixels make a picture coloured. Not one, because a stray
    /// pixel in a corner of a white mask would then switch the whole colour row off
    /// for it; not a large fraction either, because a crosshair of a few coloured
    /// pixels is exactly the thing this is for.
    /// </summary>
    internal const int OwnColourPixels = 16;

    /// <summary>
    /// Whether the pixels hold colour of their own: at least a handful of visible
    /// ones that are not grey. Premultiplied BGRA, which is what this store handles
    /// throughout; the alpha floor ignores the near-invisible fringe round a shape,
    /// where the colour that survives is an artefact of the edge rather than a choice.
    /// </summary>
    internal static bool HasOwnColours(byte[] rgba)
    {
        int found = 0;
        for (int p = 0; p + 3 < rgba.Length; p += 4)
        {
            if (rgba[p + 3] < 96)
            {
                continue;
            }

            int high = Math.Max(rgba[p], Math.Max(rgba[p + 1], rgba[p + 2]));
            int low = Math.Min(rgba[p], Math.Min(rgba[p + 1], rgba[p + 2]));
            if (high - low >= OwnColourChroma && ++found >= OwnColourPixels)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------ animation

    /// <summary>
    /// Read every frame of a GIF, build the picture each one means, thin the
    /// animation to the frame limit if it is longer, and write the frames side by
    /// side as one PNG with the timings in a file beside it.
    /// </summary>
    private static StoredPicture StoreAnimation(GifBitmapDecoder decoder, string name, string folder)
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

        return WriteAnimation(folder, name, squares, side, kept);
    }

    /// <summary>
    /// Take a WebP in. A still goes through the same square, shrunk form as every
    /// other picture; an animation becomes a strip with its timings beside it.
    /// <para>
    /// Its frames come from Skia rather than WIC, whole and already composed, so
    /// unlike a GIF there is no rectangle to draw over a canvas - each frame is the
    /// picture a viewer would show. They are read in order because that is the order
    /// the decoder walks them in without rewinding, and only the frames the
    /// animation plan kept are shrunk and stored.
    /// </para>
    /// </summary>
    private static StoredPicture StoreWebp(byte[] file, string name, string folder)
    {
        using WebpReader reader = WebpReader.Open(file)
            ?? throw new ImportProblem("That file is not the kind of picture its name says");

        if (reader.Width < 4 || reader.Height < 4)
        {
            throw new ImportProblem("That picture is too small to use");
        }

        if (reader.Width > MaxPixels || reader.Height > MaxPixels)
        {
            throw new ImportProblem("That picture is too large, keep it under 4096 pixels");
        }

        if (reader.FrameCount == 1)
        {
            byte[] pixels = reader.Frame(0) ?? throw new ImportProblem("That file could not be read as a picture");
            bool ownColours = HasOwnColours(pixels);
            byte[] square = PixelResize.FitSquare(pixels, reader.Width, reader.Height, StoredSide, out int side);
            File.WriteAllBytes(Path.Combine(folder, name), EncodePng(square, side, side));
            return new StoredPicture(name, ownColours);
        }

        if (!AnimationPlan.WithinBudget(reader.Width, reader.Height, reader.FrameCount))
        {
            throw new ImportProblem("That animation is too large, keep it under 1024 pixels and 60 frames or so");
        }

        List<(int Index, int DelayMs)> kept = AnimationPlan.Reduce(reader.DelaysMs, AnimationPlan.MaxFrames);
        Dictionary<int, int> slotOf = new();
        for (int k = 0; k < kept.Count; k++)
        {
            slotOf[kept[k].Index] = k;
        }

        byte[]?[] squares = new byte[kept.Count][];
        int frameSide = 0;

        for (int i = 0; i < reader.FrameCount; i++)
        {
            byte[]? canvas = reader.Frame(i)
                ?? throw new ImportProblem("That file could not be read as a picture");

            if (slotOf.TryGetValue(i, out int slot))
            {
                squares[slot] = PixelResize.FitSquare(canvas, reader.Width, reader.Height, AnimatedSide, out frameSide);
            }
        }

        return WriteAnimation(folder, name, squares, frameSide, kept);
    }

    /// <summary>
    /// Write an animation in the form it is drawn from: every frame side by side in
    /// one PNG, and how long each lasts in a file beside it.
    /// <para>
    /// The strip is the whole animation as it will reach the screen, so it is what
    /// decides whether this one has colours worth keeping. Deciding on the frames
    /// before they are shrunk would count pixels that never get drawn.
    /// </para>
    /// </summary>
    private static StoredPicture WriteAnimation(
        string folder,
        string name,
        IReadOnlyList<byte[]?> squares,
        int side,
        IReadOnlyList<(int Index, int DelayMs)> kept)
    {
        int stripWidth = side * squares.Count;
        byte[] strip = new byte[stripWidth * side * 4];

        for (int k = 0; k < squares.Count; k++)
        {
            byte[] square = squares[k] ?? throw new InvalidDataException("A frame of the animation could not be built.");
            for (int y = 0; y < side; y++)
            {
                Buffer.BlockCopy(square, y * side * 4, strip, ((y * stripWidth) + (k * side)) * 4, side * 4);
            }
        }

        bool ownColours = HasOwnColours(strip);
        string stripPath = Path.Combine(folder, name);

        // Timing first and picture second: a crash between the two leaves a timing
        // file with no picture, which is pruned, rather than a strip with no timing,
        // which would show as one very wide still.
        //
        // Written through a temp file and swapped in, because the sidecar is what
        // makes the strip an animation: a crash part way through a plain write left
        // a truncated JSON file, which reads as no timing at all. The picture itself
        // is already whole-or-absent by construction.
        string timingPath = TimingPathFor(stripPath);
        string timingTemp = timingPath + ".tmp";
        File.WriteAllText(timingTemp, new AnimationInfo(side, kept.Select(f => f.DelayMs).ToArray()).ToJson());
        ProfileManager.AtomicReplace(timingTemp, timingPath);
        File.WriteAllBytes(stripPath, EncodePng(strip, stripWidth, side));

        return new StoredPicture(name, ownColours);
    }

    /// <summary>
    /// The GIF's canvas size, for a reader outside this store. The pack browser plays
    /// the same file this importer would take, and reading it two different ways is
    /// how two views of one picture come to disagree.
    /// </summary>
    internal static (int Width, int Height) GifCanvas(GifBitmapDecoder decoder) => LogicalScreen(decoder);

    /// <summary>One stored GIF frame as the compositor wants it.</summary>
    internal static GifRawFrame GifFrame(BitmapFrame frame) => ReadFrame(frame);

    /// <summary>How long a GIF frame stays up, in milliseconds, by the importer's own rule.</summary>
    internal static int GifFrameDelay(BitmapFrame frame)
    {
        return AnimationPlan.DelayMs(Query(frame.Metadata as BitmapMetadata, "/grctlext/Delay", 0));
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
