using System;

namespace GamerTool.Services;

/// <summary>
/// The one place where the bypass switch and the setting it drives are allowed to
/// disagree, which they necessarily do.
/// <para>
/// The switch is labelled BYPASS, so a user reads "on" as "the chain is silenced".
/// The setting is named for what it holds, so it reads "on" as "the chain is
/// live". Those are opposites, and the conversion is a pure function of a bool, so
/// it is written down here once instead of being open to a sign error at each
/// call site.
/// </para>
/// <para>
/// Deliberately separate from <c>MainWindow</c>, which cannot be constructed in a
/// test, and from <see cref="Models.AppSettings"/>, which is data. The mapping is
/// the whole of the behaviour and it is worth pinning down: with it wired the wrong
/// way round the app comes up with BYPASS showing on while the equaliser runs, and
/// throwing the switch silences the audio instead of engaging a bypass.
/// </para>
/// </summary>
public static class BypassToggle
{
    /// <summary>Whether the BYPASS switch should be drawn engaged.</summary>
    /// <param name="effectsEnabled">The value of the setting.</param>
    public static bool IsEngaged(bool effectsEnabled)
    {
        return !effectsEnabled;
    }

    /// <summary>The setting value implied by a BYPASS switch position.</summary>
    /// <param name="engaged">Whether the switch is on.</param>
    public static bool EffectsFromSwitch(bool engaged)
    {
        return !engaged;
    }
}
