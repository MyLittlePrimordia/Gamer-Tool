using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Adding the game that is playing right now.
/// <para>
/// The problem this solves has a shape that makes it look impossible: the button
/// that adds the game you are playing is a button you have to press, and pressing
/// it takes the game out of the foreground. So the answer has to have been noticed
/// before the button was pressed.
/// </para>
/// <para>
/// What matters therefore is that what is remembered can never be this app or the
/// shell - <see cref="ProcessWatcherService.Read"/> refuses both, which is why
/// <c>LastForeign</c> is fed from its answer rather than from a second read.
/// </para>
/// </summary>
public class AddCurrentGameTests
{
    private static WatchedWindow Window(string title, string process) => new()
    {
        Title = title,
        ProcessName = process,
        ExePath = @"C:\Games\" + process + @"\" + process + ".exe"
    };

    [Fact]
    public void AWatcherThatHasSeenNothingHasNothingToAdd()
    {
        ProcessWatcherService watcher = new();

        Assert.Null(watcher.LastForeign);
        Assert.False(watcher.HasRecentForeign);
    }

    [Fact]
    public void AnOldMemoryIsNotOffered()
    {
        // The button sits on screen after the user has walked away from the
        // machine. An answer from an hour ago is a wrong answer with a confident
        // look about it, so it is treated as no answer at all.
        Assert.Equal(TimeSpan.FromMinutes(15), ProcessWatcherService.RecentForeignWindow);
        Assert.True(ProcessWatcherService.RecentForeignWindow < TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData("Witcher 3", "witcher3", "Witcher 3")]
    [InlineData("Cyberpunk 2077", "Cyberpunk2077", "Cyberpunk 2077")]
    public void TheWindowTitleNamesTheGame(string title, string process, string expected)
    {
        // The only one of the three that is ever the game's own name. A process
        // called launcher.exe has a window that says what it is launching.
        Assert.Equal(expected, SlotService.NameForCurrentGame(Window(title, process)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("launcher")]
    public void AWindowThatSaysNothingUsefulFallsBackToTheProcessName(string title)
    {
        // Games that draw their own chrome leave the window title as the exe name,
        // and a slot called "witcher3" is better than one called "Witcher 3" that
        // is the same thing twice.
        Assert.Equal("launcher", SlotService.NameForCurrentGame(Window(title, "launcher")));
    }

    [Fact]
    public void AnAbsurdlyLongTitleIsNotUsedAsASlotName()
    {
        // A title can be a path, a URL, a line of a log. A slot row is one line
        // high and the name is what the user reads on the board.
        string enormous = new('x', 400);

        Assert.Equal("game", SlotService.NameForCurrentGame(Window(enormous, "game")));
    }

    [Fact]
    public void ResetForgetsTheRememberedApplication()
    {
        // ApplyWatchState calls Reset on both arms of the switch, so a remembered
        // game that survived it would be offered to a user who had just turned
        // auto-load off - describing what was in the foreground before they did.
        ProcessWatcherService watcher = new();
        watcher.Reset();

        Assert.Null(watcher.LastForeign);
        Assert.Equal(DateTime.MinValue, watcher.LastForeignAt);
    }
}

/// <summary>
/// The third-party launcher scanners.
/// <para>
/// Each one reads what that launcher writes on disk or to the registry. None of
/// it is a documented API, so the property that matters is not "works" but "fails
/// quietly": a user without Epic installed must get an empty list and no log full
/// of noise, and a user whose Epic moved must get an empty list and no crash.
/// </para>
/// <para>
/// Nothing here reads a network, calls a launcher API, or touches a running game.
/// </summary>
public class LauncherScanTests
{
    private readonly AppLibraryService _library = new();

    [Fact]
    public void ScanningAMachineWithNoLaunchersReturnsNothingAndDoesNotThrow()
    {
        // The state almost every test machine is in, and the state a user who only
        // uses Steam is in. It must look exactly like "no games found" rather than
        // like a failure.
        IReadOnlyList<Models.AppCandidate> epic = _library.ScanEpic();
        IReadOnlyList<Models.AppCandidate> gog = _library.ScanGog();

        Assert.NotNull(epic);
        Assert.NotNull(gog);
    }

    [Fact]
    public void EveryLauncherScanIsReachedThroughOneFailSoftEntryPoint()
    {
        // Scan() is what the Scan button calls. If a launcher threw out of here the
        // whole scan dies, taking Steam and the installed programs with it - which
        // is the failure the per-source wrapper exists to prevent.
        IReadOnlyList<Models.AppCandidate> all = _library.ScanLaunchers();

        Assert.NotNull(all);
    }

    [Fact]
    public void TheWholeScanStillWorksWithNoLaunchersAtAll()
    {
        IReadOnlyList<Models.AppCandidate> all = _library.Scan();

        Assert.NotNull(all);
    }

    [Fact]
    public void LauncherEntriesCarryTheirOwnSource()
    {
        // The badge on the dropdown has to say where an entry came from, and a
        // launcher entry that claimed to be a Steam one would be a lie about a
        // path that is perfectly real.
        IReadOnlyList<Models.AppCandidate> all = _library.ScanLaunchers();

        foreach (Models.AppCandidate candidate in all)
        {
            Assert.Contains(candidate.Source, new[] { "EPIC", "GOG" });
            Assert.False(string.IsNullOrWhiteSpace(candidate.Name));
            Assert.True(File.Exists(candidate.ExePath), "an entry whose executable is not there: " + candidate.ExePath);
        }
    }

    [Fact]
    public void LauncherEntriesHaveNoDuplicates()
    {
        // Scanned in from three sources now, so two of them can name the same game.
        List<string> paths = _library.ScanLaunchers().Select(c => c.ExePath).ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}