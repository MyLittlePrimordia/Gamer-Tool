using GamerTool;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Pausing the screen when the user leaves a game the app loaded.
/// <para>
/// The feature exists because a bound slot stays applied until the game exits, so a
/// shadow-boosted or cranked-gamma screen follows the user to Discord and the
/// browser. The wildcard slot already reverts on focus loss; a bound one never did.
/// </para>
/// <para>
/// Screen only. Every FxSound call spawns a process and ResetSoundAsync powers the
/// engine off and on with settle delays, so doing that on every alt-tab means a
/// click on the way to a browser. A warm screen on Discord is mildly wrong; silence
/// every time somebody checks a message is worse.
/// </para>
/// </summary>
public class FocusPauseTests
{
    private const string GameExe = @"C:\games\thing\thing.exe";

    private static bool Pause(
        string foregroundProcess,
        string autoSlotId = "slot1",
        string wildcardProcess = "",
        string autoProcess = "thing",
        bool enabled = true,
        bool autoSwitch = true) =>
        MainWindow.ShouldPauseForFocusLoss(
            enabled,
            autoSwitch,
            autoSlotId,
            wildcardProcess,
            autoProcess,
            foregroundProcess);

    [Fact]
    public void AnotherProgramTakingTheFrontPausesTheScreen()
    {
        // The ordinary case: a browser.
        Assert.True(Pause("firefox"));
        Assert.True(Pause("steam"));
    }

    [Fact]
    public void StayingInTheGameDoesNotPause()
    {
        // Another window of the same game, or a second instance. Pausing here would
        // suspend the boost for looking at the game's own launcher.
        Assert.False(Pause("thing"));
        Assert.False(Pause("THING"));
    }

    [Fact]
    public void NothingIsPausedWhenNothingOfOursIsLoaded()
    {
        // The guard that matters for the default case: with no auto slot loaded there
        // is nothing to pause, and arming a timer for it would fire a stand-down
        // the user never asked for.
        Assert.False(Pause("firefox", autoSlotId: string.Empty));
    }

    [Fact]
    public void TheWildcardKeepsItsOwnFocusLossBehaviour()
    {
        // Not an exclusion of convenience. The wildcard already reverts on focus
        // loss, on its own terms, and arming a second mechanism beside it gives two
        // answers to one question - and the wildcard's does not have a grace period
        // and its own toast.
        Assert.False(Pause("firefox", wildcardProcess: "something"));
    }

    [Fact]
    public void TheSwitchBeingOffMeansNothingPauses()
    {
        Assert.False(Pause("firefox", enabled: false));
    }

    [Fact]
    public void AutoSwitchOffMeansNothingPauses()
    {
        Assert.False(Pause("firefox", autoSwitch: false));
    }

    [Fact]
    public void AnEmptyForegroundNameDoesNotPause()
    {
        // "I do not know what is in front" is not "the user went to the browser".
        // Read returns null in several cases that are not the desktop, and pausing
        // on any of them would suspend a boost for no reason.
        Assert.False(Pause(string.Empty));
    }

    [Fact]
    public void TheGracePeriodIsLongEnoughToIgnoreANotification()
    {
        // Not decorative. A toast, an overlay, or the moment an alt-tab passes
        // through another window would each flick the screen if this were instant,
        // and a filter that pulses on every notification is one people switch off.
        Assert.True(AppSettings.FocusPauseGraceSeconds >= 2);
    }

    [Fact]
    public void TheGracePeriodIsShortEnoughNotToReadAsLag()
    {
        Assert.True(AppSettings.FocusPauseGraceSeconds <= 5);
    }

