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
    private void OnBackupClick(object sender, RoutedEventArgs e)
    {
        Commit();

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "SAVE YOUR BACKUP",
            Filter = "Gamer Tool backup|*.json|All files|*.*",
            FileName = _backups.SuggestedFileName(),
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _backups.Export(_settings, dialog.FileName);
            string when = File.GetLastWriteTime(dialog.FileName).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            ShowBackupDetail(
                "Last backup " + when + " - " + Path.GetFileName(dialog.FileName),
                "Saved settings, " + _settings.CustomDisplayPresets.Count.ToString(CultureInfo.InvariantCulture)
                    + " screen and " + _settings.CustomAudioPresets.Count.ToString(CultureInfo.InvariantCulture)
                    + " sound presets, " + _settings.Slots.Count.ToString(CultureInfo.InvariantCulture) + " slots and their keys.");
            Flash("Backup saved");
        }
        catch (Exception ex)
        {
            TraceLog.Write("BACKUP", ex);
            MessageBox.Show(this, "The backup file could not be written.", "Gamer Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "PICK A BACKUP FILE",
            Filter = "Gamer Tool backup|*.json|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AppSettings? incoming = _backups.Import(dialog.FileName, out RestoreReport report, out string error);
        if (incoming is null)
        {
            MessageBox.Show(this, "That file is not a Gamer Tool backup.", "Gamer Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            TraceLog.Write("BACKUP IMPORT " + error);
            return;
        }

        await EnsureAppList();

        List<string> monitors = _display.MonitorChoices.Where(c => c.Device.Length > 0).Select(c => c.Device).ToList();
        List<string> soundDevices = DeviceBox.ItemsSource is IEnumerable<DeviceChoice> listed
            ? listed.Select(d => d.Id).Where(id => id.Length > 0).ToList()
            : new List<string>();

        _backups.Repair(incoming, monitors, soundDevices, _appList, _audio.ExePath, report);

        MessageBoxResult answer = MessageBox.Show(
            this,
            "Replace everything on this PC with the backup?" + Environment.NewLine + Environment.NewLine
                + report.Headline + Environment.NewLine + Environment.NewLine
                + "Slots with a game that is not installed here come back as not set, with auto start off.",
            "Restore backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        ApplyRestored(incoming, report);
    }


    private void ApplyRestored(AppSettings incoming, RestoreReport report)
    {
        _settings = incoming;
        _audio.ExePath = _settings.FxSoundPath;

        _display.SetLock(_settings.GammaLock);
        _watcher.Stop();
        _hotkeys.Clear();

        GammaLockBox.IsChecked = _settings.GammaLock;
        OsdBox.IsChecked = _settings.ShowOsd;
        AutoSwitchBox.IsChecked = _settings.AutoSwitch;
        StartHiddenBox.IsChecked = _settings.StartHidden;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        StartWithWindowsBox.IsChecked = _startup.SetEnabled(_settings.StartWithWindows);
        _settings.StartWithWindows = _startup.IsEnabled;

        _workDisplay = (FindDisplay(_settings.ActiveDisplayPresetId) ?? DisplayPreset.Flat()).Copy();
        _workAudio = (FindAudio(_settings.ActiveAudioPresetId) ?? AudioPreset.Flat()).Copy();
        _activeDisplayId = _workDisplay.Id;
        _activeAudioId = _workAudio.Id;

        LoadDevices();
        LoadTune(_workDisplay, _workAudio);
        RefreshPresetBoxes();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();
        UpdateFxBanner();
        _ = RefreshFxStateAsync(true);
        Commit();

        // The hint is dropped rather than left standing: it names a file and a
        // time from the last time a backup was written, which after a restore is
        // history rather than news.
        ShowBackupDetail(
            string.Empty,
            report.AnythingToReport
                ? "Restored with fixes: " + report.Summary.ToLowerInvariant() + "."
                : "Restored clean. Everything in the file matched this PC.");

        if (report.Notes.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, report.Notes),
                "Restored with a few changes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        Flash("Backup restored");
    }


    /// <summary>
    /// Fills in the two lines under the backup row and takes the space back when
    /// there is nothing to say.
    /// <para>
    /// They start collapsed rather than empty on purpose. An empty caption is
    /// still a laid out caption, and two of them sitting open under the last row
    /// is most of the height that used to push this page past the bottom of its
    /// panel and hide the backup buttons.
    /// </para>
    /// </summary>
    private void ShowBackupDetail(string hint, string note)
    {
        BackupHintText.Text = hint;
        BackupNoteText.Text = note;
        BackupHintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        BackupNoteText.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        BackupDetailRow.Visibility = BackupHintText.Visibility == Visibility.Visible
            || BackupNoteText.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
