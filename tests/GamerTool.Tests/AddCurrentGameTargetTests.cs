using System;
using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Binding a slot to whatever the user was just playing.
/// <para>
/// The shape of this problem is what makes it worth testing rather than reading:
/// the control that adds the game you are playing takes the game out of the
/// foreground the moment it is clicked, so the answer has to have been noticed
/// beforehand. Everything below is about whether the remembered answer is still
/// worth offering, and the refusals matter more than the offers - a wrong offer
/// binds a slot to something the user is not playing, and it does it with the
/// app's own confident wording.
/// </para>
/// </summary>
public class AddCurrentGameTests2
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static WatchedWindow Window(string title, string process, string? path = null) => new()
    {
        Title = title,
        ProcessName = process,
        ExePath = path ?? @"C:\Games\" + process + @"\" + process + ".exe"
    };

    [Fact]
    public void AWindowSeenAMomentAgoIsOffered()
    {
        CurrentGameTarget? target = AddCurrentGame.From(
            Window("Cyberpunk 2077", "Cyberpunk2077"),
            Now.AddSeconds(-5),
            Now);

        Assert.NotNull(target);
        Assert.Equal("Cyberpunk 2077", target!.Value.Name);
        Assert.Equal(@"C:\Games\Cyberpunk2077\Cyberpunk2077.exe", target.Value.ExePath);
        Assert.Equal("Cyberpunk2077", target.Value.ProcessName);
    }

    [Fact]
    public void NothingRememberedMeansNothingToOffer()
    {
        // The ordinary state on a machine where the user has only opened this
        // window: the watcher has never seen a game, so there is no answer and the
        // control has nothing to do.
        Assert.Null(AddCurrentGame.From(null, DateTime.MinValue, Now));
    }

    [Fact]
    public void AnOldMemoryIsNotOffered()
    {
        // The control is still on screen long after the user has walked away. An
        // answer from an hour ago is a wrong answer with a confident look about it,
        // and binding a slot to it would be worse than offering nothing.
        Assert.Null(AddCurrentGame.From(
            Window("Cyberpunk 2077", "Cyberpunk2077"),
            Now.AddHours(-1),
            Now));
    }

    [Fact]
    public void TheOfferIsStillGoodJustInsideTheFreshnessWindow()
    {
        // The boundary, approached from the inside. A rule tested only at its
        // extremes passes for the wrong reasons.
        TimeSpan window = ProcessWatcherService.RecentForeignWindow;

        Assert.NotNull(AddCurrentGame.From(
            Window("Game", "game"),
            Now - window + TimeSpan.FromSeconds(1),
            Now));
    }

    [Fact]
    public void TheOfferIsGoneAtTheFreshestPointItCouldExpire()
    {
        // And from the outside. The two together pin the comparison rather than
        // leaving it to whichever way round somebody wrote it.
        Assert.Null(AddCurrentGame.From(
            Window("Game", "game"),
            Now - ProcessWatcherService.RecentForeignWindow,
            Now));
    }

    [Fact]
    public void AWindowWithNoExecutablePathIsNotOffered()
    {
        // There would be nothing to bind. A slot bound to an empty path matches
        // nothing and reads as broken rather than as unset.
        Assert.Null(AddCurrentGame.From(
            Window("Game", "game", path: string.Empty),
            Now,
            Now));

        Assert.Null(AddCurrentGame.From(
            Window("Game", "game", path: "   "),
            Now,
            Now));
    }

    [Fact]
    public void AMemoryWithNoProcessNameIsNotOffered()
    {
        // The name would be empty, and an empty slot name is the one thing the
        // board cannot render meaningfully.
        WatchedWindow nameless = new()
        {
            Title = string.Empty,
            ProcessName = string.Empty,
            ExePath = @"C:\Games\game\game.exe"
        };

        Assert.Null(AddCurrentGame.From(nameless, Now.AddSeconds(-1), Now));
    }

    [Fact]
    public void TheProcessNameIsTheFallbackWhenTheTitleSaysNothingUseful()
    {
        // A game that draws its own chrome leaves the title as the exe name, so the
        // two are the same string and the process name is the honest answer.
        CurrentGameTarget? target = AddCurrentGame.From(
            Window("game", "game"),
            Now.AddSeconds(-1),
            Now);

        Assert.NotNull(target);
        Assert.Equal("game", target!.Value.Name);
    }

    [Fact]
    public void AFreshWindowIsOnlyOfferedIfTheClockSaysSo()
    {
        // The staleness rule is evaluated against the supplied time and not against
        // the machine's, which is what makes every test above deterministic. Asserted
        // so a later change to read DateTime.Now internally cannot quietly undo it.
        WatchedWindow remembered = Window("Game", "game");

        Assert.NotNull(AddCurrentGame.From(remembered, Now.AddMinutes(-1), Now));
        Assert.Null(AddCurrentGame.From(remembered, Now.AddMinutes(-1), Now.AddMinutes(30)));
    }

    [Fact]
    public void ASlotAlreadyPointingAtTheGameIsFound()
    {
        // Used to decide between adding a row and moving an existing one. Two rows
        // for one game is how a user ends up with a slot they have forgotten about
        // and no way to tell which of them fires.
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "1", Name = "Other", AppExePath = @"C:\Games\other\other.exe" },
            new HotkeySlot { Id = "2", Name = "Cyberpunk", AppExePath = @"C:\Games\cp\cp.exe" }
        };

        HotkeySlot? found = AddCurrentGame.SlotFor(slots, @"C:\Games\cp\cp.exe");

        Assert.NotNull(found);
        Assert.Equal("2", found!.Id);
    }

    [Fact]
    public void TheLookupForAnExistingSlotIgnoresCase()
    {
        // One path comes from the process watcher and the other from a profile
        // file, so the two are not guaranteed to agree on casing.
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "1", AppExePath = @"C:\Games\CP\CP.EXE" }
        };

        Assert.NotNull(AddCurrentGame.SlotFor(slots, @"c:\games\cp\cp.exe"));
    }

    [Fact]
    public void AGameNoSlotPointsAtHasNoExistingSlot()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "1", AppExePath = @"C:\Games\other\other.exe" },
            new HotkeySlot { Id = "2", AppExePath = null },
            new HotkeySlot { Id = "3", Name = "Wildcard", IsAnyGameTarget = true }
        };

        Assert.Null(AddCurrentGame.SlotFor(slots, @"C:\Games\cp\cp.exe"));
    }

    [Fact]
    public void AnEmptyPathMatchesNothing()
    {
        // Guarded because the caller passes whatever the watcher produced, and an
        // empty path comparing equal to a slot whose path is also empty would find
        // the wrong slot and retarget it.
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "1", AppExePath = null },
            new HotkeySlot { Id = "2", AppExePath = string.Empty }
        };

        Assert.Null(AddCurrentGame.SlotFor(slots, string.Empty));
        Assert.Null(AddCurrentGame.SlotFor(slots, "  "));
    }

    [Fact]
    public void AnEmptySlotListIsNotAnError()
    {
        Assert.Null(AddCurrentGame.SlotFor(new List<HotkeySlot>(), @"C:\Games\cp\cp.exe"));
    }
}