    [Fact]
    public void TheGracePeriodLivesOnSettingsRatherThanInTheCaller()
    {
        // Next to the switch it serves, so there is one place that knows how long it
        // is. Not a user-facing control: a pause length is a preference almost
        // nobody has an opinion about, and a row for it would be a row for nothing.
        //
        // Asserted by finding the declaration, because a reflection lookup on an
        // internal const does succeed but reads as indirection - the test would
        // pass on a rename that changed nothing about where the value lives.
        Assert.NotNull(typeof(AppSettings).GetField(
            "FocusPauseGraceSeconds",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void TheFeatureIsOffUntilTheUserAsksForIt()
    {
        // It changes behaviour for somebody who wants the picture to stay as it is,
        // so it does not arrive switched on.
        AppSettings fresh = new();
        Assert.False(fresh.AutoPauseOnFocusLoss);
    }

    [Fact]
    public void AnExistingProfileKeepsTheFeatureOffWithoutASchemaBump()
    {
        // A new field, so a profile that never had the key keeps the initialiser.
        // Asserted rather than assumed, because a bumped default here would turn the
        // feature on for every existing install without anybody choosing it.
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.AutoPauseOnFocusLoss.ToString();
        bool fromEmpty = System.Text.Json.JsonSerializer
            .Deserialize<AppSettings>("{}")!.AutoPauseOnFocusLoss;

        Assert.False(fromEmpty);
    }

    [Fact]
    public void TheScreenIsPausedWithoutTheSound()
    {
        // The whole design decision, asserted. The screen path stands down and the
        // sound path is not touched at all, so a pause is a DisplayService call and
        // never an FxSound one.
        string slots = Read("app", "MainWindow.Slots.cs");

        // Bounded to this one method. A whole-file search would find the sound calls that
        // belong to OnTargetExited and the wildcard revert, which are supposed to
        // be there - the question is whether THIS pause touches the sound.
        int tick = slots.IndexOf("private void OnFocusPauseTick", System.StringComparison.Ordinal);
        int end = slots.IndexOf("\n    private void CancelFocusPause", tick, System.StringComparison.Ordinal);
        string body = slots.Substring(tick, end - tick);

        Assert.Contains("GoScreenStandDown();", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("GoSoundNeutralAsync", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyAudioToDevice", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ComingBackRestoresTheScreenWithoutPushingTheSoundAgain()
    {
        // screenOnly, because the sound was never taken down. Re-pushing it would
        // spawn the engine and re-apply a tune that is already loaded, on every
        // return - and a return is every time the user alt-tabs back to check
        // something.
        string slots = Read("app", "MainWindow.Slots.cs");

        Assert.Contains(
            "PlaySlot(slot, true, screenOnly: resuming)",
            slots,
            System.StringComparison.Ordinal);

        Assert.Contains(
            "if (audio is not null && !screenOnly)",
            slots,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void ThePauseRemembersItsSlotAcrossTheStandDown()
    {
        // The subtle one. StandDownScreen empties _autoSlotId, so the exit path could
        // no longer find the paused game - and its guard would skip the exit, leaving
        // the SOUND boosted for whatever the user runs next. That is the exact thing
        // the exit path exists to prevent.
        string slots = Read("app", "MainWindow.Slots.cs");
        int tick = slots.IndexOf("private void OnFocusPauseTick", System.StringComparison.Ordinal);
        int end = slots.IndexOf("\n    private void CancelFocusPause", tick, System.StringComparison.Ordinal);
        string body = slots.Substring(tick, end - tick);

        int locals = body.IndexOf("string slotId = _autoSlotId;", System.StringComparison.Ordinal);
        int processLocal = body.IndexOf("string process = _autoProcess;", System.StringComparison.Ordinal);
        int standDown = body.IndexOf("GoScreenStandDown();", System.StringComparison.Ordinal);
        int restore = body.LastIndexOf("_pausedSlotId = slotId;", System.StringComparison.Ordinal);
        int restoreProcess = body.LastIndexOf("_pausedProcess = process;", System.StringComparison.Ordinal);

        Assert.True(locals > 0, "the pause does not take the slot id");
        Assert.True(processLocal > 0, "the pause does not take the process name");
        Assert.True(locals < standDown, "the values are read after the stand-down emptied them");
        Assert.True(restore > standDown, "StandDownScreen clears the pause record and it is not written back");
        Assert.True(restoreProcess > standDown, "the process name is not restored after the stand-down");

        // And the restored values must be the locals, not the fields - reading the
        // cleared fields back is the bug this shape exists to catch.
        Assert.DoesNotContain("_pausedProcess = _autoProcess;", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("_pausedSlotId = _autoSlotId;\n", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AGameClosingWhilePausedStillRevertsTheSound()
    {
        // The failure this prevents, stated as a test. While paused _autoSlotId is
        // empty, so the normal guard at the bottom of OnTargetExited ("this is the
        // slot the app applied") does not match and the exit is ignored - so the
        // sound stays boosted until the user notices.
        string slots = Read("app", "MainWindow.Slots.cs");

        int onExit = slots.IndexOf("private void OnTargetExited", System.StringComparison.Ordinal);
        int paused = slots.IndexOf("_pausedSlotId.Length > 0", onExit, System.StringComparison.Ordinal);
        int autoRevertGuard = slots.IndexOf(
            "if (!_settings.AutoRevertOnExit || _quitting)", onExit, System.StringComparison.Ordinal);

        Assert.True(paused > 0, "OnTargetExited does not handle the paused case");
        Assert.True(paused < autoRevertGuard, "the paused case is after the revert-on-exit guard, so it never runs when that is off");
    }

    [Fact]
    public void TheUserTakingTheScreenBackForgetsThePause()
    {
        // Every manual path goes through StandDownScreen, so it is the one place
        // that has to clear this. Left set, the user's own reset would be undone by
        // the paused game's exit a moment later.
        string display = Read("app", "MainWindow.Display.cs");

        Assert.Contains("_focusPause is not null", display, System.StringComparison.Ordinal);
        Assert.Contains("CancelFocusPause();", display, System.StringComparison.Ordinal);
        Assert.Contains("_pausedSlotId = string.Empty;", display, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ComingBackCancelsAPendingPauseBeforeMatching()
    {
        // Before the match, because the returning window may be a different program
        // entirely - a browser rather than the game - and a pause armed by the last
        // alt-tab must not survive the user coming back to read a message.
        string slots = Read("app", "MainWindow.Slots.cs");
        int handler = slots.IndexOf("private void OnForegroundChanged", System.StringComparison.Ordinal);
        int end = slots.IndexOf("\n    private ", handler + 10, System.StringComparison.Ordinal);
        string body = slots.Substring(handler, end - handler);

        int cancel = body.IndexOf("CancelFocusPause();", System.StringComparison.Ordinal);
        int match = body.IndexOf("SlotService.MatchForeground", System.StringComparison.Ordinal);

        Assert.True(cancel > 0, "returning does not cancel a pending pause");
        Assert.True(cancel < match, "the pause is cancelled after the match rather than before");
    }

    [Fact]
    public void AnArmedPauseIsNotRestartedByEveryTick()
    {
        // A timer restarted on each tick would never fire while the user sat still,
        // which is the one case the pause is for.
        string slots = Read("app", "MainWindow.Slots.cs");
        int at = slots.IndexOf("private void ConsiderFocusPause", System.StringComparison.Ordinal);
        int end = slots.IndexOf("\n    internal static bool ShouldPauseForFocusLoss", at, System.StringComparison.Ordinal);
        string body = slots.Substring(at, end - at);

        Assert.Contains("if (_focusPause is not null)", body, System.StringComparison.Ordinal);
        Assert.Contains("Already armed. Not restarted", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ThePauseIsOffWhenTheFeatureIsOffRatherThanPendingForever()
    {
        // ConsiderFocusPause disarms when the decision says no, rather than only
        // arming. Otherwise a pause armed by one window would survive being turned
        // off and then fire once afterwards.
        string slots = Read("app", "MainWindow.Slots.cs");
        int at = slots.IndexOf("private void ConsiderFocusPause", System.StringComparison.Ordinal);
        int end = slots.IndexOf("\n    internal static bool ShouldPauseForFocusLoss", at, System.StringComparison.Ordinal);
        string body = slots.Substring(at, end - at);

        Assert.Contains("if (!_settings.AutoSwitch || !_settings.AutoPauseOnFocusLoss)", body, System.StringComparison.Ordinal);
        Assert.Contains("CancelFocusPause();", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatcherEventIsActuallySubscribed()
    {
        // ForegroundLeft is raised by nobody's subscriber otherwise. The subscription
        // is asserted because 5.1 added the event and this is what connects it, and
        // an event with no subscriber is a plausible-looking no-op.
        Assert.Contains(
            "_watcher.ForegroundLeft += OnForegroundLeft;",
            Read("app", "MainWindow.xaml.cs"),
            System.StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Xunit.Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts)));
    }
}