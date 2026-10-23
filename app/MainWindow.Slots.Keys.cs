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

// Every shortcut: capture, registration, refusal reporting, and what a registered key does when pressed.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// Slots whose shortcut is not actually registered: the operating system refused
    /// it because another program owns it, or an earlier slot already has it.
    /// <para>
    /// Kept so the row can say so. The refusal is reported once, in a toast and a
    /// rail line, and both are gone a moment later - after that the keycap went on
    /// rendering a shortcut that silently did nothing. Ids rather than keys, because
    /// a duplicate key is live for the slot that won it.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _deadHotkeySlots = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Identifies the panic key inside the hotkey service. Slot bindings are
    /// "slot:" plus the slot id, so this has to be a shape no slot can produce.
    /// </summary>
    private const string EmergencyTargetId = "emergency";


    /// <summary>
    /// Registration id for the panic key. Slot ids count up from one and there
    /// are a handful of slots, so a high number keeps the two apart without
    /// threading a counter through.
    /// </summary>
    private const int EmergencyHotkeyId = 1000;


    /// <summary>
    /// Identifies the bypass key inside the hotkey service. Same shape as
    /// <see cref="EmergencyTargetId"/> and for the same reason: "slot:" plus a slot
    /// id is the only other thing the service is ever asked about, so any other
    /// string is safe.
    /// </summary>
    private const string BypassTargetId = "bypass";


    /// <summary>
    /// Registration id for the bypass key. One above the panic key's, which is
    /// arbitrary but leaves room if a third non-slot binding ever appears.
    /// </summary>
    private const int BypassHotkeyId = 1001;

    private const int CrosshairHotkeyId = 1002;

    private const string CrosshairTargetId = "crosshair";

    private const int CrosshairCycleHotkeyId = 1003;

    private const string CrosshairCycleTargetId = "crosshair-cycle";


    /// <summary>
    /// Registers every armed slot, plus the global off key.
    ///
    /// The duplicate rule lives here rather than in the key capture handler on
    /// purpose. Capture only checks the slots that are armed at that moment, so a
    /// key can still end up shared: disable a slot, give its key to another one,
    /// then re-arm the first. A restored backup can do the same thing without
    /// anyone typing. Enforcing it in one place means the invariant holds however
    /// the state got that way, first slot wins, and the loser is named so the
    /// silent "KEY BUSY" in the status bar becomes something you can act on.
    /// </summary>
    private void RegisterHotkeys()
    {
        _hotkeys.Clear();
        HashSet<string> taken = new(StringComparer.Ordinal);
        List<HotkeySlot> skipped = new();
        HashSet<string> deadSlots = new(StringComparer.OrdinalIgnoreCase);

        // Keys some other program already owns, which is a different problem from
        // a duplicate in here and gets its own wording.
        HashSet<string> refusedKeys = new(StringComparer.OrdinalIgnoreCase);
        int id = 1;

        // The panic key is offered to Windows before any slot, so that if the two
        // ever meet it is the slot that gives way. It is the one binding whose
        // entire job is to still work when something else has gone wrong, and a
        // slot is a shortcut. A slot that loses the combo is reported below like
        // any other duplicate rather than silently going dead.
        string panicKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(_settings.EmergencyHotkey))
        {
            panicKey = HotkeyService.Normalise(_settings.EmergencyHotkey);

            // Checked, and claimed only on success, exactly as a slot is below.
            // This threw the answer away, and that made two things wrong at once
            // when another program owned the combo: the key stayed in `taken`, so
            // the slot holding it was skipped and then reported as "the panic key
            // took CTRL+ALT+F12 from <slot>" - a claim that was false, because the
            // panic key was not registered either. Both bindings were dead and the
            // message named the wrong one.
            if (_hotkeys.Register(EmergencyHotkeyId, EmergencyTargetId, _settings.EmergencyHotkey))
            {
                taken.Add(panicKey);
            }
            else
            {
                panicKey = string.Empty;
            }
        }

        // The bypass key is offered to Windows after the panic key and before any slot,
        // so a contested chord goes to the panic key first and then to the bypass
        // key, and only a slot ever gives way. The ordering is the whole rule and it
        // is here rather than in a helper because "which of these three wins" is
        // only ever asked in one place.
        if (!string.IsNullOrWhiteSpace(_settings.BypassHotkey))
        {
            string bypassKey = HotkeyService.Normalise(_settings.BypassHotkey);

            // Claimed only on success, as the panic key above does. Claiming it
            // first and releasing it on failure skips the slot holding it and then
            // reports the bypass key as having taken it, which is false and is the
            // bug the panic key's own comment describes.
            if (taken.Add(bypassKey))
            {
                if (!_hotkeys.Register(BypassHotkeyId, BypassTargetId, _settings.BypassHotkey))
                {
                    taken.Remove(bypassKey);
                    refusedKeys.Add(_settings.BypassHotkey);
                }
            }
            else
            {
                // The panic key already has this chord. Reported as its own case
                // rather than silently dead, because BindBypassHotkey refuses to
                // create this state and a restored profile can still arrive with it.
                skipped.Add(new HotkeySlot { Name = "bypass key" });
            }
        }

        // The crosshair key goes in after the bypass key and before the slots, the
        // same way: a slot gives way to it, never the other way round.
        if (!string.IsNullOrWhiteSpace(_settings.CrosshairHotkey))
        {
            string crosshairKey = HotkeyService.Normalise(_settings.CrosshairHotkey);

            if (taken.Add(crosshairKey))
            {
                if (!_hotkeys.Register(CrosshairHotkeyId, CrosshairTargetId, _settings.CrosshairHotkey))
                {
                    taken.Remove(crosshairKey);
                    refusedKeys.Add(_settings.CrosshairHotkey);
                }
            }
            else
            {
                skipped.Add(new HotkeySlot { Name = "crosshair key" });
            }
        }

        if (!string.IsNullOrWhiteSpace(_settings.CrosshairCycleHotkey))
        {
            string cycleKey = HotkeyService.Normalise(_settings.CrosshairCycleHotkey);

            if (taken.Add(cycleKey))
            {
                if (!_hotkeys.Register(CrosshairCycleHotkeyId, CrosshairCycleTargetId, _settings.CrosshairCycleHotkey))
                {
                    taken.Remove(cycleKey);
                    refusedKeys.Add(_settings.CrosshairCycleHotkey);
                }
            }
            else
            {
                skipped.Add(new HotkeySlot { Name = "crosshair cycle key" });
            }
        }

        foreach (HotkeySlot slot in _settings.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Hotkey) && slot.Enabled)
            {
                string key = HotkeyService.Normalise(slot.Hotkey);
                if (taken.Add(key))
                {
                    // The answer matters. RegisterHotKey fails when some other
                    // program already owns the combo system wide, and the failure
                    // was thrown away here: the slot counted as bound, its keycap
                    // kept rendering the shortcut, and nothing in the list showed
                    // that pressing it would do nothing. The only sign was one
                    // status line naming the key rather than the slot, which the
                    // next apply or reset overwrote.
                    if (!_hotkeys.Register(id, "slot:" + slot.Id, slot.Hotkey))
                    {
                        taken.Remove(key);
                        skipped.Add(slot);
                        deadSlots.Add(slot.Id);
                        refusedKeys.Add(slot.Hotkey);
                    }
                }
                else
                {
                    skipped.Add(slot);
                    deadSlots.Add(slot.Id);
                }
            }

            id++;
        }

        if (skipped.Count > 0)
        {
            string names = string.Join(", ", skipped.Select(s => s.Name));
            string keys = string.Join(", ", skipped.Select(s => s.Hotkey).Distinct(StringComparer.OrdinalIgnoreCase));
            bool lostToPanic = panicKey.Length > 0
                && skipped.Any(s => HotkeyService.Normalise(s.Hotkey) == panicKey);

            // Refused by the operating system rather than by this app. Said
            // separately, because the user's next move is different: close the
            // other program, rather than free up a duplicate in here.
            bool refusedByOs = refusedKeys.Count > 0;

            SetRail(
                (refusedByOs ? "KEY HELD BY ANOTHER PROGRAM ON " : "DUP KEY SKIPPED ON ")
                    + names.ToUpperInvariant(),
                RailTone.Failed);

            Flash(
                refusedByOs
                    ? "Another program already owns " + string.Join(", ", refusedKeys) + ", so " + names + " will not fire"
                    : lostToPanic
                        ? "Panic key took " + keys + " from " + names
                        : "Key already on another slot, skipped on " + names,
                true);

            TraceLog.Write("HOTKEY " + (refusedByOs ? "refused by the system" : "duplicate")
                + " " + keys + " on " + names);
        }

        // The refusal is remembered rather than announced once. The toast is gone in
        // a moment and the rail line is overwritten by the next apply, so the row
        // went back to rendering a shortcut that does nothing. Rebuilt only when the
        // set actually changed, and deferred, so this cannot recurse through the
        // handlers the rows it creates would attach.
        if (!_deadHotkeySlots.SetEquals(deadSlots))
        {
            _deadHotkeySlots.Clear();
            _deadHotkeySlots.UnionWith(deadSlots);

            if (_ready)
            {
                Dispatcher.InvokeAsync(BuildSlots);
            }
        }
    }


    /// <summary>
    /// A key press, routed to whichever binding it belongs to: the bypass switch,
    /// the panic reset, or a slot toggled.
    /// <para>
    /// This is an <c>async void</c> event handler, so an escape from it would go
    /// straight to the dispatcher's unhandled path - which in the middle of a game
    /// means a dialog over the picture. Everything below is inside one try for
    /// that reason, and the three branches are all in this method rather than out
    /// in a helper so that what a key means and the guard around it cannot be read
    /// apart.
    /// </para>
    /// </summary>
    private async void OnHotkeyPressed(HotkeyBinding binding) => await HandleHotkeyAsync(binding);


    /// <summary>
    /// The routing itself, shaped as a task so a caller can await the work rather
    /// than merely start it.
    /// <para>
    /// A key press arrives through an <c>async void</c> event, which nothing can
    /// await and nothing can reach from a test. This used to be a task-shaped
    /// wrapper around that handler which returned <c>Task.CompletedTask</c> straight
    /// after calling it - so "await the key press" completed while the apply it
    /// started was still running. The body is here now and the handler above is the
    /// adapter, which is the only arrangement in which awaiting means anything.
    /// </para>
    /// <para>
    /// It deliberately does not reach what was loaded before a game. A reset is a
    /// reset on every path that asks for one.
    /// </para>
    /// </summary>
    private async Task HandleHotkeyAsync(HotkeyBinding binding)
    {
        // A key pressed in the middle of a game must never raise the error
        // dialog. This is reached from an async void handler, so anything thrown
        // after an await would otherwise go straight to the dispatcher's unhandled
        // path.
        try
        {
            if (string.Equals(binding.TargetId, BypassTargetId, StringComparison.OrdinalIgnoreCase))
            {
                // Through the switch rather than through the setting. BypassToggle's
                // handler is the one place that sets EffectsEnabled, commits, queues the
                // engine push and verifies the result landed - four things that all have
                // to happen together or the switch and the engine disagree.
                //
                // The inversion is BypassToggle's own and must not be second guessed here:
                // the switch is labelled BYPASS, so IsChecked == true means the effects
                // are OFF. AppSettings.EffectsEnabled says the same thing the other way
                // round, and the two disagreeing by design is documented there.
                if (!_audio.IsInstalled)
                {
                    Flash("Sound needs FxSound first", true);
                    return;
                }

                BypassBox.IsChecked = BypassBox.IsChecked != true;
                Flash(BypassBox.IsChecked == true ? "Sound effects off" : "Sound effects on");
                TraceLog.Write("BYPASS key pressed, effects "
                    + (BypassBox.IsChecked == true ? "off" : "on"));
                return;
            }

            if (string.Equals(binding.TargetId, CrosshairTargetId, StringComparison.OrdinalIgnoreCase))
            {
                ToggleCrosshairFromKey();
                return;
            }

            if (string.Equals(binding.TargetId, CrosshairCycleTargetId, StringComparison.OrdinalIgnoreCase))
            {
                CycleCrosshairFromKey();
                return;
            }

            if (string.Equals(binding.TargetId, EmergencyTargetId, StringComparison.OrdinalIgnoreCase))
            {
                // The same neutral path a slot takes when it is switched off, so the
                // panic key lands in exactly the state quitting would have left, and
                // there is only one definition of "back to normal" in the app.
                //
                // emergency: true is what makes this the one path that answers in full.
                // The panic key exists for a screen that is already wrong and a user who
                // cannot click anything, so "the night filter is still scheduled" has to
                // lose to "I can see what is happening". Every other stand-down - the
                // tray, the reset button, a game closing - leaves the filter alone.
                //
                // The panic key is deliberately not undoable and deliberately does not
                // spend the undo step: it is the app cleaning up, and asking to take
                // that back is asking to put the wrong picture back on the screen. Nor
                // does it consult what was loaded before a game - a hard reset is a
                // hard reset, on every path that asks for one.
                GoScreenNeutral(emergency: true);
                SuppressNightUntilNextWindow();
                await GoSoundNeutralAsync();
                SetRail("PANIC RESET");
                Flash(_nightSuppressed && _settings.NightBlueLight
                    ? "Screen and sound reset, night filter paused"
                    : "Screen and sound reset");
                TraceLog.Write("PANIC key pressed, screen and sound reset, night filter "
                    + (_nightSuppressed ? "suppressed" : "untouched"));
                return;
            }

            if (binding.TargetId.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
            {
                string id = binding.TargetId["slot:".Length..];
                HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == id);
                if (slot is null)
                {
                    return;
                }

                await ToggleSlot(slot);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("HOTKEY " + binding.TargetId, ex);
            Flash("That key did not work. Details are in the log.", true);
        }
    }


    /// <summary>
    /// Loads a slot, or takes it back off again if it is the one that is already
    /// loaded.
    /// <para>
    /// Out here so the tray and the key are the same gesture. The tray menu can
    /// load a slot, and the first version of that wired it straight to
    /// <see cref="PlaySlot"/>, which has no off switch: choosing the slot you
    /// were already on would reload it and say so, where the same choice made
    /// with the key would have put the screen and the sound back to normal. Two
    /// ways of asking the same question with two answers, one of which looks
    /// like a bug to anybody who tries the other one first.
    /// </para>
    /// <para>
    /// Which preset is live decides which way it goes, not a remembered flag, so
    /// this still behaves for a slot that was loaded by auto-switch or by
    /// clicking its row on the board.
    /// </para>
    /// <para>
    /// A slot key is a toggle: the first press loads the slot, and pressing the
    /// same key again takes it back off again, which puts the screen and the
    /// sound back to their neutral presets. There is no separate off key, so
    /// every slot only ever needs one binding and the gesture that loaded a
    /// setup is also the gesture that backs it out.
    /// </para>
    /// <para>
    /// Holding the key down cannot toggle twice and undo the work, because every
    /// binding is registered with MOD_NOREPEAT, so Windows delivers one press
    /// per physical press rather than a stream of repeats.
    /// </para>
    /// <para>
    /// The load is the one call that opts into being undoable. Undo means "take
    /// back what I just asked for", and this is the only path where the user
    /// asked - the auto-load and the focus-pause resume come through
    /// <see cref="PlaySlot"/> too and take its default.
    /// </para>
    /// </summary>
    private async Task ToggleSlot(HotkeySlot slot)
    {
        if (!slot.HasWork)
        {
            SetRail("NOTHING SET", RailTone.Warn);
            Flash("Nothing set on " + slot.Name, true, ScreenForSlot(slot));
            return;
        }

        // A slot that only puts up a crosshair has no screen or sound to compare, so
        // whether it is "loaded" is whether its crosshair is up.
        if (string.IsNullOrWhiteSpace(slot.DisplayPresetId) && string.IsNullOrWhiteSpace(slot.AudioPresetId))
        {
            if (_crosshairVisible && _crosshairBySlot)
            {
                HideCrosshair();
                Flash(slot.Name + " off");
                return;
            }

            PlaySlot(slot, true, deliberate: true);
            return;
        }

        if (SlotService.IsLoaded(slot, _activeDisplayId, _activeAudioId))
        {
            // A stand-down, not a hard reset. The user is turning a preset off, not
            // asking for the whole screen rebuilt, so the panel is put back only if
            // the preset they were running was the thing that dimmed it.
            GoScreenStandDown();
            await GoSoundNeutralAsync();
            Flash(slot.Name + " off");
            return;
        }

        PlaySlot(slot, true, deliberate: true);
    }


    /// <summary>
    /// One of the two keycaps that are not a slot's, wired for capture.
    /// <para>
    /// The panic key and the bypass key behave identically apart from which
    /// setting they write to and what they are called in a message, so they share
    /// this rather than having a bind and a clear and a focus pair each. A third
    /// keycap later is one line here.
    /// </para>
    /// <para>
    /// Both keycaps set the shared capture state to themselves, which is what stops
    /// two of them being armed at once: a keycap only starts listening when it has
    /// keyboard focus, and only one control can.
    /// </para>
    /// </summary>
    /// <param name="box">The keycap, declared in the page.</param>
    /// <param name="which">Which of the two this is.</param>
    private void WireKeycap(TextBox box, KeycapCapture which)
    {
        Func<string> read = () => which switch
        {
            KeycapCapture.Panic => _settings.EmergencyHotkey,
            KeycapCapture.Crosshair => _settings.CrosshairHotkey,
            KeycapCapture.CrosshairCycle => _settings.CrosshairCycleHotkey,
            _ => _settings.BypassHotkey
        };

        Describe(box, read());

        box.GotKeyboardFocus += (s, e) =>
        {
            _capturingKeycap = which;
            _captureSlotId = null;
            _captureBox = box;
            box.Text = "PRESS A KEY";
            box.ToolTip = "Esc cancels  ·  Backspace clears  ·  F1 to F24 bind on their own";
        };
        box.LostKeyboardFocus += (s, e) =>
        {
            if (!ReferenceEquals(_captureBox, box))
            {
                return;
            }

            // Clicked away without choosing anything, so it keeps what it had.
            Describe(box, read());
            _captureBox = null;
            _capturingKeycap = KeycapCapture.None;
        };
    }

    /// <summary>The name a keycap uses in a message.</summary>
    private static string LabelOf(KeycapCapture which) => which switch
    {
        KeycapCapture.Panic => "Panic key",
        KeycapCapture.Crosshair => "Crosshair key",
        KeycapCapture.CrosshairCycle => "Crosshair cycle key",
        _ => "Bypass key"
    };

    /// <summary>
    /// What a non-slot keycap shows when it is not listening.
    /// <para>
    /// Goes through <see cref="ShowSlotKey"/> rather than a second implementation,
    /// because the two keycaps and every slot keycap have to read the same way and a
    /// copy of this is a copy that will drift.
    /// </para>
    /// </summary>
    private static void Describe(TextBox box, string hotkey) => ShowSlotKey(box, hotkey);

    /// <summary>
    /// Points the panic key at a new combo, taking it off any slot that held it.
    /// The steal is the same rule the slots use, the thing you are pointing at
    /// wins, because a slot quietly keeping a chord the user has just given away
    /// is how you end up with two things bound to one key and no way to tell.
    /// </summary>
    private void BindPanicHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        // The bypass key gives way rather than the panic key. The panic key is the
        // one binding whose entire job is to still work when something else has gone
        // wrong, and a bypass is a convenience - so this is the same precedence
        // RegisterHotkeys applies, kept in one place so the two cannot disagree.
        if (string.Equals(HotkeyService.Normalise(_settings.BypassHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.BypassHotkey = string.Empty;
            Flash("Panic key took it from the bypass key");
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Panic key took it from " + clash.Name);
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.CrosshairHotkey = string.Empty;
            ShowSlotKey(CrosshairKeyBox, string.Empty);
            Flash("Panic key took it from the crosshair key");
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairCycleHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.CrosshairCycleHotkey = string.Empty;
            ShowSlotKey(CrosshairCycleKeyBox, string.Empty);
            Flash("Panic key took it from the crosshair cycle key");
        }

        _settings.EmergencyHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(PanicKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Panic key set to " + text);
    }


    /// <summary>
    /// Points the bypass key at a new combo, taking it off any slot that held it.
    /// <para>
    /// Same rule as the panic key, and deliberately weaker: the bypass key wins
    /// against a slot because the user is pointing at it, and loses against the
    /// panic key because the panic key has to keep working when something has gone
    /// wrong. It is refused outright if it is already the panic key's, rather than
    /// taking it - two controls bound to one chord is the state this whole method
    /// exists to prevent.
    /// </para>
    /// </summary>
    private void BindBypassHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        if (string.Equals(HotkeyService.Normalise(_settings.EmergencyHotkey), wanted, StringComparison.Ordinal))
        {
            // Refused rather than bound. Stealing it here would leave the bypass
            // keycap showing a chord that does nothing, and the user would have no
            // way to tell which of the two keycaps is lying.
            EndCapture(restore: true);
            Flash("That is the panic key already", true);
            return;
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Bypass key took it from " + clash.Name);
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.CrosshairHotkey = string.Empty;
            ShowSlotKey(CrosshairKeyBox, string.Empty);
            Flash("Bypass key took it from the crosshair key");
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairCycleHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.CrosshairCycleHotkey = string.Empty;
            ShowSlotKey(CrosshairCycleKeyBox, string.Empty);
            Flash("Bypass key took it from the crosshair cycle key");
        }

        _settings.BypassHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(BypassKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Bypass key set to " + text);
    }


    /// <summary>Backspace or Delete on the panic keycap drops the binding.</summary>
    private void ClearPanicHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EmergencyHotkey))
        {
            _settings.EmergencyHotkey = string.Empty;
            Flash("Panic key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(PanicKeyBox, _settings.EmergencyHotkey);
        RegisterHotkeys();
    }


    /// <summary>Backspace or Delete on the bypass keycap drops the binding.</summary>
    private void ClearBypassHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BypassHotkey))
        {
            _settings.BypassHotkey = string.Empty;
            Flash("Bypass key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(BypassKeyBox, _settings.BypassHotkey);
        RegisterHotkeys();
    }


    /// <summary>
    /// Points the crosshair show/hide key at a new combo. Refused outright if it
    /// is the panic key's or the bypass key's, because those two have to keep
    /// working; it takes the combo from a slot or from the cycle key, since the
    /// user is pointing at it.
    /// </summary>
    private void BindCrosshairHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        if (string.Equals(HotkeyService.Normalise(_settings.EmergencyHotkey), wanted, StringComparison.Ordinal))
        {
            EndCapture(restore: true);
            Flash("That is the panic key already", true);
            return;
        }

        if (string.Equals(HotkeyService.Normalise(_settings.BypassHotkey), wanted, StringComparison.Ordinal))
        {
            EndCapture(restore: true);
            Flash("That is the bypass key already", true);
            return;
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Crosshair key took it from " + clash.Name);
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairCycleHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.CrosshairCycleHotkey = string.Empty;
            ShowSlotKey(CrosshairCycleKeyBox, string.Empty);
            Flash("Crosshair key took it from the cycle key");
        }

        _settings.CrosshairHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(CrosshairKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Crosshair key set to " + text);
    }


    /// <summary>
    /// Points the crosshair cycle key at a new combo. Same refusals as the show/hide
    /// key, plus that key itself; it takes the combo from a slot.
    /// </summary>
    private void BindCrosshairCycleHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        if (string.Equals(HotkeyService.Normalise(_settings.EmergencyHotkey), wanted, StringComparison.Ordinal))
        {
            EndCapture(restore: true);
            Flash("That is the panic key already", true);
            return;
        }

        if (string.Equals(HotkeyService.Normalise(_settings.BypassHotkey), wanted, StringComparison.Ordinal))
        {
            EndCapture(restore: true);
            Flash("That is the bypass key already", true);
            return;
        }

        if (string.Equals(HotkeyService.Normalise(_settings.CrosshairHotkey), wanted, StringComparison.Ordinal))
        {
            EndCapture(restore: true);
            Flash("That is the crosshair key already", true);
            return;
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Cycle key took it from " + clash.Name);
        }

        _settings.CrosshairCycleHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(CrosshairCycleKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Crosshair cycle key set to " + text);
    }


    private void ClearCrosshairCycleHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CrosshairCycleHotkey))
        {
            _settings.CrosshairCycleHotkey = string.Empty;
            Flash("Crosshair cycle key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(CrosshairCycleKeyBox, _settings.CrosshairCycleHotkey);
        RegisterHotkeys();
    }


    private void ClearCrosshairHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CrosshairHotkey))
        {
            _settings.CrosshairHotkey = string.Empty;
            Flash("Crosshair key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(CrosshairKeyBox, _settings.CrosshairHotkey);
        RegisterHotkeys();
    }


    private void WirePanicKeycap()
    {
        WireKeycap(PanicKeyBox, KeycapCapture.Panic);
    }


    /// <summary>The bypass keycap, which is the panic keycap with another target.</summary>
    private void WireBypassKeycap()
    {
        WireKeycap(BypassKeyBox, KeycapCapture.Bypass);
    }


    private void OnHotkeyFailed(string text)
    {
        SetRail("KEY BUSY " + text, RailTone.Failed);
    }
}
