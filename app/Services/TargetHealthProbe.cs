using System;
using System.Collections.Generic;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// What the driver says about the display target behind a monitor, and whether
/// that account hangs together.
/// <para>
/// This exists to answer one question that a missing DDC/CI handle cannot: is the
/// driver refusing to talk to a display it has properly brought up, or has it never
/// brought the display up at all? Those need opposite advice, they look identical
/// from outside, and guessing wrong sends people to change settings that were never
/// the problem.
/// </para>
/// </summary>
public enum TargetHealth
{
    /// <summary>Nothing could be asked, so nothing is claimed.</summary>
    Unknown,

    /// <summary>
    /// The driver described the target fully: real link type, real refresh rate,
    /// and it answered the target queries. The link is up.
    /// </summary>
    Complete,

    /// <summary>
    /// The target is coherent but the driver will not describe it. Consistent with
    /// a driver deliberately withholding access, and the reason content protection
    /// was the first theory.
    /// </summary>
    Withheld,

    /// <summary>
    /// The target's own numbers contradict each other. Not an interpretation and
    /// not a judgement about anyone's intentions: the driver is reporting a mode
    /// index that does not exist, or a refresh rate of one hertz, for a display it
    /// is simultaneously driving at full specification.
    /// </summary>
    Stub
}


/// <summary>
/// The raw answers, kept apart from the verdict so the verdict can be tested
/// without a display attached.
/// </summary>
public sealed record TargetDescription
{
    /// <summary>Whether the compositor considers this target attached at all.</summary>
    public bool Available { get; init; }

    public uint VideoOutputTechnology { get; init; }

    public uint RefreshNumerator { get; init; }

    public uint RefreshDenominator { get; init; }

    /// <summary>
    /// The mode index the driver says describes this target.
    /// </summary>
    public uint TargetModeInfoIdx { get; init; }

    /// <summary>
    /// How many modes the compositor says exist. An index at or past this is
    /// pointing at nothing.
    /// </summary>
    public uint ModeCount { get; init; }

    /// <summary>True when the source's mode index is also out of range.</summary>
    public bool SourceModeInfoOutOfRange { get; init; }

    public bool TargetNameRefused { get; init; }

    public bool TargetInfoRefused { get; init; }

    public bool AdvancedColorRefused { get; init; }

    /// <summary>
    /// True when the source query succeeded. Carried because it is the control: if
    /// the source answers and the target does not, the compositor is talking to the
    /// driver and the driver is declining about one specific thing.
    /// </summary>
    public bool SourceNameOk { get; init; }

    public string GdiSourceName { get; init; } = string.Empty;
}


/// <summary>One target's description and the verdict drawn from it.</summary>
public sealed class TargetHealthReading
{
    public TargetHealthReading(TargetHealth health, IReadOnlyList<string> reasons)
    {
        Health = health;
        Reasons = reasons;
    }

    public TargetHealth Health { get; }

    public IReadOnlyList<string> Reasons { get; }

    public string Summary =>
        Health == TargetHealth.Complete
            ? "driver described the target fully"
            : Health + (Reasons.Count == 0 ? string.Empty : " - " + string.Join("; ", Reasons));

    public override string ToString() => Summary;
}


/// <summary>
/// Decides whether a display target's self-description is coherent.
/// <para>
/// Pure on purpose, and separately testable for the same reason everything else in
/// this area is: the interesting cases need a display in a particular state, and a
/// decision this consequential cannot be left to whatever machine happens to run
/// the tests.
/// </para>
/// </summary>
public static class TargetHealthProbe
{
    /// <summary>
    /// The one that settles it.
    /// <para>
    /// A mode index at or past the mode count is not a subtle signal and does not
    /// depend on knowing anything about link types, refresh rates or content
    /// protection. The compositor allocated a fixed number of mode slots and the
    /// driver named one that is not among them. Whatever else is true, the driver
    /// is not describing a target it has set up.
    /// <para>
    /// This is the check that would have caught the original problem. The missing
    /// DDC/CI handle was read as a driver withholding access, and it was not: the
    /// same probe was reporting a mode index of 10 out of 2 the whole time, in the
    /// one log line nobody read the second field of.
    /// </para>
    /// </summary>
    public const string OutOfRangeModeReason =
        "the driver names mode index {0} but the compositor only has {1}, so the target is not set up";

    public const string DegenerateRefreshReason =
        "the driver reports a refresh rate of {0}/{1}, which is a placeholder rather than a real mode";

    public const string TargetRefusedReason =
        "the driver refused every query about the target (name, capabilities, advanced colour) while answering for the source";

    public const string SourceRefusedReason =
        "the driver refused the source query too, so the whole topology query is unavailable rather than selective";

    public const string NoTargetReason =
        "the compositor does not consider this display an attached target";

    public static TargetHealthReading Classify(TargetDescription d)
    {
        List<string> reasons = new();

        if (!d.Available)
        {
            return new TargetHealthReading(TargetHealth.Unknown, new[] { NoTargetReason });
        }

        bool sourceIndexOk = !d.SourceModeInfoOutOfRange;
        bool targetIndexOk = d.TargetModeInfoIdx < d.ModeCount;
        bool degenerateRefresh = d.RefreshNumerator <= 1 && d.RefreshDenominator <= 1;

        if (!targetIndexOk)
        {
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                OutOfRangeModeReason,
                d.TargetModeInfoIdx,
                d.ModeCount));
        }

        if (!sourceIndexOk)
        {
            reasons.Add("the source's mode index is out of range as well, so the whole path is unpopulated");
        }

        if (degenerateRefresh && targetIndexOk)
        {
            // Only worth saying on its own when the index is sane, because a target
            // that was never set up reports both and the refresh rate adds nothing
            // to what the index already proved.
            reasons.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                DegenerateRefreshReason,
                d.RefreshNumerator,
                d.RefreshDenominator));
        }

        if (d.TargetNameRefused && d.TargetInfoRefused && d.AdvancedColorRefused)
        {
            reasons.Add(d.SourceNameOk ? TargetRefusedReason : SourceRefusedReason);
        }

        if (reasons.Count > 0)
        {
            // A contradiction outranks a refusal. A driver that says the mode does
            // not exist has already told us the target is not up, and whether it
            // would also have withheld the DDC/CI handle is then a side question
            // rather than the explanation.
            TargetHealth health = !targetIndexOk || !sourceIndexOk || degenerateRefresh
                ? TargetHealth.Stub
                : TargetHealth.Withheld;

            return new TargetHealthReading(health, reasons);
        }

        return new TargetHealthReading(TargetHealth.Complete, Array.Empty<string>());
    }

    /// <summary>
    /// Whether the AMD HDCP hint should be offered for a display whose target is
    /// in this state.
    /// <para>
    /// False for a stub, and that is the whole point of the type. Telling somebody
    /// whose display target was never initialised to go and change a content
    /// protection setting is worse than saying nothing: the setting is real, the
    /// instruction is clickable, and following it changes nothing while making it
    /// look as though the diagnosis was checked.
    /// </para>
    /// </summary>
    public static bool HdcpHintIsPlausible(TargetHealthReading reading) =>
        reading.Health is TargetHealth.Complete or TargetHealth.Withheld;
}
