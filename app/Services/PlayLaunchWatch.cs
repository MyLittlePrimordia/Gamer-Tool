using System;

namespace GamerTool.Services;

/// <summary>
/// How a Play click started the game, which is the whole of what the launch
/// watchdog needs to know about it.
/// </summary>
public enum PlayLaunchKind
{
    /// <summary>The slot named an executable and it was started directly.</summary>
    Direct,

    /// <summary>
    /// The game was handed to its launcher, which may spend minutes updating,
    /// signing in or waiting for a click before the game itself exists.
    /// </summary>
    Launcher,
}

public enum PlayLaunchWatchAction
{
    /// <summary>Nothing has happened yet. Keep waiting.</summary>
    Wait,

    /// <summary>The process the slot names is running. The launch worked.</summary>
    Started,

    /// <summary>The time is up and nothing started. Put the slot back.</summary>
    GiveUp,
}

/// <summary>
/// The launch watchdog's decision, as a pure function.
/// <para>
/// Play applies a slot before it starts the game, and the revert is normally
/// driven by the watcher seeing that game exit. A launch that never produced a
/// process - a cancelled UAC prompt, a launcher that stopped to update, a crash
/// at the splash screen before the process was ever matched - never exits, so
/// nothing would ever put the screen, the routes and HDR back. That is the gap
/// this covers.
/// </para>
/// <para>
/// Out here rather than inside the window's timer for the same reason
/// <c>AutoApplyDecision</c> is: the interesting cases are the boundaries, and a
/// boundary reachable only by waiting ninety real seconds is a boundary nobody
/// tests.
/// </para>
/// </summary>
public static class PlayLaunchWatch
{
    /// <summary>
    /// How long a directly started executable gets to show up. Generous on
    /// purpose: a big game on a slow drive can spend a while before there is a
    /// process to see, and standing a working slot down because it was slow
    /// would be worse than waiting.
    /// </summary>
    public const int DirectTimeoutSeconds = 90;

    /// <summary>
    /// How long a launcher gets. Ten minutes, because Steam and Epic will happily
    /// spend that on an update or a first-run sign-in before the game exists,
    /// and none of that is a failure.
    /// </summary>
    public const int LauncherTimeoutSeconds = 600;

    public static TimeSpan TimeoutFor(PlayLaunchKind kind)
    {
        return TimeSpan.FromSeconds(kind == PlayLaunchKind.Launcher ? LauncherTimeoutSeconds : DirectTimeoutSeconds);
    }

    /// <summary>
    /// Whether to keep waiting, stop because the game is up, or give up.
    /// <para>
    /// The process check comes first, so a game that appears exactly on the
    /// timeout is a success rather than a last-moment stand-down: the question
    /// the watchdog is really asking is "did this ever start", and a slow start
    /// is still a start.
    /// </para>
    /// </summary>
    public static PlayLaunchWatchAction Decide(TimeSpan elapsed, bool processSeen, PlayLaunchKind kind)
    {
        if (processSeen)
        {
            return PlayLaunchWatchAction.Started;
        }

        return elapsed >= TimeoutFor(kind) ? PlayLaunchWatchAction.GiveUp : PlayLaunchWatchAction.Wait;
    }

    /// <summary>
    /// The one line the user sees when a launch is undone. Written here so the
    /// exception path and the timeout path say the same thing, and so it is
    /// pinned by a test rather than by a string literal inside a timer handler.
    /// </summary>
    public static string GiveUpMessage(string slotName)
    {
        return (string.IsNullOrWhiteSpace(slotName) ? "The game" : slotName) + " didn't start, settings put back";
    }
}
