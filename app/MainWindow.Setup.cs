using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>
    /// True while an install or an update is running. Holds the page disabled,
    /// the modal on its progress state with no way to dismiss it, and the window
    /// itself from closing, so the run cannot be interrupted half way.
    /// </summary>
    private bool _installBusy;


    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.GammaLock = GammaLockBox.IsChecked == true;
        _settings.ShowOsd = OsdBox.IsChecked == true;
        _settings.AutoSwitch = AutoSwitchBox.IsChecked == true;
        _settings.AutoRevertOnExit = AutoRevertBox.IsChecked == true;
        _settings.FxPromptDisabled = FxPromptBox.IsChecked == true;
        _settings.StartHidden = StartHiddenBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        SyncNightScheduleControls();

        bool wasEnabled = _settings.HardwareBrightnessEnabled;
        _settings.HardwareBrightnessEnabled = HardwareBrightnessBox.IsChecked == true;
        _display.SetLock(_settings.GammaLock);

        // Off to on is the way back from a monitor that was written off, and from a
        // feature the app turned off by itself. The exclusion list is persisted, so
        // without this a single bad minute left a display permanently greyed out
        // with no route back except editing settings.json by hand, and the HasProbed
        // guard below meant switching off and on again did not even re-probe.
        if (_settings.HardwareBrightnessEnabled && !wasEnabled)
        {
            Backlight.ForgetExclusions();
            Backlight.Availability.Restore();

            // Cleared on disk as well as in memory. The note is driven off the
            // persisted flag, so leaving it set would put the "turned off
            // automatically" line back under a switch the user has just turned on
            // themselves.
            _settings.HardwareBrightnessRetired = false;
            _settings.HardwareBrightnessFailedRounds = 0;
        }

        // Switching this on is the only thing that ever starts a probe, and the
        // probe runs off the UI thread because it talks to a monitor over I2C.
        if (_settings.HardwareBrightnessEnabled && !Backlight.HasProbed)
        {
            _ = Task.Run(() =>
            {
                Backlight.Probe();
                Dispatcher.InvokeAsync(() =>
                {
                    // Deliberately not doing the retirement check here. It is what
                    // AfterBacklightProbe is for, and calling it here as well meant
                    // every retirement was handled twice on this path - two log
                    // lines for one event, and the switch moved twice.
                    AfterBacklightProbe();
                });
            });
        }
        else
        {
            RefreshBacklightRows();
            RefreshBacklightNote();
        }

        ApplyWatchState();
        Commit();
    }

    /// <summary>
    /// Everything that has to happen on the UI thread once a probe has finished.
    /// <para>
    /// Shared by both probe call sites rather than written twice. The tab switch
    /// had its own copy that only refreshed the rows, so a probe started by
    /// opening the Display tab could retire the feature without the switch ever
    /// moving: the second round that triggers retirement is almost always reached
    /// by opening the tab, so that is precisely the path that missed it.
    /// </para>
    /// </summary>
    private void AfterBacklightProbe()
    {
        // The probe may have decided the feature is not worth offering on this
        // machine at all. Acting on it here rather than inside the probe keeps the
        // settings write and the repaint on the UI thread, where they belong.
        if (Backlight.ConsumeRetirement())
        {
            _settings.HardwareBrightnessEnabled = false;
            HardwareBrightnessBox.IsChecked = false;
        }

        // The counter is persisted by the probe itself, so this is what saves it.
        if (Backlight.ConsumeSettingsChanged())
        {
            Commit();
        }

        RefreshBacklightRows();
        RefreshBacklightNote();
    }


    /// <summary>
    /// The info badge beside the hardware brightness switch, and nothing at all
    /// when there is nothing to explain.
    /// <para>
    /// A badge rather than a line of text. The amber sentence that used to sit
    /// under the switch was its own row in the middle of a list of switches, it
    /// pushed the last row of the settings list under the fold, and it started a
    /// scrollbar on a tab that had never needed one, which is a worse problem
    /// than the one it was explaining. The switch it belongs to is labelled in
    /// plain English on the same row, so the badge only has to say that there is
    /// a reason to ask.
    /// </para>
    /// </summary>
    private void RefreshBacklightNote()
    {
        if (HardwareBrightnessBox is null)
        {
            return;
        }

        // Set on the control rather than on a separate element, so the template
        // collapses the badge and its column in one step and there is no way for
        // a glyph to be visible with nothing to say. The tooltip is static copy
        // and lives with the rest of the row's copy in the XAML.
        HardwareBrightnessBox.BadgeGlyph = Backlight.Availability.Retired
            ? "info"
            : string.Empty;
    }

    /// <summary>
    /// The full explanation, in a dialog.
    /// <para>
    /// Says what was measured and what was not, and deliberately names no
    /// particular setting to change. It used to, and it was confidently wrong on
    /// real hardware: the steps it gave were for a graphics setting that was
    /// already correct, and following them cost several restarts and changed
    /// nothing. There is also no way for this app to tell whether following advice
    /// helped, so anything it offers is a guess wearing a numbered list.
    /// </para>
    /// <para>
    /// No warning block, and no closing line about turning it back on. The dialog
    /// was three paragraphs and an amber triangle for a fact that is already on
    /// screen: the switch this explains is right there, off, with a badge on its
    /// own row inviting the question. Telling somebody they can flip a visible
    /// switch back on is noise, and a caution icon on a dialog that only
    /// describes something that already happened makes it read as a fault.
    /// </para>
    /// </summary>
    private void OnHardwareBrightnessWhy(object sender, RoutedEventArgs e)
    {
        ShowInfoModal(
            "Hardware brightness was turned off",
            Backlight.Availability.Note,
            string.Empty);
    }


    private void OnStartupOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        bool wanted = StartWithWindowsBox.IsChecked == true;
        bool ok = _startup.SetEnabled(wanted);
        Commit();

        if (!ok && wanted)
        {
            StartWithWindowsBox.IsChecked = false;

            // No mention of being an administrator. The key is under the current
            // user, which never needs elevation, so "try again as admin" named a
            // remedy that cannot possibly be the answer and would have sent
            // somebody off to run a portable app elevated for no reason. The
            // realistic causes are a locked-down profile folder or a policy, and
            // the user can also just tick the box in Task Manager's Startup tab.
            Flash("Could not write the startup entry", true);
        }
    }


    private void ApplyWatchState()
    {
        bool wanted = _settings.AutoSwitch && _settings.Slots.Any(s => s.Enabled && s.AutoActivate);

        // Cleared on the way into both arms, not only when arming. The watcher
        // remembers the last foreground window so it can raise a change once
        // rather than on every tick, and that memory outlives a stop: without
        // this, turning auto-switch off and on again while sitting in a game
        // re-armed the watcher with the game's window still recorded, and the
        // next tick read the same window and either missed the change or, worse,
        // matched a slot again for a game the user had already left.
        _watcher.Reset();

        if (wanted)
        {
            _watcher.PrimeProcesses(SlotService.TargetProcessNames(_settings.Slots));
            _watcher.Start(1500);
        }
        else
        {
            _watcher.Stop();
        }
    }


    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureBox is null)
        {
            return;
        }

        // With Alt held, WPF reports Key.System and hides the real key in
        // SystemKey. Reading e.Key directly is what produced "ALT+SYSTEM".
        Key key = HotkeyService.ResolveKey(e.Key, e.SystemKey);
        HotkeyModifiers mods = HotkeyService.CurrentModifiers();

        switch (HotkeyService.ClassifyCapture(key, mods))
        {
            case HotkeyCapture.NeedsModifier:
                // A lone modifier is not a mistake, so the prompt stays put. A
                // bare character is worth hinting about, because it will not bind.
                if (!HotkeyService.IsModifierKey(key))
                {
                    _captureBox.Text = "MOD + KEY";
                }

                e.Handled = true;
                return;

            case HotkeyCapture.Cancel:
                EndCapture(restore: true);
                e.Handled = true;
                return;

            case HotkeyCapture.Clear:
                if (_capturingPanic)
                {
                    ClearPanicHotkey();
                }
                else
                {
                    ClearCapturedHotkey();
                }

                e.Handled = true;
                return;

            case HotkeyCapture.Bind:
                break;

            default:
                // A case nobody has thought about should not bind something.
                EndCapture(restore: true);
                e.Handled = true;
                return;
        }

        string text = HotkeyService.FromInput(key, mods);
        if (text.Length == 0)
        {
            EndCapture(restore: true);
            e.Handled = true;
            return;
        }

        // Checked before the slot id, because the panic keycap deliberately has
        // no slot behind it.
        if (_capturingPanic)
        {
            BindPanicHotkey(text);
            e.Handled = true;
            return;
        }

        if (_captureSlotId is null)
        {
            EndCapture(restore: true);
            e.Handled = true;
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == _captureSlotId);
        if (slot is not null)
        {
            // Slots steal a contested key: the one you are editing wins, because
            // that is the one you are actively pointing at.
            string wanted = HotkeyService.Normalise(text);
            HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
                s.Id != slot.Id
                && s.Enabled
                && HotkeyService.Normalise(s.Hotkey) == wanted);

            if (clash is not null)
            {
                clash.Hotkey = string.Empty;
                Flash("Key moved, taken from " + clash.Name);
            }

            slot.Hotkey = text;
        }

        // Leave capture before rebuilding. The list is about to be recreated, and
        // staying armed over a box that is about to be thrown away is how a good
        // bind used to get overwritten by the next key pressed.
        EndCapture(restore: false);
        Commit();
        BuildSlots();
        RegisterHotkeys();
        Flash(text + " set");
        e.Handled = true;
    }


    /// <summary>
    /// Leaves listening mode. With <paramref name="restore"/> the keycap gets the
    /// slot's saved binding back, which is what cancelling means. Either way focus
    /// is dropped, so the next keystroke is not swallowed by a stale capture.
    /// </summary>
    private void EndCapture(bool restore)
    {
        if (restore && _captureBox is not null)
        {
            if (_capturingPanic)
            {
                ShowSlotKey(_captureBox, _settings.EmergencyHotkey);
            }
            else if (_captureSlotId is not null)
            {
                HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == _captureSlotId);
                ShowSlotKey(_captureBox, slot?.Hotkey);
            }
        }

        _captureSlotId = null;
        _captureBox = null;
        _capturingPanic = false;
        Keyboard.ClearFocus();
    }


    /// <summary>Backspace or Delete on a listening keycap drops the binding.</summary>
    private void ClearCapturedHotkey()
    {
        if (_captureSlotId is not null)
        {
            HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == _captureSlotId);
            if (slot is not null && !string.IsNullOrWhiteSpace(slot.Hotkey))
            {
                slot.Hotkey = string.Empty;
                Flash("Key cleared");
            }
        }

        EndCapture(restore: false);
        Commit();
        BuildSlots();
        RegisterHotkeys();
    }


    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        _ = InstallAsync(true);
    }


    private void OnDirectInstallClick(object sender, RoutedEventArgs e)
    {
        _ = InstallAsync(false);
    }


    private void OnUpgradeFxSoundClick(object sender, RoutedEventArgs e)
    {
        // Two jobs, decided by what is already known. The banner's button says
        // "Check update" until a check has happened, and pressing it then is an
        // explicit request to go and look - which is the only path in the app that
        // touches the network for anything other than an install the user asked
        // for. Once an update is known the same button performs it.
        if (_fxUpdateAvailable)
        {
            _ = UpgradeFxSoundAsync();
            return;
        }

        _ = CheckFxSoundUpdateAsync();
    }


    private async System.Threading.Tasks.Task InstallAsync(bool useWinget)
    {
        if (_installBusy)
        {
            return;
        }

        _install?.Cancel();
        _install = new CancellationTokenSourceHolder();

        // Set before the await and cleared in the finally, not just around the
        // happy path. Anything thrown between here and the end used to leave the
        // page greyed out for the rest of the session with no way back.
        _installBusy = true;
        Pages.IsEnabled = false;
        RailStatus.Text = useWinget ? "INSTALLING FXSOUND" : "DOWNLOADING FXSOUND";
        ShowProgressModal("INSTALLING FXSOUND", showCaution: true);

        System.Progress<SetupStage> progress = new(stage =>
        {
            RailStatus.Text = "FXSOUND " + stage.Text;
            SetModalProgress(stage);
        });

        bool ok = false;
        try
        {
            ok = useWinget
                ? await _setup.InstallBestAsync(_audio, progress, _install.Token)
                : await _setup.DownloadAndInstallAsync(_audio, progress, _install.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("INSTALL", ex);
        }
        finally
        {
            Pages.IsEnabled = true;
            _installBusy = false;
        }

        UpdateFxBanner();
        if (!ok)
        {
            if (useWinget)
            {
                // winget was tried and did not manage it, and it is not quietly
                // retried by another route. The reason is almost always winget's
                // own plumbing rather than anything to do with FxSound, so that is
                // what the message says, and taking the other route is a choice
                // rather than something that happens on its own.
                ShowResultModal(
                    "INSTALL FAILED",
                    "winget could not install FxSound. That is usually the network, the package "
                    + "source, or winget itself. You can try again without winget, or install "
                    + "FxSound yourself and Gamer Tool will find it.",
                    failed: true,
                    "Try without winget",
                    () => _ = InstallAsync(false),
                    "Close",
                    () => { });
            }
            else
            {
                ShowResultModal(
                    "INSTALL FAILED",
                    "FxSound did not install. The log has the details, and the settings tab can try again.",
                    failed: true,
                    "Try again",
                    () => _ = InstallAsync(false),
                    "Close",
                    () => { });
            }

            Flash("Install failed, try again", true);
            return;
        }

        _fxUpdateAvailable = false;
        _fxUpdateVersion = string.Empty;

        // The rest of a fresh launch, without the fresh launch. AdoptInstalledPath
        // has already re-resolved the exe, and this is everything else the app
        // does at startup that touches the engine. It waits for the reads rather
        // than blocking, because the dialog below claims the app is already
        // talking to the engine and that claim should not be made ahead of the
        // work that backs it.
        bool reached = await HandshakeWithEngineAsync("FxSound ready, sound is live");

        if (!reached)
        {
            // The install itself worked, so this is not a failure. But the dialog
            // is not going to claim the app is already talking to the engine when
            // the handshake says otherwise: a restart usually clears it, and if it
            // does not, the settings tab can retry without reinstalling.
            ShowResultModal(
                "FXSOUND INSTALLED",
                "FxSound installed, but Gamer Tool could not reach it yet. This often clears on "
                + "restart. If it does not, the settings tab can try again.",
                failed: false,
                "Restart Gamer Tool",
                RestartSelf,
                "Not now",
                () => { });
            return;
        }

        // Restarting is offered, not imposed, and not claimed to be required: the
        // apply above is verified against the engine, so if the engine is not
        // taking the preset the app says so rather than the user having to notice.
        ShowResultModal(
            "FXSOUND INSTALLED",
            "FxSound is installed and Gamer Tool is already talking to it. Restarting is the "
            + "safest way to be sure the new audio engine is picked up cleanly.",
            failed: false,
            "Restart Gamer Tool",
            RestartSelf,
            "Not now",
            () => { });
    }


    /// <summary>
    /// Hands the running app over to a fresh one.
    /// <para>
    /// The single-instance claim is given up first, and that is the whole trick.
    /// The mutex is held by this process until it exits, so a replacement started
    /// first would find it taken, tell the user Gamer Tool is already running and
    /// quit, leaving nothing on screen at all. The claim is owned by the thread
    /// that created it, which is the UI thread this runs on, so it can simply be
    /// released. Nothing is lost by letting go of it: this process is on its way
    /// out either way.
    /// </para>
    /// </summary>
    private void RestartSelf()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            ShowResultModal(
                "COULD NOT RESTART",
                "Gamer Tool could not find its own executable to restart. Close it and open it again.",
                failed: true,
                "Close",
                () => { _quitting = true; Application.Current.Shutdown(); },
                "Stay",
                () => { });
            return;
        }

        try
        {
            ((App)Application.Current).ReleaseSingleInstanceClaim();
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("RESTART", ex);
            ShowResultModal(
                "COULD NOT RESTART",
                "Gamer Tool could not start a replacement process. Close it and open it again.",
                failed: true,
                "Close",
                () => { _quitting = true; Application.Current.Shutdown(); },
                "Stay",
                () => { });
            return;
        }

        _quitting = true;
        Application.Current.Shutdown();
    }


    private async System.Threading.Tasks.Task UpgradeFxSoundAsync()
    {
        if (_installBusy)
        {
            return;
        }

        _install?.Cancel();
        _install = new CancellationTokenSourceHolder();

        _installBusy = true;
        Pages.IsEnabled = false;
        RailStatus.Text = "UPDATING FXSOUND";
        FxUpgradeButton.IsEnabled = false;
        ShowProgressModal("UPDATING FXSOUND", showCaution: true);

        System.Progress<SetupStage> progress = new(stage =>
        {
            RailStatus.Text = "FXSOUND " + stage.Text;
            SetModalProgress(stage);
        });

        bool ok = false;
        try
        {
            ok = await _setup.UpgradeAsync(_audio, progress, _install.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("UPGRADE", ex);
        }
        finally
        {
            Pages.IsEnabled = true;
            FxUpgradeButton.IsEnabled = true;
            _installBusy = false;
        }

        UpdateFxBanner();
        if (!ok)
        {
            ShowResultModal(
                "UPDATE FAILED",
                "FxSound did not update. The log has the details, and the settings tab can try again.",
                failed: true,
                "Try again",
                () => _ = UpgradeFxSoundAsync(),
                "Close",
                () => { });
            return;
        }

        _fxUpdateAvailable = false;
        _fxUpdateVersion = string.Empty;

        // The update landed but the handshake is what says the app can actually
        // use it, so the two are reported separately rather than the update being
        // called done on the strength of the installer alone.
        if (await HandshakeWithEngineAsync("FxSound updated"))
        {
            return;
        }

        ShowResultModal(
            "UPDATED, NOT CONNECTED",
            "FxSound updated, but Gamer Tool could not reach it yet. This often clears on restart.",
            failed: false,
            "Restart Gamer Tool",
            RestartSelf,
            "Not now",
            () => { });
    }



    private void OnStartEngineClick(object sender, RoutedEventArgs e)
    {
        if (_audio.IsInstalled)
        {
            _audio.StartEngine();
            ApplyAudio(_workAudio.Copy(), false);
            _ = RefreshFxStateAsync(true);
        }
        else
        {
            _ = InstallAsync(true);
        }
    }


    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // An install or an update owns the modal and cannot be dismissed, so the
        // window is not allowed to go either: quitting mid-run would delete the
        // installer out from under the process still executing it. The tray Quit
        // item is the way out if it truly wedges, and winget and the download both
        // carry their own timeouts.
        if (_installBusy && !_quitting)
        {
            e.Cancel = true;
            return;
        }

        if (!_quitting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Past this point the window really is closing, so the fault handlers stop
        // interrupting. Set here rather than in the close-to-tray branch above,
        // which is not a shutdown at all.
        _quitting = true;
        SessionState.Current.ShuttingDown = true;

        _audioPreview.Pause();
        _previewFrameTimer?.Stop();
        _diagFeedbackTimer?.Stop();
        _previewTimer.Stop();
        StopNightScheduleTimer();
        _tray?.Dispose();
        _tray = null;
        _hotkeys.Dispose();
        _watcher.Stop();
        _display.StopLock();
        _audioPreview.Dispose();
        _spectrum?.Release();
        EmergencyReset.Run();

        // Written after the restore, not before it, and the order is the whole
        // point. Restoring the backlight empties OriginalHardwareBrightness, which
        // is what records the value each display was found at. Saving first left
        // that map on disk with the session's entries still in it, and the next
        // launch trusted them: TrySet only captures a display's brightness when
        // the map has nothing for it, so a stale entry from a previous session
        // suppressed the capture, and a restore three sessions later dragged a
        // monitor back to a brightness that had nothing to do with this one.
        // Saving afterwards persists the empty map, which is the honest record
        // that there is nothing outstanding to put back.
        Commit();

        if (_stateTimer is not null)
        {
            _stateTimer.Stop();
            _stateTimer = null;
        }

        Application.Current.Shutdown();
    }


}
