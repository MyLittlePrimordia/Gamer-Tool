using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Which revert an exit gets, and which exits get none.
/// <para>
/// These are the four answers that used to be derived separately in four methods
/// inside the window, each from the same five fields and in its own order, with
/// nothing but comments recording that the order was load-bearing. None of the four
/// could be reached from a test: they need a real foreground window, a real process
/// and a real display.
/// </para>
/// <para>
/// The tests that matter most here are the ones asserting <c>Ignore</c>. A revert
/// that fails to fire leaves a boosted profile, which the user notices. A revert
/// that fires when it should not moves the screen and the sound under someone who
/// was doing something else entirely, which is the failure the guard exists to
/// prevent and the one a regression would introduce.
/// </para>
/// </summary>
public class AutoApplyDecisionTests
{
    private const string WildcardSlot = "wild";
    private const string BoundSlot = "bound";

    /// <summary>The ordinary case: a bound game the app applied, now closed.</summary>
    [Fact]
    public void ABoundGameClosingRevertsItsOwnSlot()
    {
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "witcher3",
            wildcardProcess: string.Empty,
            autoSlotId: BoundSlot,
            boundSlotId: BoundSlot,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Bound, decision.Kind);
        Assert.Equal(BoundSlot, decision.SlotId);
        Assert.True(decision.ShouldRevert);
        Assert.True(decision.IncludesScreen);
    }

    [Fact]
    public void AWildcardGamesClosingRevertsTheWildcard()
    {
        // The wildcard matched a process that is bound to nothing, so the name
        // lookup finds nothing by construction. Both halves have to agree instead:
        // the process remembered at apply time, and the slot still being the loaded
        // one.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "somegame",
            wildcardProcess: "somegame",
            autoSlotId: WildcardSlot,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: true);

        Assert.Equal(RevertKind.Wildcard, decision.Kind);
        Assert.Equal(WildcardSlot, decision.SlotId);
        Assert.True(decision.IncludesScreen);
    }

    [Fact]
    public void APausedGamesClosingPutsTheSoundBackAndLeavesTheScreenAlone()
    {
        // The paused case has to come first, and this is why: a focus pause stands
        // the screen down and clears the auto slot on its way through, so by the time
        // a paused game exits there is no auto slot left to match. Without this arm
        // the exit is ignored and the sound stays boosted for whatever runs next.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: BoundSlot,
            pausedExit: true,
            exitedProcess: "witcher3",
            wildcardProcess: string.Empty,
            autoSlotId: string.Empty,
            boundSlotId: BoundSlot,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.SoundOnlyWhilePaused, decision.Kind);
        Assert.True(decision.ShouldRevert);
        Assert.False(decision.IncludesScreen);
    }

    [Fact]
    public void ThePausedArmWinsEvenWhenABoundSlotAlsoMatches()
    {
        // The order, stated as a test rather than as a comment. A paused game whose
        // process is also bound to a slot must take the paused arm, because the
        // screen is already neutral and pushing it again is the work the paused arm
        // exists to skip.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: BoundSlot,
            pausedExit: true,
            exitedProcess: "witcher3",
            wildcardProcess: string.Empty,
            autoSlotId: BoundSlot,
            boundSlotId: BoundSlot,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.SoundOnlyWhilePaused, decision.Kind);
    }

    [Fact]
    public void ADifferentGameClosingWhileAnotherIsPausedIsNotOurs()
    {
        // The process has to be the paused one. Otherwise any program the user closes
        // while a game is paused in the background would flatten their sound.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: BoundSlot,
            pausedExit: false,
            exitedProcess: "notepad",
            wildcardProcess: string.Empty,
            autoSlotId: string.Empty,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
    }

    [Fact]
    public void AProgramNoSlotEverClaimedIsLeftAlone()
    {
        // The common case for a watched process going away, and the reason the guard
        // exists: quitting a browser must not move the user's preset.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "chrome",
            wildcardProcess: string.Empty,
            autoSlotId: BoundSlot,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
        Assert.False(decision.ShouldRevert);
    }

    [Fact]
    public void NothingIsUndoneWhenAutomaticRevertIsSwitchedOff()
    {
        // Including the paused arm, which is the one that would be easy to leak: the
        // setting has to gate all four answers, not three.
        Assert.Equal(
            RevertKind.Ignore,
            AutoApplyDecision.ClassifyExit(
                revertEnabled: false,
                pausedSlotId: BoundSlot,
                pausedExit: true,
                exitedProcess: "witcher3",
                wildcardProcess: string.Empty,
                autoSlotId: BoundSlot,
                boundSlotId: BoundSlot,
                autoSlotIsWildcard: false).Kind);

        Assert.Equal(
            RevertKind.Ignore,
            AutoApplyDecision.ClassifyExit(
                revertEnabled: false,
                pausedSlotId: string.Empty,
                pausedExit: false,
                exitedProcess: "witcher3",
                wildcardProcess: string.Empty,
                autoSlotId: BoundSlot,
                boundSlotId: BoundSlot,
                autoSlotIsWildcard: false).Kind);

        Assert.Equal(
            RevertKind.Ignore,
            AutoApplyDecision.ClassifyExit(
                revertEnabled: false,
                pausedSlotId: string.Empty,
                pausedExit: false,
                exitedProcess: "somegame",
                wildcardProcess: "somegame",
                autoSlotId: WildcardSlot,
                boundSlotId: string.Empty,
                autoSlotIsWildcard: true).Kind);
    }

    [Fact]
    public void TheUserHavingReplacedThePresetStopsTheRevert()
    {
        // The slot the app applied is no longer the slot loaded, so the user has
        // pressed a key or hit Reset since. Reverting would stomp the preset they
        // chose on purpose, which is the single thing this guard is for.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "witcher3",
            wildcardProcess: string.Empty,
            autoSlotId: "something-else",
            boundSlotId: BoundSlot,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
    }

    [Fact]
    public void AWildcardProcessWithTheWrongSlotLoadedIsNotOurs()
    {
        // Both halves, part two: the process is the remembered one but the slot that
        // applied it is not the slot loaded any more.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "somegame",
            wildcardProcess: "somegame",
            autoSlotId: BoundSlot,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
    }

    [Fact]
    public void AProcessRememberedByTheWildcardThatIsNoLongerLoadedIsNotOurs()
    {
        // Both halves, part one: the process is not the one remembered at apply time.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "somegame",
            wildcardProcess: "adifferentgame",
            autoSlotId: WildcardSlot,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: true);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
    }

    [Fact]
    public void AnEmptyProcessNameMatchesNothing()
    {
        // The watcher can report a name it could not read, and an empty string must
        // not be treated as a match for an empty field - which is exactly what a
        // plain equality check against blank state would do.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: string.Empty,
            wildcardProcess: string.Empty,
            autoSlotId: BoundSlot,
            boundSlotId: string.Empty,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Ignore, decision.Kind);
    }

    [Fact]
    public void ABoundMatchIsCaseInsensitive()
    {
        // Process names come from the image path on one side and from a profile file
        // on the other, so the casing is not guaranteed to agree.
        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedSlotId: string.Empty,
            pausedExit: false,
            exitedProcess: "Witcher3",
            wildcardProcess: string.Empty,
            autoSlotId: BoundSlot,
            boundSlotId: BoundSlot,
            autoSlotIsWildcard: false);

        Assert.Equal(RevertKind.Bound, decision.Kind);
    }

    [Fact]
    public void TheSameSlotAndProcessAreNotAppliedTwice()
    {
        Assert.True(AutoApplyDecision.AlreadyApplied("s1", "game", "s1", "game"));
        Assert.True(AutoApplyDecision.AlreadyApplied("s1", "game", "S1", "GAME"));
    }

    [Fact]
    public void ADifferentProcessUnderTheSameSlotIsApplied()
    {
        // The slot id alone would refuse a second game started under a different
        // process, which is a game the user is now playing.
        Assert.False(AutoApplyDecision.AlreadyApplied("s1", "game", "s1", "othergame"));
    }

    [Fact]
    public void ADifferentSlotForTheSameProcessIsApplied()
    {
        // The process alone would refuse a different slot loaded for the same
        // program, which is a slot the user has just pointed at it.
        Assert.False(AutoApplyDecision.AlreadyApplied("s1", "game", "s2", "game"));
    }

    [Fact]
    public void NothingIsAlreadyAppliedWhenNothingHasBeenApplied()
    {
        Assert.False(AutoApplyDecision.AlreadyApplied(string.Empty, string.Empty, "s1", "game"));
    }

    [Fact]
    public void AWildcardStandsDownWhenTheForegroundMovesToAnotherProgram()
    {
        Assert.True(AutoApplyDecision.WildcardShouldStandDown("somegame", "chrome"));
    }

    [Fact]
    public void AWildcardDoesNotStandDownForAnotherWindowOfItsOwnGame()
    {
        // The game's own launcher, or a second instance of it, is not a focus loss.
        // Standing down for it would suspend the boost for looking at the game.
        Assert.False(AutoApplyDecision.WildcardShouldStandDown("somegame", "somegame"));
        Assert.False(AutoApplyDecision.WildcardShouldStandDown("somegame", "SOMEGAME"));
    }

    [Fact]
    public void AWildcardThatNeverAppliedIsNotStandingDownForAnything()
    {
        // Nothing of ours is loaded, so there is nothing to stand down - and saying
        // otherwise would have the wildcard revert a preset the user chose by hand.
        Assert.False(AutoApplyDecision.WildcardShouldStandDown(string.Empty, "chrome"));
        Assert.False(AutoApplyDecision.WildcardShouldStandDown(string.Empty, string.Empty));
    }

    [Fact]
    public void AWildcardStandsDownForAnEmptyForegroundRatherThanStayingUp()
    {
        // An unreadable foreground is "I do not know", and the wildcard is already
        // only meant to hold while its own game is in front. Leaving the boost on for
        // a window the app cannot identify is the wrong side to fail towards.
        Assert.True(AutoApplyDecision.WildcardShouldStandDown("somegame", string.Empty));
    }
}
