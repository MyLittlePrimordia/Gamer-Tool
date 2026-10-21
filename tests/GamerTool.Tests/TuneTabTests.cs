using System.Linq;
using GamerTool;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The two tuning tabs driven the way a person drives them: picking a preset in the
/// dropdown, moving a fader, and pressing Reset.
/// <para>
/// These are not tests of the services underneath. A preset box wired to the wrong
/// handler, a fader that renames the tune but forgets the label, or a reset that
/// clears the panel without remembering it are all invisible to a service test and
/// all fail here.
/// </para>
/// <para>
/// The harness points the engine at a path that does not exist, so the sound tab's
/// gestures stop at the panel and the profile. Nothing here writes a gamma ramp,
/// starts FxSound or puts a toast on the screen.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class TuneTabTests
{
    // ------------------------------------------------------------------ display

    [Fact]
    public void The_display_tab_opens_on_the_saved_screen_preset()
    {
        DisplayTab.Run(tab =>
        {
            Assert.Equal(DisplayTab.SavedId, tab.Work.Id);
            Assert.Equal("My screen", tab.Work.Name);
            Assert.Equal(DisplayTab.SavedGamma, tab.Work.Gamma, 2);

            // Both halves of "loaded": what the tab is editing and what the dropdown
            // says. A box showing one preset while the sliders show another is the
            // defect a read of either one alone cannot see.
            Assert.Equal(DisplayTab.SavedId, tab.ActiveId);
            Assert.Equal(DisplayTab.SavedId, tab.SelectedId);

            // And the readouts agree with the faders.
            Assert.Equal("1.55", tab.Window.GammaValue.Text);
            Assert.Equal("+12%", tab.Window.ContrastValue.Text);
        });
    }

    [Fact]
    public void Picking_a_screen_preset_loads_its_tune_into_the_faders()
    {
        DisplayTab.Run(tab =>
        {
            tab.Pick("flat");

            Assert.Equal("flat", tab.ActiveId);
            Assert.Equal("flat", tab.Work.Id);
            Assert.Equal("flat", tab.SelectedId);
            Assert.Equal(1.00, tab.Window.GammaSlider.Value, 2);
            Assert.Equal("1.00", tab.Window.GammaValue.Text);

            // Picking a preset is choosing it, not editing the one that was open:
            // the saved copy has to come back exactly as it was saved.
            DisplayPreset saved = tab.Settings.CustomDisplayPresets.Single(p => p.Id == DisplayTab.SavedId);
            Assert.Equal(DisplayTab.SavedGamma, saved.Gamma, 2);
            Assert.Equal("My screen", saved.Name);
        });
    }

    [Fact]
    public void Moving_a_screen_fader_makes_the_tune_Tuned_without_saving_it()
    {
        DisplayTab.Run(tab =>
        {
            tab.MoveGamma(1.70);

            Assert.Equal("Tuned", tab.Work.Name);
            Assert.Equal(1.70, tab.Work.Gamma, 2);
            Assert.Equal("1.70", tab.Window.GammaValue.Text);

            // The rename lands on the working copy, not on the user's saved preset.
            // An unnamed tweak is deliberately not written into the profile; doing
            // that is what once made an edit look thrown away on the next launch.
            DisplayPreset saved = tab.Settings.CustomDisplayPresets.Single();
            Assert.Equal("My screen", saved.Name);
            Assert.Equal(DisplayTab.SavedGamma, saved.Gamma, 2);
        });
    }

    [Fact]
    public void Reset_puts_the_screen_back_to_Standard_and_remembers_it()
    {
        DisplayTab.Run(tab =>
        {
            tab.MoveGamma(1.70);
            tab.PressReset();

            // The panel first.
            Assert.Equal("flat", tab.Work.Id);
            Assert.Equal("flat", tab.ActiveId);
            Assert.Equal("flat", tab.SelectedId);
            Assert.Equal(1.00, tab.Window.GammaSlider.Value, 2);
            Assert.Equal("1.00", tab.Window.GammaValue.Text);

            // And the profile, which is the half that decides what the app opens on
            // tomorrow. A reset that is only in memory survives until the next launch
            // and no further.
            string profile = new ProfileManager(tab.Harness.ProfileFolder).Load().ActiveDisplayPresetId;
            Assert.Equal("flat", profile);

            // The user's own preset is not the thing being reset.
            Assert.Contains(tab.Settings.CustomDisplayPresets, p => p.Id == DisplayTab.SavedId);
        });
    }

    // -------------------------------------------------------------------- audio

    [Fact]
    public void The_audio_tab_opens_on_the_saved_sound_preset()
    {
        AudioTab.Run(tab =>
        {
            Assert.Equal(AudioTab.SavedId, tab.Work.Id);
            Assert.Equal("My sound", tab.Work.Name);
            Assert.Equal(AudioTab.SavedMaster, tab.Work.MasterGain, 2);
            Assert.Equal(AudioTab.SavedBands, tab.Work.NumBands);
            Assert.Equal(AudioTab.SavedBands, tab.SliderBands);

            Assert.Equal(AudioTab.SavedId, tab.ActiveId);
            Assert.Equal(AudioTab.SavedId, tab.SelectedId);
            Assert.Equal("+6.5", tab.Window.MasterGainValue.Text);
        });
    }

    [Fact]
    public void Picking_a_sound_preset_loads_its_own_band_layout()
    {
        AudioTab.Run(tab =>
        {
            tab.Pick(AudioTab.OtherId);

            Assert.Equal(AudioTab.OtherId, tab.ActiveId);
            Assert.Equal(AudioTab.OtherMaster, tab.Window.MasterGainSlider.Value, 2);
            Assert.Equal("-3.0", tab.Window.MasterGainValue.Text);

            // Ten bands is a layout, not just a number: the faders have to be
            // rebuilt, because the previous preset's thirty one would otherwise stay
            // on screen and take the next edit with them.
            Assert.Equal(AudioTab.OtherBands, tab.Work.NumBands);
            Assert.Equal(AudioTab.OtherBands, tab.SliderBands);
        });
    }

    [Fact]
    public void Moving_a_sound_fader_makes_the_tune_Tuned_and_never_reaches_the_engine()
    {
        AudioTab.Run(tab =>
        {
            tab.MoveMaster(4.5);

            Assert.Equal("Tuned", tab.Work.Name);
            Assert.Equal(4.5, tab.Work.MasterGain, 2);
            Assert.Equal("+4.5", tab.Window.MasterGainValue.Text);

            // The fader asks for a live push on every move. With no engine installed
            // that request has to stop at the flag: none of this may ever be about to
            // write to whatever FxSound the machine running the suite happens to have.
            Assert.False(tab.LiveTuneQueued);

            // A tweak is scratch, so the saved preset keeps its name.
            AudioPreset saved = tab.Settings.CustomAudioPresets.Single(p => p.Id == AudioTab.SavedId);
            Assert.Equal("My sound", saved.Name);
        });
    }

    [Fact]
    public void Reset_zeroes_the_sound_and_keeps_the_band_layout()
    {
        AudioTab.Run(tab =>
        {
            tab.MoveMaster(4.5);
            tab.PressReset();
            tab.WaitForReset();

            Assert.Equal("flat", tab.ActiveId);
            Assert.Equal("flat", tab.SelectedId);
            Assert.Equal("flat", tab.Work.Id);
            Assert.Equal(0, tab.Work.MasterGain, 2);
            Assert.Equal(0, tab.Work.BassBoost, 2);
            Assert.Equal(0, tab.Window.MasterGainSlider.Value, 2);

            // Thirty one bands in, thirty one bands out: a reset zeroes the gains
            // rather than quietly dropping someone back to ten faders.
            Assert.Equal(AudioTab.SavedBands, tab.Work.NumBands);
            Assert.Equal(AudioTab.SavedBands, tab.SliderBands);

            // Persisted as well as set, for the same reason the screen reset is.
            string profile = new ProfileManager(tab.Harness.ProfileFolder).Load().ActiveAudioPresetId;
            Assert.Equal("flat", profile);
        });
    }
}
