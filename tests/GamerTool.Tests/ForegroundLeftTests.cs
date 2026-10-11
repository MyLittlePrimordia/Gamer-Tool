using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The watcher reporting that the foreground moved to the desktop.
/// <para>
/// ForegroundChanged cannot report this transition at all, which is the reason
/// this event exists. Read() returns null for the shell processes - they are
/// filtered on purpose, because they are not "the app the user is in" - and OnTick
/// only raises for a non-null window. So alt-tabbing from a game to the desktop
/// raised nothing, and coming back raised nothing either, because _lastKey still
/// held the game's.
/// </para>
/// <para>
/// Both halves of that sentence are this feature's whole value, and the second is
/// the one that is easy to get wrong: an event for leaving with no way to see the
/// return is half a feature, and a caller that only listens for leaving cannot
/// tell the user coming back from the user never going back.
/// </para>
/// </summary>
public class ForegroundLeftTests
{
    [Fact]
    public void LeavingIsRaisedWhenTheShellTakesTheForeground()
    {
        Assert.True(ProcessWatcherService.ShouldRaiseLeft("explorer", lastKey: @"C:\game.exe|game"));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("SearchHost")]
    [InlineData("ShellExperienceHost")]
    [InlineData("StartMenuExperienceHost")]
    public void EveryShellProcessCountsAsHavingLeft(string shell)
    {
        Assert.True(ProcessWatcherService.ShouldRaiseLeft(shell, lastKey: @"C:\game.exe|game"));
    }

    [Fact]
    public void NothingIsRaisedWhenTheForegroundIsSimplyUnknown()
    {
        // GetForegroundWindow returns zero for a moment during every alt-tab, and
        // "I do not know what is in front" is not the same claim as "the user went
        // to the desktop". Raising on it would fire the event on every switch
        // between two windows.
        Assert.False(ProcessWatcherService.ShouldRaiseLeft(string.Empty, lastKey: @"C:\game.exe|game"));
    }

    [Fact]
    public void NothingIsRaisedWhenNothingWasEverInTheForeground()
    {
        // The clause that stops this firing forever. A locked or minimized desktop
        // has no foreground window at all, and with an empty _lastKey there is
        // nothing that could have been left - so a monitor with nothing open would
        // raise this on every tick, and a caller suspending on it would suspend
        // from a state it was never in.
        Assert.False(ProcessWatcherService.ShouldRaiseLeft("explorer", lastKey: string.Empty));
        Assert.False(ProcessWatcherService.ShouldRaiseLeft(string.Empty, lastKey: string.Empty));
    }

    [Fact]
    public void LeavingIsIdempotentWhileTheShellStaysInFront()
    {
        // The state machine, as a state machine. Once raised, _lastKey is empty, so
        // the next tick cannot raise it again - which is why the guard checks
        // lastKey rather than remembering a separate "already left" flag.
        string lastKey = @"C:\game.exe|game";

        if (ProcessWatcherService.ShouldRaiseLeft("explorer", lastKey))
        {
            lastKey = string.Empty;
        }

        Assert.False(ProcessWatcherService.ShouldRaiseLeft("explorer", lastKey));
        Assert.False(ProcessWatcherService.ShouldRaiseLeft("SearchHost", lastKey));
    }

    [Fact]
    public void ComingBackIsVisibleBecauseLastKeyWasEmptied()
    {
        // The half that matters most, and the reason the event clears _lastKey
        // rather than only raising. ForegroundChanged fires on a key that differs
        // from _lastKey, so leaving it as the game's means returning to the game
        // compares equal and nothing re-applies.
        string lastKey = @"C:\game.exe|game";

        // Leave.
        string returning = @"C:\game.exe|game";
        if (ProcessWatcherService.ShouldRaiseLeft("explorer", lastKey))
        {
            lastKey = string.Empty;
        }

        // Return. The key is the same as it was before leaving, and it differs now.
        Assert.NotEqual(returning, lastKey);
    }

