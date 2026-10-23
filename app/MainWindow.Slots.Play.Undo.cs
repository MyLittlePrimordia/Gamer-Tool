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

// Taking a play back: the one undo step, and the record of what the machine looked
// like before a game so an exit or a focus loss can put it back.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// Whether there is a load worth taking back.
    /// <para>
    /// One step rather than a stack, deliberately. A stack means deciding what it
    /// means when a preset has been deleted since it was pushed, and a menu to
    /// choose from - and an Undo that opens a list is a different feature from an
    /// Undo that takes back what you just did.
    /// </para>
    /// </summary>
    private bool _undoAvailable;

    /// <summary>The screen preset that was loaded when the step was recorded.</summary>
    private string _undoDisplayId = string.Empty;

    /// <summary>The sound preset that was loaded when the step was recorded.</summary>
    private string _undoAudioId = string.Empty;

    /// <summary>
    /// The screen the remembered preset was on, so Undo puts it back on the same
    /// monitor rather than on whichever one happens to be selected now.
    /// </summary>
    private string _undoMonitor = string.Empty;

    /// <summary>
    /// Records what is loaded now, so a deliberate load can be taken back.
    /// </summary>
    /// <remarks>
    /// Called only from the deliberate branch of <see cref="PlaySlot"/>, and that
    /// is the whole rule. An auto-load is the app deciding on its own and a revert
    /// or a panic key is the app cleaning up; putting either behind Undo would
    /// turn the control into a way to argue with the watcher rather than a way to
    /// take back what you just did.
    /// <para>
    /// A load with nothing behind it is still recorded, and still marked
    /// available. Undo then lands on flat, which is the honest answer: the user
    /// had nothing loaded and Undo takes them back to nothing loaded.
    /// </para>
    /// </remarks>
    private void RememberForUndo()
    {
        _undoAvailable = true;
        _undoDisplayId = _activeDisplayId;
        _undoAudioId = _activeAudioId;
        _undoMonitor = _appliedMonitor;

        PushUndoAvailability();
    }

    /// <summary>
    /// Tells the tray whether the Undo line can do anything.
    /// </summary>
    /// <remarks>
    /// Pushed rather than read as the menu opens. The two toggles beside it are
    /// answered by reading a switch the window already owns, and this is not a
    /// field: "is the last load still takeable back" is decided at the moment of
    /// the load and cannot be reconstructed from the state left behind.
    /// </remarks>
    private void PushUndoAvailability()
    {
        _tray?.SetUndoAvailable(_undoAvailable);
    }

    /// <summary>
    /// Takes back the last preset the user loaded, from the tray or from the
    /// board.
    /// </summary>
    /// <remarks>
    /// Through the same apply path the preset boxes use - <see cref="ApplyDisplay"/>
    /// and <see cref="ApplyAudio"/> rather than a bare ramp write and a queue push.
    /// A parallel path would leave the profile, the tray tooltip and the live
    /// labels describing a load that never happened, which is the exact failure
    /// this app already fixed once for the preset boxes.
    /// <para>
    /// Spent on the way in, and only after the availability check above, so a
    /// second press has nothing to do. The restore itself is deliberately not
    /// wrapped in a retry: a step that throws is a step the user can see failed,
    /// and leaving it standing would mean a button that appears to work twice.
    /// </para>
    /// </remarks>
    private async System.Threading.Tasks.Task UndoAsync()
    {
        if (!_undoAvailable)
        {
            Flash("Nothing to undo");
            return;
        }

        _undoAvailable = false;
        PushUndoAvailability();

        string displayId = _undoDisplayId;
        string audioId = _undoAudioId;
        string monitor = _undoMonitor;

        // Flat means "nothing was loaded", which is what the two reset paths write
        // into the remembered id. Undoing to it is a reset rather than a load, and
        // goes through the reset so the panel ownership, the night filter and the
        // remembered id are all handled the way every other reset handles them.
        if (displayId.Length == 0 || string.Equals(displayId, PresetIds.Flat, StringComparison.OrdinalIgnoreCase))
        {
            GoScreenStandDown();
        }
        else if (FindDisplay(displayId) is { } display)
        {
            LoadTune(display.Copy(), _workAudio.Copy());
            ApplyDisplay(display.Copy(), monitor, false);
        }

        if (audioId.Length == 0 || string.Equals(audioId, PresetIds.Flat, StringComparison.OrdinalIgnoreCase))
        {
            await GoSoundNeutralAsync();
        }
        else if (FindAudio(audioId) is { } audio)
        {
            // The same entry point the sound box uses. A slot's own output route is
            // deliberately not part of what was recorded, because the box does not
            // choose one either: the step being taken back is the preset, not the
            // routing.
            ApplyAudio(audio.Copy(), announce: false);
        }

        // One call for every surface that renders which preset is loaded, rather
        // than three the next reader has to keep in step. That includes the tray
        // lines and both dropdowns, which are rebuilt from the ids just changed.
        RefreshActivePreset();

        SetRail("UNDONE");
        Flash("Back to what was loaded before");
    }


    /// <summary>
    /// Whether <see cref="RevertToPreGame"/> has anything worth putting back.
    /// <para>
    /// Only the two ids are needed, and only because the exit path still has to
    /// reset the sound when it is false. A flag rather than the presets themselves
    /// so the answer cannot go stale while the presets are edited.
    /// </para>
    /// </summary>
    private bool _preGameValid;

    /// <summary>What was loaded before the app first took the screen over for a game.</summary>
    private string _preGameDisplayId = string.Empty;

    /// <summary>What was playing before the app first took the sound over for a game.</summary>
    private string _preGameAudioId = string.Empty;

    /// <summary>Which screen the remembered preset was on.</summary>
    private string _preGameMonitor = string.Empty;

    /// <summary>
    /// Records what the user had loaded, before the app starts driving the screen
    /// and the sound for a game.
    /// </summary>
    /// <remarks>
    /// First capture wins, and the emptiness of <c>_autoSlotId</c> is the test for
    /// "the app is not already in charge". That has to be read before
    /// <see cref="ShouldAutoApply"/> sets it, or the second alt-tab into a game
    /// would record the game's own preset as the thing to put back - which is the
    /// app undoing its own undo.
    /// <para>
    /// "Flat" is captured as flat rather than as nothing to do. A user who had
    /// explicitly reset before starting a game gets the reset back, and their
    /// reading preset is not silently reinstated.
    /// </para>
    /// </remarks>
    private void CapturePreGame()
    {
        _preGameValid = true;
        _preGameDisplayId = _activeDisplayId;
        _preGameAudioId = _activeAudioId;
        _preGameMonitor = _appliedMonitor;
    }

    /// <summary>
    /// Puts the screen and the sound back to what the user had before the game.
    /// </summary>
    /// <remarks>
    /// Not a hard reset, and the difference is the whole point. Auto-revert used to
    /// go to neutral always, which is right for a user who had nothing loaded and
    /// wrong for one who had a reading preset on before starting a game: they
    /// alt-tab back to a flat screen nobody asked for. Falls through to the neutral
    /// path whenever there is nothing worth restoring, which is the common case and
    /// the one the old behaviour got right.
    /// </remarks>
    private void RevertToPreGame(HotkeySlot slot)
    {
        if (!_preGameValid)
        {
            return;
        }

        string displayId = _preGameDisplayId;
        string audioId = _preGameAudioId;
        string monitor = _preGameMonitor;

        // Spent as it is read. A second exit for the same slot must not find this
        // record still standing and restore the same state twice.
        _preGameValid = false;

        // Refused when what was captured is the preset being reverted. Nothing to
        // put back, and neutral is the better answer: reverting to the preset being
        // reverted would be the app undoing its own undo.
        if (string.Equals(displayId, slot.DisplayPresetId, StringComparison.OrdinalIgnoreCase))
        {
            displayId = string.Empty;
        }

        if (string.Equals(audioId, slot.AudioPresetId, StringComparison.OrdinalIgnoreCase))
        {
            audioId = string.Empty;
        }

        if (displayId.Length > 0 && !string.Equals(displayId, PresetIds.Flat, StringComparison.OrdinalIgnoreCase)
            && FindDisplay(displayId) is { } display)
        {
            LoadTune(display.Copy(), _workAudio.Copy());
            ApplyDisplay(display.Copy(), monitor, false);
            TraceLog.Write("AUTO EXIT restored screen " + display.Name);
        }

        if (audioId.Length > 0 && !string.Equals(audioId, PresetIds.Flat, StringComparison.OrdinalIgnoreCase)
            && FindAudio(audioId) is { } audio)
        {
            ApplyAudio(audio.Copy(), announce: false);
            TraceLog.Write("AUTO EXIT restored sound " + audio.Name);
        }
    }
}
