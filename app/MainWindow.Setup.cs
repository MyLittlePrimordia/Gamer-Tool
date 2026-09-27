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
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.GammaLock = GammaLockBox.IsChecked == true;
        _settings.ShowOsd = OsdBox.IsChecked == true;
        _settings.AutoSwitch = AutoSwitchBox.IsChecked == true;
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
        _install?.Cancel();
        _install = new CancellationTokenSourceHolder();

        Pages.IsEnabled = false;
        RailStatus.Text = useWinget ? "INSTALLING FXSOUND" : "DOWNLOADING FXSOUND";

        System.Progress<SetupStage> progress = new(stage => RailStatus.Text = "FXSOUND " + stage.Text);

        bool ok = useWinget
            ? await _setup.InstallBestAsync(_audio, progress, _install.Token)
            : await _setup.DownloadAndInstallAsync(_audio, progress, _install.Token);

        Pages.IsEnabled = true;
        UpdateFxBanner();
        if (ok)
        {
            _fxUpdateAvailable = false;
            _fxUpdateVersion = string.Empty;
            LoadDevices();
            _ = RefreshFxStateAsync(true);
            UpdateFxBanner();
            ApplyAudio(_workAudio.Copy(), false);
            Flash("FxSound ready, sound is live");
        }
        else
        {
            Flash("Install failed, try again", true);
        }
    }


    private async System.Threading.Tasks.Task UpgradeFxSoundAsync()
    {
        _install?.Cancel();
        _install = new CancellationTokenSourceHolder();

        Pages.IsEnabled = false;
        RailStatus.Text = "UPDATING FXSOUND";
        FxUpgradeButton.IsEnabled = false;

        System.Progress<SetupStage> progress = new(stage => RailStatus.Text = "FXSOUND " + stage.Text);
        bool ok = await _setup.UpgradeAsync(_audio, progress, _install.Token);

        Pages.IsEnabled = true;
        FxUpgradeButton.IsEnabled = true;
        if (ok)
        {
            _fxUpdateAvailable = false;
            _fxUpdateVersion = string.Empty;
            _audio.InvalidateCache();
            LoadDevices();
            _ = RefreshFxStateAsync(true);
            UpdateFxBanner();
            ApplyAudio(_workAudio.Copy(), false);
            Flash("FxSound updated to the latest");
        }
        else
        {
            UpdateFxBanner();
            Flash("Update failed, try again", true);
        }
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
        if (!_quitting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _audioPreview.Pause();
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
