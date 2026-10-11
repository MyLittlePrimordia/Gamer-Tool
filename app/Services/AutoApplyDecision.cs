using System;

namespace GamerTool.Services;

/// <summary>
/// Which kind of revert, if any, a watched process exiting should cause.
/// </summary>
/// <remarks>
/// <see cref="Ignore"/> is the deliberate default for anything not recognised. An
/// exit the app did not cause must never move the screen or the sound: the whole
/// purpose of the guard is that a game the user was not running through a slot
/// leaves their preset alone.
/// </remarks>
public enum RevertKind
{
    /// <summary>Not ours to undo, or the user has switched automatic revert off.</summary>
    Ignore,

    /// <summary>
    /// The sound only. The screen was already stood down when the focus pause
    /// fired, so pushing it again would be work that is not needed - and the sound
    /// is the half that would otherwise stay boosted for whatever runs next.
    /// </summary>
    SoundOnlyWhilePaused,

    /// <summary>Screen and sound: a wildcard slot claimed this process itself.</summary>
    Wildcard,

    /// <summary>Screen and sound: a slot bound to this program by name or path.</summary>
    Bound,
}

/// <summary>
/// What an exit should do, and which slot it is about.
/// </summary>
/// <param name="Kind">Which of the four answers this exit gets.</param>
/// <param name="SlotId">
/// The slot to revert, or empty for <see cref="RevertKind.Ignore"/>. Carried as an
/// id rather than the slot so the decision has no way to read a preset off it -
/// that is the window's job and keeping it out is what makes this testable.
/// </param>
public readonly record struct RevertDecision(RevertKind Kind, string SlotId)
{
    /// <summary>Nothing to do.</summary>
    public static RevertDecision Ignore => new(RevertKind.Ignore, string.Empty);

    /// <summary>Whether this exit should move anything at all.</summary>
    public bool ShouldRevert => Kind != RevertKind.Ignore;

    /// <summary>
    /// Whether the screen should be put back as well as the sound.
    /// </summary>
    /// <remarks>
    /// False for the paused case and true for the other two, which is the
    /// distinction that keeps a paused game's exit from pushing a screen that a
    /// focus pause has already stood down.
    /// </remarks>
    public bool IncludesScreen => Kind is RevertKind.Wildcard or RevertKind.Bound;
}

