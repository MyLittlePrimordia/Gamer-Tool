namespace GamerTool.Models;

/// <summary>
/// The preset ids that are part of the app's own vocabulary rather than a user's
/// data.
/// <para>
/// <see cref="Flat"/> is the one that matters: it means "nothing applied, leave
/// the hardware as it is", and it is stored in the profile, named by slots, and
/// compared in both the display and the audio half of the app. It used to be the
/// literal string in twenty places, which is one typo away from a slot that points
/// at a preset nobody has and a comparison that silently never matches.
/// </para>
/// </summary>
public static class PresetIds
{
    /// <summary>The neutral preset every profile starts on and every reset returns to.</summary>
    public const string Flat = "flat";
}