/// <summary>
/// The wiring, which is the half that was missing for as long as this feature has
/// existed.
/// </summary>
/// <remarks>
/// The decision above has been finished and tested for a long time, and none of it
/// was ever reachable: nothing in the app read <c>LastForeign</c>, and there was no
/// control to press. So these assert the connection rather than the logic - that
/// the control exists, that its handler asks the remembered window, and that it
/// looks for an existing row before adding a second one. A handler cannot be driven
/// from a test without a window, a dispatcher and a real foreground process, which
/// is exactly why the decision was extracted in the first place and why the seam
/// between the two is worth pinning here.
/// </remarks>
public class AddCurrentGameWiringTests
{
    private static string Read(string file)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir!.FullName, "app", file);

        return SlotsSource.IsSlotsFile(path)
            ? SlotsSource.ReadFolder(System.IO.Path.GetDirectoryName(path)!)
            : System.IO.File.ReadAllText(path);
    }

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[at..] : source[at..end];
    }

    [Fact]
    public void ThereIsAControlToPressAndItIsOnTheHotkeysTab()
    {
        string xaml = Read("MainWindow.xaml");

        Assert.Contains("x:Name=\"AddPlayingButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddPlayingClick\"", xaml, StringComparison.Ordinal);

        // Above the slot list rather than inside it: it adds a row to that list, so
        // it has to be reachable without scrolling to whatever happens to be last.
        int button = xaml.IndexOf("x:Name=\"AddPlayingButton\"", StringComparison.Ordinal);
        int scroll = xaml.IndexOf("x:Name=\"SlotScroll\"", StringComparison.Ordinal);
        Assert.True(button > 0 && scroll > 0 && button < scroll, "the control moved below the slot list");
    }

    [Fact]
    public void TheHandlerAsksTheRememberedWindowRatherThanTheForeground()
    {
        // The point of the feature. Reading the foreground at the moment of the
        // click would return this app, because clicking the control is what brought
        // the user here.
        string body = Body(Read("MainWindow.Slots.cs"), "private void OnAddPlayingClick");

        Assert.Contains("_watcher.LastForeign", body, StringComparison.Ordinal);
        Assert.Contains("_watcher.LastForeignAt", body, StringComparison.Ordinal);
        Assert.Contains("AddCurrentGame.From(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHandlerMovesAnExistingRowRatherThanAddingASecond()
    {
        // Two rows for one game is how a user ends up unable to tell which of them
        // fires.
        string body = Body(Read("MainWindow.Slots.cs"), "private void OnAddPlayingClick");

        Assert.Contains("AddCurrentGame.SlotFor(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlFollowsTheMemoryRatherThanTheOtherWayRound()
    {
        // A control that does nothing when pressed is worse than one that is visibly
        // unavailable.
        string body = Body(Read("MainWindow.Slots.cs"), "private void RefreshAddPlayingState");

        Assert.Contains("AddPlayingButton.IsEnabled", body, StringComparison.Ordinal);
        Assert.Contains("AddCurrentGame.From(", body, StringComparison.Ordinal);

        // And it is called when the tab it lives on is opened, or the state would
        // only ever be whatever it was at launch.
        string page = Read("MainWindow.xaml.cs");
        Assert.Contains("RefreshAddPlayingState();", Body(page, "private void SelectPage"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatcherRunsWhetherOrNotAnythingIsArmedToAutoLoad()
    {
        // The flaw this exists for, and it was live for exactly as long as it took
        // somebody to look at the control.
        //
        // The watcher used to be started only when some slot was armed to auto-load.
        // But the watcher does two jobs - it applies slots on a foreground change,
        // and it remembers what was in the foreground - and only the first of those
        // is behind the switch. The second is what the offer reads. So on a fresh
        // install, where nothing is bound to a game yet, nothing was watching, there
        // was never anything to offer, and the control could only light up after the
        // user had already done by hand the thing it exists to save them doing.
        //
        // Nothing caught it. The decision underneath is tested thoroughly and the
        // wiring above it is tested; what was untested was the condition the state
        // was computed from, which is the part that made all of it unreachable.
        string body = Body(Read("MainWindow.Setup.cs"), "private void ApplyWatchState");

        int branch = body.IndexOf("if (autoApply)", StringComparison.Ordinal);
        int otherwise = branch < 0 ? -1 : body.IndexOf("else", branch, StringComparison.Ordinal);
        int start = body.IndexOf("_watcher.Start(1500);", StringComparison.Ordinal);

        Assert.True(branch >= 0, "the watch state no longer branches on whether anything auto-loads");
        Assert.True(otherwise > branch, "the off branch was removed");
        Assert.True(start > otherwise, "the watcher is started inside the arming branch, so a fresh install watches nothing");

        // And turning auto-load off no longer stops it.
        Assert.DoesNotContain("_watcher.Stop();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningAutoLoadOffStillDropsTheStaleAnswerWithoutStoppingTheMemory()
    {
        // The other direction. Reset clears the remembered window, which is its
        // documented job and the reason it is still called on the off branch - a
        // pre-toggle answer is the one moment it is least wanted. What it must not
        // do is run on the arming arm as well, where it used to: that wiped the
        // memory on every edit to every slot, so the offer survived only until the
        // user touched something.
        string body = Body(Read("MainWindow.Setup.cs"), "private void ApplyWatchState");

        int arming = body.IndexOf("if (autoApply)", StringComparison.Ordinal);
        int otherwise = body.IndexOf("else", arming, StringComparison.Ordinal);

        Assert.True(
            body.IndexOf("_watcher.Reset();", otherwise, StringComparison.Ordinal) > otherwise,
            "turning auto-load off no longer clears the remembered window");
    }
}
