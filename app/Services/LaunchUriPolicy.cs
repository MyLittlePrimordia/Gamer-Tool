using System;

namespace GamerTool.Services;

/// <summary>
/// How a game is started: handed to Steam, handed to Epic, or run from its own
/// executable.
/// </summary>
public enum LaunchVia
{
    /// <summary>The slot's executable is started directly.</summary>
    None,

    Steam,

    Epic,
}

/// <summary>
/// The complete list of URIs this app is willing to hand to the shell.
/// <para>
/// Launching a URI launches whatever registered that protocol, which is a
/// command line on this machine with the app's blessing. The value is also
/// persisted in the profile and can arrive from a backup somebody else wrote, so
/// this is a closed list rather than a scheme check: two exact shapes, digits or
/// a narrow name in a fixed position, no query beyond the one Epic needs, and
/// nothing else at all. Anything unrecognised is not "probably fine" - it is
/// cleared, and the slot falls back to its executable.
/// </para>
/// <para>
/// Deliberately no <c>Uri</c> parsing. The class normalises, unescapes and
/// accepts things this policy means to refuse, and the whole question here is
/// whether the string is one of two literals with a checked middle, which is
/// cheaper to read as string work than as a parse plus a list of surprises.
/// </para>
/// </summary>
public static class LaunchUriPolicy
{
    /// <summary>
    /// Steam app ids are 32-bit numbers. Ten digits is the cap that fits, and it
    /// is also the point past which a "number" is somebody testing the validator.
    /// </summary>
    public const int MaxAppIdDigits = 10;

    /// <summary>
    /// Epic's own names are short. The cap exists so a restored profile cannot
    /// carry a kilobyte of anything into a launcher call.
    /// </summary>
    public const int MaxEpicNameLength = 96;

    /// <summary>
    /// Both allowed forms are well under this. A longer string is not one of
    /// them, and checking the length first keeps the rest of the work bounded.
    /// </summary>
    private const int MaxLength = 200;

    private const string SteamPrefix = "steam://rungameid/";
    private const string EpicPrefix = "com.epicgames.launcher://apps/";
    private const string EpicQuery = "?action=launch&silent=true";

    /// <summary>
    /// Which launcher this URI would start, or <see cref="LaunchVia.None"/> when
    /// it is not allowed.
    /// </summary>
    public static LaunchVia ViaOf(string? uri)
    {
        if (!IsAllowed(uri))
        {
            return LaunchVia.None;
        }

        return uri!.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase)
            ? LaunchVia.Steam
            : LaunchVia.Epic;
    }

    public static bool IsAllowed(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxLength)
        {
            return false;
        }

        // Anything with a space, a tab, a newline or a control character in it is
        // either two arguments or an attempt at one.
        if (uri.Trim() != uri)
        {
            return false;
        }

        foreach (char c in uri)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        if (uri.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return IsAppId(uri.Substring(SteamPrefix.Length));
        }

        if (uri.StartsWith(EpicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!uri.EndsWith(EpicQuery, StringComparison.Ordinal)
                || uri.Length <= EpicPrefix.Length + EpicQuery.Length)
            {
                return false;
            }

            string name = uri.Substring(EpicPrefix.Length, uri.Length - EpicPrefix.Length - EpicQuery.Length);
            return IsEpicName(name);
        }

        return false;
    }

    /// <summary>
    /// The URI for a Steam app, or empty when the id is not a plain number. Built
    /// here so the scanners cannot invent a shape the policy would then refuse.
    /// </summary>
    public static string SteamUri(string? appId)
    {
        return IsAppId(appId) ? SteamPrefix + appId!.Trim() : string.Empty;
    }

    /// <summary>
    /// The URI for an Epic app, or empty when the name is not one the policy
    /// would accept.
    /// </summary>
    public static string EpicUri(string? appName)
    {
        if (appName is null)
        {
            return string.Empty;
        }

        string name = appName.Trim();
        return IsEpicName(name) ? EpicPrefix + name + EpicQuery : string.Empty;
    }

    /// <summary>
    /// The one phrase the pre-flight row and the log use, so "via Steam" means
    /// the same thing in all three places it appears.
    /// </summary>
    public static string Describe(LaunchVia via)
    {
        return via switch
        {
            LaunchVia.Steam => "via Steam",
            LaunchVia.Epic => "via Epic",
            _ => "direct",
        };
    }

    private static bool IsAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        string id = appId.Trim();
        if (id.Length == 0 || id.Length > MaxAppIdDigits)
        {
            return false;
        }

        bool any = false;
        foreach (char c in id)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            if (c != '0')
            {
                any = true;
            }
        }

        // Zero is not a game, and "0000000000" is the shape a fuzzer writes.
        return any;
    }

    /// <summary>
    /// Epic app names are alphanumeric with the separators its own manifests use.
    /// Everything that could end the path segment or start a query - slash,
    /// question mark, hash, ampersand, equals, percent - is refused, because a
    /// name that ends the segment is a different URI.
    /// </summary>
    private static bool IsEpicName(string name)
    {
        if (name.Length == 0 || name.Length > MaxEpicNameLength)
        {
            return false;
        }

        foreach (char c in name)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '_' or '-' or ':';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
