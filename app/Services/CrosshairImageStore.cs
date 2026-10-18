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
/// profile, no decompression bomb, and a small file whatever was picked. PNG only,
/// because it is the one common format with real transparency, and SVG is left
/// out on purpose, since an SVG is a document that can reference other files.
/// </para>
/// <para>
/// Presets refer to an image by its file name inside this folder, never by a
/// path, so a hand edited or restored profile cannot make the app open a file
/// from somewhere else.
/// </para>
/// </summary>
public static class CrosshairImageStore
{
    /// <summary>The longest side the stored copy keeps.</summary>
    public const int StoredSide = 256;

    /// <summary>The largest file taken, before decoding.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>The largest picture taken in either direction, before decoding it in full.</summary>
    public const int MaxPixels = 4096;

    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder => Path.Combine(ProfileManager.AppDataFolder, "crosshairs");

    /// <summary>
    /// Import one picture. Returns the stored file name, or null with the reason
    /// in <paramref name="problem"/> worded for the user.
    /// </summary>
    public static string? Import(string sourcePath, out string problem)
    {
        problem = string.Empty;

        try
        {
            if (!string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                problem = "Pick a PNG file";
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

            BitmapSource decoded = Decode(File.ReadAllBytes(sourcePath));
            if (decoded.PixelWidth < 4 || decoded.PixelHeight < 4)
            {
                problem = "That picture is too small to use";
                return null;
            }

            if (decoded.PixelWidth > MaxPixels || decoded.PixelHeight > MaxPixels)
            {
                problem = "That picture is too large, keep it under 4096 pixels";
                return null;
            }

            byte[] png = Normalise(decoded);
            Directory.CreateDirectory(Folder);

            string name = "custom-" + Guid.NewGuid().ToString("N")[..8] + ".png";
            File.WriteAllBytes(Path.Combine(Folder, name), png);
            return name;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMPORT", ex);
            problem = "That file could not be read as a picture";
            return null;
        }
    }

    /// <summary>The stored picture, or null if there is none by that name.</summary>
    public static BitmapSource? Load(string? fileName)
    {
        if (!IsSafeName(fileName))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(fileName!, out BitmapSource? cached))
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

            BitmapSource image = Decode(File.ReadAllBytes(path));
            lock (Cache)
            {
                Cache[fileName!] = image;
            }

            return image;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMAGE", ex);
            return null;
        }
    }

    /// <summary>Remove a stored picture. Best effort, because a leftover file is harmless.</summary>
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
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR IMAGE DELETE", ex);
        }
    }

    /// <summary>
    /// Delete every stored picture no saved crosshair uses. Imports that were
    /// tried and then abandoned would otherwise pile up for ever, and nothing
    /// else ever looks in this folder. Best effort.
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
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PRUNE", ex);
        }
    }

    /// <summary>
    /// A plain file name ending in .png, with no folder in it. Anything else is
    /// refused rather than resolved, which is what stops a profile from naming
    /// a file outside the crosshair folder.
    /// </summary>
    public static bool IsSafeName(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

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

    /// <summary>Fit the picture inside a transparent square and encode it as a new PNG.</summary>
    private static byte[] Normalise(BitmapSource source)
    {
        double scale = Math.Min(1.0, (double)StoredSide / Math.Max(source.PixelWidth, source.PixelHeight));
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;

        // Square, so Size on the tab means the same thing for every picture and a
        // wide image is centred rather than stretched.
        int side = (int)Math.Ceiling(Math.Max(width, height));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect((side - width) / 2.0, (side - height) / 2.0, width, height));
        }

        var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
