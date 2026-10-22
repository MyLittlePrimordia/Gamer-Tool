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
            int pictures = _backups.Export(_settings, dialog.FileName);
            string when = File.GetLastWriteTime(dialog.FileName).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            ShowBackupDetail(
                "Last backup " + when + " - " + Path.GetFileName(dialog.FileName),
                "Saved settings, " + _settings.CustomDisplayPresets.Count.ToString(CultureInfo.InvariantCulture)
                    + " screen and " + _settings.CustomAudioPresets.Count.ToString(CultureInfo.InvariantCulture)
                    + " sound presets, " + _settings.Slots.Count.ToString(CultureInfo.InvariantCulture) + " slots and their keys"
                    + (pictures > 0
                        ? ", and " + pictures.ToString(CultureInfo.InvariantCulture)
                            + (pictures == 1 ? " crosshair picture." : " crosshair pictures.")
                        : "."));
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
        try
        {
            await PickAndRestoreAsync();
        }
        catch (Exception ex)
        {
            TraceLog.Write("RESTORE CLICK", ex);
            Flash("Could not restore that backup. Details are in the log.", true);
        }
    }

    /// <summary>
    /// Asks for a file and restores it, or restores one already chosen. Split out so
    /// a rejected file can offer another go without sending the user back to
    /// Settings first, and so a backup file gets the same import, repair and
    /// confirmation however it was reached.
    /// </summary>
    private async Task PickAndRestoreAsync(string? chosenPath = null)
    {
        string path = chosenPath ?? string.Empty;

        if (chosenPath is null)
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

            path = dialog.FileName;
        }

        AppSettings? incoming = _backups.Import(path, out RestoreReport report, out string error);
        if (incoming is null)
        {
            TraceLog.Write("BACKUP IMPORT " + error);
            // Three answers, not two. "FILE COULD NOT BE READ" is what a truncated,
            // half-written or unreadable file comes back as, and it fell into the
            // "not a Gamer Tool backup" branch - which is the wrong diagnosis for
            // the most likely way a user reaches this: their own export, cut short
            // by a crash or a full disk. Being told the file is the wrong kind of
            // thing sends them looking for a different one.
            ShowResultModal(
                error == "FILE NOT FOUND"
                    ? "FILE NOT FOUND"
                    : error == "FILE COULD NOT BE READ"
                        ? "FILE COULD NOT BE READ"
                        : "NOT A GAMER TOOL BACKUP",
                error == "FILE NOT FOUND"
                    ? "That file is not there any more."
                    : error == "FILE COULD NOT BE READ"
                        ? "That file could not be read. It may be damaged, or still being written by something else."
                        : "That file is not a Gamer Tool backup, so there is nothing in it to restore.",
                failed: true,
                "Pick another",
                () => _ = PickAndRestoreAsync(),
                "Close",
                () => { });
            return;
        }

        await EnsureAppList();

        List<MonitorChoice> monitors = _display.MonitorChoices.Where(c => c.Device.Length > 0).ToList();
        List<string> soundDevices = DeviceBox.ItemsSource is IEnumerable<DeviceChoice> listed
            ? listed.Select(d => d.Id).Where(id => id.Length > 0).ToList()
            : new List<string>();

        // Every endpoint this PC remembers, not just the ones plugged in now. A
        // slot routed to a headset that is switched off is a route that works, and
        // the repair keeps it; the active list would have cleared it.
        List<string> knownEndpoints = _devices.GetKnownEndpointIds().ToList();

        _backups.Repair(incoming, monitors, soundDevices, _appList, _audio.ExePath, knownEndpoints, report);

        // The app's own dialog rather than a Windows one. This is the most
        // expensive mis-click in the app, and it is also the one that has to look
        // like the delete-a-preset and reset-the-slots confirmations it sits
        // beside, so the user gets one warning shape rather than two.
        ShowConfirmModal(
            "REPLACE EVERYTHING ON THIS PC?",
            report.Headline + ". Slots with a game that is not installed here come back as not set, with auto start off.",
            "Replace",
            () => ApplyRestored(incoming, report),
            "Cancel");
    }


    /// <summary>
    /// Back to what the app ships with.
    /// <para>
    /// The one action in the app that costs the user their work in bulk, so it is
    /// asked about in full and the profile it replaces is written to a file beside
    /// the profile first. That file is an ordinary backup, so "Restore" is the way
    /// back, and the wording says so rather than promising an undo button that does
    /// not exist.
    /// </para>
    /// </summary>
    private void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        if (_modalLayer.Visibility == Visibility.Visible)
        {
            Flash("Close the open dialog first", true);
            return;
        }

        ShowConfirmModal(
            "RESET EVERYTHING?",
            "Every preset, slot, hotkey, crosshair and screen setting goes back to what Gamer Tool ships with. "
                + "The settings they replace are saved as a backup file in your profile folder first, so Restore can bring them back.",
            "Reset everything",
            ApplyFactoryReset,
            "Cancel");
    }

    private void ApplyFactoryReset()
    {
        try
        {
            // Written before anything is replaced. If the copy cannot be made, the
            // reset does not happen: the whole point of offering this is that it can
            // be taken back, and a reset without its rescue is one that cannot.
            string folder = Path.GetDirectoryName(_profiles.SettingsPath) ?? string.Empty;
            string rescue = FactoryReset.RescuePath(folder, DateTime.Now);
            _backups.Export(_settings, rescue);

            TraceLog.Write("SETTINGS reset; the settings it replaced are in " + rescue);

            ApplyProfile(_profiles.Defaults());

            ShowBackupDetail(
                string.Empty,
                "Settings reset. What they were is in " + Path.GetFileName(rescue) + ", beside your settings file.");
            Flash("Everything reset to defaults");
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETTINGS reset", ex);
            Flash("The reset did not finish. Details are in the log.", true);
        }
    }

    private void ApplyRestored(AppSettings incoming, RestoreReport report)
    {
        ApplyProfile(incoming);

        // The hint is dropped rather than left standing: it names a file and a
        // time from the last time a backup was written, which after a restore is
        // history rather than news.
        string pictures = report.PicturesRestored == 1
            ? "1 crosshair picture"
            : report.PicturesRestored.ToString(CultureInfo.InvariantCulture) + " crosshair pictures";

        ShowBackupDetail(
            string.Empty,
            report.AnythingToReport
                ? "Restored with fixes: " + report.Summary.ToLowerInvariant() + "."
                : report.PicturesRestored > 0
                    ? "Restored clean. Everything in the file matched this PC, including " + pictures + "."
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
    /// Swaps the profile the window is running on and brings every surface in line.
    /// Shared by a restore and a reset, because both replace the whole profile and a
    /// second copy of this list is how one of them ends up leaving a control showing
    /// the profile that is no longer loaded.
    /// </summary>
    private void ApplyProfile(AppSettings incoming)
    {
        _settings = incoming;
        _audio.ExePath = _settings.FxSoundPath;

        // The backlight service was built holding the profile that was just
        // replaced, so it is repointed before anything can ask it anything.
        _backlight?.Rebind(_settings);

        _watcher.Stop();
        _hotkeys.Clear();

        // One call for every switch, so a control cannot be left showing the old
        // profile's value while the profile holds the new one.
        SyncControlsFromSettings();

        _workDisplay = (FindDisplay(_settings.ActiveDisplayPresetId) ?? DisplayPreset.Flat()).Copy();
        _workAudio = (FindAudio(_settings.ActiveAudioPresetId) ?? AudioPreset.Flat()).Copy();
        _activeDisplayId = _workDisplay.Id;
        _activeAudioId = _workAudio.Id;
        ReloadCrosshairFromSettings();

        // The picker is refilled from a worker. Enumerating the output devices
        // means asking the engine, which is the slowest thing in the app, and this
        // used to do it inline - so confirming a restore froze the window on the
        // button that was supposed to be the confirmation. Fire and forget: the
        // rest of the restore does not depend on it, and the picker is already
        // showing the old list until it lands.
        _ = LoadDevicesAsync();

        LoadTune(_workDisplay, _workAudio);
        RefreshPresetBoxes();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();
        UpdateFxBanner();
        RefreshBacklightRows();
        _ = RefreshFxStateAsync(true);
        Commit();
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
