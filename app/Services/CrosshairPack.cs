using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// The crosshair pack: a folder of pictures, PNG, GIF, WebP, JPG or BMP, that is
/// read when the browser opens.
/// <para>
/// Nothing about the pictures is known to the program ahead of time. There is no
/// list, no manifest and no naming scheme: any picture that is in the folder, or in a
/// folder inside it, is in the pack, whatever it is called. Drop a file in and it
/// is there the next time the browser is opened, with no change to the program.
/// </para>
/// <para>
/// The starter pictures are embedded in the executable, so the program is a
/// single file. They are unpacked into <c>crosshair-pack</c> inside the app data
/// folder (<c>%AppData%\GamerTool</c>, or beside the program in portable mode) the
/// first time they are needed. A later version that ships a new picture adds it,
/// and one that ships a corrected copy of a picture replaces the old copy, but only
/// if the user never touched it. A picture the user deleted stays deleted, and
/// nothing the user added or changed is ever overwritten.
/// </para>
/// </summary>
public static class CrosshairPack
{
    /// <summary>
    /// A cap on what the browser will list. A folder with a hundred thousand
    /// pictures is a mistake, and listing it would stall the window.
    /// </summary>
    public const int MaxPictures = 2000;

    /// <summary>Prefix of the embedded pictures. The rest of the name is the path under the pack folder.</summary>
    private const string ResourcePrefix = "GamerTool.pack.";

    /// <summary>Remembers, for each starter picture, a fingerprint of what was last written to the folder.</summary>
    private const string MarkerName = ".seeded";

    private static readonly object SeedGate = new();

    private static bool _seeded;

    public static string Folder => Path.Combine(ProfileManager.AppDataFolder, "crosshair-pack");

    /// <summary>
    /// Make sure the folder exists and holds the starter pictures, so there is
    /// something to browse and somewhere for "open folder" to open.
    /// </summary>
    public static bool EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            SeedFromExecutable();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK FOLDER", ex);
            return false;
        }
    }

    /// <summary>
    /// Brings the folder up to date with the embedded pictures. Runs once per
    /// launch. A picture the user deleted is not brought back, and a file the user
    /// edited is never overwritten; see <see cref="PackSeedPolicy"/> for the rules.
    /// </summary>
    internal static void SeedFromExecutable()
    {
        // Held for the whole copy, not just the flag, so a browser opened while the
        // startup copy is still running waits for it rather than listing half a pack.
        lock (SeedGate)
        {
            if (_seeded)
            {
                return;
            }

            SeedCore();
            _seeded = true;
        }
    }

    private static void SeedCore()
    {
        Assembly app = typeof(CrosshairPack).Assembly;
        string[] names = app.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            return;
        }

        string root = Path.GetFullPath(Folder);
        string marker = Path.Combine(root, MarkerName);
        string? markerText = File.Exists(marker) ? File.ReadAllText(marker) : null;

        // What this program wrote last time, picture by picture. See PackSeedPolicy
        // for why: it is what lets a corrected starter picture replace an old one
        // without ever replacing one the user changed.
        Dictionary<string, string> previous = PackSeedPolicy.ParseMarker(markerText);
        Dictionary<string, string> current = new(StringComparer.Ordinal);

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        foreach (string name in names)
        {
            string relative = name.Substring(ResourcePrefix.Length)
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            string target = Path.GetFullPath(Path.Combine(root, relative));

            // The names come from this assembly, but a path that climbs out of the
            // folder is never one to write to.
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The same key whichever way the build spelled the separator.
            string key = relative.Replace(Path.DirectorySeparatorChar, '/');
            bool seededBefore = previous.TryGetValue(key, out string? lastWritten);

            try
            {
                byte[]? shipped = ReadResource(app, name);
                if (shipped is null)
                {
                    continue;
                }

                string shippedHash = PackSeedPolicy.Hash(shipped);
                bool onDisk = File.Exists(target);

                // The disk copy is only read when it can change the answer: a file
                // already known to match what ships has nothing to decide.
                string? diskHash = onDisk && (!seededBefore || !string.Equals(lastWritten, shippedHash, StringComparison.Ordinal))
                    ? PackSeedPolicy.Hash(File.ReadAllBytes(target))
                    : null;

                SeedAction action = PackSeedPolicy.Decide(onDisk, seededBefore, lastWritten, diskHash, shippedHash);
                if (action == SeedAction.Write)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, shipped);
                }

                current[key] = PackSeedPolicy.Record(action, onDisk, seededBefore, lastWritten, diskHash, shippedHash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One picture held open by another program must not stop the rest.
                // Its record carries over so the next launch tries it again.
                AppLog.Error("CROSSHAIR PACK SEED", ex);
                if (seededBefore)
                {
                    current[key] = lastWritten ?? string.Empty;
                }
            }
        }

        string text = PackSeedPolicy.SerializeMarker(current);
        if (!string.Equals(markerText, text, StringComparison.Ordinal))
        {
            File.WriteAllText(marker, text);
        }
    }

    private static byte[]? ReadResource(Assembly app, string name)
    {
        using Stream? source = app.GetManifestResourceStream(name);
        if (source is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Every picture in the pack (PNG, GIF, WebP, JPG or BMP), in a natural order:
    /// crosshair-2 before crosshair-10, and a folder's pictures grouped together.
    /// Empty if there is no folder.
    /// </summary>
    public static List<string> List()
    {
        try
        {
            EnsureFolder();

            if (!Directory.Exists(Folder))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFiles(Folder, "*.*", SearchOption.AllDirectories)
                .Where(CrosshairFileKinds.IsSupported)
                .OrderBy(path => path, NaturalOrder.Instance)
                .Take(MaxPictures)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK LIST", ex);
            return new List<string>();
        }
    }

    /// <summary>Compares names so that numbers inside them sort as numbers.</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;

            int i = 0;
            int j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int startX = i;
                    int startY = j;
                    while (i < x.Length && char.IsDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsDigit(y[j]))
                    {
                        j++;
                    }

                    string numX = x.Substring(startX, i - startX).TrimStart('0');
                    string numY = y.Substring(startY, j - startY).TrimStart('0');

                    if (numX.Length != numY.Length)
                    {
                        return numX.Length.CompareTo(numY.Length);
                    }

                    int byDigits = string.CompareOrdinal(numX, numY);
                    if (byDigits != 0)
                    {
                        return byDigits;
                    }
                }
                else
                {
                    int byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (byChar != 0)
                    {
                        return byChar;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
