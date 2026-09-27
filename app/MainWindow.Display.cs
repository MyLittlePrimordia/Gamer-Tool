using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace GamerTool;

public partial class MainWindow : Window
{
    private void UpdateScreenLabels(DisplayPreset preset)
    {
        _workDisplay = preset;
        GammaValue.Text = preset.Gamma.ToString("0.00", CultureInfo.InvariantCulture);
        ShadowValue.Text = Signed(preset.ShadowBoost, "0") + "%";
        BrightValue.Text = Signed(preset.Brightness, "0") + "%";
        ContrastValue.Text = Signed(preset.Contrast, "0") + "%";
        QueueDisplayPreview();
    }


    private void OnDisplayPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || DisplayPresetBox.SelectedItem is not PresetChoice choice)
        {
            return;
        }

        DisplayPreset? preset = FindDisplay(choice.Id);
        if (preset is null)
        {
            return;
        }

        _activeDisplayId = preset.Id;
        LoadTune(preset.Copy(), _workAudio.Copy());
        UpdatePresetChrome();
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
        _workDisplay.Name = "Tuned";
        UpdateScreenLabels(_workDisplay);
        UpdatePresetChrome();
    }


    private void OnDeleteScreenClick(object sender, RoutedEventArgs e)
    {
        DisplayPreset? mine = _settings.CustomDisplayPresets
            .FirstOrDefault(p => string.Equals(p.Id, _activeDisplayId, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("Built in presets stay put");
            return;
        }

        string removed = mine.Name;
        ShowConfirmModal(
            "DELETE \u201C" + removed.ToUpperInvariant() + "\u201D?",
            "This takes the preset out of Gamer Tool and out of any slot that points at it.",
            "Delete",
            () =>
            {
                _settings.CustomDisplayPresets.RemoveAll(p => p.Id == mine.Id);
                _activeDisplayId = string.Empty;
                Commit();
                RefreshPresetBoxes();
                LoadTune(DisplayPreset.Flat(), _workAudio);
                BuildSlots();
                Flash(removed + " deleted");
            });
    }


    private void OnSaveAsScreenClick(object sender, RoutedEventArgs e)
    {
        ShowModal("NAME THIS SCREEN PRESET", "My screen", name =>
        {
            DisplayPreset preset = _workDisplay.Copy();
            preset.Id = AppProfileTools.NewId("screen");
            preset.Name = name;
            preset.Tag = "Mine";
            _settings.CustomDisplayPresets.Add(preset);
            _activeDisplayId = preset.Id;
            Commit();
            RefreshPresetBoxes();
            BuildSlots();
            Flash(name + " saved");
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

        BitmapSource? frame = _displayPreview.Render(EffectiveDisplay(), _previewScene, _previewFrame);
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
        QueueDisplayPreview();
        Commit();
    }


    /// <summary>
    /// Labels the scene the preview is on. The icon and the tint both say where you
    /// are, not where a click would take you, so a scene is named by what is on the
    /// screen rather than by the button that changes it.
    /// </summary>
    private void UpdatePreviewPills()
    {
        (string Glyph, string Tooltip) = _previewScene switch
        {
            PreviewScene.Night => ("moon", "Night scene"),
            PreviewScene.Transition => ("moonscape", "Day to night loop"),
            _ => ("sun", "Day scene")
        };

        PreviewSceneGlyph.Glyph = Glyph;
        PreviewSceneButton.ToolTip = Tooltip;

        // Tinted with the accent of the scene actually on screen, so the pill says
        // where you are rather than where you would go.
        PreviewSceneButton.Background = _previewScene switch
        {
            PreviewScene.Night => (Brush)FindResource("AccentHotkeysGlow"),
            PreviewScene.Transition => (Brush)FindResource("AccentAudioGlow"),
            _ => (Brush)FindResource("AccentDisplayGlow")
        };
    }


    private void OnPreviewSceneClick(object sender, RoutedEventArgs e)
    {
        _previewScene = _previewScene switch
        {
            PreviewScene.Day => PreviewScene.Night,
            PreviewScene.Night => PreviewScene.Transition,
            _ => PreviewScene.Day
        };

        // Each scene starts at the top of itself, so a loop that has run part way
        // through does not pick up in the middle when it comes back round.
        _previewFrame = 0;

        UpdatePreviewPills();

        // Drawn straight away, so a scene that is already decoded appears on the
        // click rather than a frame later.
        RenderDisplayPreview();
        _ = FinishPreviewSceneSwitch();
    }


    /// <summary>
    /// Waits for a scene that is not decoded yet, then draws it and starts or stops
    /// the loop to match. The first click on the looping scene therefore shows a
    /// still for a moment and then the clip, rather than waiting on the decode
    /// before showing anything.
    /// </summary>
    private async System.Threading.Tasks.Task FinishPreviewSceneSwitch()
    {
        try
        {
            await _displayPreview.EnsureSceneReadyAsync(_previewScene);
        }
        catch (Exception ex)
        {
            AppLog.Error("PREVIEW SCENE", ex);
        }

        RenderDisplayPreview();
        UpdatePreviewPlayback();
    }


    /// <summary>
    /// Starts or stops the loop so it runs only when there is something moving to
    /// look at on screen.
    /// </summary>
    private void UpdatePreviewPlayback()
    {
        bool wanted = _previewScene == PreviewScene.Transition
            && _displayPreview.IsAnimated(PreviewScene.Transition)
            && Pages.SelectedIndex == 0;

        if (!wanted)
        {
            _previewFrameTimer?.Stop();
            return;
        }

        if (_previewFrameTimer is null)
        {
            _previewFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _previewFrameTimer.Tick += OnPreviewFrameTick;
        }

        // Set every time, not just at the start. A clip is free to hold a frame
        // for longer than the last one did, and a timer left on the first frame's
        // timing would quietly flatten that out.
        _previewFrameTimer.Stop();
        _previewFrameTimer.Interval = TimeSpan.FromMilliseconds(
            _displayPreview.FrameDelayMs(PreviewScene.Transition, _previewFrame));
        _previewFrameTimer.Start();
    }


    private void OnPreviewFrameTick(object? sender, EventArgs e)
    {
        int count = _displayPreview.FrameCount(PreviewScene.Transition);
        if (count <= 1 || _previewFrameTimer is null)
        {
            _previewFrameTimer?.Stop();
            return;
        }

        _previewFrame = (_previewFrame + 1) % count;
        _previewFrameTimer.Interval = TimeSpan.FromMilliseconds(
            _displayPreview.FrameDelayMs(PreviewScene.Transition, _previewFrame));

        RenderDisplayPreview();
    }


    /// <summary>
    /// Rounds the corners of the scene itself, not just the frame around it.
    ///
    /// The clip is taken from the frame rather than from the image, because the
    /// image's own width can land a fraction of a pixel wider than the frame's
    /// inner edge once the two are snapped to device pixels. The overhang then
    /// covered the frame's rounding on the right, which is why only the right hand
    /// corners looked square. Clipping to the frame's measured size keeps the
    /// rounding on all four corners lined up.
    /// </summary>
    private void OnPreviewFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border frame || e.NewSize.Width <= 0.0 || e.NewSize.Height <= 0.0)
        {
            return;
        }

        const double radius = 10.0;
        var rect = new Rect(0, 0, frame.ActualWidth, frame.ActualHeight);
        PreviewImage.Clip = new RectangleGeometry(rect, radius, radius);
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
        _settings.ActiveDisplayPresetId = preset.Id;
        _activeDisplayId = preset.Id;
        UpdateScreenLabels(preset);
        UpdateLiveLabels();
        UpdatePresetChrome();
        Commit();
        if (announce)
        {
            string scope = monitorDevice.Length == 0 ? string.Empty : "  " + monitorDevice.ToUpperInvariant();
            Flash(preset.Name + scope + " applied");
        }
    }


    private void OnApplyScreenClick(object sender, RoutedEventArgs e)
    {
        ApplyDisplay(_workDisplay.Copy(), true);
    }


    /// <summary>
    /// Renames the loaded preset in place when it is one of your own, and refuses
    /// for a built in so the shipped presets can never be edited. Use the plus
    /// button to copy a built in one first.
    /// </summary>
    private void OnRenameScreenClick(object sender, RoutedEventArgs e)
    {
        DisplayPreset? mine = _settings.CustomDisplayPresets
            .FirstOrDefault(p => string.Equals(p.Id, _activeDisplayId, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("Built in presets cannot be renamed", true);
            return;
        }

        ShowModal("RENAME SCREEN PRESET", mine.Name, name =>
        {
            mine.Name = name;
            _workDisplay.Name = name;
            _activeDisplayId = mine.Id;
            Commit();
            RefreshPresetBoxes();
            UpdateScreenLabels(_workDisplay);
            UpdatePresetChrome();
            BuildSlots();
        });
    }


    /// <summary>
    /// The one definition of "back to normal" for the screen. The reset button,
    /// the tray menu and the global off hotkey all call this, so they cannot
    /// drift apart.
    ///
    /// The hardware goes back to the ramp it had before Gamer Tool touched it,
    /// and the panel loads Standard, so the two can never disagree. Standard is a
    /// real built-in, so the dropdown lands on it instead of going blank and
    /// reading as "Unsaved tune".
    /// </summary>
    public void GoScreenNeutral()
    {
        _display.Reset();
        _liveDisplayName = "STANDARD";
        _activeDisplayId = "flat";

        // Taking the screen back to neutral by hand takes over from the app, so
        // the auto-apply guard is stood down here. Without this, a slot the app
        // loaded would still be remembered as ours after the user had reset it,
        // and quitting that game would run a second, redundant revert on top of
        // a screen the user had already put back themselves.
        _autoSlotId = string.Empty;
        _autoProcess = string.Empty;

        LoadTune(DisplayPreset.Flat(), _workAudio);
        RefreshPresetBoxes();
        UpdateLiveLabels();
        UpdateScreenLabels(_workDisplay);
    }


    private void OnResetScreenClick(object sender, RoutedEventArgs e)
    {
        GoScreenNeutral();
        Flash("Screen back to normal");
    }

}
