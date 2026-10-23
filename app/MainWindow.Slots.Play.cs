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

// Playing a slot: the request queue, the checklist, applying it to the screen and the
// sound, and the HDR settle that has to land before the game is started.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// Whether the window has finished loading and the first startup restore has
    /// had its chance. A play request that arrives before this is kept rather
    /// than applied against half-loaded audio devices.
    /// </summary>
    private bool _startupComplete;

    /// <summary>An IPC play request that arrived before the window was ready.</summary>
    private string _pendingPlayId = string.Empty;

    /// <summary>
    /// The settle wait for the HDR flip this Play started. Play awaits it before
    /// launching, because starting a game while the display is mid-mode-switch
    /// risks the game reading a transitional mode. Null when HDR was not moved.
    /// </summary>
    private Task<bool>? _lastHdrSettleTask;

    /// <summary>HDR warnings for the same one OSD line as the route warnings.</summary>
    private readonly List<string> _lastHdrWarnings = new();

    /// <summary>
    /// Whether the last <see cref="PlaySlot"/> call managed to push the screen.
    /// Kept beside the call because the Play summary is written after the audio
    /// queue has drained, by which time the local in that method is gone.
    /// </summary>
    private bool _lastPlayScreenTookIt = true;

    private PlayPreflight PlayPreflightFor(HotkeySlot slot)
    {
        bool monitorTargeted = !string.IsNullOrWhiteSpace(slot.MonitorKey)
            || !string.IsNullOrWhiteSpace(slot.MonitorDevice);

        string resolved = _display.ResolveDevice(slot.MonitorKey, slot.MonitorDevice);

        // The slot board is built before the first display scan on a cold start.
        // An empty monitor list is "not known yet", not "the panel was unplugged";
        // saying the latter on every row's tooltip would be a false warning that
        // disappears only if something happens to rebuild the rows later.
        bool monitorResolved = !monitorTargeted
            || resolved.Length > 0
            || _display.Monitors.Count == 0;

        return PlayPreflight.Build(
            slot,
            targetExists: PlayPreflight.TargetExists(slot),
            displayExists: FindDisplay(slot.DisplayPresetId) is not null,
            audioExists: FindAudio(slot.AudioPresetId) is not null,
            crosshairExists: FindCrosshair(slot.CrosshairPresetId) is not null,
            audioInstalled: _audio.IsInstalled,
            monitorResolved: monitorResolved);
    }

    private async void OnSlotPlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        // Through the one handler guard rather than its own try/catch. The body is
        // already a task, so there is nothing to extract; what changes is that the
        // policy for a handler failure - log, one line of feedback, never a modal
        // over a game - now lives in one place instead of being retyped here.
        await UiGuard.RunAsync(
            "PLAY",
            () => PlaySlotByIdAsync(id),
            _ => Flash("Could not start that slot. Details are in the log.", true));
    }

    /// <summary>
    /// A play request that came from another process, delivered by the window
    /// hook. Kept until the window is ready if it is early, and otherwise handed
    /// to the same path the button uses.
    /// </summary>
    private void QueuePlayRequest(string slotId)
    {
        if (!PlayRequest.TrySlotId(slotId, out string id))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_startupComplete)
            {
                _pendingPlayId = id;
                return;
            }

            _ = PlaySlotByIdAsync(id);
        }), DispatcherPriority.Normal);
    }

    /// <summary>
    /// The cold-start half of the same request. Waits for the startup audio
    /// restore, then applies the slot, so a slow engine cannot have the restore
    /// land after the game's profile and overwrite it.
    /// </summary>
    private async Task RunStartupPlayAsync(Task startupAudio, string commandLineSlotId)
    {
        try
        {
            await startupAudio;
        }
        catch (Exception ex)
        {
            TraceLog.Write("STARTUP PLAY", ex);
        }

        _startupComplete = true;

        string slotId = _pendingPlayId.Length > 0 ? _pendingPlayId : commandLineSlotId;
        _pendingPlayId = string.Empty;

        if (slotId.Length > 0)
        {
            await PlaySlotByIdAsync(slotId);
        }
    }

    private async Task PlaySlotByIdAsync(string slotId)
    {
        if (!PlayRequest.TrySlotId(slotId, out string id))
        {
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        if (slot is null)
        {
            // A shortcut outlives the slot it names when a profile is edited.
            // Bring the window up rather than leaving the click with no visible
            // result at all.
            SetRail("SLOT NOT FOUND", RailTone.Failed);
            Flash("That Play shortcut points at a slot that is gone", true);
            ShowFromSecondLaunch();
            return;
        }

        if (_playInFlight)
        {
            Flash("Already starting a game");
            return;
        }

        // Before anything is applied, not after: the whole question is whether this
        // path may be executed at all, and a slot applied for a launch the user then
        // cancels would have to be reverted for no reason.
        if (slot.NeedsLaunchConfirmation
            && PlayPreflight.TryLaunchTarget(slot, out string unconfirmedUri, out _, out _))
        {
            ShowLaunchConfirm(slot, PlayPreflight.ConfirmationTarget(slot, unconfirmedUri));
            return;
        }

        _playInFlight = true;
        try
        {
            await PlaySlotWithLaunchAsync(slot);
        }
        catch (Exception ex)
        {
            TraceLog.Write("PLAY", ex);
            SetRail("PLAY FAILED", RailTone.Failed);
            Flash("Could not start that slot. Details are in the log.", true);
        }
        finally
        {
            _playInFlight = false;
        }
    }


    /// <summary>
    /// Work out the checklist, apply the slot, wait for the audio engine, then
    /// start the game.
    /// <para>
    /// The wait is the whole point of the feature. Applying a slot already queues
    /// the sound; starting the game immediately would leave the first seconds of
    /// it on the previous tune, and on a slow engine that is long enough to hear.
    /// The watcher is told the slot is already loaded before the process exists,
    /// so its next poll does not apply the profile a second time - and does not
    /// overwrite the pre-game record with the profile it just applied.
    /// </para>
    /// </summary>
    private async Task PlaySlotWithLaunchAsync(HotkeySlot slot)
    {
        PlayPreflight preflight = PlayPreflightFor(slot);

        if (!preflight.CanLaunch)
        {
            SetRail("PLAY BLOCKED", RailTone.Failed);
            Flash(preflight.BlockedReason, true);
            TraceLog.Write("PLAY BLOCKED " + slot.Name + " <- " + preflight.BlockedReason);
            return;
        }

        // Which of the two Play will do, decided here and used below rather than
        // re-read from the slot: the watchdog's timeout and the log line both
        // depend on this answer, and a policy that changed between the two reads
        // would leave them disagreeing about what happened.
        LaunchVia via = PlayPreflight.ViaFor(slot, out string launchUri);
        PlayPreflight.TryTarget(slot, out string exePath);
        string processName = AppProfileTools.ProcessNameOf(exePath);
        bool alreadyRunning = IsProcessRunning(processName);

        // First capture wins, exactly as the auto path does. The slot is about to
        // replace the active ids, so this has to be read before PlaySlot.
        if (_autoSlotId.Length == 0)
        {
            CapturePreGame();
        }

        // Set before PlaySlot rather than after Process.Start. The watcher polls
        // in the background, and a poll between the two would see a new process
        // and apply the slot a second time.
        _autoStamp = DateTime.UtcNow;
        _autoSlotId = slot.Id;
        _autoProcess = processName;
        _autoWildcardProcess = string.Empty;

        PlaySlot(slot, announce: false);

        // PlaySlot queues the engine half. Nothing below should start while that
        // queue is still moving.
        await WaitForAudioQueueAsync();

        // The game waits for the display to settle. The gamma half was pushed
        // already and is re-pushed by the settle task if Windows discarded it.
        if (_lastHdrSettleTask is { } hdrSettle)
        {
            if (!await hdrSettle)
            {
                _lastHdrWarnings.Add("HDR did not switch");
            }
        }

        if (!alreadyRunning)
        {
            bool started = via == LaunchVia.None
                ? TryStartGame(exePath, LaunchArguments.Normalise(slot.LaunchArguments), out string failure)
                : TryStartLauncher(launchUri, out failure);

            if (!started)
            {
                // Undone here rather than by the exit path, because there is no
                // process to exit. This was the case that left a slot's screen,
                // routes and HDR applied for the rest of the session: a cancelled
                // UAC prompt, a deleted exe, or a launcher that refused to run. The
                // reason travels with the line, because "did not start" on its own
                // sends the user looking for the wrong thing.
                RevertFailedPlay(slot, "start failed", failure);
                return;
            }

            // The game may take a while to appear - and through a launcher, much
            // longer than that. Until it does, nothing else would ever put this
            // slot back if it never appears, so the watchdog owns that window and
            // cancels itself the moment the process exists.
            ArmPlayWatchdog(
                slot,
                processName,
                via == LaunchVia.None ? PlayLaunchKind.Direct : PlayLaunchKind.Launcher);
        }

        List<string> parts = new()
        {
            alreadyRunning ? slot.Name + " profile loaded" : "Playing " + slot.Name,
        };

        if (!alreadyRunning && preflight.Summary != "no profile changes")
        {
            parts.Add(preflight.Summary);
        }

        // How it was started belongs on the same line as what was loaded: through
        // a launcher, the game can be a minute away from appearing, and the user
        // should not be left guessing whether the click did anything.
        if (!alreadyRunning && via != LaunchVia.None)
        {
            parts.Add(LaunchUriPolicy.Describe(via));
        }

        if (!_lastPlayScreenTookIt)
        {
            parts.Add("screen blocked");
        }

        if (preflight.Warnings.Count > 0)
        {
            parts.Add(string.Join(", ", preflight.Warnings.Select(w =>
                w.Label.ToLowerInvariant() + " " + w.Detail)));
        }

        // The route is applied after the checklist is built, because it depends
        // on what is plugged in and on whether FxSound is running right now. Its
        // outcome belongs on the same line as the rest of the play result.
        if (_lastWindowsAudioWarnings.Count > 0)
        {
            parts.Add(string.Join(", ", _lastWindowsAudioWarnings));
        }

        if (_lastHdrWarnings.Count > 0)
        {
            parts.Add(string.Join(", ", _lastHdrWarnings));
        }

        if (alreadyRunning)
        {
            parts.Add("already running");
        }

        string message = string.Join(" · ", parts);
        bool warn = preflight.Warnings.Count > 0
            || _lastWindowsAudioWarnings.Count > 0
            || _lastHdrWarnings.Count > 0
            || !_lastPlayScreenTookIt;

        SetRail("PLAYING " + slot.Name.ToUpperInvariant());

        // The screen the game will open on, rather than the one this window is on:
        // at this moment the game has only just been asked to start.
        Flash(message, warn, ScreenForSlot(slot));

        TraceLog.Write("PLAY " + slot.Name + " <- " + (via == LaunchVia.None ? exePath : launchUri)
            + (via == LaunchVia.None ? string.Empty : " (" + LaunchUriPolicy.Describe(via) + ")")
            + (alreadyRunning ? " (already running)" : " (started)"));
    }


    /// <summary>
    /// The screen a slot is keyed to, as a work area, or null when it is keyed to
    /// none.
    /// <para>
    /// What a slot says belongs on the screen the slot is for, because that is the
    /// screen it just changed. The foreground window is a good proxy while a game is
    /// running and a poor one while Play is starting it: at that moment the
    /// foreground window is this app or a launcher, and the plate would land on the
    /// screen the game is not about to open on.
    /// </para>
    /// <para>
    /// Null - a slot that applies to every screen, or one whose screen has been
    /// unplugged since it was written - sends the toast back to the foreground
    /// window, which is the screen the user is looking at.
    /// </para>
    /// </summary>
    private Rect? ScreenForSlot(HotkeySlot slot)
    {
        string device = _display.ResolveDevice(slot.MonitorKey, slot.MonitorDevice);
        return MonitorWorkArea.ForDevice(device);
    }


    /// <param name="screenOnly">
    /// Push the screen only and leave the sound alone. Used when the screen is being
    /// restored from a focus pause: the sound was never taken down, so pushing it
    /// again would spawn the engine and re-apply a tune that is already loaded, on
    /// every return from the pause - and a return is every time the user alt-tabs
    /// back to check something.
    /// </param>
    /// <param name="deliberate">
    /// The user asked for this load, rather than the app deciding on its own. Only
    /// then is it worth a step back, and it is a parameter that has to be asked for
    /// rather than inferred from what is loaded, because the same method is reached
    /// by a hotkey, by a game launching, and by a focus pause resuming. A caller
    /// that passes nothing gets the default and cannot start being undoable by
    /// accident.
    /// </param>
    private void PlaySlot(HotkeySlot slot, bool announce, bool screenOnly = false, bool deliberate = false)
    {
        if (deliberate)
        {
            // Before anything is applied, because what is being remembered is the
            // preset that is live right now - which is exactly what the two lines
            // below replace. Taken after the fact it would record this slot.
            RememberForUndo();

            // And a load the user asked for takes the profile off the app's own
            // automatic hands. Otherwise a game started after the user had changed
            // something by hand would revert, on exit, to a state they had already
            // replaced on purpose.
            _preGameValid = false;
        }

        DisplayPreset? display = FindDisplay(slot.DisplayPresetId);
        AudioPreset? audio = FindAudio(slot.AudioPresetId);

        if (display is null
            && audio is null
            && FindCrosshair(slot.CrosshairPresetId) is null
            && slot.HdrMode == SlotHdrMode.Leave
            && !slot.HasWindowsAudioRouting)
        {
            // There is nothing to load, so nothing is about to be loaded. Saying
            // otherwise left the status bar and the toast claiming a preset that
            // was never applied, and left the key looking bound to something when
            // it was bound to nothing at all.
            SetRail("NOTHING SET", RailTone.Warn);
            Flash("Nothing set on " + slot.Name, true, ScreenForSlot(slot));
            return;
        }

        // HDR first, because the mode change throws the gamma ramp away. A resume
        // from a focus pause does not touch it: the alt-tab did not end the slot,
        // and flipping HDR there would blink the panel every time.
        ApplySlotHdr(slot, announce, screenOnly);

        bool screenTookIt = true;
        if (display is not null)
        {
            LoadTune(display.Copy(), (audio ?? _workAudio).Copy());
            // The panel the slot is keyed to, resolved against what is attached
            // now. Following the saved device name alone is what let a renumbered
            // screen take a preset meant for another one.
            screenTookIt = ApplyDisplay(
                display.Copy(),
                _display.ResolveDevice(slot.MonitorKey, slot.MonitorDevice),
                false);
        }

        // Read by the Play path after the audio queue has drained. The local
        // above is gone by then, and the refusal still has to reach the one OSD
        // line the play gesture shows.
        _lastPlayScreenTookIt = screenTookIt;

        // Announced before the slow half, not after it.
        //
        // Everything this toast reports as done happens above and below this line:
        // a gamma ramp, a DDC/CI call that takes its time over I2C, and a profile
        // write to disk. The plate used to go up once all of that had finished, so
        // a slot key did nothing visible for a beat and then the toast appeared -
        // which reads as the keypress having been missed rather than as the app
        // answering it. Saying it up front means the confirmation lands inside the
        // same gesture.
        //
        // It is a claim, not a report, and it is corrected below if the screen
        // refuses. The correction is a second toast rather than a retraction
        // because there is no way to unsay the first one, and an amber plate
        // arriving a moment after a grey one says "that did not work" in a way the
        // user is already used to reading.
        SetRail(slot.Name.ToUpperInvariant());
        if (announce)
        {
            Flash(slot.Name + " loaded", ScreenForSlot(slot));
        }

        // The crosshair goes up whether or not the screen took its preset, and on a
        // resume after an alt-tab as well, because standing the screen down hid it.
        ApplySlotCrosshair(slot);

        // Windows defaults are part of what the slot asked for. They are not
        // applied on a resume: an alt-tab did not put the game's headset back on
        // the desk, and re-running the switch there would fight a user who had
        // changed the default in the meantime.
        if (!screenOnly)
        {
            ApplyWindowsAudioRouting(slot);

            if (announce && _lastWindowsAudioWarnings.Count > 0)
            {
                Flash(string.Join(", ", _lastWindowsAudioWarnings), true, ScreenForSlot(slot));
            }
        }
        else
        {
            _lastWindowsAudioWarnings = Array.Empty<string>();
        }

        if (audio is not null && !screenOnly)
        {
            ApplyAudioToDevice(audio.Copy(), false, slot);
        }

        // The sound half is applied either way. A slot is one gesture covering two
        // independent things, and a display that refuses a gamma ramp is no reason
        // to leave the audio preset the user asked for unapplied as well. What
        // changes is only what gets claimed afterwards: the rail and the toast
        // describe the whole slot, so they say the screen did not take it rather
        // than reporting a clean load for something half of which is missing.
        if (!screenTookIt)
        {
            SetRail("SCREEN BLOCKED", RailTone.Failed);

            if (announce)
            {
                Flash(slot.Name + " loaded, screen did not take it", true, ScreenForSlot(slot));
            }
        }
    }


    /// <summary>
    /// Applies a slot's HDR request and starts the settle wait, if there is one.
    /// <para>
    /// The apply is synchronous because the mode switch has to happen before the
    /// gamma ramp is written. The wait is not: a flip can take a few seconds on a
    /// slow panel, and blocking the dispatcher for it would freeze the window on
    /// the app's primary gesture. Play awaits the returned task before it starts
    /// the game; a hotkey lets it finish in the background and re-pushes the ramp.
    /// </para>
    /// </summary>
    private void ApplySlotHdr(HotkeySlot slot, bool announce, bool screenOnly)
    {
        _lastHdrWarnings.Clear();
        _lastHdrSettleTask = null;

        if (screenOnly || slot.HdrMode == SlotHdrMode.Leave)
        {
            return;
        }

        if (slot.IsAnyGameTarget)
        {
            // A wildcard would flip HDR for a video player or a browser game. The
            // slot still loads everything else; this part is refused.
            _lastHdrWarnings.Add("HDR is not applied on Any game");
            TraceLog.Write("HDR " + slot.Name + " <- Any game");
            return;
        }

        string device = _display.ResolveDevice(slot.MonitorKey, slot.MonitorDevice);

        if (device.Length == 0)
        {
            _lastHdrWarnings.Add("Pick one screen for HDR");
            TraceLog.Write("HDR " + slot.Name + " <- no screen");
            return;
        }

        HdrApplyResult result = _hdr.Apply(device, slot.MonitorKey, slot.HdrMode);

        if (result.Warning.Length > 0)
        {
            _lastHdrWarnings.Add(result.Warning);
            TraceLog.Write("HDR " + slot.Name + " <- " + result.Warning);
        }

        if (!result.Applied)
        {
            if (announce && _lastHdrWarnings.Count > 0)
            {
                Flash(string.Join(", ", _lastHdrWarnings), true, MonitorWorkArea.ForDevice(device));
            }

            return;
        }

        bool wanted = slot.HdrMode == SlotHdrMode.On;
        _lastHdrSettleTask = SettleHdrAndRepushAsync(device, wanted);

        TraceLog.Write("HDR " + slot.Name + " -> " + (wanted ? "on" : "off")
            + (result.LegacyPath ? " (legacy advanced colour)" : string.Empty));

        if (announce && _lastHdrWarnings.Count > 0)
        {
            Flash(string.Join(", ", _lastHdrWarnings), true, MonitorWorkArea.ForDevice(device));
        }
    }


    /// <summary>
    /// Waits for the flip to land, then puts the gamma ramp back if Windows threw
    /// it away. The re-push is the reason the apply can stay synchronous: the first
    /// ramp write happens before the mode change has settled, and this is what
    /// makes the picture right once it has.
    /// </summary>
    private async Task<bool> SettleHdrAndRepushAsync(string device, bool wanted)
    {
        bool settled = await _hdr.SettleAsync(device, wanted, TimeSpan.FromSeconds(4));

        if (!settled)
        {
            TraceLog.Write("HDR did not settle " + device);
            return false;
        }

        if (_display.ShouldDefend)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _display.Push();
                TraceLog.Write("DISPLAY ramp re-pushed after an HDR flip");
            });
        }

        return true;
    }
}
