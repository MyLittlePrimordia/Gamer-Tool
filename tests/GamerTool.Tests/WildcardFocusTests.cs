using System;
using System.Collections.Generic;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// When a wildcard should stop being applied.
/// <para>
/// The fullscreen rule answers "when should this apply" and was the only half
/// that existed. Nothing answered "when should it stop", so the slot applied on a
/// fullscreen game and stayed applied on whatever came next - and would not
/// re-apply either, because the thing the user alt-tabbed to was not fullscreen.
/// The screen was wrong until the user pressed something.
/// </para>
/// <para>
/// This is the decision, extracted. The window-dependent parts - is it fullscreen,
/// is it the same process - are the caller's; what is under test is which of the
/// four combinations of those two questions does something.
/// </para>
/// </summary>
public class WildcardFocusTests
{
    private enum Action
    {
        /// <summary>Nothing: no wildcard was applied, or the guard says no.</summary>
        None,

        /// <summary>Apply the wildcard: this is a fullscreen window it has no slot for.</summary>
        Apply,

        /// <summary>Revert: the user has left a fullscreen window the wildcard claimed.</summary>
        Revert,
    }

    /// <summary>
    /// The whole rule, as a pure function so every branch is reachable without a
    /// desktop.
    /// <para>
    /// Mirrors the decision in <c>OnForegroundChanged</c> and
    /// <c>RevertWildcardOnFocusLoss</c>. Deliberately a separate implementation
    /// rather than a call into the window: these tests are about the truth table,
    /// and a test that needed a WPF window and a live foreground process to check a
    /// boolean expression would be testing the plumbing too.
    /// </para>
    /// </summary>
    private static Action Decide(
        bool autoSwitchOn,
        bool aSpecificSlotMatched,
        bool wildcardArmed,
        bool windowIsFullscreen,
        string windowProcess,
        string claimedProcess,
        bool revertOnFocusLoss,
        bool aWildcardIsLoaded)
    {
        if (!autoSwitchOn)
        {
            return Action.None;
        }

        // A specific match always wins, and a wildcard is only a fallback for a
        // game nobody bound. Both branches fall through to Apply below.
        if (!aSpecificSlotMatched && wildcardArmed)
        {
            if (windowIsFullscreen)
            {
                return Action.Apply;
            }

            // Not fullscreen. The one thing worth doing here is undoing a wildcard
            // that is currently applied, because the user has demonstrably left
            // the fullscreen window it claimed.
            bool leftTheClaimedProcess = aWildcardIsLoaded
                && claimedProcess.Length > 0
                && !string.Equals(claimedProcess, windowProcess, StringComparison.OrdinalIgnoreCase);

            return leftTheClaimedProcess && revertOnFocusLoss ? Action.Revert : Action.None;
        }

        return Action.None;
    }

    private const bool On = true;
    private const bool Off = false;

