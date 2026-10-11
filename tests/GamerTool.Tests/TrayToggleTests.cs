using System;
using System.IO;
using System.Windows.Forms;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The tray's two checkable lines, and the state they show.
/// <para>
/// The tray is where the app is when the window is closed, so a control that only
/// exists inside the window is a control that has stopped working for anyone using
/// the app the way they actually use it. These two lines are the bypass and the
/// night schedule, which is everything the window does that a slot does not.
/// </para>
/// <para>
/// The tick state is the interesting part and the failure is specific: a tick beside
/// a switch showing the opposite leaves the user with no way to tell which of the
/// two is lying. So the state is read as the menu opens rather than pushed from
/// each place that changes it, and the mapping from the setting to the tick goes
/// through BypassToggle rather than being re-derived.
/// </para>
/// </summary>
[Collection("wpf")]
public class TrayToggleTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts)));
    }

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n        _tray", at + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("\n    }", at + signature.Length, StringComparison.Ordinal);
        }

        if (end < 0)
        {
            end = source.Length;
        }

        return source.Substring(at, end - at);
    }

    /// <summary>The two toggles as the tray builds them, effects hidden.</summary>
    private static (ToolStripMenuItem Effects, ToolStripMenuItem Night) Toggles()
    {
        ToolStripMenuItem effects = DarkTrayMenuRenderer.CheckableItem(
            "Sound effects", "Tick means the effects are OFF.", false, (s, e) => { });
        ToolStripMenuItem night = DarkTrayMenuRenderer.CheckableItem(
            "Night filter", "Warm the screen on a schedule", false, (s, e) => { });
        return (effects, night);
    }

    [Fact]
    public void ATickedEffectLineMeansTheEffectsAreOff()
    {
        // The inversion, stated once here rather than left implicit. The line is
        // called "Sound effects" and the tick beside it means the opposite of what a
        // user would assume, because the switch it mirrors is labelled BYPASS.
        //
        // Read through BypassToggle, which is the one place that conversion is
        // allowed. Get it backwards and the tray mutes when the user asked to hear
        // something - the worst thing this menu can do.
        Assert.True(BypassToggle.IsEngaged(effectsEnabled: false));
        Assert.False(BypassToggle.IsEngaged(effectsEnabled: true));
    }

    [Fact]
    public void TheTickIsNotTheSwitchesOwnState()
    {
        // CheckOnClick off, so a click cannot tick the line. The window owns the
        // state and pushes it; if the item ticked itself the tick would show the
        // user's intent rather than reality, which is the specific lie this design
        // exists to avoid.
        (ToolStripMenuItem effects, ToolStripMenuItem night) = Toggles();

        Assert.False(effects.CheckOnClick);
        Assert.False(night.CheckOnClick);
    }

    [Fact]
    public void TheEffectsLineSaysWhichWayRoundItsTickIs()
    {
        // Because "Sound effects" with a tick is genuinely ambiguous. Said in the
        // hover text rather than in the line, since the line has to stay short.
        string tray = Read("app", "Services", "TrayService.cs");

        Assert.Contains(
            "Tick means the effects are OFF",
            tray,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEffectsLineIsHiddenRatherThanDisabledWithoutFxSound()
    {
        // A menu item that cannot work is a question the user has to ask the answer
        // to. Hidden is an answer.
        string window = Read("app", "MainWindow.xaml.cs");

        Assert.Contains("effectsAvailable: _audio.IsInstalled", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTogglesAreAskedForAsTheMenuOpensRatherThanPushed()
    {
        // There are half a dozen places that can change either switch - the Audio
        // tab, the Settings tab, the bypass key, a slot applying, a profile restore -
        // and a missed push leaves a tick beside a switch showing the opposite. The
        // menu can only be opened by the user, so reading the state at that moment
        // is the version that cannot drift.
        string tray = Read("app", "Services", "TrayService.cs");
        Assert.Contains("menu.Opening +=", tray, StringComparison.Ordinal);
        Assert.Contains("public event Action? MenuOpening;", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayToggleGoesThroughTheSwitchAndNotTheSetting()
    {
        // The switch's own handler is the one place that sets the field, commits,
        // queues the engine push and verifies it landed. Flipping IsChecked runs
        // all four; setting EffectsEnabled from here would skip three of them.
        string window = Read("app", "MainWindow.xaml.cs");

        Assert.Contains("_tray.EffectsToggleRequested", window, StringComparison.Ordinal);
        Assert.Contains("BypassBox.IsChecked = BypassBox.IsChecked != true;", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_tray.EffectsToggleRequested += () => _settings.EffectsEnabled", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNightToggleGoesThroughTheNightSwitch()
    {
        // Same reason. Night blue light is a schedule rather than a preset, so there
        // is nothing to apply - it is a question about whether the app watches the
        // clock, and the switch that answers it is NightBox.
        string window = Read("app", "MainWindow.xaml.cs");

        // Whitespace-collapsed, because the handler wraps across two lines. Asserting
        // on raw text here would fail on formatting alone and get "fixed" by deleting
        // the assertion.
        string flat = System.Text.RegularExpressions.Regex.Replace(window, @"\s+", " ");

        Assert.Contains("_tray.NightToggleRequested", window, StringComparison.Ordinal);
        Assert.Contains(
            "NightBox.IsChecked = NightBox.IsChecked != true);",
            flat,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheToggleHandlersAreOnTheDispatcher()
    {
        // The tray is WinForms hosted in WPF, and the rest of the tray handlers
        // already do this. Setting a WPF control's IsChecked from the wrong thread
        // throws, so it is not optional tidiness.
        string window = Read("app", "MainWindow.xaml.cs");

        Assert.Contains(
            "_tray.EffectsToggleRequested += () => Dispatcher.Invoke",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_tray.NightToggleRequested += () => Dispatcher.Invoke",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEffectsToggleStaysInertWithoutTheEngine()
    {
        // Checked in the handler as well as in the visible state. The line can be
        // hidden by a state push between the click and this running, and a toggle
        // that changed a setting nothing would act on is worse than one that does
        // nothing.
        string window = Read("app", "MainWindow.xaml.cs");
        string body = Body(window, "_tray.EffectsToggleRequested");

        Assert.Contains("if (!_audio.IsInstalled)", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTogglesSitBetweenTheSlotsAndTheResets()
    {
        // Not appended at the bottom. A menu whose lines run Open, Slots, Resets,
        // Toggles, Quit reads as though the toggles are a sub-setting of Quit.
        string tray = Read("app", "Services", "TrayService.cs");

        int effects = tray.IndexOf("menu.Items.Add(_effectsItem);", StringComparison.Ordinal);
        int resetScreen = tray.IndexOf("menu.Items.Add(resetScreen);", StringComparison.Ordinal);
        int quit = tray.IndexOf("menu.Items.Add(quit);", StringComparison.Ordinal);

        Assert.True(effects > 0 && resetScreen > effects, "the toggles are not before the resets");
        Assert.True(quit > resetScreen, "the menu is not in the order the shape test expects");
    }

    [Fact]
    public void SetToggleStatesIsCalledOnlyFromTheOpenHandler()
    {
        // If something else also pushed state, there would be two writers and the
        // tick would depend on which ran last. One caller means one answer.
        string window = Read("app", "MainWindow.xaml.cs");
        int count = 0;
        int at = 0;

        while (true)
        {
            int found = window.IndexOf("SetToggleStates(", at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count++;
            at = found + 1;
        }

        Assert.Equal(1, count);
    }
}