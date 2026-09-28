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
        _settings.HardwareBrightnessEnabled = HardwareBrightnessBox.IsChecked == true;
        _display.SetLock(_settings.GammaLock);

        // Switching this on is the only thing that ever starts a probe, and the
        // probe runs off the UI thread because it talks to a monitor over I2C.
        if (_settings.HardwareBrightnessEnabled && !Backlight.HasProbed)
        {
            _ = Task.Run(() =>
            {
                Backlight.Probe();
                Dispatcher.InvokeAsync(RefreshBacklightRows);
            });
        }
        else
        {
            RefreshBacklightRows();
        }

        ApplyWatchState();
        Commit();
    }


    private void OnStartupOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        bool wanted = StartWithWindowsBox.IsChecked == true;
        bool ok = _startup.SetEnabled(wanted);
        _settings.StartWithWindows = ok && wanted;
        Commit();

        if (!ok && wanted)
        {
            StartWithWindowsBox.IsChecked = false;
            Flash("Could not enable, try again as admin", true);
        }
    }


    private void ApplyWatchState()
    {
        bool wanted = _settings.AutoSwitch && _settings.Slots.Any(s => s.Enabled && s.AutoActivate);
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

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        HotkeyModifiers mods = HotkeyService.CurrentModifiers();
        if (mods == HotkeyModifiers.None)
        {
            _captureBox.Text = "HOLD CTRL OR ALT";
            e.Handled = true;
            return;
        }

        string text = HotkeyService.FromInput(e.Key, mods);

        if (_captureSlotId is null)
        {
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

        _captureBox.Text = text;
        _captureSlotId = null;
        Commit();
        BuildSlots();
        RegisterHotkeys();
        Flash(text + " set");
        e.Handled = true;
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
        _ = UpgradeFxSoundAsync();
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

        _audioPreview.Pause();
        _previewFrameTimer?.Stop();
        _diagFeedbackTimer?.Stop();
        Commit();
        _tray?.Dispose();
        _tray = null;
        _hotkeys.Dispose();
        _watcher.Stop();
        _display.StopLock();
        _audioPreview.Dispose();
        _spectrum?.Release();
        EmergencyReset.Run();
        if (_stateTimer is not null)
        {
            _stateTimer.Stop();
            _stateTimer = null;
        }

        Application.Current.Shutdown();
    }


}