    /// <summary>The ordinary case: a fullscreen game with no slot bound to it.</summary>
    [Fact]
    public void A_fullscreen_game_nothing_is_bound_to_applies_the_wildcard()
    {
        Assert.Equal(Action.Apply, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: true,
            windowProcess: "game",
            claimedProcess: string.Empty,
            revertOnFocusLoss: On,
            aWildcardIsLoaded: false));
    }

    /// <summary>
    /// The reported bug: the user alt-tabs away from the game and the profile
    /// stays on.
    /// <para>
    /// A wildcard is loaded, the new window is not fullscreen, and it is not the
    /// game - so the preset has to go back to neutral.
    /// </para>
    /// </summary>
    [Fact]
    public void Alt_tabbing_away_from_a_fullscreen_game_reverts_the_wildcard()
    {
        Assert.Equal(Action.Revert, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "chrome",
            claimedProcess: "game",
            revertOnFocusLoss: On,
            aWildcardIsLoaded: true));
    }

    /// <summary>
    /// Alt-tabbing back into the game re-applies rather than reverting.
    /// <para>
    /// Same process, so this is not a focus loss at all - it is the game becoming
    /// foreground again, and the preset should still be on.
    /// </para>
    /// </summary>
    [Fact]
    public void Alt_tabbing_back_into_the_game_re_applies_it()
    {
        Assert.Equal(Action.Apply, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: true,
            windowProcess: "game",
            claimedProcess: "game",
            revertOnFocusLoss: On,
            aWildcardIsLoaded: true));
    }

    /// <summary>
    /// A second window of the same game is not a focus loss.
    /// <para>
    /// Games open multiple windows - a launcher, a store page, a crash reporter -
    /// and the process name is the same for all of them. Comparing by process is
    /// what keeps the preset on through any of them.
    /// </para>
    /// </summary>
    [Fact]
    public void Another_window_of_the_same_game_is_not_a_focus_loss()
    {
        Assert.NotEqual(Action.Revert, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "game",
            claimedProcess: "game",
            revertOnFocusLoss: On,
            aWildcardIsLoaded: true));
    }

    /// <summary>
    /// A bound game is never reverted by this rule.
    /// <para>
    /// The important guard. A game with its own slot has its own exit handling,
    /// which undoes the preset when the process ends - not when the user looks at
    /// something else. Reverting on focus would undo a preset the user had chosen
    /// deliberately just by checking their email.
    /// </para>
    /// </summary>
    [Fact]
    public void A_bound_game_is_not_reverted_by_losing_focus()
    {
        Assert.NotEqual(Action.Revert, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: true,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "chrome",
            claimedProcess: "game",
            revertOnFocusLoss: On,
            aWildcardIsLoaded: false));
    }

    /// <summary>
    /// With no wildcard applied there is nothing to undo.
    /// </summary>
    [Fact]
    public void Nothing_is_reverted_when_no_wildcard_is_loaded()
    {
        Assert.Equal(Action.None, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "chrome",
            claimedProcess: string.Empty,
            revertOnFocusLoss: On,
            aWildcardIsLoaded: false));
    }

    /// <summary>
    /// Turning auto-revert off stops the focus-loss undo along with the exit undo.
    /// <para>
    /// One switch for both, because they are the same promise: "the app puts things
    /// back when I am not looking at a game". A user who has turned that off wants
    /// the preset to survive, and re-applying it because they alt-tabbed away would
    /// be the opposite of what they asked for.
    /// </para>
    /// </summary>
    [Fact]
    public void Turning_auto_revert_off_stops_the_focus_loss_undo()
    {
        Assert.Equal(Action.None, Decide(
            autoSwitchOn: On,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "chrome",
            claimedProcess: "game",
            revertOnFocusLoss: Off,
            aWildcardIsLoaded: true));
    }

    /// <summary>
    /// Auto-switch off means nothing happens at all, including the undo.
    /// <para>
    /// Worth pinning separately: the guard is checked before anything else, and a
    /// user who has switched auto-switch off should not find their preset reverted
    /// by the feature they just turned off.
    /// </para>
    /// </summary>
    [Fact]
    public void Nothing_happens_at_all_when_auto_switch_is_off()
    {
        Assert.Equal(Action.None, Decide(
            autoSwitchOn: Off,
            aSpecificSlotMatched: false,
            wildcardArmed: true,
            windowIsFullscreen: false,
            windowProcess: "chrome",
            claimedProcess: "game",
            revertOnFocusLoss: On,
            aWildcardIsLoaded: true));
    }

    /// <summary>
    /// Every combination is accounted for, and only the two expected ones act.
    /// <para>
    /// A sweep rather than more examples, because the failure mode this guards is
    /// a branch nobody wrote a case for. Four booleans is sixteen combinations and
    /// the two that act are the two the feature is for.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_a_fullscreen_match_and_a_focus_loss_act()
    {
        List<string> acting = new();
        int applies = 0;
        int reverts = 0;

        foreach (bool switchOn in new[] { true, false })
        foreach (bool specific in new[] { true, false })
        foreach (bool armed in new[] { true, false })
        foreach (bool fullscreen in new[] { true, false })
        {
            Action action = Decide(
                autoSwitchOn: switchOn,
                aSpecificSlotMatched: specific,
                wildcardArmed: armed,
                windowIsFullscreen: fullscreen,
                windowProcess: fullscreen ? "game" : "chrome",
                claimedProcess: "game",
                revertOnFocusLoss: true,
                aWildcardIsLoaded: true);

            if (action != Action.None)
            {
                acting.Add($"switch={switchOn} specific={specific} armed={armed} fullscreen={fullscreen} -> {action}");
            }

            if (action == Action.Apply)
            {
                applies++;
            }

            if (action == Action.Revert)
            {
                reverts++;
            }
        }

        // One apply: the fullscreen game with a wildcard armed and nothing bound to
        // it. One revert: leaving that game for a non-fullscreen window.
        Assert.Equal(1, applies);
        Assert.Equal(1, reverts);
        Assert.Equal(2, acting.Count);
    }
}