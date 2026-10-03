using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Which exe files the app library is willing to offer as a game.
/// <para>
/// This filter decides what the user sees in the app picker, and a game that is
/// wrongly filtered out does not fail loudly - it simply is not there. "The list is
/// missing my game" is indistinguishable from the list being broken, and there is no
/// screen anywhere that would say otherwise.
/// </para>
/// <para>
/// So the tests are two-sided on purpose. The junk names have to stay junk, or the
/// picker fills with redistributables and crash handlers, and the game names have to
/// stay in, because the previous filter took them out by matching fragments: anything
/// containing "unity" lost CommunityGame, anything containing "mono" lost Monopoly
/// and Monolith, and anything containing "crash" lost Crash Bandicoot.
/// </para>
/// </summary>
public class JunkNameTests
{
    /// <summary>Names that must never reach the picker.</summary>
    [Theory]
    [InlineData("unins000.exe")]
    [InlineData("uninstall.exe")]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("UnityCrashHandler32.exe")]
    [InlineData("UnityDomainLoad.exe")]
    [InlineData("CrashReportClient.exe")]
    [InlineData("CrashHandler.exe")]
    [InlineData("UnrealCEFSubProcess.exe")]
    [InlineData("QtWebEngineProcess.exe")]
    [InlineData("dotnet.exe")]
    [InlineData("BEService.exe")]
    [InlineData("BEService_x64.exe")]
    [InlineData("EasyAntiCheat_Setup.exe")]
    [InlineData("Updater.exe")]
    [InlineData("GameUpdater.exe")]
    [InlineData("Installer.exe")]
    [InlineData("Setup.exe")]
    [InlineData("Helper.exe")]
    [InlineData("GameHelper.exe")]
    [InlineData("vc_redist.x64.exe")]
    [InlineData("vc_redist.x86.exe")]
    [InlineData("DXSETUP.exe")]
    [InlineData("ue4prereq_x64.exe")]
    [InlineData("Config.exe")]
    public void Plumbing_is_still_filtered_out(string name) =>
        Assert.True(AppLibraryService.IsJunk(name), name + " should not be offered as a game");

    /// <summary>
    /// The names the fragment filter got wrong. Each of these was skipped by a
    /// substring test, and each is a game somebody owns.
    /// </summary>
    [Theory]
    [InlineData("Monopoly.exe")]
    [InlineData("Monolith.exe")]
    [InlineData("CommunityGame.exe")]
    [InlineData("Crash Bandicoot.exe")]
    [InlineData("CrashBandicootNSaneTrilogy.exe")]
    [InlineData("Update2.exe")]
    [InlineData("ServicePack.exe")]
    [InlineData("Reporter.exe")]
    [InlineData("Minecraft.exe")]
    public void Games_the_old_filter_deleted_are_still_here(string name) =>
        Assert.False(AppLibraryService.IsJunk(name), name + " is a game and must not be filtered out");

    [Fact]
    public void Crash_on_its_own_is_not_enough_to_filter_a_game()
    {
        // The whole reason "crash" is not a plain word in the filter. Both of these
        // begin with it and both are games; what separates them from CrashReportClient
        // is only what follows.
        Assert.False(AppLibraryService.IsJunk("CrashBandicoot.exe"));
        Assert.False(AppLibraryService.IsJunk("CrashBandicoot3.exe"));
        Assert.True(AppLibraryService.IsJunk("CrashBandicootHandler.exe"));
    }

    [Fact]
    public void A_game_called_Unity_Is_Still_A_Game()
    {
        // Unity the engine's plumbing is filtered by exact name and by the crash
        // pairing, never by the word "unity" on its own - which also catches the
        // engine's own game.
        Assert.False(AppLibraryService.IsJunk("Unity.exe"));
        Assert.False(AppLibraryService.IsJunk("UnityPlayer.exe"));
        Assert.True(AppLibraryService.IsJunk("UnityCrashHandler64.exe"));
    }

    [Fact]
    public void A_name_given_without_an_extension_is_still_judged()
    {
        // The scan hands over whatever is on disk and a hand-picked path may not have
        // one, so a filter that only understood ".exe" would quietly let a redist
        // through whenever a path arrived without its suffix.
        Assert.True(AppLibraryService.IsJunk("vc_redist.x64"));
        Assert.True(AppLibraryService.IsJunk("CrashReportClient"));
        Assert.False(AppLibraryService.IsJunk("Monopoly"));
    }

    [Fact]
    public void The_filter_is_decided_on_the_stem_not_the_extension()
    {
        // Helper.bin is filtered, because "helper" is a word of the stem. An earlier
        // version of this test assumed the extension was skipped, which was wishful:
        // the split is on GetFileNameWithoutExtension, so a name whose only "helper"
        // is in the suffix behaves differently. Worth pinning, because which of the
        // two happens is decided entirely by the extension the publisher happened to
        // choose, and nothing in the app tells the user that.
        Assert.True(AppLibraryService.IsJunk("Helper.bin"));
        Assert.True(AppLibraryService.IsJunk("CrashReportClient.dat"));
    }

    [Fact]
    public void Nothing_to_filter_is_not_itself_filtered()
    {
        // Throwing or answering "yes" here would drop a real game the moment the scan
        // handed over a blank, and a blank is what an unreadable path looks like.
        Assert.False(AppLibraryService.IsJunk(string.Empty));
        Assert.False(AppLibraryService.IsJunk("   "));
        Assert.False(AppLibraryService.IsJunk(".exe"));
    }

    [Fact]
    public void Camel_case_and_digits_both_break_a_word()
    {
        // Both halves of the split are load-bearing. "UnityCrashHandler64" only comes
        // out as Crash plus Handler if the digit boundary is caught, and the exact
        // name list is not what is being relied on here.
        Assert.False(AppLibraryService.IsJunk("CrashHeliStatue.exe"));
        Assert.True(AppLibraryService.IsJunk("CrashHeliStatueHandler64.exe"));
    }

    [Fact]
    public void A_lowercase_name_is_still_read_as_camel_case()
    {
        // Windows does not care about case and a folder is not obliged to use it, so
        // a filter that only split on capitals would do nothing at all to a name
        // written in lower case.
        Assert.True(AppLibraryService.IsJunk("crashreportclient.exe"));
        Assert.False(AppLibraryService.IsJunk("crashbandicoot.exe"));
    }
}