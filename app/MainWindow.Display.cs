using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace GamerTool;

public partial class MainWindow : Window
{
    private void BuildDisplayCards()
    {
        DisplayPresetGrid.Children.Clear();
        _displayCards.Clear();

        foreach (DisplayPreset preset in AllDisplayPresets())
        {
            bool isMine = _settings.CustomDisplayPresets.Any(p => p.Id == preset.Id);
            DisplayPresetGrid.Children.Add(PresetCard(preset, isMine, _displayCards, OnDisplayCardClick, OnDisplayCardDelete, OnDisplayCardRename));
        }

        HighlightCards();
    }


    private void UpdateScreenLabels(DisplayPreset preset)
    {
        _workDisplay = preset;
        GammaValue.Text = preset.Gamma.ToString("0.00", CultureInfo.InvariantCulture);
        ShadowValue.Text = Signed(preset.ShadowBoost, "0") + "%";
        BrightValue.Text = Signed(preset.Brightness, "0") + "%";
        ContrastValue.Text = Signed(preset.Contrast, "0") + "%";
        RedValue.Text = preset.RedGain.ToString("0.00", CultureInfo.InvariantCulture);
        GreenValue.Text = preset.GreenGain.ToString("0.00", CultureInfo.InvariantCulture);
        BlueValue.Text = preset.BlueGain.ToString("0.00", CultureInfo.InvariantCulture);
        ScreenActiveName.Text = preset.Name;
        ScreenActiveSpec.Text = "SHADOW " + preset.ShadowText + "   BRIGHT " + preset.BrightnessText + "   CONTRAST " + preset.ContrastText;
        ScreenActiveRgb.Text = "RGB " + DisplayPreset.WithBlueLight(preset, _settings.BlueLightFilter).RgbText;
        QueueDisplayPreview();
    }


