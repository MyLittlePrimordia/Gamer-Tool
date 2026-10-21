using System;
using GamerTool;
using GamerTool.Models;

namespace GamerTool.Tests;

/// <summary>
/// The Display tab as a test drives it: the harness, plus a saved screen preset to
/// open on and the three gestures the tab is made of - pick a preset, move a
/// slider, reset.
/// <para>
/// The preset is tuned away from every default, so a tab that loaded nothing is
/// visible rather than looking right by accident. All three gestures go through the
/// controls themselves: the box is a selection change, the slider is a value change,
/// and the reset is a pressed button, each reaching the handler written in markup.
/// </para>
/// </summary>
internal sealed class DisplayTab
{
    /// <summary>The user's own screen preset the tab opens on.</summary>
    public const string SavedId = "test-screen";

    public const double SavedGamma = 1.55;
    public const double SavedContrast = 12;

    private readonly WindowHarness _harness;

    private DisplayTab(WindowHarness harness) => _harness = harness;

    /// <summary>The window, its temp folders and its teardown.</summary>
    public WindowHarness Harness => _harness;

    public MainWindow Window => _harness.Window;

    /// <summary>The window's own settings object, which is the one its handlers write to.</summary>
    public AppSettings Settings => _harness.Settings;

    /// <summary>The screen tune being edited, i.e. what Apply would push.</summary>
    public DisplayPreset Work => _harness.Read<DisplayPreset>("_workDisplay");

    /// <summary>The preset the window considers loaded, which the slot board also reads.</summary>
    public string ActiveId => _harness.Read<string>("_activeDisplayId");

    /// <summary>The preset the dropdown is showing.</summary>
    public string SelectedId => WindowHarness.SelectedPresetId(Window.DisplayPresetBox);

    /// <summary>Stand the tab up on <see cref="SavedId"/>, run <paramref name="body"/>, take it down.</summary>
    public static void Run(Action<DisplayTab> body, Action<WindowSetup>? setup = null)
    {
        WindowHarness.Run(
            MainWindowTabs.Display,
            harness => body(new DisplayTab(harness)),
            window =>
            {
                DisplayPreset saved = DisplayPreset.Flat();
                saved.Id = SavedId;
                saved.Name = "My screen";
                saved.Tag = "Mine";
                saved.Gamma = SavedGamma;
                saved.Contrast = SavedContrast;

                window.Settings.CustomDisplayPresets.Add(saved);
                window.Settings.ActiveDisplayPresetId = saved.Id;

                setup?.Invoke(window);
            });
    }

    /// <summary>Pick a preset in the dropdown, which is what loads it.</summary>
    public void Pick(string id) => WindowHarness.PickPreset(Window.DisplayPresetBox, id);

    /// <summary>Move the gamma fader, which is what makes the tune the user's own.</summary>
    public void MoveGamma(double value) => Window.GammaSlider.Value = value;

    /// <summary>Press Reset, which is the round arrow beside Apply.</summary>
    public void PressReset() => WindowHarness.Click(Window.ScreenResetButton);
}
