using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// Naming and pruning for the once-a-day copies of the settings file. Pure, so the
/// rules (one per day, newest five kept, newest tried first on recovery) are tested
/// without touching a disk.
/// </summary>
internal static class SettingsSnapshots
{
    public const string Pattern = "settings.json.day-*.bak";

    public const int Keep = 5;

    public static string NameFor(DateTime day)
        => "settings.json.day-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bak";

    /// <summary>Newest first. The date is in the name in a sortable form, so the name is the order.</summary>
    public static IEnumerable<string> Newest(IEnumerable<string> paths)
        => paths.OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal);

    /// <summary>
    /// The day a snapshot belongs to, read from its name, or null for a file that
    /// does not follow the pattern.
    /// <para>
    /// From the name rather than the file's timestamp, because a copy written at
    /// 23:59 and one at 00:01 are two different days by intent - and the file
    /// time is whatever a backup tool or a copy to another drive made it.
    /// </para>
    /// </summary>
    public static DateTime? DayOf(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = "settings.json.day-";
        const string suffix = ".bak";

        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string digits = name[prefix.Length..^suffix.Length];
        return DateTime.TryParseExact(
            digits,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime day)
            ? day
            : null;
    }

    /// <summary>Everything past the newest <see cref="Keep"/>.</summary>
    public static IEnumerable<string> Surplus(IEnumerable<string> paths)
        => Newest(paths).Skip(Keep);
}
