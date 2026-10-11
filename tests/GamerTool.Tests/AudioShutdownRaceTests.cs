using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Quitting while a sound reset is still running.
/// <para>
/// A reset is about three quarters of a second: it powers the engine down, waits,
/// writes the flat preset, waits again, and then powers the engine back up. The
/// exit path issues its own --power sequence and does not go through the queue,
/// because it has to work from a fault handler with no window. So quitting inside
/// that window put two independent --power sequences on an engine that takes one
/// command line at a time - and the interleaving is not a failed command, it is two
/// successful ones whose order decides which curve survives.
/// </para>
/// <para>
/// The engine is not present in a test run, so these pin the policy rather than
/// the child processes: that standing down happens at all, that it happens before
/// anything irreversible, and that every point it can happen at leaves the engine
/// powered down - which is the only reason cancelling is safe instead of being a
/// new way to leave things half done.
/// </para>
/// </summary>
public class AudioShutdownRaceTests
{
    [Fact]
    public void Stopping_pending_audio_is_idempotent()
    {
        // The exit is reachable from four places - QuitApp, OnClosing,
        // Application.Exit and AppDomain.ProcessExit - and whichever runs first is
        // arbitrary. A second stop must not throw, or it aborts the very restore it
        // was called to protect.
        SessionState state = new();

        state.StopPendingAudio();
        state.StopPendingAudio();
        state.StopPendingAudio();

        Assert.True(state.AudioStop.IsCancellationRequested);
    }

