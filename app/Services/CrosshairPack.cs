using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// The crosshair pack: a folder of PNG pictures that is read when the browser
/// opens.
/// <para>
/// Nothing about the pictures is known to the program ahead of time. There is no
/// list, no manifest and no naming scheme: any PNG that is in the folder, or in a
/// folder inside it, is in the pack, whatever it is called. Drop a file in and it
/// is there the next time the browser is opened, with no change to the program.
/// </para>
/// <para>
/// The starter pictures are embedded in the executable, so the program is a
/// single file. They are unpacked into <c>crosshair-pack</c> inside the app data
/// folder (<c>%AppData%\GamerTool</c>, or beside the program in portable mode) the
/// first time they are needed, and again when a new version ships pictures the
/// folder does not have. A picture the user deleted stays deleted until then, and
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

    /// <summary>Remembers which set of embedded pictures the folder was last filled from.</summary>
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
    /// Copies the embedded pictures the folder is missing. Runs once per launch,
    /// and does nothing at all unless the embedded set changed since the folder
    /// was last filled, so a picture the user deleted is not brought back on every
    /// start. Existing files are never overwritten.
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

        string signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", names))));

        string root = Path.GetFullPath(Folder);
        string marker = Path.Combine(root, MarkerName);

        if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), signature, StringComparison.Ordinal))
        {
            return;
        }

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
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            {
                continue;
            }

            using Stream? source = app.GetManifestResourceStream(name);
            if (source is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream destination = File.Create(target);
            source.CopyTo(destination);
        }

        File.WriteAllText(marker, signature);
    }

    /// <summary>
    /// Every PNG in the pack, in a natural order: crosshair-2 before crosshair-10,
    /// and a folder's pictures grouped together. Empty if there is no folder.
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
                .Where(path => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
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
