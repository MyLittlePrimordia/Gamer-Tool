using System;
using GamerTool;
using GamerTool.Models;

namespace GamerTool.Tests;

/// <summary>
/// The Audio tab as a test drives it: the harness, a saved sound preset with a band
/// layout of its own, and the three gestures the tab is made of - pick a preset,
/// move a fader, reset.
/// <para>
/// The saved preset carries thirty one bands on purpose. A reset is documented to
/// zero the gains without quietly dropping someone off thirty one bands and back to
/// ten, and a fixture that opened on ten could not tell the two apart.
/// </para>
/// </summary>
internal sealed class AudioTab
{
    /// <summary>The user's own sound preset the tab opens on: thirty one bands, lifted.</summary>
    public const string SavedId = "test-sound";

    /// <summary>A second saved preset, so picking one is a real change of preset.</summary>
    public const string OtherId = "test-quiet";

    public const double SavedMaster = 6.5;
    public const double SavedBass = 4;
    public const double OtherMaster = -3;
    public const int SavedBands = 31;
    public const int OtherBands = 10;

    private readonly WindowHarness _harness;

    private AudioTab(WindowHarness harness) => _harness = harness;

    /// <summary>The window, its temp folders and its teardown.</summary>
    public WindowHarness Harness => _harness;

    public MainWindow Window => _harness.Window;

    /// <summary>The window's own settings object, which is the one its handlers write to.</summary>
    public AppSettings Settings => _harness.Settings;

    /// <summary>The sound tune being edited, i.e. what Apply would push.</summary>
    public AudioPreset Work => _harness.Read<AudioPreset>("_workAudio");

    /// <summary>The preset the window considers loaded, which the slot board also reads.</summary>
    public string ActiveId => _harness.Read<string>("_activeAudioId");

    /// <summary>The preset the dropdown is showing.</summary>
    public string SelectedId => WindowHarness.SelectedPresetId(Window.AudioPresetBox);

    /// <summary>The filter layout the faders are showing right now.</summary>
    public int SliderBands => _harness.Read<System.Collections.Generic.List<System.Windows.Controls.Slider>>("_bandSliders").Count;

    /// <summary>
    /// Whether a fader move asked for the working tune to be pushed to the engine.
    /// The harness points the engine at a path that does not exist, so this has to
    /// stay false: a test that started the timer would be about to write to whatever
    /// FxSound the machine has.
    /// </summary>
    public bool LiveTuneQueued => _harness.Peek("_liveTuneTimer") is not null;

    /// <summary>Stand the tab up on <see cref="SavedId"/>, run <paramref name="body"/>, take it down.</summary>
    public static void Run(Action<AudioTab> body, Action<WindowSetup>? setup = null)
    {
        WindowHarness.Run(
            MainWindowTabs.Audio,
            harness => body(new AudioTab(harness)),
            window =>
            {
                AudioPreset saved = AudioPreset.Flat(SavedBands);
                saved.Id = SavedId;
                saved.Name = "My sound";
                saved.Tag = "Mine";
                saved.MasterGain = SavedMaster;
                saved.BassBoost = SavedBass;

                AudioPreset other = AudioPreset.Flat(OtherBands);
                other.Id = OtherId;
                other.Name = "Quiet";
                other.Tag = "Mine";
                other.MasterGain = OtherMaster;

                window.Settings.CustomAudioPresets.Add(saved);
                window.Settings.CustomAudioPresets.Add(other);
                window.Settings.ActiveAudioPresetId = saved.Id;

                setup?.Invoke(window);
            });
    }

    /// <summary>Pick a preset in the dropdown, which is what loads it.</summary>
    public void Pick(string id) => WindowHarness.PickPreset(Window.AudioPresetBox, id);

    /// <summary>Move the master fader, which is what makes the tune the user's own.</summary>
    public void MoveMaster(double value) => Window.MasterGainSlider.Value = value;

    /// <summary>Press Reset, which is the round arrow beside Save in FxSound.</summary>
    public void PressReset() => WindowHarness.Click(Window.SoundResetButton);

    /// <summary>
    /// Wait for a reset to finish. It runs through the push queue and a worker, so
    /// the state it writes is not there the instant the button is pressed.
    /// </summary>
    public void WaitForReset() =>
        _harness.PumpUntil(() => ActiveId == "flat" && Window.MasterGainSlider.Value == 0);
}
