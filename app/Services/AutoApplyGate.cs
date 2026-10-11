using System;

namespace GamerTool.Services;

/// <summary>
/// The two second window in which an auto-applied slot is left alone.
/// <para>
/// Its own type for one reason: the sentinel. <see cref="DateTime.MinValue"/> is
/// what "no claim is outstanding" is spelled as, and the stand-down paths write it
/// to clear a claim. That was the defect - the field was written on one of the four
/// stand-down paths and not the others, so a game that closed and was relaunched
/// inside two seconds was rejected as a duplicate apply and nothing re-checked.
/// </para>
/// <para>
/// Making the sentinel mean something means a cleared guard cannot be read as an
/// ancient one, and gives the rule a place to be tested as arithmetic rather than
/// inferred from a WPF window that cannot be constructed in a test.
/// </para>
/// </summary>
internal static class AutoApplyGate
{
    /// <summary>
    /// Long enough to swallow the duplicate foreground events one window change
    /// produces, short enough that a user relaunching a game immediately is not
    /// left wondering why it did not apply.
    /// </summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a claim made at <paramref name="stamp"/> is still inside its window
    /// at <paramref name="now"/>.
    /// </summary>
    internal static bool IsDebounced(DateTime stamp, DateTime now)
    {
        // The sentinel checked explicitly rather than left to the subtraction.
        // <c>now - DateTime.MinValue</c> is an enormous TimeSpan and so happens to
        // compare false anyway, which is precisely why the bug was invisible: the
        // arithmetic was never wrong, the field was just never written. Spelling
        // the sentinel out means the two cases cannot be confused for one another.
        if (stamp == DateTime.MinValue)
        {
            return false;
        }

        TimeSpan elapsed = now - stamp;

        // Negative elapsed time means the clock went backwards, not that the claim
        // is inside its window - and a negative TimeSpan compares as less than the
        // window, so without this the naive check debounces the claim for the whole
        // skew. UtcNow is not monotonic: an NTP correction, or a resume from sleep,
        // can both put it behind the stamp, and on a laptop that has just woken up
        // that is exactly when the user expects the game they launched to apply.
        if (elapsed < TimeSpan.Zero)
        {
            return false;
        }

        return elapsed < Debounce;
    }
}