    private void OnScreenSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        _workDisplay.Gamma = Math.Round(GammaSlider.Value, 2);
        _workDisplay.ShadowBoost = ShadowSlider.Value;
        _workDisplay.Brightness = BrightSlider.Value;
        _workDisplay.Contrast = ContrastSlider.Value;
        _workDisplay.RedGain = Math.Round(RedSlider.Value, 2);
        _workDisplay.GreenGain = Math.Round(GreenSlider.Value, 2);
        _workDisplay.BlueGain = Math.Round(BlueSlider.Value, 2);
        _workDisplay.Name = "TUNED SCREEN";
        UpdateScreenLabels(_workDisplay);
        QueueDisplayPreview();
    }


    private void OnNeutralColourClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        RedSlider.Value = 1.0;
        GreenSlider.Value = 1.0;
        BlueSlider.Value = 1.0;
    }


    private void OnDisplayCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        DisplayPreset? preset = FindDisplay(id);
        if (preset is null)
        {
            return;
        }

        LoadTune(preset.Copy(), _workAudio.Copy());
        _activeDisplayId = preset.Id;
        HighlightCards();
        UpdateScreenLabels(preset);
        QueueDisplayPreview();
    }


    private void OnDisplayCardDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        _settings.CustomDisplayPresets.RemoveAll(p => p.Id == id);
        Commit();
        BuildDisplayCards();
        BuildSlots();
        Flash("[ PRESET DELETED ]", id.ToUpperInvariant());
    }


    private void OnDisplayCardRename(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        DisplayPreset? preset = _settings.CustomDisplayPresets.FirstOrDefault(p => p.Id == id);
        if (preset is null)
        {
            return;
        }

        ShowModal("RENAME SCREEN PRESET", preset.Name, name =>
        {
            preset.Name = name;
            Commit();
            BuildDisplayCards();
            BuildSlots();
        });
    }


    private void OnSaveAsScreenClick(object sender, RoutedEventArgs e)
    {
        ShowModal("NAME YOUR SCREEN PRESET", "MY SCREEN", name =>
        {
            DisplayPreset preset = _workDisplay.Copy();
            preset.Id = AppProfileTools.NewId("screen");
            preset.Name = name;
            preset.Tag = "MINE";
            _settings.CustomDisplayPresets.Add(preset);
            Commit();
            BuildDisplayCards();
            BuildSlots();

            // Make the copy the loaded preset so Rename works on it straight away.
            _activeDisplayId = preset.Id;
            LoadTune(preset, _workAudio);
            HighlightCards();
            Flash("[ SCREEN PRESET SAVED ]", name);
        });
    }


    private void QueueDisplayPreview()
    {
        if (_previewQueued)
        {
            return;
        }

        _previewQueued = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTimerTick;
        _previewTimer.Tick += OnPreviewTimerTick;
        _previewTimer.Start();
    }


    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTimerTick;
        _previewQueued = false;
        RenderDisplayPreview();
    }


    private void RenderDisplayPreview()
    {
        if (!_displayPreview.Ready)
        {
            return;
        }

        BitmapSource? frame = _displayPreview.Render(EffectiveDisplay(), _previewNight);
        if (frame is not null)
        {
            PreviewImage.Source = frame;
        }
    }


    /// <summary>
    /// The staged preset with the blue light filter trim folded in, so the preview
    /// matches what Apply will actually push.
    /// </summary>
    private DisplayPreset EffectiveDisplay()
    {
        return DisplayPreset.WithBlueLight(_workDisplay, _settings.BlueLightFilter);
    }


    private void OnBlueLightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || BlueLightBox.SelectedItem is not string name)
        {
            return;
        }

        int level = Array.IndexOf(DisplayPreset.BlueLightNames, name);
        if (level < 0)
        {
            return;
        }

        _settings.BlueLightFilter = level;
        ScreenActiveRgb.Text = "RGB " + EffectiveDisplay().RgbText;
        QueueDisplayPreview();
        Commit();
    }


    private void UpdatePreviewPills()
    {
        DayPillButton.Style = (Style)FindResource(_previewNight ? "PillTab" : "PillTabActive");
        NightPillButton.Style = (Style)FindResource(_previewNight ? "PillTabActive" : "PillTab");
    }


    private void OnDayPreviewClick(object sender, RoutedEventArgs e)
    {
        _previewNight = false;
        UpdatePreviewPills();
        RenderDisplayPreview();
    }


    private void OnNightPreviewClick(object sender, RoutedEventArgs e)
    {
        _previewNight = true;
        UpdatePreviewPills();
        RenderDisplayPreview();
    }


    private void ApplyDisplay(DisplayPreset preset, bool announce)
    {
        ApplyDisplay(preset, string.Empty, announce);
    }


    private void ApplyDisplay(DisplayPreset preset, string monitorDevice, bool announce)
    {
        DisplayPreset effective = DisplayPreset.WithBlueLight(preset, _settings.BlueLightFilter);
        _display.Apply(effective, monitorDevice);
        _liveDisplayName = preset.Name.ToUpperInvariant();
        UpdateLiveLabels();
        SessionState.Current.DisplayTouched = true;
        _settings.ActiveDisplayPresetId = preset.Id;
        _activeDisplayId = preset.Id;
        UpdateScreenLabels(preset);
        HighlightCards();
        Commit();
        if (announce)
        {
            string scope = monitorDevice.Length == 0 ? string.Empty : "  " + monitorDevice.ToUpperInvariant();
            Flash("[ " + preset.Name + scope + " ]", "SCREEN READY");
        }
    }


    private void OnApplyScreenClick(object sender, RoutedEventArgs e)
    {
        ApplyDisplay(_workDisplay.Copy(), true);
    }


    /// <summary>
    /// Renames the loaded preset in place when it is one of your own, and refuses
    /// for a built in so the shipped presets can never be edited. Use Save as to
    /// copy a built in one first.
    /// </summary>
    private void OnRenameScreenClick(object sender, RoutedEventArgs e)
    {
        DisplayPreset? mine = _settings.CustomDisplayPresets
            .FirstOrDefault(p => string.Equals(p.Id, _workDisplay.Id, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("[ BUILT IN PRESET ]", "USE SAVE AS TO MAKE YOUR OWN COPY");
            return;
        }

        ShowModal("RENAME SCREEN PRESET", mine.Name, name =>
        {
            mine.Name = name;
            Commit();
            BuildDisplayCards();
            BuildSlots();
        });
    }


    private void OnResetScreenClick(object sender, RoutedEventArgs e)
    {
        _display.Reset();
        _workDisplay = DisplayPreset.Flat();
        LoadTune(_workDisplay, _workAudio);
        _activeDisplayId = string.Empty;
        _liveDisplayName = "NOTHING";
        UpdateLiveLabels();
        HighlightCards();
        Flash("[ SCREEN RESET ]", "BACK TO NORMAL");
    }


}
