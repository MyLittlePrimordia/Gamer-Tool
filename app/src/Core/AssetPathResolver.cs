using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GamerTool.Core;

/// <summary>
/// Resolves the app's runtime assets from EMBEDDED resources, so the
/// single-file exe is fully self-contained (no Content extraction needed):
///   - tray icons + preview scene: loaded directly from resource streams
///   - sample.mp3: MediaPlayer requires a real file, so it extracts once
///     to %LOCALAPPDATA%\GamerTool\ and that path is reused.
/// </summary>
public static class AssetPathResolver
{
    private static string? _cachedMp3Path;

    private static Stream? OpenResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream($"GamerTool.Resources.{name}");
    }

    /// <summary>Loads the embedded preview scene as a BitmapSource (already decoded).</summary>
    public static BitmapSource? LoadPreviewScene()
    {
        try
        {
            using var stream = OpenResource("preview_scene.png");
            if (stream is null)
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts an embedded icon to a temp file and returns its path (the
    /// tray LoadImage P/Invoke needs a real on-disk path).
    /// </summary>
    public static string? ExtractIconToTemp(string resourceName, string fileName)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "GamerTool");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, fileName);

            // Refresh the file if missing or stale (app update).
            using (var source = OpenResource(resourceName))
            {
                if (source is null)
                    return null;

                using var dst = File.Create(target);
                source.CopyTo(dst);
            }

            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The sample.mp3 test loop, extracted once to %LOCALAPPDATA% (a stable,
    /// user-writable location MediaPlayer can read across sessions).
    /// </summary>
    public static string? GetOrExtractSampleMp3()
    {
        if (_cachedMp3Path is not null && File.Exists(_cachedMp3Path))
            return _cachedMp3Path;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamerTool");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "sample.mp3");

            using (var source = OpenResource("sample.mp3"))
            {
                if (source is null)
                    return null;

                using var dst = File.Create(target);
                source.CopyTo(dst);
            }

            _cachedMp3Path = target;
            return target;
        }
        catch
        {
            return null;
        }
    }
}
