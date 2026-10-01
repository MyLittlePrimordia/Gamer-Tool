using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Two fixes that cannot be caught any other way, held in place.
/// <para>
/// Both are the same shape of defect: a recovery path that did not actually
/// recover, on the one control a user reaches for when something has already gone
/// wrong. Neither is visible from a unit test, because both live in
/// <c>MainWindow</c>, whose constructor builds the window, wires hotkeys and
/// reaches for the displays - so there is no seam to drive it from CI.
/// </para>
/// <para>
/// That leaves reading the source. A source assertion is a weak instrument and
/// should not be used where a real test is possible; here there is not one, and
/// the alternative is a comment describing an intention that nothing checks.
/// </para>
/// </summary>
public class RecoveryPathTests
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

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The body of a method, from its signature to the brace that closes it.</summary>
    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "could not find " + signature);

        int open = source.IndexOf('{', at);
        Assert.True(open > 0, "could not find the body of " + signature);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(open, i - open);
                }
            }
        }

        Assert.Fail("the body of " + signature + " is not closed");
        return string.Empty;
    }

    [Fact]
    public void Going_back_to_normal_also_puts_the_monitor_brightness_back()
    {
        // BUG-001. The panic key, the Reset button, the tray's Reset Display and
        // switching a loaded slot off all funnel through GoScreenNeutral, which is
        // the one definition of "back to normal" for the screen. It reset the gamma
        // ramp and left the hardware backlight where the last slot had put it, so
        // the control a user reaches for when the picture is already wrong was
        // the one control that left the picture wrong.
        string display = Read("app/MainWindow.Display.cs");
        string body = Body(display, "public void GoScreenNeutral()");

        Assert.Contains(
            "Backlight.RestoreAll()",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_panic_key_goes_through_that_same_path()
    {
        // The panic key must reach the method that restores the backlight, not
        // its own narrower idea of neutral. It calls both halves directly today,
        // which is correct as long as the screen half is the shared one.
        string slots = Read("app/MainWindow.Slots.cs");
        string body = Body(slots, "private async void OnHotkeyPressed");

        Assert.Contains("GoScreenNeutral()", body, StringComparison.Ordinal);
        Assert.Contains("GoSoundNeutralAsync()", body, StringComparison.Ordinal);

        // And nothing on the panic path may reach for EmergencyReset, which also
        // powers the engine off. A panic key that turned the engine off would be a
        // different and much more surprising behaviour than the one it documents.
        Assert.DoesNotContain(
            "EmergencyReset",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_game_watcher_forgets_the_window_it_was_looking_at_before_re_arming()
    {
        // BUG-003. ProcessWatcherService.Reset clears the remembered foreground
        // window, but nothing called it: ApplyWatchState stopped and started the
        // timer while leaving that memory intact, so switching auto-load off and
        // on again re-armed the watcher with a game still recorded as current.
        string setup = Read("app/MainWindow.Setup.cs");
        string body = Body(setup, "private void ApplyWatchState()");

        Assert.Contains(
            "_watcher.Reset()",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_watcher_reset_actually_clears_the_remembered_window()
    {
        // The other half. A call to Reset is only worth making if Reset does
        // something, and the field it clears is private, so this reads it.
        string watcher = Read("app/Services/ProcessWatcherService.cs");
        string body = Body(watcher, "public void Reset()");

        Assert.Contains("_lastKey = string.Empty", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hardware_brightness_tooltip_admits_a_forced_kill_cannot_be_recovered_from()
    {
        // The disclosure the tooltip now owes the user. A forced kill runs no
        // managed code, so the app cannot put the panel back, and a tooltip that
        // only mentions the driver-crash risk reads as though the app covers
        // every way this can go wrong.
        string xaml = Read("app/MainWindow.xaml");

        int at = xaml.IndexOf("HardwareBrightnessBox", StringComparison.Ordinal);
        Assert.True(at >= 0, "the hardware brightness row is gone from the settings tab");

        int close = xaml.IndexOf("/>", at, StringComparison.Ordinal);
        Assert.True(close > at, "the hardware brightness row is not self-closing");

        string row = xaml.Substring(at, close - at);

        Assert.Contains("End task", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Task Manager", row, StringComparison.OrdinalIgnoreCase);
    }
}