/// <summary>
/// The decisions behind automatic apply, as pure functions.
/// </summary>
/// <remarks>
/// These are the answers that used to be spread across four methods inside the
/// window - <c>OnForegroundChanged</c>, <c>OnTargetLaunched</c>,
/// <c>RevertWildcardOnFocusLoss</c> and <c>OnTargetExited</c> - each re-deriving
/// "is this ours to undo?" from the same five fields in its own order. Four
/// derivations of one question is four chances to disagree, and they could not be
/// tested at all: every one of them needs a real foreground window, a real process
/// and a real display to reach.
/// <para>
/// Out here the whole decision is arguments in and an enum out. Every branch is
/// reachable from a test, including the ones that matter most - the exits that must
/// do <em>nothing</em>, which are the ones a regression would turn into a screen
/// change nobody asked for.
/// </para>
/// <para>
/// Deliberately not a state object holding the fields. The window keeps its own
/// state and passes it in; moving the fields out would have been a much larger and
/// riskier change for the same decisions, and the source-text tests address several
/// of those fields by name.
/// </para>
/// </remarks>
public static class AutoApplyDecision
{
    /// <summary>
    /// What an exit means, given everything the app remembers about what it applied.
    /// </summary>
    /// <param name="revertEnabled">Whether automatic revert is switched on, and the app is not quitting.</param>
    /// <param name="pausedExit">
    /// Whether the exiting process is the one a focus pause was armed for. Decided by
    /// the caller rather than here, so the window keeps the pause comparison visible
    /// in the method whose ordering depends on it.
    /// </param>
    /// <param name="pausedSlotId">The slot whose screen a focus pause stood down, or empty.</param>
    /// <param name="exitedProcess">The process that has just gone.</param>
    /// <param name="wildcardProcess">The process a wildcard slot claimed, or empty.</param>
    /// <param name="autoSlotId">The slot the app last applied by itself, or empty.</param>
    /// <param name="boundSlotId">The id of the slot bound to <paramref name="exitedProcess"/>, or empty for none.</param>
    /// <param name="autoSlotIsWildcard">Whether the slot in <paramref name="autoSlotId"/> is a wildcard.</param>
    /// <remarks>
    /// The order of the checks is the whole design and it is asserted by test.
    /// <list type="number">
    /// <item>
    /// The paused case comes first, and it has to. A focus pause stands the screen
    /// down and clears the auto slot on its way through, so by the time a paused
    /// game exits there is no auto slot left to match - the exit would be ignored
    /// and the sound would stay boosted for whatever the user ran next, which is the
    /// one thing the exit path exists to prevent.
    /// </item>
    /// <item>
    /// The settings guard comes next, and before any matching. Auto-revert off, or
    /// the app on its way out, means nothing is undone whatever the process is.
    /// </item>
    /// <item>
    /// A bound slot is matched by name. When that finds nothing the process may
    /// still be one a wildcard claimed, because a wildcard matches something that is
    /// bound to nothing - which is exactly why the name lookup cannot find it.
    /// </item>
    /// <item>
    /// Both halves then have to agree for the wildcard: the process must be the one
    /// remembered at apply time <em>and</em> the slot that applied it must still be
    /// the slot loaded. The first stops any unbound program's exit from reverting
    /// the profile; the second stops a revert firing for a preset the user has since
    /// replaced with their own key press.
    /// </item>
    /// <item>
    /// The last check is for both kinds: the slot has to be the one the app applied.
    /// Without it, quitting a game would stomp a preset the user chose by hand.
    /// </item>
    /// </list>
    /// </remarks>
    public static RevertDecision ClassifyExit(
        bool revertEnabled,
        bool pausedExit,
        string pausedSlotId,
        string exitedProcess,
        string wildcardProcess,
        string autoSlotId,
        string boundSlotId,
        bool autoSlotIsWildcard)
    {
        // 1. A paused game's exit. Sound only - the screen is already neutral.
        if (pausedExit)
        {
            return revertEnabled
                ? new RevertDecision(RevertKind.SoundOnlyWhilePaused, pausedSlotId)
                : RevertDecision.Ignore;
        }

        string exited = exitedProcess ?? string.Empty;

        // 2. Nothing is undone at all when the user has switched it off, or while
        //    the app is quitting.
        if (!revertEnabled)
        {
            return RevertDecision.Ignore;
        }

        // 3, 4. A bound slot by name, or a wildcard's own process with both halves
        //       agreeing.
        bool wildcard = false;
        string slotId = boundSlotId ?? string.Empty;

        if (slotId.Length == 0)
        {
            if (!string.Equals(wildcardProcess, exited, StringComparison.OrdinalIgnoreCase))
            {
                return RevertDecision.Ignore;
            }

            if (!autoSlotIsWildcard)
            {
                return RevertDecision.Ignore;
            }

            wildcard = true;
            slotId = autoSlotId;
        }

        // 5. The slot has to be the one the app applied by itself.
        if (!string.Equals(autoSlotId, slotId, StringComparison.OrdinalIgnoreCase))
        {
            return RevertDecision.Ignore;
        }

        return new RevertDecision(wildcard ? RevertKind.Wildcard : RevertKind.Bound, slotId);
    }

    /// <summary>
    /// Whether this slot has already been applied for this process, and so should
    /// not be applied again.
    /// </summary>
    /// <remarks>
    /// The guard that stops the foreground tick re-applying a slot on every pass.
    /// Both halves are needed: the slot id alone would refuse a second game started
    /// under a different process, and the process alone would refuse a different
    /// slot loaded for the same program.
    /// </remarks>
    public static bool AlreadyApplied(string autoSlotId, string autoProcess, string slotId, string processName)
    {
        return string.Equals(autoSlotId, slotId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(autoProcess, processName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a wildcard should stand down because the window in front is not
    /// fullscreen and is not the game it claimed.
    /// </summary>
    /// <param name="wildcardProcess">The process the wildcard applied itself for, or empty.</param>
    /// <param name="foregroundProcess">What has just taken the foreground.</param>
    /// <remarks>
    /// The second half is the one that is easy to get wrong. A wildcard's game
    /// having another window in front - its own launcher, a second instance of
    /// itself - is not a focus loss, and standing down for it would suspend the
    /// boost for looking at the game.
    /// </remarks>
    public static bool WildcardShouldStandDown(string wildcardProcess, string foregroundProcess)
    {
        if (wildcardProcess.Length == 0)
        {
            return false;
        }

        return !string.Equals(wildcardProcess, foregroundProcess ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
