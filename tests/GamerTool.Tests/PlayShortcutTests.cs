using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The shortcut's name and path. The COM write itself is deliberately not
/// exercised here: a test that creates a real .lnk would need a desktop and a
/// COM shell, and the part that can go wrong in a way worth pinning is the file
/// name the user ends up reading.
/// </summary>
public class PlayShortcutTests
{
    [Fact]
    public void AShortcutIsNamedAfterTheSlot()
    {
        Assert.Equal("Tactical FPS", PlayShortcut.FileNameFor("Tactical FPS"));
    }

    [Fact]
    public void CharactersWindowsRefusesBecomeDashesRatherThanDisappearing()
    {
        string name = PlayShortcut.FileNameFor("A/B:C*D?E\"F<G>H|I");

        Assert.Equal("A-B-C-D-E-F-G-H-I", name);
    }

    [Fact]
    public void AnEmptyNameStillProducesAFile()
    {
        Assert.Equal("Play slot", PlayShortcut.FileNameFor("   "));
        Assert.Equal("Play slot", PlayShortcut.FileNameFor("..."));
    }

    [Fact]
    public void ThePathEndsInLink()
    {
        string path = PlayShortcut.PathFor(@"C:\Users\User\Desktop", "Arena");

        Assert.Equal(@"C:\Users\User\Desktop\Arena.lnk", path);
    }

    [Fact]
    public void ALongNameIsCutToSomethingAFileSystemWillAccept()
    {
        string name = PlayShortcut.FileNameFor(new string('a', 200));

        Assert.Equal(80, name.Length);
    }

    [Fact]
    public void AShortcutCarriesTheTrayAndPlayArguments()
    {
        Assert.Equal("--tray --play slot_arena", PlayRequest.ShortcutArguments("slot_arena"));
        Assert.True(PlayRequest.TryParse(
            new[] { "--tray", "--play", "slot_arena" },
            out string id));
        Assert.Equal("slot_arena", id);
    }
}
