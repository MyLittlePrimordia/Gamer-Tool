using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GamerTool.Core;

/// <summary>
/// Central catalog of the preset/combo icon set (FluentUI Emoji flat style,
/// 32px PNGs embedded as resources - WPF can't render color emoji fonts, so
/// the app ships real color icons instead). Keys are stable identifiers
/// persisted in settings.json; values are embedded resource bitmaps.
///
/// The 20px "tray" variants are pre-rasterized for crisp 16x20 HICONs.
/// </summary>
public static class IconCatalog
{
    /// <summary>All selectable icon keys, in catalog/picker order.</summary>
    public static readonly string[] Keys =
    {
        "videogame", "bullseye", "ghost", "bomb",
        "crossswords", "alien", "robot", "crescent",
        "sun", "trophy", "headphone", "loudspeaker",
        "music", "fire", "owl", "shield"
    };

    private static readonly Dictionary<string, ImageSource?> _cache32 = new();
    private static readonly Dictionary<string, Bitmap?> _cacheTray = new();

    /// <summary>32px icon as a frozen WPF ImageSource (thread-safe after freeze).</summary>
    public static ImageSource? GetIcon(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        if (_cache32.TryGetValue(key, out var cached))
            return cached;

        ImageSource? source = null;
        try
        {
            var bytes = LoadResource($"GamerTool.Resources.icons.{key}_32.png");
            if (bytes is not null)
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                source = image;
            }
        }
        catch
        {
            source = null; // missing icon -> null -> callers render name-only
        }

        _cache32[key] = source;
        return source;
    }

    /// <summary>20px icon as a GDI Bitmap for HICON conversion (tray).</summary>
    public static Bitmap? GetTrayBitmap(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        if (_cacheTray.TryGetValue(key, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            var bytes = LoadResource($"GamerTool.Resources.icons.tray.{key}_20.png");
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
        }
        catch
        {
            bitmap = null;
        }

        _cacheTray[key] = bitmap;
        return bitmap;
    }

    private static byte[]? LoadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
            return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
