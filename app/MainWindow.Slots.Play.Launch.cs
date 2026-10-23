using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
namespace GamerTool;

// Starting a slot's game and watching it start: the confirmation before a path the
// machine has never run, the shell and launcher starts, the desktop shortcut, and the
// watchdog that puts a slot back when the launch never produced a process.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>One launch at a time, so a double click cannot start two games.</summary>
    private bool _playInFlight;

    /// <summary>
    /// Watches a launch that has not produced a process yet, so a slot applied
    /// for a game that never starts can still be undone. Null when nothing is
    /// being watched.
    /// </summary>
    private DispatcherTimer? _playWatch;

    /// <summary>When the launch being watched was started.</summary>
    private DateTime _playWatchStarted;

    /// <summary>The slot the watched launch is for.</summary>
    private string _playWatchSlotId = string.Empty;

    /// <summary>The process the watched launch should produce.</summary>
    private string _playWatchProcess = string.Empty;

    /// <summary>Direct or through a launcher, which is what sets the timeout.</summary>
    private PlayLaunchKind _playWatchKind;

    /// <summary>
    /// The one confirmation before Play runs a path this machine has never seen
    /// anybody choose.
    /// <para>
    /// Until Play, a slot's path was only ever compared against process names.
    /// Now it is executed, and it can arrive in a backup written elsewhere - the
    /// same untrusted file that the FxSound path is checked against before it is
    /// ever started. The modal shows the exact target rather than the game's name,
    /// because the name is the part a planted path gets right. For a launcher slot
    /// that target is the URI handed to Steam or Epic, which is the thing that
    /// runs; <see cref="PlayPreflight.ConfirmationTarget"/> decides between the two,
    /// so the string in the dialog is never a description of the launch.
    /// </para>
    /// <para>
    /// Deliberately not a warning on every Play. Once somebody on this PC has
    /// looked at the path and said yes, the slot is confirmed and Play behaves
    /// like any other.
    /// </para>
    /// </summary>
    private void ShowLaunchConfirm(HotkeySlot slot, string target)
    {
        // A Play can arrive from another process while the window is hidden in
        // the tray, and a dialog behind a hidden window is indistinguishable from
        // nothing happening at all.
        ShowFromSecondLaunch();

        if (_modalLayer.Visibility == Visibility.Visible)
        {
            // Something else already owns the dialog - an install in progress, most
            // likely. Replacing it would interrupt that; stacking is not something
            // this modal system can do.
            Flash("Close the open dialog first, then press Play", true);
            return;
        }

        StackPanel content = new();

        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            Text = "This slot came from an imported backup, so what it starts has never been checked on this PC. Play will start:",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 12, 0, 0),
        });

        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            Text = target,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextMid"),
            Margin = new Thickness(0, 8, 0, 0),
        });

        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            Text = "If that is not the game you expect, cancel and set the target again.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextMid"),
            Margin = new Thickness(0, 12, 0, 0),
        });

        ShowCustomModal("START THIS GAME?", content, "PLAY", () =>
        {
            // Saved before the launch rather than after it, so a launch that fails
            // does not ask again the next time.
            slot.LaunchConfirmed = true;
            Commit();

            _ = PlaySlotByIdAsync(slot.Id);
        });
    }

    private static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        Process[] matches;
        try
        {
            matches = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            // Logged rather than reported as "not running": a permission failure
            // and a genuinely absent process are different answers, and the one
            // this returns is used to decide whether a game has already started.
            TraceLog.Write("PROCESS LOOKUP " + processName, ex);
            return false;
        }

        try
        {
            return matches.Length > 0;
        }
        finally
        {
            // Each match wraps a live OS handle. The watcher disposes its own for
            // this reason; this helper never did, so every preflight leaked them.
            foreach (Process match in matches)
            {
                match.Dispose();
            }
        }
    }

    /// <summary>
    /// Asks the launcher to start the game, which is how the game expects to be
    /// started.
    /// <para>
    /// The URI is checked here rather than trusted from the slot, because this is
    /// the line where a string out of a profile becomes a command the shell runs.
    /// It is the same policy the picker used, applied again at the only moment
    /// that matters.
    /// </para>
    /// <para>
    /// No arguments and no working directory: the launcher owns both. A machine
    /// with no handler registered for the scheme fails here with the shell's own
    /// error, which <see cref="RevertFailedPlay"/> then puts the slot back for.
    /// </para>
    /// </summary>
    private static bool TryStartLauncher(string uri, out string error)
    {
        error = string.Empty;

        if (!LaunchUriPolicy.IsAllowed(uri))
        {
            error = "that launcher link is not one this app starts";
            TraceLog.Write("PLAY START refused " + uri);
            return false;
        }

        try
        {
            ProcessStartInfo start = new()
            {
                FileName = uri,
                UseShellExecute = true
            };

            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TraceLog.Write("PLAY START", ex);
            return false;
        }
    }

    private static bool TryStartGame(string exePath, string arguments, out string error)
    {
        error = string.Empty;

        try
        {
            ProcessStartInfo start = new()
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,

                // Empty for a slot that wants none, which is what the field is for
                // and what every slot made before it existed carries.
                Arguments = arguments,
                UseShellExecute = true
            };

            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TraceLog.Write("PLAY START", ex);
            return false;
        }
    }

    /// <summary>
    /// Puts everything a Play applied back, because the game never started.
    /// <para>
    /// The exit path's three steps in the same order - the screen stands down and
    /// releases the auto claim, the user's own preset goes back on, then the sound
    /// falls through to neutral with the Windows routes restored - because from the
    /// user's side this is the same event. A launch that failed and left the screen
    /// dimmed and the headset as the default microphone would be the app changing
    /// things and then having no way to change them back.
    /// </para>
    /// <para>
    /// Guarded on the claim, the way every other revert is: if the slot has already
    /// been stood down or replaced, or the app is quitting, there is nothing here
    /// that is still ours to undo.
    /// </para>
    /// </summary>
    private void RevertFailedPlay(HotkeySlot slot, string reason, string detail)
    {
        CancelPlayWatch();

        bool ours = !_quitting
            && string.Equals(_autoSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);

        if (ours)
        {
            GoScreenStandDown();
            RevertToPreGame(slot);
            _ = GoSoundNeutralAsync();
        }

        SetRail("PLAY FAILED", RailTone.Failed);

        string message = PlayLaunchWatch.GiveUpMessage(slot.Name);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += " · " + detail;
        }

        Flash(message, true);

        TraceLog.Write("PLAY " + slot.Name + " <- " + reason
            + (ours ? " (settings put back)" : " (nothing to put back)"));
    }


    /// <summary>
    /// Starts watching a launch that has not produced its process yet.
    /// <para>
    /// Armed only after a successful <c>Process.Start</c>. A game that is already
    /// running needs none of this: the slot was loaded for something that is up,
    /// and its exit is already watched.
    /// </para>
    /// </summary>
    private void ArmPlayWatchdog(HotkeySlot slot, string processName, PlayLaunchKind kind)
    {
        CancelPlayWatch();

        if (string.IsNullOrWhiteSpace(processName))
        {
            // Nothing to look for, so there is no question this can answer. The
            // pre-flight already refuses a slot with no exact target.
            return;
        }

        _playWatchSlotId = slot.Id;
        _playWatchProcess = processName;
        _playWatchKind = kind;
        _playWatchStarted = DateTime.UtcNow;

        // One tick a second, on the dispatcher. The work is a process list and two
        // string comparisons, and doing it here means the revert it may trigger
        // runs on the thread that owns the window without a second hop.
        _playWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _playWatch.Tick += OnPlayWatchTick;
        _playWatch.Start();

        TraceLog.Write("PLAY WATCH " + slot.Name + " <- " + processName
            + " for " + (int)PlayLaunchWatch.TimeoutFor(kind).TotalSeconds + "s");
    }

    private void CancelPlayWatch()
    {
        if (_playWatch is null)
        {
            return;
        }

        _playWatch.Stop();
        _playWatch.Tick -= OnPlayWatchTick;
        _playWatch = null;
        _playWatchSlotId = string.Empty;
        _playWatchProcess = string.Empty;
    }

    /// <summary>
    /// One poll of the launch watchdog.
    /// <para>
    /// The claim is the authority rather than the fields this timer was armed
    /// with: a stand-down, the slot's own key, the panic key, quitting or another
    /// Play all change it, and none of them want this watchdog finishing its job
    /// afterwards. That check is also what stops a second Play from inheriting the
    /// first one's countdown.
    /// </para>
    /// </summary>
    private void OnPlayWatchTick(object? sender, EventArgs e)
    {
        if (_quitting)
        {
            CancelPlayWatch();
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _playWatchSlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null
            || !string.Equals(_autoSlotId, _playWatchSlotId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_autoProcess, _playWatchProcess, StringComparison.OrdinalIgnoreCase))
        {
            CancelPlayWatch();
            return;
        }

        PlayLaunchWatchAction action = PlayLaunchWatch.Decide(
            DateTime.UtcNow - _playWatchStarted,
            IsProcessRunning(_playWatchProcess),
            _playWatchKind);

        if (action == PlayLaunchWatchAction.Wait)
        {
            return;
        }

        if (action == PlayLaunchWatchAction.Started)
        {
            TraceLog.Write("PLAY WATCH " + slot.Name + " <- started");
            CancelPlayWatch();
            return;
        }

        TraceLog.Write("PLAY WATCH " + slot.Name + " <- gave up after "
            + (int)(DateTime.UtcNow - _playWatchStarted).TotalSeconds + "s");

        RevertFailedPlay(slot, "did not start", string.Empty);
    }

    private void CreatePlayShortcut(HotkeySlot slot)
    {
        PlayPreflight preflight = PlayPreflightFor(slot);
        if (!preflight.CanLaunch)
        {
            Flash(preflight.BlockedReason, true);
            return;
        }

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(folder))
        {
            Flash("Could not find the desktop", true);
            return;
        }

        if (!PlayShortcut.TryCreate(folder, slot.Name, slot.Id, out string path, out string error))
        {
            TraceLog.Write("SHORTCUT " + slot.Name + " <- " + error);
            Flash("Could not create the shortcut", true);
            return;
        }

        SetRail("SHORTCUT CREATED");
        Flash("Play shortcut created: " + Path.GetFileName(path));
    }
}
