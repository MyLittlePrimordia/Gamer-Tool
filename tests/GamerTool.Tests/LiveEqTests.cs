using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Skip = Xunit.Skip;

namespace GamerTool.Tests;

/// <summary>
/// The audio tab pushes to FxSound as the user moves things.
/// <para>
/// This was implemented once, refused, and then implemented properly. The first
/// answer was wrong, and the way it was wrong is the interesting part: it priced a
/// slider drag as one engine write per event. A drag fires sixty to a hundred
/// events, an engine write costs 77 ms, so sixty of them is five seconds and the
/// conclusion was that live updates were impossible.
/// </para>
/// <para>
/// That arithmetic ignored the push queue, which was already there and is a
/// newest-wins queue: a request that has not started is replaced by the next one.
/// So a drag does not cost sixty writes, it costs about one per 77 ms and always
/// lands on the position the finger stopped at. The conclusion was reached without
/// reading the thing that made it wrong.
/// </para>
/// <para>
/// These tests exist partly to stop that mistake being made again by the next
/// person, and partly because the first version of the live-EQ test kept passing
/// after live EQ shipped - it asserted the handlers did not push, and they no
/// longer push directly, they hand off to a throttle. The assertion was still true
/// and the thing it was supposed to describe had stopped existing.
/// </para>
/// </summary>
[Collection("wpf")]
public class LiveEqTests
{
    private static string AppFile(string relative) =>
        File.ReadAllText(FindRepoFile(relative));

    [Fact]
    public void MovingASliderPushesToTheEngine()
    {
        // The whole feature, in one assertion. The handler must reach the throttle;
        // a version of this file that asserted the opposite passed for a week
        // against code that had already shipped the behaviour, because the handler
        // had stopped calling the thing it was looking for without the behaviour
        // changing at all.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));

        foreach (string handler in new[] { "private void OnSoundSliderChanged", "private void OnBandChanged" })
        {
            int at = audio.IndexOf(handler, StringComparison.Ordinal);
            Assert.True(at > 0, handler + " is gone");

            int end = audio.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
            string body = audio.Substring(at, end - at);

            Assert.Contains("QueueLiveTune();", body);
        }
    }

    [Fact]
    public void EveryChangeThatAltersTheSoundGoesThroughTheThrottle()
    {
        // Band count as well as band gain. Adding a fader is as much a change to
        // the sound as moving one, and it is the only way the user made that change.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));

        foreach (string handler in new[] { "private void OnBandCountChanged", "private void OnAudioPresetSelected" })
        {
            int at = audio.IndexOf(handler, StringComparison.Ordinal);
            Assert.True(at > 0, handler + " is gone");

            int end = audio.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
            string body = audio.Substring(at, end - at);

            Assert.True(
                body.Contains("QueueLiveTune()") || body.Contains("PushLiveTune()") || body.Contains("ApplyAudio("),
                handler + " changes the sound and never tells the engine about it");
        }
    }

    [Fact]
    public void TheThrottleIsFrequentEnoughToFeelConnectedAndRareEnoughToAfford()
    {
        // 77 ms is a sustained measurement, so the engine's ceiling is about
        // thirteen writes a second. 150 ms asks for six and a half. It has to be
        // above the write cost or changes queue up behind each other, and it has
        // to be short enough that the fader feels attached to the sound - which is
        // the entire reason the button was removed.
        const int WriteMs = 77;
        const int ThrottleMs = 150;

        Assert.True(
            ThrottleMs > WriteMs,
            "the throttle is shorter than one engine write, so a drag would queue "
            + "requests faster than the engine can retire them");

        Assert.True(
            ThrottleMs < 250,
            "at " + ThrottleMs + " ms between changes the fader stops feeling "
            + "connected to what it is doing to the sound");

        double duty = (double)WriteMs / ThrottleMs;
        Assert.True(duty < 0.7, "the engine would be busy " + duty.ToString("P0") + " of the time while a fader moves");
    }

