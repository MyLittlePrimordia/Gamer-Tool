using System;
using System.Globalization;
using System.IO;

namespace GamerTool.Services;

/// <summary>
/// The file a factory reset keeps before it replaces anything.
/// <para>
/// A reset is the one action in the app that costs the user their work in bulk:
/// presets, slots, keys and tuning all at once. It is offered because starting over
/// is a real answer to a profile that has been tuned into a mess, and it is safe to
/// offer because the settings it is about to replace are written out first, where
/// Restore can read them back.
/// </para>
/// </summary>
public static class FactoryReset
{
    /// <summary>Names the copies so they sort by when the reset happened.</summary>
    public const string Prefix = "settings-before-reset-";

    /// <summary>
    /// A path beside the profile, stamped to the second so two resets in a row cannot
    /// overwrite each other's rescue.
    /// </summary>
    public static string RescuePath(string folder, DateTime now)
    {
        string name = Prefix + now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        return Path.Combine(folder, name);
    }
}
