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
    /// Working assumption, to be calibrated against a real panel (A-001/I-003).
    /// Until calibrated, callers must treat this as advisory: when false the
    /// Display tab says "Can't tell whether HDR is on; if colors don't change,
    /// turn HDR off in Windows" rather than claiming SDR. Record captured flag
    /// values in AdvancedColorTests when calibrating and update this rule.
    /// </para>
    /// </summary>
    public bool HdrOn => Enabled && !WideColorEnforced;

    /// <summary>True only when the flags positively say SDR (advanced colour off).</summary>
    public bool DefinitelySdr => !Enabled;
}

/// <summary>
/// The compositor's answer from <c>GET_ADVANCED_COLOR_INFO_2</c>, which is the
/// only answer that can tell real HDR from advanced colour on an SDR panel.
/// <para>
/// Deliberately a separate type with its own decoder rather than a widening of
/// <see cref="AdvancedColorFlags"/>. The two structs use the same bit positions
/// for different meanings: bit 2 is "wide colour enforced" in the legacy flags
/// and reserved here, and bits 4 to 7 do not exist in the legacy answer at all.
/// Reusing the old decoder would read reserved as wide colour and would never
/// see HDR support or the user's HDR switch.
/// </para>
/// </summary>
/// <param name="Supported">Advanced colour is supported at all.</param>
/// <param name="Active">Advanced colour is being driven right now.</param>
/// <param name="LimitedByPolicy">Windows policy is holding advanced colour down.</param>
/// <param name="HdrSupported">The panel can do HDR.</param>
/// <param name="HdrUserEnabled">The user's HDR switch is on.</param>
/// <param name="WcgSupported">The panel can do wide colour gamut.</param>
/// <param name="WcgUserEnabled">The user's wide colour switch is on.</param>
/// <param name="ActiveColorMode">0 SDR, 1 WCG, 2 HDR.</param>
internal readonly record struct AdvancedColorInfo2(
    bool Supported,
    bool Active,
    bool LimitedByPolicy,
    bool HdrSupported,
    bool HdrUserEnabled,
    bool WcgSupported,
    bool WcgUserEnabled,
    uint ActiveColorMode)
{
    /// <summary>
    /// Splits the native flags word and the separate active-mode field.
    /// <para>
    /// Bit 2 is masked out rather than assigned. It is reserved in this struct,
    /// and a driver is entitled to set it; treating it as the legacy
    /// "wide colour enforced" flag would make a working HDR panel look like ACM.
    /// </para>
    /// </summary>
    public static AdvancedColorInfo2 Decode(uint value, uint activeColorMode) => new(
        Supported: (value & 0x01) != 0,
        Active: (value & 0x02) != 0,
        LimitedByPolicy: (value & 0x08) != 0,
        HdrSupported: (value & 0x10) != 0,
        HdrUserEnabled: (value & 0x20) != 0,
        WcgSupported: (value & 0x40) != 0,
        WcgUserEnabled: (value & 0x80) != 0,
        ActiveColorMode: activeColorMode);

    /// <summary>
    /// HDR is being driven right now. This is the ground truth; the user-enabled
    /// bit is the switch's position and can disagree with it when policy wins.
    /// </summary>
    public bool HdrActive => ActiveColorMode == 2;
}