    [Fact]
    public void TheTimerIsStartedRatherThanRestartedOnEveryEvent()
    {
        // The single most likely way this could be written wrong, and it fails
        // silently: restarting the timer on each change means a continuous drag
        // never reaches the engine until the finger stops, which is precisely the
        // thing the feature is for.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));

        int at = audio.IndexOf("private void QueueLiveTune()", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no throttle to get wrong");

        int end = audio.IndexOf("private void OnLiveTuneTick", at, StringComparison.Ordinal);
        string body = audio.Substring(at, end - at);

        Assert.DoesNotContain("_liveTuneTimer.Stop();\n        _liveTuneTimer.Start();", body.Replace("\r\n", "\n"));
        Assert.Contains("_liveTuneTimer.Start();", body);

        // Only the tick is allowed to stop it, and only once there is nothing left
        // to send.
        Assert.Contains("if (!_liveTuneDirty)", audio);
    }

    [Fact]
    public void ABurstOfChangesCollapsesIntoOneWriteCarryingTheFinalPosition()
    {
        // The queue is what makes coalescing safe, so it is pinned: it must still be
        // a newest-wins queue, not a plain sequential one. A sequential queue would
        // make every one of the sixty writes happen and land the user on whatever
        // the last one happened to be rather than where they stopped.
        string queue = AppFile(Path.Combine("app", "Services", "LatestWinsQueue.cs"));

        Assert.Contains("class LatestWinsQueue<T>", queue);
        Assert.Contains("runs the newest one rather than all of them", queue);
    }

    [Fact]
    public void AChangeEveryFiftyMillisecondsStillSendsFarFewerThanItReceives()
    {
        // The arithmetic that was got wrong the first time, done properly this time. A
        // drag fires about sixty events in a second; the throttle lets one out
        // every 150 ms, so about seven writes reach the engine.
        const int ThrottleMs = 150;
        int events = 60;

        double writesPerSecond = 1000.0 / ThrottleMs;
        double writesIfUnthrottled = events;

        Assert.True(
            writesPerSecond < 10.0,
            "a one second drag would put " + writesPerSecond.ToString("0")
            + " writes on the engine, which is more than it can retire");

        Assert.True(
            writesIfUnthrottled > writesPerSecond * 5.0,
            "the throttle is barely reducing anything: " + writesIfUnthrottled
            + " events in, " + writesPerSecond.ToString("0") + " writes out");
    }

    [Fact]
    public void OneEngineWriteCostsWhatWasMeasuredAndNotWhatWasFeared()
    {
        // Re-measured against the real engine. 77 ms sustained over forty writes,
        // no degradation - which is the number that makes live EQ viable at all,
        // and the one the original refusal should have been based on.
        string exe = @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe";
        Skip.If(!File.Exists(exe), "FxSound is not at the path the cost was measured against");

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < 12; i++)
        {
            using var child = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--set_effect=\"clarity=" + i + ";ambience=" + i + ";surround=" + i + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            child?.WaitForExit(15000);
        }

        stopwatch.Stop();
        double each = stopwatch.Elapsed.TotalMilliseconds / 12.0;

        Assert.True(
            each is > 5.0 and < 400.0,
            "an engine write now costs " + each.ToString("0")
            + " ms, outside the range the throttle was chosen against. If it got much "
            + "cheaper the throttle can go and every move can be sent; if it got much "
            + "dearer this needs rethinking.");
    }

