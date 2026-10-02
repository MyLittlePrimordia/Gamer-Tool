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
    /// <summary>
    /// The screen the last applied tune went to, so something that has to push
    /// the same tune again - the night schedule putting the blue light filter on,
    /// or taking it off - lands on the monitor the user was actually looking at.
    /// <para>
    /// Without it, the only value available is "no particular monitor", which
    /// applies the tune to every screen. For the schedule that is the wrong
    /// answer twice over: a two screen user with a night filter has the trim
    /// appear on the display they were not using, and turning it off at seven in
    /// the morning silently overwrites whatever the other monitor is showing.
    /// </para>
    /// </summary>
    private string _appliedMonitor = string.Empty;

    private void UpdateScreenLabels(DisplayPreset preset)
    {
        _workDisplay = preset;
        GammaValue.Text = preset.Gamma.ToString("0.00", CultureInfo.InvariantCulture);
        ShadowValue.Text = Signed(preset.ShadowBoost, "0") + "%";
        BrightValue.Text = Signed(preset.Brightness, "0") + "%";
        ContrastValue.Text = Signed(preset.Contrast, "0") + "%";

        // The three channel trims, which had no readout at all and so were the one
        // control in this panel that could only be set by eye. A gain of 1.12 against
        // 1.00 is a real, saveable, shareable setting that you could write down but
        // not read back off the screen, and the other four sliders above have shown
        // their numbers the whole time. Two decimals because that is the step the
        // faders snap to, so the text is the value rather than a rounded version of
        // it.
        RedValue.Text = preset.RedGain.ToString("0.00", CultureInfo.InvariantCulture);
        GreenValue.Text = preset.GreenGain.ToString("0.00", CultureInfo.InvariantCulture);
        BlueValue.Text = preset.BlueGain.ToString("0.00", CultureInfo.InvariantCulture);

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
        return DisplayPreset.WithBlueLight(_workDisplay, ActiveBlueLightLevel);
    }


    private void OnBlueLightChanged(object sender, SelectionChangedEventArgs e)
    {
        // _updating, because this is the one selection in the app that the app
        // itself moves. The night schedule writes the level it is applying into
        // this box so the dropdown and the screen agree, and without the guard
        // that write came straight back down here and was saved as the user's
        // own choice. The filter would then be stuck on EXTRA WARM for good the
        // first time the schedule ever fired, because the value the user picked
        // would have been overwritten by the value the schedule happened to want.
        if (!_ready || _updating || BlueLightBox.SelectedItem is not string name)
        {
            return;
        }

        int level = Array.IndexOf(DisplayPreset.BlueLightNames, name);
        if (level < 0)
        {
            return;
        }

        _settings.BlueLightFilter = level;

        if (_nightApplied)
        {
            // A level picked by hand while the schedule is running is the user
            // overriding it, and it goes to the screen now. At every other time
            // this dropdown only previews, and the apply button is what pushes
            // it, which is the contract the whole Display tab has.
            StandNightFilterDown();
        }
        else
        {
            QueueDisplayPreview();
        }

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


    /// <summary>
    /// Applies a screen preset, and records it only if the screen took it.
    /// <para>
    /// The return value of the display service used to be thrown away, so a
    /// rejected gamma ramp still set the live label, still wrote the active
    /// preset id to the profile and still committed it. A driver that refuses
    /// SetDeviceGammaRamp - protected content, a remote session, a driver with
    /// the feature off - therefore left the app claiming a preset it had not put
    /// on the screen, and that claim survived a restart because it had been
    /// persisted. Worse, the service sets its "a ramp was taken" flag only on
    /// success, so the emergency reset on the way out had nothing to undo while
    /// the app believed it had applied something.
    /// </para>
    /// <para>
    /// So the write is checked, and everything that asserts the preset is live
    /// sits inside that. The labels, the profile and the commit all describe what
    /// is on the screen, which means they now agree with each other in the case
    /// where the screen refused.
    /// </para>
    /// </summary>
    private bool ApplyDisplay(DisplayPreset preset, string monitorDevice, bool announce)
    {
        DisplayPreset effective = DisplayPreset.WithBlueLight(preset, ActiveBlueLightLevel);
        if (!_display.Apply(effective, monitorDevice))
        {
            // The service has already logged what the driver said. This says the
            // same thing where the user is looking, because a slider that did
            // nothing and a panel that says it worked is the worst of both.
            Flash(preset.Name + " was blocked by this display", true);
            TraceLog.Write("DISPLAY refused preset=" + preset.Id + " monitor=" + monitorDevice);
            return false;
        }

        // The panel, after the ramp rather than with it. Two reasons for that
        // order: the gamma write is what can be refused outright, and there is no
        // point moving a monitor's brightness for a preset that never reached the
        // screen; and a bus write can take a second, which is time the user would
        // spend looking at a screen that has already changed while the app is still
        // working on the part they cannot see.
        ApplyPresetBacklight(effective, monitorDevice, announce);

        _appliedMonitor = monitorDevice;
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

        return true;
    }


    /// <summary>
    /// Sets the hardware backlight for a preset that carries one, on a worker.
    /// <para>
    /// Fire and forget rather than awaited. A DDC write can take up to two seconds
    /// when a scaler is slow, and this sits directly behind a key press or a game
    /// launch - so blocking the dispatcher on it would freeze the window on the
    /// app's primary workflow. The gamma half has already landed, so there is
    /// nothing to roll back if this does not.
    /// </para>
    /// <para>
    /// Null means leave the panel alone, which is the case that matters most: every
    /// preset that existed before this field, and Standard, are null. Nothing is
    /// written for them at all - not a read, not a write, not a capture of the
    /// current value. That is what keeps a slot toggle-off from dragging a monitor
    /// back to a brightness the user has since changed by hand.
    /// </para>
    /// </summary>
    private void ApplyPresetBacklight(DisplayPreset preset, string monitorDevice, bool announce)
    {
        if (!Backlight.IsEnabled || preset.Backlight is not { } wanted)
        {
            // Deliberately leaves the ownership flag alone rather than clearing it.
            // This preset is not touching the panel, but the panel may still be
            // sitting at a value an earlier preset put there, and forgetting that
            // would strand it: A (with a backlight) writes 40, B (without one)
            // loads and leaves the panel at 40, and toggling B off would then
            // decide nobody owns it and leave a monitor dimmed by this app for
            // the rest of the session.
            //
            // So the flag means "the app has a panel value outstanding that it
            // wrote", which is a statement about the hardware rather than about the
            // preset file in front of it. On a machine where no preset has ever
            // named a brightness it is false, and a toggle-off leaves the panel
            // exactly as the user set it in Windows - which is the case that
            // matters, and the reason the backlight value is optional at all.
            return;
        }

        // The slot's own screen, or the first one that can be driven. A preset
        // scoped to a display that will not answer has nothing to write to, and
        // writing to some other screen instead would be a genuinely surprising
        // thing for a preset to do.
        MonitorProbe? target = FindBacklightTarget(monitorDevice);
        if (target is null)
        {
            TraceLog.Write("backlight preset skipped: no display can take it, monitor=" + monitorDevice);
            return;
        }

        uint value = Math.Clamp(wanted, target.Brightness!.Minimum, target.Brightness.Maximum);

        // Claimed before the write, not after it succeeds. The write is on a worker
        // and can come back Busy, refused, or never; a preset that asked for a
        // brightness and did not get it still wants it put back the moment anything
        // moves the panel again, and a stand-down in between has to know the
        // difference between "owns the panel" and "never heard of a panel".
        _presetOwnedPanelDevice = target.DeviceName;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            BusOutcome outcome = Backlight.TrySetPresetBacklight(target, value, out string? why);
            bool settingsChanged = Backlight.ConsumeSettingsChanged();

            Dispatcher.InvokeAsync(() =>
            {
                if (settingsChanged)
                {
                    Commit();
                }

                // Said only on failure, and only when the user asked for this
                // preset by hand. A slot firing in the background must not put a
                // toast over the top of a game, and the profile is already showing
                // the gamma half which did land.
                if (outcome == BusOutcome.Ok || !announce)
                {
                    return;
                }

                TraceLog.Write("backlight preset write " + outcome.ToString()
                    + (string.IsNullOrEmpty(why) ? string.Empty : ": " + why));

                Flash("The panel brightness did not take", true);
            });
        });
    }

    /// <summary>
    /// The display a preset's backlight should go to.
    /// <para>
    /// The slot's own screen when it names one that can be driven, and otherwise
    /// the first that can - so a preset scoped to a monitor with no DDC/CI pathway
    /// still sets the panel on a laptop screen rather than doing nothing at all.
    /// That is the common single-laptop case.
    /// </para>
    /// </summary>
    private MonitorProbe? FindBacklightTarget(string monitorDevice)
    {
        foreach (MonitorProbe monitor in Backlight.Monitors)
        {
            if (!monitor.CanControlBacklight)
            {
                continue;
            }

            if (monitorDevice.Length == 0
                || string.Equals(monitor.DeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }

        return null;
    }

    private void OnApplyScreenClick(object sender, RoutedEventArgs e)
    {
        // The panel value is captured here rather than written, because this is the
        // only place a user says "the screen should look like this". A slot press or
        // a game launch replays a stored value; pressing Apply on a hand-tuned screen
        // is a statement about the panel as it is right now.
        CaptureBacklightIntoWorking();
        ApplyDisplay(_workDisplay.Copy(), true);
    }

    /// <summary>
    /// Copies the live backlight reading into the working preset.
    /// <para>
    /// Only when there is exactly one display that can be driven. With two, "the"
    /// brightness is ambiguous and picking one silently would be worse than not
    /// offering the capture at all - the user would have a preset whose backlight
    /// means one particular monitor with nothing on screen saying which.
    /// </para>
    /// <para>
    /// Left null when nothing can be read, so the preset keeps saying "leave the
    /// panel alone" rather than recording a zero that would black out a display.
    /// </para>
    /// </summary>
    private void CaptureBacklightIntoWorking()
    {
        if (!Backlight.IsEnabled)
        {
            return;
        }

        MonitorProbe[] usable = Backlight.Monitors.Where(m => m.CanControlBacklight).ToArray();

        if (usable.Length == 0)
        {
            _workDisplay.Backlight = null;
            return;
        }

        if (usable.Length > 1)
        {
            _workDisplay.Backlight = null;
            return;
        }

        // Re-read rather than trusting the array element's null-forgiving form:
        // CanControlBacklight is a computed property, so the compiler has no way to
        // know the reading is there, and a probe finishing on another thread between
        // the filter and here can make it genuinely absent.
        if (usable[0].Brightness is not { } reading)
        {
            _workDisplay.Backlight = null;
            return;
        }

        _workDisplay.Backlight = reading.Current;
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
    /// The one definition of "back to normal" for the screen: a hard reset.
    /// <para>
    /// The reset button, the tray menu, the global off hotkey and the panic key all
    /// call this, so they cannot drift apart. It restores the panel unconditionally
    /// because every one of those is the user asking for the whole screen back, and
    /// leaving the panel dim while the ramp went flat is the failure the panic key
    /// exists to undo.
    /// </para>
    /// </summary>
    public void GoScreenNeutral()
    {
        StandDownScreen(restorePanel: true);
    }


    /// <summary>
    /// Takes the screen back to normal because the thing that was driving it stopped
    /// - a slot toggled off, a game closed, a wildcard losing fullscreen.
    /// <para>
    /// Same flat gamma ramp as the hard reset, and deliberately a different answer
    /// for the panel, because the question is different. A hard reset is the user
    /// saying "put everything back"; this is the app tidying up after itself. If the
    /// preset that was loaded never named a brightness then it never touched the
    /// panel, and restoring one anyway would override a brightness the user had
    /// changed by hand in Windows since - which is the whole reason the backlight
    /// value is optional in a preset in the first place.
    /// </para>
    /// <para>
    /// When the preset did name a brightness, the panel is put back to where it was
    /// before the app found it. Leaving it at the game's value would mean the desktop
    /// is dimmer or brighter than the user set it every time they alt-tabbed out.
    /// </para>
    /// </summary>
    public void GoScreenStandDown()
    {
        StandDownScreen(restorePanel: _presetOwnsPanel);
    }


    /// <param name="restorePanel">
    /// Whether to put the hardware backlight back regardless of what the loaded
    /// preset asked for. True for the paths where the user is explicitly asking for
    /// a full reset.
    /// </param>
    private void StandDownScreen(bool restorePanel)
    {
        // The apply debounce, cleared here and nowhere else.
        //
        // It was being cleared on one deactivation path and not the others, which
        // is the same class of bug twice: the focus-loss revert cleared it, the
        // exit revert did not, and StandDownScreen stood the guard down by emptying
        // _autoSlotId and _autoProcess without touching the two second window it
        // also checks. So a game that closed and was relaunched inside two seconds
        // was rejected as a duplicate apply - the first guard missed because the
        // process field was now empty, the second caught it - and nothing re-checked
        // afterwards, because the watcher only raises on a foreground *change* and
        // the same window was still recorded.
        //
        // Here rather than at each call site because there are four of them and one
        // of them had already been forgotten once. Unconditional, so the early
        // return below for a ramp that would not come off still stands the guard
        // down.
        _autoStamp = DateTime.MinValue;

        // The monitor's own brightness is part of the screen going back to normal,
        // and this used to leave it alone. The ramp above is only half of what this
        // app can change: with hardware brightness switched on, the display is left
        // sitting at whatever the last slot asked for. That is worst exactly where
        // it hurts most - the panic key is the one control a user reaches for when
        // the picture is already wrong, and it restored the gamma and left the panel
        // dim.
        //
        // RestoreAll is a no-op when nothing was ever applied, because the map it
        // walks is only populated by a write this app made, and it empties that map
        // before writing so calling it twice cannot double-restore. So this is safe
        // on the paths where hardware brightness was never used, which is every
        // machine that has the feature switched off.
        if (restorePanel)
        {
            Backlight.RestoreAll();
        }

        // Only a reset of a ramp this app actually put there can fail. On a machine
        // that never applied one there is nothing to put back, Reset reports that,
        // and calling that a refusal would put an error on every reset that the
        // user never touched hardware to cause. IsEnabled is the service's own
        // answer to "is there a ramp of ours on a screen at the moment".
        bool hadRamp = _display.IsEnabled;
        bool restored = _display.Reset();
        if (hadRamp && !restored)
        {
            // A ramp that cannot be taken off is still out there. The identity is
            // deliberately left alone: the app does not know what the monitor is
            // showing now, so it must not go on recording that it is standard.
            // The service keeps its own flag set too, which is what gives the exit
            // path another turn at it.
            Flash("The screen would not take the reset", true);
            UpdateLiveLabels();
            return;
        }

        _appliedMonitor = string.Empty;
        _liveDisplayName = "STANDARD";
        _activeDisplayId = "flat";

        // Persisted as well as set in memory. This path is reached by the Reset
        // button, the panic key, and switching a loaded slot off, and all three of
        // those are the user choosing a preset. Only writing the in-memory field
        // left the remembered id pointing at whatever was applied before, so the
        // screen went to Standard on screen, the next Commit saved the old id, and
        // the app came back up on the previous preset. A reset that does not
        // survive a restart is the exact behaviour the remember-last-used setting
        // is supposed to prevent.
        _settings.ActiveDisplayPresetId = "flat";

        // Taking the screen back to neutral by hand takes over from the app, so
        // the auto-apply guard is stood down here. Without this, a slot the app
        // loaded would still be remembered as ours after the user had reset it,
        // and quitting that game would run a second, redundant revert on top of
        // a screen the user had already put back themselves.
        _autoSlotId = string.Empty;
        _autoProcess = string.Empty;

        // The wildcard's own record, for the same reason. Left set, the revert
        // would still fire when the program that triggered it closed, and this is
        // the path that stands the guard down for the bound case - so the wildcard
        // case has to be stood down here too or it becomes the one that reverts a
        // screen the user put back on purpose.
        //
        // Released before it is cleared, because the name only exists on this side
        // of that line. The game is very often still running at this point - this
        // is reached by toggling a slot off and by alt-tabbing away - and leaving
        // it watched would mean its eventual exit raised a revert for a preset that
        // had already been stood down.
        _watcher.ForgetUnbound(_autoWildcardProcess);
        _autoWildcardProcess = string.Empty;

        // Nothing owns the panel any more. Only cleared once the restore above has
        // had its answer, so a stand-down that skipped the restore because the
        // preset had no backlight does not also lose the memory of that, and a
        // preset applied afterwards starts from a clean answer again.
        _presetOwnedPanelDevice = string.Empty;

        LoadTune(DisplayPreset.Flat(), _workAudio);
        RefreshPresetBoxes();
        UpdateLiveLabels();
        UpdateScreenLabels(_workDisplay);

        // The write, once the UI agrees with it. Committing before the labels are
        // repainted would be harmless, but the point of the field above is that the
        // remembered preset and the dropdown can never disagree, and doing it last
        // keeps that true even if something above throws.
        Commit();
    }


    private void OnResetScreenClick(object sender, RoutedEventArgs e)
    {
        GoScreenNeutral();
        Flash("Screen back to normal");
    }


    /// <summary>
    /// Looks for attached screens again, on demand.
    /// <para>
    /// The automatic hook answers the cases Windows tells us about. This answers
    /// the one it cannot: a screen on the far side of a KVM or a dock the OS
    /// decided not to report a topology change for. It is the same affordance the
    /// audio tab already has beside its output picker, for the same reason - both
    /// enumerate hardware that can be swapped while the app is open, and neither
    /// has a way to notice without being told.
    /// </para>
    /// <para>
    /// The count is reported either way. A button that silently re-enumerates gives
    /// no way to tell a screen that was found from one that was not, and "no
    /// change" is indistinguishable from "the click did nothing".
    /// </para>
    /// </summary>
    private void OnRescanScreensClick(object sender, RoutedEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> before = _display.Monitors;

            _display.Rescan();

            IReadOnlyList<string> after = _display.Monitors;
            int count = after.Count;

            // Read before the rebuild, which is what destroys it. See
            // RunDisplayRescan for why this matters: the button is on the Display
            // tab, but the slot board it refreshes is on the Hotkeys tab, and a
            // user can be part-way through editing a row either way.
            string? focused = FocusedSlotTag();

            // The lists and the rows, because a slot's monitor dropdown is a
            // snapshot taken when the row was built. Without this the dropdown
            // keeps offering a screen that was unplugged an hour ago, which is
            // how someone ends up scoped to a display that is not there.
            RefreshPresetBoxes();
            BuildSlots();
            RestoreSlotFocus(focused);
            RefreshTraySlots();

            // And the ramp, because Windows has just discarded it. Only when there
            // is one of ours to put back - pushing with nothing applied would
            // write a flat ramp over a screen nobody had tuned.
            if (_display.ShouldDefend)
            {
                _display.Push();
            }

            int delta = count - before.Count;
            if (delta > 0)
            {
                Flash(delta.ToString(CultureInfo.InvariantCulture) + " more screen"
                    + (delta == 1 ? string.Empty : "s") + " found");
            }
            else if (delta < 0)
            {
                Flash((-delta).ToString(CultureInfo.InvariantCulture) + " screen"
                    + (delta == -1 ? string.Empty : "s") + " gone");
            }
            else
            {
                Flash(count.ToString(CultureInfo.InvariantCulture)
                    + (count == 1 ? " screen" : " screens") + " found");
            }

            TraceLog.Write("DISPLAY manual rescan: "
                + before.Count.ToString(CultureInfo.InvariantCulture)
                + " -> " + count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            TraceLog.Write("DISPLAY manual rescan", ex);
            Flash("Could not look for screens", true);
        }
    }

}
