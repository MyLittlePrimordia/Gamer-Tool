using System;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the app is prepared to say about FxSound's health, decided before it says
/// it.
/// <para>
/// The failure this exists for is the ordinary one: a user's EQ does nothing, the
/// Audio tab looks fine, and there is nothing anywhere saying the engine is not
/// running.
/// </para>
/// <para>
/// Decided from values rather than produced as text, so the wording is one decision
/// and the presentation is a separate one. The same answers have to be reachable
/// from the Settings line and the tray tooltip without the two disagreeing, and
/// that is only true if neither of them writes their own copy.
/// </para>
/// <para>
/// The engine's own state is three-valued, not two. That is not fussiness: it is the
/// fix for a status line that told people their EQ was switched off while it was
/// on. See <see cref="AnUnreportedPowerFieldIsNotASwitchedOffEq"/>.
/// </para>
/// </summary>
public class FxHealthTests
{
    private static FxHealth.PowerState On => FxHealth.PowerState.On;

    private static FxHealth.PowerState Off => FxHealth.PowerState.Off;

    /// <summary>What the engine did not say anything about.</summary>
    private static FxHealth.PowerState Unknown => FxHealth.PowerState.Unknown;

    [Fact]
    public void NotInstalledIsItsOwnAnswer()
    {
        // The loudest of them and the only one that offers to install. Deciding it
        // from IsInstalled rather than from a null snapshot keeps it answerable when
        // the file simply does not exist, which is what not-installed looks like from
        // the outside.
        FxHealth state = FxHealth.Decide(
            installed: false,
            running: false,
            snapshotPresent: false,
            snapshotFresh: false,
            power: Unknown);

        Assert.Equal(FxHealth.State.Missing, state.HowItIs);
        Assert.True(state.OffersInstall);
        Assert.False(state.OffersStart);
    }

    [Fact]
    public void InstalledButNotRunningOffersToStartIt()
    {
        // Distinct from Missing, and it has to be: the install button on a machine
        // that already has FxSound does nothing useful, and a user told to install
        // something they already installed concludes the app is confused.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: false,
            snapshotPresent: false,
            snapshotFresh: false,
            power: Unknown);

