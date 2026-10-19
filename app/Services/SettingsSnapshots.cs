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

    /// <summary>Everything past the newest <see cref="Keep"/>.</summary>
    public static IEnumerable<string> Surplus(IEnumerable<string> paths)
        => Newest(paths).Skip(Keep);
}
