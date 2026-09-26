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
        _display.SetLock(_settings.GammaLock);
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
            Flash("[ COULD NOT ENABLE ]", "TRY AGAIN OR RUN AS ADMIN");
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
        if (_captureSlotId is null || _captureBox is null)
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
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == _captureSlotId);
        if (slot is not null)
        {
            HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
                s.Id != slot.Id
                && s.Enabled
                && !string.IsNullOrWhiteSpace(s.Hotkey)
                && string.Equals(s.Hotkey, text, StringComparison.OrdinalIgnoreCase));

            if (clash is not null)
            {
                clash.Hotkey = string.Empty;
                Flash("[ KEY MOVED ]", "TAKEN FROM " + clash.Name.ToUpperInvariant());
            }

            slot.Hotkey = text;
        }

        _captureBox.Text = text;
        _captureSlotId = null;
        Commit();
        BuildSlots();
        RegisterHotkeys();
        Flash("[ KEY SET ]", text);
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
            LoadDevices();
            RefreshFxState(true);
            ApplyAudio(_workAudio.Copy(), false);
            Flash("[ FXSOUND READY ]", "SOUND IS LIVE");
        }
        else
        {
            Flash("[ INSTALL FAILED ]", "TRY AGAIN");
        }
    }


    private void OnStartEngineClick(object sender, RoutedEventArgs e)
    {
        if (_audio.IsInstalled)
        {
            _audio.StartEngine();
            ApplyAudio(_workAudio.Copy(), false);
            RefreshFxState(true);
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
        EmergencyReset.Run();
        if (_stateTimer is not null)
        {
            _stateTimer.Stop();
            _stateTimer = null;
        }

        Application.Current.Shutdown();
    }


}