    [Fact]
    public void TheWatcherRefusesToReportGamerToolAsTheForeground()
    {
        // Read must still refuse it - the window is not a bindable game target -
        // while ForegroundLeft must not fire for it, because opening the app to
        // look at a preset is not leaving the game. It is coming to the one place
        // the boost can be turned off.
        string source = Read("ProcessWatcherService.cs");

        Assert.Contains("name.Equals(\"GamerTool\"", source, System.StringComparison.Ordinal);

        // And GamerTool is deliberately not in the shell list, which is the two
        // requirements being different rather than one overriding the other.
        int shellList = source.IndexOf("ShellProcessNames =", System.StringComparison.Ordinal);
        int end = source.IndexOf("};", shellList, System.StringComparison.Ordinal);
        string list = source.Substring(shellList, end - shellList);

        Assert.DoesNotContain("GamerTool", list, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheShellListIsSharedWithTheReadFilterRatherThanDuplicated()
    {
        // One list. Two copies of these names would drift, and the second one to
        // be edited would be the one Read no longer honours - so a new shell
        // process would be reported as leaving but never filtered out, or the
        // reverse, and neither would show up as a failure.
        string source = Read("ProcessWatcherService.cs");

        Assert.Equal(
            1,
            Count(source, "\"ShellExperienceHost\""));

        Assert.Contains(
            "foreach (string shell in ShellProcessNames)",
            source,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableForegroundIsTreatedAsUnknownRatherThanAsLeaving()
    {
        // A process that exits between the window being read and the name being
        // asked for is ordinary, not exceptional. Reporting it as the user leaving
        // would suspend a boost because a background process happened to close.
        string source = Read("ProcessWatcherService.cs");
        int at = source.IndexOf("private static string ReadShellForeground", System.StringComparison.Ordinal);
        Assert.True(at > 0, "the shell reader is gone");

        int end = source.IndexOf("\n    public ", at, System.StringComparison.Ordinal);
        string body = source.Substring(at, end - at);

        // Every early return is the empty string, never a shell name.
        // Only one exit returns a non-empty name, and it is the one inside the shell loop.
// Every other exit - no window, no pid, not a shell process, unreadable - returns
// empty. Asserted as a count because "there must be exactly one way to say yes" is
// the property; listing them by text would break on any reordering.
Assert.Equal(
            1,
            Count(body, "return name;"));

        Assert.Contains("return string.Empty;", body, System.StringComparison.Ordinal);
        Assert.Contains("TraceLog.Write(\"FOREGROUND\", ex);", body, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheEventIsRaisedFromInsideTheGuardedBlock()
    {
        // A handler that throws from a DispatcherTimer tick escapes as an unhandled
        // dispatcher exception, which puts a dialog on screen in the middle of a
        // game launch and keeps the timer running. The existing comment records
        // that this was fixed once for ForegroundChanged; the new raise has to be
        // inside the same try rather than beside it.
        string source = Read("ProcessWatcherService.cs");
        int onTick = source.IndexOf("private void OnTick", System.StringComparison.Ordinal);
        int end = source.IndexOf("\n    private static string ReadShellForeground", onTick, System.StringComparison.Ordinal);
        string body = source.Substring(onTick, end - onTick);

        int guard = body.IndexOf("try", StringComparison.Ordinal);
        int raise = body.IndexOf("ForegroundLeft?.Invoke(shell);", StringComparison.Ordinal);
        int catchAt = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);

        Assert.True(guard >= 0, "OnTick lost its guard");
        Assert.True(raise > guard, "ForegroundLeft is raised outside the guarded block");
        Assert.True(catchAt > raise, "the catch no longer follows the raise");
    }

    private static string Read(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Xunit.Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "app", "Services", file));
    }

    private static int Count(string source, string needle)
    {
        int n = 0;
        int at = 0;

        while (true)
        {
            int found = source.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return n;
            }

            n++;
            at = found + needle.Length;
        }
    }
}