using System;

namespace GamerTool.Services;

/// <summary>
/// The compositor's answer about a display's advanced colour support, as four
/// separate questions rather than one word.
/// <para>
/// A struct so the bit meanings have exactly one definition. The bits are the
/// Windows SDK's and are not this app's to renumber, so they are spelled out
/// here once rather than being re-derived at each use.
/// </para>
/// <para>
/// The union in the native struct packs these four flags and a plain <c>value</c>
/// word into the same four bytes, which is why the field this decodes is called
/// <c>value</c> and not <c>flags</c>: reading a named field for it would be a
/// different shape from the one Windows documents, and the size would be wrong.
/// </para>
/// </summary>
/// <param name="Supported">The display can do HDR at all, whatever is switched on.</param>
/// <param name="Enabled">Advanced colour is switched on for this display right now.</param>
/// <param name="WideColorEnforced">The wide colour gamut is being enforced.</param>
/// <param name="ForceDisabled">Something has switched advanced colour off and locked it there.</param>
internal readonly record struct AdvancedColorFlags(
    bool Supported,
    bool Enabled,
    bool WideColorEnforced,
    bool ForceDisabled)
{
    /// <summary>
    /// Splits the native flags word.
    /// <para>
    /// Unrecognised bits are ignored rather than treated as an error. Windows adds
    /// bits between releases and a driver is entitled to set one this build has
    /// never heard of; refusing to decode the answer would turn an unknown future
    /// flag into "HDR unknown" on hardware that is perfectly well behaved.
    /// </para>
    /// </summary>
    public static AdvancedColorFlags Decode(uint value) => new(
        Supported: (value & 0x1) != 0,
        Enabled: (value & 0x2) != 0,
        WideColorEnforced: (value & 0x4) != 0,
        ForceDisabled: (value & 0x8) != 0);

    /// <summary>
    /// Whether the panel is being driven in HDR, as opposed to merely being able to be.
    /// <para>
    /// Not the same as <see cref="Enabled"/>. A display can be switched to
    /// advanced colour while staying in SDR - Windows 11 does this on its own with
    /// "Automatically manage color for apps" turned on, where the panel gets a wider
    /// gamut and a better transfer curve and is still SDR - and reporting that as
    /// HDR would put a warning in front of a user whose gamma ramp is working
    /// perfectly well.
    /// </para>
    /// <para>
    /// Working assumption, to be calibrated against a real panel in 2.3. If that
    /// calibration shows advanced colour and HDR are indistinguishable here, this is
    /// the line to change and the tests to change with it.
    /// </para>
    /// </summary>
    public bool HdrOn => Enabled && !WideColorEnforced;
}