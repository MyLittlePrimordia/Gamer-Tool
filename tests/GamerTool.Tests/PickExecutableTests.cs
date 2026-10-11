using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Which exe the scan binds a game to when the folder offers several.
/// <para>
/// This decides what actually launches when a slot fires, so it is worth being
/// careful about in a way the junk filter is not. A wrongly-filtered name is a game
/// missing from a list; a wrongly-picked exe is a slot that loads the wrong program.
/// </para>
/// <para>
/// The heuristic under test is "largest exe wins", on the theory that the real game
/// is bigger than its own installer. That holds for a folder of one game and a few
/// redistributables, and it is what the tests here pin down - including the cases
/// where it is wrong, so that whoever changes it later knows what they are changing.
/// </para>
/// <para>
/// Real folders, real files. The sizes matter to the answer, so a stub returning a
/// fixed path would test nothing about which file is chosen.
/// </para>
/// </summary>
public class PickExecutableTests : IDisposable
{
    private readonly string _root;

    public PickExecutableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GamerToolPickExe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Creates a folder holding exes of the given sizes, in bytes.</summary>
    private string Folder(string name, params (string File, int Size)[] files)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        foreach ((string file, int size) in files)
        {
            // Repeated content rather than a length claim, because the picker reads
            // the file's length and a test that only set the length would be testing
            // the fixture rather than the file.
            File.WriteAllBytes(Path.Combine(folder, file), new byte[size]);
        }

        return folder;
    }

    [Fact]
    public void OneGameIsFound()
    {
        string folder = Folder("Solo", ("OnlyGame.exe", 4000));

        Assert.Equal("OnlyGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder)));
    }

    [Fact]
    public void TheGameIsChosenOverARedistributableOfAnySize()
    {
        // The reason the junk filter runs before the size comparison at all. A vc
        // redist is often larger than a small game's launcher, and picking by size
        // alone would bind the slot to the redistributable.
        string folder = Folder(
            "WithRedist",
            ("ActualGame.exe", 4000),
            ("vc_redist.x64.exe", 90_000));

        Assert.Equal("ActualGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder)));
    }

    [Fact]
    public void TheGameIsChosenOverACrashHandlerOfAnySize()
    {
        // Same reason, and this one is not hypothetical: UnityCrashHandler64 is
        // routinely larger than the game it belongs to.
        string folder = Folder(
            "WithHandler",
            ("ActualGame.exe", 4000),
            ("UnityCrashHandler64.exe", 90_000));

        Assert.Equal("ActualGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder)));
    }

    [Fact]
    public void AnEngineCrashesAreNeverChosenHoweverLarge()
    {
        string folder = Folder(
            "ThreeGames",
            ("Small.exe", 1000),
            ("Big.exe", 5000),
            ("UnityCrashHandler64.exe", 500_000),
            ("CrashReportClient.exe", 400_000));

        Assert.Equal("Big.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder)));
    }

    [Fact]
    public void TheFolderNameIsPreferredWhenThatExeExists()
    {
        // Steam names the install folder after the app, so "folder.exe" is the most
        // reliable answer available and it is checked first for that reason.
        string folder = Folder(
            "hl2",
            ("hl2.exe", 1000),
            ("SomeOtherThing.exe", 90_000));

        Assert.Equal("hl2.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder, "hl2")));
    }

    [Fact]
    public void AFolderNamedExeThatIsJunkIsNotPreferred()
    {
        // The preferred name is a hint, not an instruction. Here the folder happens
        // to be called "redist" and asking for it must not return the redist.
        string folder = Folder(
            "game",
            ("redist.exe", 1000),
            ("RealGame.exe", 2000));

        Assert.Equal("RealGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(folder, "redist")));
    }

    [Fact]
    public void AFolderWithNothingButPlumbingOffersNothing()
    {
        // Better than offering a redistributable. A candidate pointing at
        // vc_redist is a slot that would launch the redist on the next game start.
        string folder = Folder(
            "OnlyPlumbing",
            ("vc_redist.x64.exe", 4000),
            ("UnityCrashHandler64.exe", 9000));

        Assert.Null(AppLibraryService.PickExecutable(folder));
    }

    [Fact]
    public void AnEmptyFolderOffersNothing() =>
        Assert.Null(AppLibraryService.PickExecutable(Folder("Empty")));

    [Fact]
    public void AFolderThatDoesNotExistOffersNothingRatherThanThrowing()
    {
        // The scan walks registry install locations and half of them are stale. One
        // missing folder must not take the whole scan down, because the rest of the
        // list is still worth having.
        Assert.Null(AppLibraryService.PickExecutable(Path.Combine(_root, "NotThere")));
    }

    [Fact]
    public void ASubfolderIsSearchedWhenTheRootHasNothingUsable()
    {
        // Common layout: the game binary lives one level down. SkipFolders is
        // respected so a redist directory below the root is not searched.
        string root = Folder("Nested", ("vc_redist.x64.exe", 5000));
        string bin = Path.Combine(root, "binaries");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "RealGame.exe"), new byte[3000]);

        Assert.Equal("RealGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(root)));
    }

    [Fact]
    public void ARedistSubfolderIsNotSearched()
    {
        // SkipFolders exists so the redistributable tree under a game folder does not
        // provide the answer. If it did, "largest exe wins" would land on a
        // redistributable rather than the game.
        string root = Folder("NestedRedist");
        string redist = Path.Combine(root, "redist");
        Directory.CreateDirectory(redist);
        File.WriteAllBytes(Path.Combine(redist, "HugeRedist.exe"), new byte[80_000]);

        Assert.Null(AppLibraryService.PickExecutable(root));
    }

    [Fact]
    public void AGameExeInTheRootWinsEvenWhenASubfolderHasSomethingBigger()
    {
        // The root is searched first and returned from, so a root exe is preferred
        // for being in the root rather than for its size.
        //
        // This is the safe order and it is worth pinning: "largest exe wins" applied
        // across the whole tree would let a subfolder helper beat the actual game,
        // and the root is where a game's own binary normally sits. Recorded because
        // the code returns early rather than comparing across both passes, and a
        // later "simplification" to one shared comparison would change which file a
        // slot binds to for every game laid out this way.
        string root = Folder("RootWins", ("RealGame.exe", 2000));
        string sub = Path.Combine(root, "data");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "BiggerInSub.exe"), new byte[50_000]);

        Assert.Equal("RealGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(root)));
    }

    [Fact]
    public void TheRootWinsEvenWhenItIsMuchSmallerThanWhatIsUnderneath()
    {
        // The same ordering, stated with a gap big enough that a size comparison
        // across passes would visibly disagree. 2 KB against 500 KB.
        string root = Folder("RootWinsBigGap", ("RealGame.exe", 2000));
        string sub = Path.Combine(root, "extras");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "Helper.exe"), new byte[500_000]);

        Assert.Equal("RealGame.exe", Path.GetFileName(AppLibraryService.PickExecutable(root)));
    }
}