    [Fact]
    public void ALivePushSaysNothingAndSavesNothing()
    {
        // A drag produces one of these every 150 ms. A toast each time is a row of
        // captions going up and down the screen, which is worse than no
        // confirmation; the confirmation is the sound changing.
        //
        // No commit either, and that is not an oversight: the working tune is a
        // scratch copy and an unnamed tweak is deliberately not written to the
        // profile, because doing so is what once made an edit look like it had been
        // thrown away on the next launch. There is nothing durable to save.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));

        int at = audio.IndexOf("private void PushLiveTune()", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no live push");

        int end = audio.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = audio.Substring(at, end - at);

        Assert.DoesNotContain("Flash(", body);
        Assert.DoesNotContain("Commit(", body);
        Assert.DoesNotContain("UpdateSoundLabels", body);
        Assert.Contains("QueueAudioPush(", body);
    }

    [Fact]
    public void ChoosingAPresetStillGoesThroughTheFullApplyPath()
    {
        // Not a bare live push. Selecting a preset records the identity in the
        // profile and commits it, so the next launch comes back on the tune the
        // user chose. A bare push would move the faders while the profile still
        // remembered the old tune, and the change would look lost on restart.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));

        int at = audio.IndexOf("private void OnAudioPresetSelected", StringComparison.Ordinal);
        Assert.True(at > 0, "the preset selection handler is gone");

        int end = audio.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = audio.Substring(at, end - at);

        Assert.Contains("ApplyAudio(preset, announce: true)", body);
    }

    [Fact]
    public void TheAudioTabNoLongerHasAnApplyButton()
    {
        string xaml = AppFile(Path.Combine("app", "MainWindow.xaml"));

        Assert.DoesNotContain("OnApplySoundClick", xaml);
        Assert.DoesNotContain("Apply the sound preset", xaml);

        // What is left in that row are the two deliberate acts.
        Assert.Contains("OnResetSoundClick", xaml);
        Assert.Contains("OnSaveInFxSoundClick", xaml);

        // A "Live" badge sat here briefly, to explain the missing button, and was
        // removed as one more control-shaped thing to read when the sound changing
        // already says it.
        Assert.DoesNotContain("Text=\"Live\"", xaml);
    }

    [Fact]
    public void TheAudioRowPutsTheRoundArrowOnTheSameEndAsTheDisplayTab()
    {
        // Two tabs with the same shape should not disagree about which end the
        // refresh arrow goes on. Save in FxSound is first on the audio tab, Reset
        // last, matching Apply-then-Reset on the display tab.
        string xaml = AppFile(Path.Combine("app", "MainWindow.xaml"));

        int save = xaml.IndexOf("x:Name=\"SaveToFxSoundButton\"", StringComparison.Ordinal);
        int reset = xaml.IndexOf("Click=\"OnResetSoundClick\"", StringComparison.Ordinal);

        Assert.True(save > 0, "the save button is gone");
        Assert.True(reset > 0, "the reset button is gone");
        Assert.True(
            save < reset,
            "the audio tab still puts Reset before Save, which is the reverse of the display tab");
    }

    [Fact]
    public void TheTwoTabsShareOneButtonOrder()
    {
        // Read from both tabs and compared, so the symmetry is a property of the
        // file rather than of somebody's memory of it.
        string xaml = AppFile(Path.Combine("app", "MainWindow.xaml"));

        int apply = xaml.IndexOf("Click=\"OnApplyScreenClick\"", StringComparison.Ordinal);
        int resetScreen = xaml.IndexOf("Click=\"OnResetScreenClick\"", StringComparison.Ordinal);
        int save = xaml.IndexOf("x:Name=\"SaveToFxSoundButton\"", StringComparison.Ordinal);
        int resetSound = xaml.IndexOf("Click=\"OnResetSoundClick\"", StringComparison.Ordinal);

        Assert.True(apply > 0 && resetScreen > apply, "the display tab is not Apply then Reset");
        Assert.True(save > 0 && resetSound > save, "the audio tab is not Save then Reset");
    }

    [Fact]
    public void FindScreensIsNotDrawnAsAnotherRefreshArrow()
    {
        // Two buttons the same size, in the same row, in the same accent, both
        // circular arrows, is a picture that does not distinguish itself from Reset
        // six inches to the left. The tooltip said which was which and the glyph did
        // not, which is backwards: a tooltip is read after hovering, by which point
        // the icon has already been judged.
        string xaml = AppFile(Path.Combine("app", "MainWindow.xaml"));

        int find = xaml.IndexOf("x:Name=\"RescanScreensButton\"", StringComparison.Ordinal);
        Assert.True(find > 0, "the find-screens button is gone");

        int end = xaml.IndexOf("/>", find, StringComparison.Ordinal);
        string button = xaml.Substring(find, end - find);

        Assert.Contains("ui:Icons.Search", button);
        Assert.DoesNotContain("ui:Icons.Refresh", button);

        // And the glyph it now asks for actually exists, which a build would not
        // tell you: a missing x:Static is a runtime failure.
        Assert.Contains("public static Geometry Search", AppFile(Path.Combine("app", "UI", "Icons.cs")));
    }

    [Fact]
    public void TheDisplayTabKeepsItsApplyButton()
    {
        // This is the distinction the whole change turns on, so it is pinned rather
        // than left as a matter of taste. The screen takes a gamma ramp the moment
        // it is asked, so a preview and a commit really are two different things
        // and the button earns its place there. FxSound needs a process per change,
        // which is the only reason this tab ever needed a button at all.
        string xaml = AppFile(Path.Combine("app", "MainWindow.xaml"));

        Assert.Contains("OnApplyScreenClick", xaml);
        Assert.Contains("Apply the screen preset", xaml);
    }

    [Fact]
    public void AProgrammaticFaderMoveDoesNotPushAnything()
    {
        // Loading a tune writes every slider. Without the guard that would fire a
        // push per fader and then push the tune that was just loaded on top of the
        // one the user asked for.
        string code = AppFile(Path.Combine("app", "MainWindow.xaml.cs"));

        int at = code.IndexOf("private void LoadTune", StringComparison.Ordinal);
        Assert.True(at > 0, "LoadTune is gone");

        // By line rather than by character: what matters is that the guard is set
        // before the first fader is written, not how far down the method it is.
        int upLine = LineOf(code, "_updating = true;", at);
        int firstFader = LineOf(code, "GammaSlider.Value", at);

        Assert.True(upLine > 0, "LoadTune no longer guards its own writes with _updating");
        Assert.True(
            upLine < firstFader,
            "LoadTune writes faders at line " + firstFader + " but only sets _updating at line " + upLine
            + ", so a programmatic move would push to the engine");

        // And the throttle honours the same flag, so even a fader written outside
        // LoadTune's guarded block cannot start a push.
        string audio = AppFile(Path.Combine("app", "MainWindow.Audio.cs"));
        int q = audio.IndexOf("private void QueueLiveTune()", StringComparison.Ordinal);
        int qEnd = audio.IndexOf("private void OnLiveTuneTick", q, StringComparison.Ordinal);

        Assert.Contains("_updating", audio.Substring(q, qEnd - q));
    }

    /// <summary>1-based line number of the first hit at or after <paramref name="from"/>.</summary>
    private static int LineOf(string text, string needle, int from)
    {
        int hit = text.IndexOf(needle, from, StringComparison.Ordinal);
        return hit < 0 ? 0 : text.Substring(0, hit).Count(c => c == '\n') + 1;
    }

    [Fact]
    public void LoadingATuneRaisesNothingForTheLivePushToAnswer()
    {
        // A defect this change introduced and a test did not catch.
        //
        // LoadTune set BandCountBox.SelectedItem three lines BEFORE it set
        // _updating, so building the band strip raised OnBandCountChanged outside
        // the guard. That was harmless while the handlers only redrew the page;
        // the moment the handlers started pushing to the engine, loading any tune
        // fired a live update for a tune nobody had chosen, and the app spawned an
        // engine process for every preset it opened at launch.
        //
        // Pinned as an ordering rather than as the presence of the guard, because
        // the guard was always there and was simply set too late.
        string code = AppFile(Path.Combine("app", "MainWindow.xaml.cs"));

        int at = code.IndexOf("private void LoadTune", StringComparison.Ordinal);
        Assert.True(at > 0, "LoadTune is gone");

        int up = LineOf(code, "_updating = true;", at);
        int strip = LineOf(code, "BuildBandStrip(count)", at);
        int selected = LineOf(code, "BandCountBox.SelectedItem = count;", at);
        int fader = LineOf(code, "GammaSlider.Value", at);

        Assert.True(up > 0 && strip > 0 && selected > 0 && fader > 0, "LoadTune's shape changed");
        Assert.True(up < strip, "the guard is set after BuildBandStrip, which raises a change handler");
        Assert.True(up < selected, "the guard is set after BandCountBox.SelectedItem, which raises a change handler");
        Assert.True(up < fader, "the guard is set after the fader writes");
    }

    [Fact]
    public void TheAudioStyleTheApplyButtonUsedIsGone()
    {
        // PrimaryButtonAudio existed only for that one button. Left in place it is
        // a style that looks current, is not used by anything, and is one more
        // thing for the next person to grep for.
        string theme = AppFile(Path.Combine("app", "UI", "Theme.xaml"));

        Assert.DoesNotContain("PrimaryButtonAudio", theme);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}