using System;

namespace GamerTool.Services;

/// <summary>
/// Decides whether a remembered preset is something startup should put back on
/// the hardware.
/// <para>
/// The active preset is remembered as an identity, not as a report of what the
/// screen and engine are doing: the exit path restores the original ramp and
/// hands FxSound back before the app quits. The next launch therefore has to
/// re-assert the identity, or the dropdown names a preset nothing is using.
/// </para>
/// <para>
/// Flat is the one identity that means "leave the hardware alone". There is
/// nothing to put back, and writing a neutral tune would overwrite whatever the
/// user has chosen in FxSound or in Windows. A preset that has since been
/// deleted is in the same position: the id is still in the profile, but there is
/// no tune behind it to restore.
/// </para>
/// </summary>
public static class StartupRestore
{
    /// <summary>
    /// True when <paramref name="presetId"/> names a real preset that startup
    /// should apply.
    /// </summary>
    /// <param name="presetId">The id remembered in the profile, or the working tune's id.</param>
    /// <param name="presetExists">Whether the id still resolves to a preset.</param>
    public static bool ShouldApply(string? presetId, bool presetExists) =>
        presetExists
        && !string.IsNullOrWhiteSpace(presetId)
        && !string.Equals(presetId, "flat", StringComparison.OrdinalIgnoreCase);
}