        Assert.Equal(FxHealth.State.NotRunning, state.HowItIs);
        Assert.False(state.OffersInstall);
        Assert.True(state.OffersStart);
    }

    [Fact]
    public void AStaleSnapshotSaysSoRatherThanClaimingHealth()
    {
        // The case worth being careful about. A snapshot from an hour ago is
        // perfectly well formed and describes somebody's settings rather than their
        // sound, so nothing about it looks wrong - and reading it as live is how the
        // app ends up reporting a curve as applied when it was applied an hour ago.
        //
        // Checked BEFORE power, because a stale file can well say power is on.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: true,
            snapshotPresent: true,
            snapshotFresh: false,
            power: On);

        Assert.Equal(FxHealth.State.Stale, state.HowItIs);
    }

    [Fact]
    public void NoSnapshotAtAllIsStaleRatherThanHealthy()
    {
        // Same answer as a stale one, different wording. Both mean "the app cannot
        // currently see what the engine is doing", and treating them differently
        // would mean the worse case - a crash that stopped writing the file - was
        // reported as the one case that needed no attention.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: true,
            snapshotPresent: false,
            snapshotFresh: false,
            power: Unknown);

        Assert.Equal(FxHealth.State.Stale, state.HowItIs);
    }

    [Fact]
    public void HealthyNeedsAFreshSnapshotAndAPoweredEngine()
    {
        // Every one of the three. Healthy is the only answer that lets the app claim
        // it knows what the engine is doing, so it takes the strongest evidence
        // available rather than the weakest sufficient one.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: true,
            snapshotPresent: true,
            snapshotFresh: true,
            power: On);

        Assert.Equal(FxHealth.State.Ok, state.HowItIs);
        Assert.False(state.OffersInstall);
        Assert.False(state.OffersStart);
    }

    [Fact]
    public void AnEngineThatIsUpButPoweredOffIsNotHealthy()
    {
        // Distinct from NotRunning, and the wording matters: the engine is running,
        // the user has told it not to do anything, and "FxSound is off" would be wrong
        // in a way that sends them looking for a crash.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: true,
            snapshotPresent: true,
            snapshotFresh: true,
            power: Off);

        Assert.Equal(FxHealth.State.PoweredOff, state.HowItIs);
    }

    [Fact]
    public void AnUnreportedPowerFieldIsNotASwitchedOffEq()
    {
        // The bug, and the reason the engine's power is an enum with three values
        // rather than a bool with two.
        //
        // <c>Power</c> on the parsed state is a plain bool defaulting to false, and
        // status.json is another program's output that changes shape. A file with no
        // <c>power</c> key in it therefore read as powered-off, and the status line
        // said so - "EQ switched off" in front of people whose EQ was on, on a
        // machine that was answering perfectly well.
        //
        // Silence is not a denial. A field the engine did not report cannot be
        // reported as a field the engine reported false.
        FxHealth state = FxHealth.Decide(
            installed: true,
            running: true,
            snapshotPresent: true,
            snapshotFresh: true,
            power: Unknown);

        Assert.NotEqual(FxHealth.State.PoweredOff, state.HowItIs);
        Assert.Equal(FxHealth.State.Ok, state.HowItIs);
        Assert.False(state.NeedsAttention);
    }

    [Fact]
    public void OnlyTheEngineSayingOffProducesTheSwitchedOffAnswer()
    {
        // Pinned on both sides of the distinction, because the fix for a false
        // "switched off" that over-corrects into never saying it is just as wrong.
        Assert.Equal(
            FxHealth.State.PoweredOff,
            FxHealth.Decide(true, true, true, true, Off).HowItIs);

        Assert.NotEqual(
            FxHealth.State.PoweredOff,
            FxHealth.Decide(true, true, true, true, Unknown).HowItIs);

        Assert.NotEqual(
            FxHealth.State.PoweredOff,
            FxHealth.Decide(true, true, true, true, On).HowItIs);
    }

    [Fact]
    public void AStatusFileWithNoPowerKeyIsNotReadAsPoweredOff()
    {
        // The parse, rather than the decision, because this is where the two were
        // actually indistinguishable. status.json is another program's output, so a
        // version that does not carry the key is an ordinary thing to encounter and
        // not a corrupted file.
        FxSoundState withoutKey = Parse("{\"version\":\"1.2.15.0\",\"presets\":{}}");
        FxSoundState withFalse = Parse("{\"version\":\"1.2.15.0\",\"power\":false}");
        FxSoundState withTrue = Parse("{\"version\":\"1.2.15.0\",\"power\":true}");

        Assert.False(withoutKey.ReportsPower);
        Assert.True(withFalse.ReportsPower);
        Assert.True(withTrue.ReportsPower);

        Assert.False(withFalse.Power);
        Assert.True(withTrue.Power);

        // The pair that matters: same Power, different knowledge.
        Assert.Equal(withoutKey.Power, withFalse.Power);
        Assert.NotEqual(withoutKey.ReportsPower, withFalse.ReportsPower);
    }

    private static FxSoundState Parse(string json)
    {
        System.Reflection.MethodInfo? parse = typeof(FxSoundState)
            .GetMethod("Parse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(parse);
        return (FxSoundState)parse!.Invoke(null, new object?[] { json, DateTime.UtcNow })!;
    }

    [Fact]
    public void InstalledBeatsEveryOtherAnswerWhenTheFilesAreGone()
    {
        // A snapshot read before an uninstall would otherwise report NotRunning on a
        // machine with no FxSound at all. Installation is the loudest fact available
        // and it is checked first for that reason.
        FxHealth state = FxHealth.Decide(
            installed: false,
            running: true,
            snapshotPresent: true,
            snapshotFresh: true,
            power: On);

        Assert.Equal(FxHealth.State.Missing, state.HowItIs);
    }

    [Fact]
    public void EveryStateIsReachableAndEachEarlierFactMasksTheOnesAfterIt()
    {
        // The ordering is the whole of it, and the second half of this is the part
        // that matters: a decision whose checks are in the wrong order still returns
        // a plausible answer for every input, and only disagrees when two things are
        // wrong at once - which is exactly when a user cannot tell the app from a
        // coincidence.
        //
        // The version of this that was here before walked the states and then
        // asserted count >= 0, which is true of every number ever produced. It ran
        // to green without ever checking that the walk moved.
        Assert.Equal(FxHealth.State.Missing, FxHealth.Decide(false, true, true, true, On).HowItIs);
        Assert.Equal(FxHealth.State.NotRunning, FxHealth.Decide(true, false, true, true, On).HowItIs);
        Assert.Equal(FxHealth.State.Stale, FxHealth.Decide(true, true, true, false, On).HowItIs);
        Assert.Equal(FxHealth.State.Stale, FxHealth.Decide(true, true, false, false, On).HowItIs);
        Assert.Equal(FxHealth.State.PoweredOff, FxHealth.Decide(true, true, true, true, Off).HowItIs);
        Assert.Equal(FxHealth.State.Ok, FxHealth.Decide(true, true, true, true, On).HowItIs);

        // Each check outranks every one below it, so two faults at once are
        // reported as the earlier of the two rather than whichever came first in
        // the code.
        Assert.Equal(FxHealth.State.Missing, FxHealth.Decide(false, false, false, false, Off).HowItIs);
        Assert.Equal(FxHealth.State.NotRunning, FxHealth.Decide(true, false, false, false, Off).HowItIs);
        Assert.Equal(FxHealth.State.Stale, FxHealth.Decide(true, true, true, false, Off).HowItIs);

        // And the reason power is checked last: a stale file can say it is powered,
        // and reading that as live is how the app claims a curve is applied when it
        // was applied an hour ago.
        Assert.Equal(FxHealth.State.Stale, FxHealth.Decide(true, true, true, false, On).HowItIs);
        Assert.Equal(FxHealth.State.Stale, FxHealth.Decide(true, true, true, false, Off).HowItIs);
    }

    [Fact]
    public void TheCaptionSaysSomethingDifferentForEveryAnswer()
    {
        // States that share a caption are states the user cannot tell apart, which
        // is the situation this whole thing replaces.
        string[] captions = new[]
        {
            FxHealth.Decide(false, false, false, false, Unknown).Caption,
            FxHealth.Decide(true, false, false, false, Unknown).Caption,
            FxHealth.Decide(true, true, true, false, On).Caption,
            FxHealth.Decide(true, true, true, true, Off).Caption,
            FxHealth.Decide(true, true, true, true, On).Caption,
        };

        Assert.Equal(captions.Length, captions.Distinct().Count());
    }

    [Fact]
    public void EveryAnswerHasACaptionAndOnlyTheUnhealthyOnesHaveADetail()
    {
        // A detail is the extra sentence saying what to do about it. Healthy has
        // nothing to do, so an empty detail there is right rather than missing - and
        // a caption with no detail on a broken state would leave the user knowing
        // something is wrong and not what.
        foreach (FxHealth.State answer in Enum.GetValues<FxHealth.State>())
        {
            FxHealth state = FxHealth.Decide(
                answer == FxHealth.State.Missing,
                answer != FxHealth.State.Missing && answer != FxHealth.State.NotRunning,
                answer is not (FxHealth.State.Missing or FxHealth.State.NotRunning),
                answer is not (FxHealth.State.Missing or FxHealth.State.NotRunning),
                answer == FxHealth.State.PoweredOff ? Off : On);

            Assert.False(string.IsNullOrWhiteSpace(state.Caption), answer + " has no caption");

            if (answer != FxHealth.State.Ok)
            {
                Assert.False(string.IsNullOrWhiteSpace(state.Detail), answer + " has no detail");
            }
        }
    }

    [Fact]
    public void AHealthyEngineOffersNeitherButton()
    {
        // Two buttons that both appear when nothing is wrong is a settings page
        // telling the user to fix something that is not broken.
        FxHealth state = FxHealth.Decide(true, true, true, true, On);

        Assert.False(state.OffersInstall);
        Assert.False(state.OffersStart);
        Assert.True(state.IsHealthy);
    }

    [Fact]
    public void OnlyTheHealthyAnswerIsHealthy()
    {
        Assert.True(FxHealth.Decide(true, true, true, true, On).IsHealthy);
        Assert.False(FxHealth.Decide(true, true, true, true, Off).IsHealthy);
        Assert.False(FxHealth.Decide(true, true, true, false, On).IsHealthy);
        Assert.False(FxHealth.Decide(true, false, false, false, Unknown).IsHealthy);
        Assert.False(FxHealth.Decide(false, false, false, false, Unknown).IsHealthy);
    }

    [Fact]
    public void A_switched_off_eq_is_not_something_the_user_has_to_go_and_fix()
    {
        // The distinction this whole type exists to draw, and the one that was being
        // thrown away on screen. PoweredOff is genuinely unhealthy - the EQ is not
        // doing anything - and it is genuinely not a problem, because the user did
        // it on purpose and applying a preset reverses it automatically.
        //
        // Both answers were being drawn in amber, so "switched off on purpose" and
        // "the engine has stopped answering" looked identical.
        Assert.False(FxHealth.Decide(true, true, true, true, Off).IsHealthy);

        Assert.False(
            FxHealth.Decide(true, true, true, true, Off).NeedsAttention,
            "a switched-off EQ is offered as something to go and fix");
    }

    [Fact]
    public void Everything_else_that_is_unhealthy_really_does_need_someone_to_look()
    {
        // The other side of the same distinction: NeedsAttention must not be a
        // synonym for IsHealthy with the wording softened, or the fix above would
        // just have made a real fault quiet.
        Assert.True(FxHealth.Decide(false, false, false, false, Unknown).NeedsAttention);
        Assert.True(FxHealth.Decide(true, false, false, false, Unknown).NeedsAttention);
        Assert.True(FxHealth.Decide(true, true, false, false, Unknown).NeedsAttention);
        Assert.True(FxHealth.Decide(true, true, true, false, On).NeedsAttention);

        Assert.False(FxHealth.Decide(true, true, true, true, On).NeedsAttention);
    }

    [Fact]
    public void The_switched_off_message_says_what_to_do_about_it()
    {
        // "The sliders have nothing to act on" is true and useless: it tells the
        // user their controls are dead without saying that the next thing they do
        // fixes it. The point of the line is that one click reverses it.
        FxHealth state = FxHealth.Decide(true, true, true, true, Off);

        Assert.DoesNotContain("nothing to act on", state.Detail);
        Assert.Contains("Apply", state.Detail);
        Assert.Contains("by itself", state.Detail);
    }

    [Fact]
    public void The_caption_is_plain_english_rather_than_engine_jargon()
    {
        // "Bypassed" is the word the engine uses. It is also the word a new user is
        // most likely to read as "this is not working", which is why it was reported
        // as confusing.
        Assert.Equal("EQ SWITCHED OFF", FxHealth.Decide(true, true, true, true, Off).Caption);
    }

    [Fact]
    public void TheStatusLineIsRebuiltFromThePollAndNotOnlyFromEvents()
    {
        // The other half of the false "EQ switched off", and the reason it could sit
        // on screen all session. The engine's state moves on its own - the user
        // switches the EQ off inside FxSound, or the engine finishes starting and
        // starts reporting - and the four second poll reads that state and applied it
        // to the frequency labels only. The line itself was rebuilt by a handful of
        // events, so nothing the app did was watching it.
        //
        // A line describing another program has to be re-read while it is on screen,
        // or it is a snapshot with nothing saying it is one.
        string source = System.IO.File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Audio.cs")));

        int at = source.IndexOf("private void ApplyEngineState(FxSoundState state)", StringComparison.Ordinal);
        Assert.True(at > 0, "the poll's window half is gone");

        int end = source.IndexOf("private void CheckCurveApplied", at, StringComparison.Ordinal);
        string body = source.Substring(at, end - at);

        Assert.Contains("UpdateFxBanner(state)", body);
    }

    [Fact]
    public void ThePollDoesNotKickOffASecondEngineReadToRefreshTheLine()
    {
        // The poll already has the state. Asking for it again would spawn a second
        // engine read on every tick, which is the expensive thing the poll exists to
        // space out - so the refreshing overload takes the state rather than fetching
        // it, and only the event-driven callers read.
        string source = System.IO.File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Audio.cs")));

        Assert.Contains("private void UpdateFxBanner(FxSoundState? snapshot)", source);

        int at = source.IndexOf("private void UpdateFxBanner(FxSoundState? snapshot)", StringComparison.Ordinal);
        Assert.True(at > 0);

        int end = source.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadState", source.Substring(at, end - at));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }
}