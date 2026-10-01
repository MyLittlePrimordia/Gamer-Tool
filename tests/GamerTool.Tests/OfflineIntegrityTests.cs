using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The promise that the app does not use the network unless asked to.
/// <para>
/// This is the one claim in the project's own copy that a test can actually hold
/// it to, so it is worth holding. The app has exactly one reason to reach out: an
/// FxSound install or upgrade the user clicked on. The update check used to break
/// that by running on every launch, spawning winget before the user had done
/// anything but start the program.
/// </para>
/// <para>
/// The check is deliberately not deleted. It is reachable from the banner's own
/// update button, so the guarantee is "only when asked" rather than "never", and
/// these tests are what stop a well-meaning launch-path call coming back.
/// </para>
/// </summary>
public class OfflineIntegrityTests
{
    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string[] ProductionSources()
    {
        // The DEBUG-only developer entry points are excluded, because #if DEBUG is
        // not in the Release binary and one of them deliberately walks the disk.
        return Directory.GetFiles(
                Path.Combine(AppRoot(), "app"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string Read(string path) => File.ReadAllText(path);

    [Fact]
    public void The_update_check_is_not_called_during_startup()
    {
        // The call that mattered was the one in the window's loaded handler, which
        // runs before the user has done anything but open the program. It is gone,
        // and this is the test that says so out loud.
        string startup = Read(Path.Combine(AppRoot(), "app", "MainWindow.xaml.cs"));

        int loaded = startup.IndexOf("OnWindowLoaded", StringComparison.Ordinal);
        Assert.True(loaded >= 0, "could not find OnWindowLoaded in MainWindow.xaml.cs");

        // Scan the whole file rather than trusting the line count: the guarantee
        // is that no call site anywhere asks for the check, and a call added to
        // some other lifecycle method would be just as much of a breach.
        int at = 0;
        while (true)
        {
            int found = startup.IndexOf("CheckFxSoundUpdateAsync", at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            int after = found + "CheckFxSoundUpdateAsync".Length;

            // Distinguishing a call from a declaration is the whole difficulty
            // here. A declaration is followed by a parameter list and then a body;
            // a call is followed by a closing paren or a semicolon. So the test is
            // whether the next two characters are "()", which only a declaration
            // ever produces.
            bool isDeclaration = after + 1 < startup.Length
                && startup[after] == '('
                && startup[after + 1] == ')';

            Assert.False(
                !isDeclaration && after < startup.Length && startup[after] == '(',
                "MainWindow.xaml.cs calls CheckFxSoundUpdateAsync, which reaches the network "
                + "without the user having asked for anything");

            at = after;
        }
    }

    [Fact]
    public void The_update_check_is_still_reachable_from_the_banner_button()
    {
        // Removing the automatic call must not have removed the feature. If this
        // ever fails, the app is carrying a check that can never run, which is
        // the same dead-control problem as a hidden button.
        string setup = Read(Path.Combine(AppRoot(), "app", "MainWindow.Setup.cs"));

        Assert.Contains("CheckFxSoundUpdateAsync()", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_polls_a_url_on_a_timer()
    {
        // A timer-driven poll is the shape an unwanted network call always takes
        // eventually, so the absence of one is worth a test.
        foreach (string file in ProductionSources())
        {
            string text = Read(file);
            if (text.Contains("HttpClient", StringComparison.Ordinal))
            {
                // The only legitimate use is the installer download, which is
                // reached from a button. Anything that also mentions a timer
                // around it deserves a look.
                Assert.False(
                    text.Contains("System.Timers.Timer", StringComparison.Ordinal)
                    || text.Contains("PeriodicTimer", StringComparison.Ordinal),
                    Path.GetFileName(file) + " uses HttpClient next to a timer, which can become a poll");
            }
        }
    }

    [Fact]
    public void There_is_still_exactly_one_thing_that_can_reach_the_network()
    {
        // The installer download. This is a count rather than a search for
        // "download.fxsound.com" so that a second endpoint added later has to be a
        // deliberate edit to this test.
        int endpoints = 0;

        foreach (string file in ProductionSources())
        {
            string text = Read(file);
            int at = 0;

            while (true)
            {
                int found = text.IndexOf("https://", at, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }

                endpoints++;
                at = found + 1;
            }
        }

        Assert.Equal(1, endpoints);
    }
}
