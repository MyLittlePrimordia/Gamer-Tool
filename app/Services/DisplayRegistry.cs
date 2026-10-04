using System;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// The two things more than one file has to get right about the display entries
/// under <c>HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY</c>.
/// <para>
/// Both were written more than once, and the value decoder was written twice
/// because the first version was wrong: it tested for <c>byte[]</c> only, the
/// registry hands the value back as <c>Object[]</c> in practice, and the second
/// shape was discarded without a sound. That is what left one file naming a
/// perfectly good panel "Screen 1" while the other, which had already worked
/// round it, could see it - and both are still here, so the next reader has to
/// know which one to copy.
/// </para>
/// <para>
/// The tree walk is deliberately not here. One caller has a hardware id and walks
/// one panel's instances; the other has to walk every panel on the machine,
/// because it cannot rely on being given one. They look alike and are not the
/// same shape, and folding them together would buy a scope parameter and a
/// null-means-everywhere convention rather than the removal of a copy.
/// </para>
/// </summary>
internal static class DisplayRegistry
{
    /// <summary>Where Windows keeps a cached EDID per display connection.</summary>
    internal const string PanelRoot = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    /// <summary>
    /// The EDID as bytes, whichever shape the registry hands it back.
    /// <para>
    /// The value reads back as <c>byte[]</c> in theory and as <c>Object[]</c> in
    /// practice. A bare <c>is byte[]</c> test throws the second shape away
    /// silently, and an EDID that was never looked at is an ordinary monitor.
    /// </para>
    /// <para>
    /// Both shapes must be at least 128 bytes, the length of a base block. Shorter
    /// is not a short EDID, it is some other value that happens to be called
    /// EDID, and indexing it as one is how a driver ends up described as a stub
    /// target.
    /// </para>
    /// </summary>
    internal static byte[]? EdidBytes(object? raw)
    {
        return raw switch
        {
            byte[] direct when direct.Length >= 128 => direct,
            object[] boxed when boxed.Length >= 128 => boxed
                .Select(v => v is byte b ? b : (byte)0)
                .ToArray(),
            _ => null
        };
    }

    /// <summary>
    /// The panel key under <see cref="PanelRoot"/>, pulled out of a device id.
    /// <para>
    /// Drivers spell device ids several ways. The two that turn up:
    /// <c>\\?\DISPLAY#AOCA610#7&amp;272e3773&amp;0&amp;UID264#{...}</c> and
    /// <c>MONITOR\AOCA610\{...}\0001</c>. Both name the same panel by the token
    /// between the first <c>DISPLAY#</c> and the next <c>#</c>, or between the
    /// first and second backslash.
    /// </para>
    /// <para>
    /// This is a superset of the two parsers it replaces, which is the point of
    /// having replaced them. One of those took everything after the first
    /// backslash, so handed the <c>DISPLAY#</c> form it produced a key beginning
    /// <c>?\DISPLAY#</c>, which is not a key anything exists under. It was never
    /// given that form - its one caller asks for the hardware id rather than the
    /// interface name - so the difference was never visible. A parser that only
    /// works for the input its single caller happens to supply is one edit away
    /// from being wrong.
    /// </para>
    /// </summary>
    internal static string HardwareKeyOf(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        int hash = deviceId!.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        if (hash >= 0)
        {
            int start = hash + "DISPLAY#".Length;
            int end = deviceId.IndexOf('#', start);
            if (end > start)
            {
                return deviceId[start..end];
            }
        }

        int backslash = deviceId.IndexOf('\\');
        if (backslash >= 0)
        {
            int start = backslash + 1;
            int end = deviceId.IndexOf('\\', start);
            if (end > start)
            {
                return deviceId[start..end];
            }
        }

        return string.Empty;
    }
}