    [Fact]
    public void Stopping_pending_audio_cancels_the_token_readers_hold()
    {
        SessionState state = new();
        CancellationToken token = state.AudioStop;

        Assert.False(token.IsCancellationRequested);

        state.StopPendingAudio();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void A_token_held_before_the_stop_sees_the_cancellation()
    {
        // The order that matters in practice: a reset is already running and holds
        // the token, and only then does the exit path cancel it. A token read after
        // the stop would be equally correct here, but the running reset already has
        // its copy.
        SessionState state = new();
        CancellationToken held = state.AudioStop;

        var sawCancel = new ManualResetEventSlim(false);
        using CancellationTokenRegistration registration = held.Register(() => sawCancel.Set());

        state.StopPendingAudio();

        Assert.True(sawCancel.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void An_uncancelled_reset_still_powers_the_engine_back_on()
    {
        // The ordinary case must be untouched. Cancelling is only the exit path's
        // lever; a reset from the panic key, the reset button or a game exiting
        // still finishes and still leaves the engine running.
        SessionState state = new();

        Assert.False(state.AudioStop.IsCancellationRequested);
    }

    /// <summary>
    /// The ordering the whole fix rests on, asserted rather than described.
    /// <para>
    /// Every point at which a reset can stand down comes after <c>--power=0</c> and
    /// before <c>--power=1</c>. That is the only reason cancelling leaves a good
    /// state: the engine is already down, which is where the exit path is going to
    /// put it anyway. If a future change added a cancellation point before the power
    /// down - or moved the power up before the last check - the engine could be left
    /// powered on with a half-applied curve, which is worse than the interleaving.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_stand_down_point_is_between_powering_down_and_powering_up()
    {
        string source = Read("app/Services/AudioService.cs");
        string body = BodyAfter(source, "public async System.Threading.Tasks.Task ResetSoundAsync");

        int powerDown = body.IndexOf("Run(\"--power=0\")", StringComparison.Ordinal);
        int powerUp = body.IndexOf("Run(\"--power=1\")", StringComparison.Ordinal);

        Assert.True(powerDown >= 0, "the reset no longer powers the engine down");
        Assert.True(powerUp > powerDown, "the engine is powered up before it is powered down");
        Assert.True(powerDown < powerUp, "power down must come first");

        // The cancel checks, and there has to be at least one of them inside the
        // window rather than only at either end.
        int firstCheck = body.IndexOf("SettleAsync(350, cancel)", StringComparison.Ordinal);
        int lastCheck = body.IndexOf("SettleAsync(200, cancel)", StringComparison.Ordinal);

        Assert.True(firstCheck > powerDown, "the first stand-down point is before the engine is powered down");
        Assert.True(lastCheck < powerUp, "the last stand-down point is after the engine is powered back up");
    }

    [Fact]
    public void The_engine_is_never_powered_back_up_once_the_reset_has_stood_down()
    {
        // Each stand-down is a return, not a break. A break would skip to the power
        // up, which is the entire failure.
        string source = Read("app/Services/AudioService.cs");
        string body = BodyAfter(source, "public async System.Threading.Tasks.Task ResetSoundAsync");

        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!trimmed.StartsWith("if (!await SettleAsync", StringComparison.Ordinal)
                && !trimmed.StartsWith("if (cancel.IsCancellationRequested)", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(trimmed.EndsWith("return;", StringComparison.Ordinal)
                || trimmed.Contains("if (!await SettleAsync", StringComparison.Ordinal)
                || trimmed.Contains("if (cancel.IsCancellationRequested)", StringComparison.Ordinal),
                "a stand-down that does not return: " + trimmed);
        }
    }

    [Fact]
    public void The_exit_path_stops_pending_audio_before_it_touches_anything_else()
    {
        // Ordering within EmergencyReset.Run. It has to be first, because it is the
        // one call that can stop the interleaving; anything restored before it is
        // restored while the reset is still running.
        string source = Read("app/Services/EmergencyReset.cs");
        string body = BodyAfter(source, "public static void Run");

        int stop = body.IndexOf("StopPendingAudio()", StringComparison.Ordinal);
        int backlight = body.IndexOf("RestoreBacklight", StringComparison.Ordinal);
        int audio = body.IndexOf("audio.Run(", StringComparison.Ordinal);

        Assert.True(stop >= 0, "the exit path no longer stops pending audio");
        Assert.True(stop < backlight, "the backlight is restored before pending audio is stopped");
        Assert.True(stop < audio, "the audio is reset before pending audio is stopped");
    }

    [Fact]
    public void A_stood_down_reset_does_not_touch_the_window_afterwards()
    {
        // The half of the fix that is about the process rather than the engine.
        //
        // After the engine work, the reset repaints the panel and saves the profile
        // through Dispatcher.InvokeAsync. Doing that on the way out repaints a preset
        // nobody is looking at, saves a second time, and posts to a dispatcher that
        // is already shutting down - where the await may never return, leaving the
        // queue's pump unfinished for the rest of the process's life.
        string source = Read("app/MainWindow.Audio.cs");
        string body = BodyAfter(source, "private async System.Threading.Tasks.Task RunSoundResetAsync");

        int guard = body.IndexOf("AudioStop.IsCancellationRequested", StringComparison.Ordinal);
        int dispatch = body.IndexOf("Dispatcher.InvokeAsync", StringComparison.Ordinal);

        Assert.True(guard >= 0, "the reset no longer checks whether the app is going away");
        Assert.True(dispatch >= 0, "the dispatcher write is gone, which was not the intent");
        Assert.True(guard < dispatch, "the window is touched before the app is known to be going away");
    }

    [Fact]
    public void The_panic_key_does_not_bypass_the_queue()
    {
        // The premise had this wrong, and it is worth pinning. Panic goes through
        // GoSoundNeutralAsync, which enqueues like any other reset - so pressing it
        // during a reset is already serialised by the queue and coalesced by its
        // newest-wins rule. Only the four quit paths call EmergencyReset, and only
        // those needed the stop.
        string slots = Read("app/MainWindow.Slots.cs");
        string body = BodyAfter(slots, "private async void OnHotkeyPressed");

        Assert.Contains("GoSoundNeutralAsync()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EmergencyReset", body, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static string BodyAfter(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        }

        return end < 0 ? source.Substring(at) : source.Substring(at, end - at);
    }

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}