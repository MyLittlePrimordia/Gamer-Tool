using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How the watcher learns a process's name.
/// <para>
/// It used to ask a <see cref="System.Diagnostics.Process"/>: open one, read
/// <c>ProcessName</c>, throw it away. That ran on every tick for the foreground
/// window and on every pass over every process on the machine for the scan - so an
/// OS handle per process per pass, for a string that the image path it reads on the
/// next line already spells out.
/// </para>
/// <para>
/// These pin the equivalence that makes the replacement safe. The comparisons
/// done on the result - is this Gamer Tool, is this the shell - have to give the
/// same answers they gave when the value came from the OS.
/// </para>
/// </summary>
public class ProcessNameFromPathTests
{
    [Theory]
    [InlineData(@"C:\Games\Foo\foo.exe", "foo")]
    [InlineData(@"C:\Games\Foo\Foo.exe", "Foo")]
    [InlineData(@"C:\Program Files\Some Game\launcher.exe", "launcher")]
    [InlineData(@"D:\steamapps\common\Thing\bin\thing.exe", "thing")]
    public void APathNamesTheProcessWithoutOpeningIt(string path, string expected)
    {
        Assert.Equal(expected, ProcessWatcherService.NameFromImagePath(path));
    }

    [Fact]
    public void TheExtensionIsOnlyTheLastOne()
    {
        // A dot in the middle of a file name is common and ProcessName treats it
        // the same way: only the final extension is dropped.
        Assert.Equal("Some.Game.v1.2", ProcessWatcherService.NameFromImagePath(@"C:\Games\Some.Game.v1.2.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotAName(string? path)
    {
        Assert.Equal(string.Empty, ProcessWatcherService.NameFromImagePath(path!));
    }

    [Fact]
    public void TheWatcherStillRecognisesItselfByName()
    {
        // The exclusion that keeps ForegroundLeft from firing when the user opens
        // this window to turn the boost off. It compares a name derived from a
        // path now, so the comparison itself is asserted rather than assumed to
        // have survived.
        string name = ProcessWatcherService.NameFromImagePath(@"C:\Tools\GamerTool.exe");

        Assert.Equal("GamerTool", name, ignoreCase: true);
    }
}