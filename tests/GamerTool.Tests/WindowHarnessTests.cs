using System.Windows;
using GamerTool;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The shared harness itself: that a window can be opened on any single tab, that
/// the tab it was asked for is the one that comes up, and that the others stay as
/// inert as they were.
/// <para>
/// The crosshair tab's own behaviour is tested through the same harness, in
/// <see cref="CrosshairTabTests"/>. What is checked here is the part every tab
/// depends on: the profile and folders are the harness's temp ones, each tab is
/// initialised from the saved profile and not from a default, and asking for a tab
/// does not quietly initialise the rest.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class WindowHarnessTests
{
    private const double Gamma = 1.37;
    private const double Contrast = 12;
    private const double MasterGain = 6.5;

    [Fact]
    public void The_display_tab_loads_the_saved_screen_tune()
    {
        WindowHarness.Run(
            MainWindowTabs.Display,
            h =>
            {
                Assert.Equal(Gamma, h.Window.GammaSlider.Value, 2);
                Assert.Equal(Contrast, h.Window.ContrastSlider.Value);
            },
            WithTune);
    }

    [Fact]
    public void The_audio_tab_loads_the_saved_sound_tune()
    {
        WindowHarness.Run(
            MainWindowTabs.Audio,
            h => Assert.Equal(MasterGain, h.Window.MasterGainSlider.Value, 2),
            WithTune);
    }

    [Fact]
    public void The_slots_tab_builds_its_board_from_the_profile()
    {
        // One slot in the profile is one card on the board, under its own header row.
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                Assert.Equal("1 slot", h.Window.SlotCountText.Text);
                Assert.Equal(Visibility.Collapsed, h.Window.SlotEmptyHint.Visibility);
                Assert.Equal(2, h.Window.SlotList.Children.Count);
            },
            WithTune);
    }

    [Fact]
    public void An_empty_profile_gets_the_empty_board()
    {
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                Assert.Equal("No slots", h.Window.SlotCountText.Text);
                Assert.Equal(Visibility.Visible, h.Window.SlotEmptyHint.Visibility);
                Assert.Empty(h.Window.SlotList.Children);
            });
    }

    [Fact]
    public void A_window_asked_for_no_tabs_starts_none_of_them()
    {
        // The same profile the tests above open on, but no tab asked for. Everything
        // that a tab's initialisation writes has to still be untouched, which is what
        // makes a failure in one of those tests about that tab rather than about the
        // window having quietly brought the rest up as well.
        WindowHarness.Run(
            MainWindowTabs.None,
            h =>
            {
                Assert.NotEqual(Gamma, h.Window.GammaSlider.Value);
                Assert.NotEqual(MasterGain, h.Window.MasterGainSlider.Value);
                // The board's header still says the XAML default, because nothing has
                // counted the slots yet.
                Assert.Equal("SLOTS", h.Window.SlotCountText.Text);
                Assert.Empty(h.Window.SlotList.Children);
                Assert.False(h.Read<bool>("_crosshairReady"));
            },
            WithTune);
    }

    [Fact]
    public void Each_run_gets_its_own_profile()
    {
        // The profile a run saves must not be the one the next run loads, or a test
        // that only passes after another test has run would go unnoticed.
        WindowHarness.Run(MainWindowTabs.None, h => Assert.Single(h.Settings.Slots), WithTune);
        WindowHarness.Run(MainWindowTabs.None, h => Assert.Empty(h.Settings.Slots));
    }

    /// <summary>
    /// A profile that is tuned away from every default, so a tab that failed to load
    /// it is visible rather than looking right by accident.
    /// </summary>
    private static void WithTune(WindowSetup setup)
    {
        DisplayPreset display = DisplayPreset.Flat();
        display.Id = "test-screen";
        display.Name = "Test screen";
        display.Gamma = Gamma;
        display.Contrast = Contrast;
        setup.Settings.CustomDisplayPresets.Add(display);
        setup.Settings.ActiveDisplayPresetId = display.Id;

        AudioPreset audio = AudioPreset.Flat();
        audio.Id = "test-sound";
        audio.Name = "Test sound";
        audio.MasterGain = MasterGain;
        setup.Settings.CustomAudioPresets.Add(audio);
        setup.Settings.ActiveAudioPresetId = audio.Id;

        setup.Settings.Slots.Add(SlotService.NewSlot(1));
    }
}
