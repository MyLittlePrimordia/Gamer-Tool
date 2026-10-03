using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using GamerTool.Services;

namespace GamerTool.Models;

public sealed class AppSettings
{
    /// <summary>
    /// Guards this profile against being read while something is writing to it.
    /// <para>
    /// The profile is not only touched by the window. The backlight worker runs on
    /// a pool thread and writes to it - the exclusion list when a display refuses,
    /// and the remembered brightness on every successful write - while the window
    /// can be serialising the whole thing from a click handler at the same moment.
    /// <c>Dictionary</c> and <c>List</c> are not safe against that: one throws or
    /// silently produces half a file, and the symptom is a settings file that has
    /// lost a slot or a preset.
    /// </para>
    /// <para>
    /// It lives on the profile rather than in any one service because the profile
    /// is the thing being protected, and because the two ends are in different
    /// files: the backlight service holds this reference to write, and
    /// <see cref="Services.ProfileManager"/> holds it to serialise. A lock in
    /// either one alone would only cover half the problem.
    /// </para>
    /// <para>
    /// It is never held across I/O. A bus write or a disk write inside this lock
    /// would turn every other reader into a stall, which is the failure this is
    /// here to prevent.
    /// </para>
    /// </summary>
    internal object Gate { get; } = new();

    /// <summary>
    /// A fresh install opens on the two neutral presets, not on one of the
    /// game tunes. Starting on a preset that boosts shadows and lifts the low end
    /// would quietly alter the screen and the sound before the user has touched
    /// anything, which is a strange thing for a utility to do on first launch.
    /// </summary>
    public string ActiveDisplayPresetId { get; set; } = "flat";

    public string ActiveAudioPresetId { get; set; } = "flat";

    public string OutputDeviceId { get; set; } = string.Empty;

    public string OutputDeviceName { get; set; } = string.Empty;

    public string FxSoundPath { get; set; } = AudioService.DefaultFxSoundPath;

    public bool GammaLock { get; set; } = true;

    public bool ShowOsd { get; set; } = true;

    public bool StartHidden { get; set; } = false;

    public bool CloseToTray { get; set; } = false;

    /// <summary>
    /// Not stored here on purpose.
    /// <para>
    /// This used to be a settings field, and it was a lie. The switch that starts
    /// the app with Windows writes an entry under the current user's Run key, and
    /// that registry entry is the only thing that decides whether it happens - the
    /// field was written from the registry's answer and then never read back.
    /// So it appeared in settings.json, it looked like editing the file by hand
    /// would turn the feature on, and doing exactly that did nothing.
    /// </para>
    /// <para>
    /// The registry is the right place for this one. It is per-user rather than
    /// per-install, so a copy of the executable on a second drive does not claim
    /// the same start-up entry, and the user can turn it off in Task Manager's
    /// Startup tab without knowing this app exists. <see cref="StartupService"/>
    /// is the single reader.
    /// </para>
    /// </summary>
    public bool AutoSwitch { get; set; } = false;

    /// <summary>
    /// Put the screen and sound back to neutral when a game that was auto loaded
    /// closes, instead of leaving the boost running for whatever comes next.
    /// <para>
    /// On by default because the failure it prevents is the common one: the app is
    /// left looking correct for a game that is no longer running, which reads as a
    /// bug in Gamer Tool rather than as a boost the user asked for and forgot
    /// about. It only ever undoes a slot the app applied by itself, so a tune the
    /// user loaded on purpose is never touched.
    /// </para>
    /// </summary>
    public bool AutoRevertOnExit { get; set; } = true;

    /// <summary>
    /// Global key that puts the screen and the sound straight back to neutral,
    /// for when a boost has gone wrong and the window is not reachable.
    /// <para>
    /// This exists because the tray can do it but only if you can get to the tray,
    /// and quitting only helps once you have closed the app. A game that renders
    /// unplayable while a gamma ramp is stuck is exactly the moment you cannot
    /// click anything.
    /// </para>
    /// <para>
    /// Defaulted rather than left empty on purpose: a safety net that has to be
    /// configured before it works is not one, and this key is deliberately
    /// awkward to hit by accident. It is a normal bindable hotkey, so it can be
    /// moved like any other if it lands on something.
    /// </para>
    /// </summary>
    public string EmergencyHotkey { get; set; } = "CTRL+ALT+F12";

    /// <summary>
    /// Put the screen back to neutral when the user alt-tabs out of a game the
    /// app loaded, and bring it back when they return.
    /// <para>
    /// Screen only, and that is the whole design decision rather than a
    /// limitation. Every FxSound call spawns a process and
    /// <c>ResetSoundAsync</c> powers the engine off and on again with settle delays,
    /// so doing that on every alt-tab means audible gaps and pops on the way to a
    /// browser. A shadow-boosted screen on Discord is mildly wrong; a click of
    /// silence every time somebody checks a message is worse.
    /// </para>
    /// <para>
    /// Off by default because it changes behaviour for somebody who wants the
    /// picture to stay as it is. The sound staying loaded is why this is tolerable
    /// once it is on: the user still hears their game, only the screen goes
    /// ordinary.
    /// </para>
    /// </summary>
    public bool AutoPauseOnFocusLoss { get; set; }

    /// <summary>
    /// How long the user has to be away from the game before the screen is
    /// paused, in seconds.
    /// <para>
    /// Not a setting. A notification, an overlay flashing up, or the brief moment
    /// an alt-tab passes through another window would each flick the screen if the
    /// answer were instant, and a filter that pulses on every notification is one
    /// people switch off. Two seconds is long enough to walk the mouse somewhere
    /// else and short enough that nobody reads the pause as lag.
    /// </para>
    /// </summary>
    internal const int FocusPauseGraceSeconds = 2;

    /// <summary>
    /// Optional key that throws the sound bypass, for A/B-ing a tune without
    /// leaving the game.
    /// <para>
    /// Empty by default, and that is deliberate where the panic key is not. The
    /// panic key defaults to a chord because a safety net that has to be configured
    /// before it works is not one. This is a convenience, and a convenience that
    /// claimed a chord nobody chose would take it away from whatever the user
    /// actually uses it for. The tray is the discoverable way in.
    /// </para>
    /// <para>
    /// Global rather than per slot, like the bypass setting itself: someone who
    /// wants to compare two games wants to throw the same switch in both.
    /// </para>
    /// </summary>
    public string BypassHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Auto preamp: trims master gain by the largest EQ boost so boosted bands
    /// cannot hit 0 dBFS and hard-clip.
    /// </summary>
    public bool AntiClip { get; set; } = true;

    /// <summary>
    /// Whether a loud game is to be held down through the engine's own volume
    /// levelling. Off by default, and global rather than part of a saved tune for
    /// the same reason the bypass is: someone who wants their games quieter wants
    /// that to hold for whatever they load next.
    /// </summary>
    public bool LoudGuard { get; set; }

    /// <summary>
    /// Whether the equaliser bands and the effects reach the output at all.
    /// <c>true</c> is the normal state: the curve and the effects are live.
    /// <c>false</c> is the bypass: every band flat and every effect at zero.
    /// </summary>
    /// <remarks>
    /// Global rather than part of a saved tune, so throwing it holds for whatever
    /// sound preset is loaded next.
    /// <para>
    /// Named for what it holds rather than for the switch that drives it, because
    /// the two genuinely point opposite ways: the user throws a BYPASS, and this
    /// goes false. It used to be called <c>BypassEnabled</c> while behaving
    /// exactly like this, so <c>BypassEnabled == true</c> meant the chain was
    /// live. The setting was therefore named for the opposite of its own value,
    /// the BYPASS switch on the audio tab was wired straight to it, and the app
    /// came up with BYPASS showing on while the equaliser was running. A name
    /// that has to be second-guessed is worse than no name, so the setting says
    /// what it holds and <see cref="Services.BypassToggle"/> holds the one
    /// deliberate inversion between it and the switch.
    /// </para>
    /// </remarks>
    public bool EffectsEnabled { get; set; } = true;

    /// <summary>
    /// The name <see cref="EffectsEnabled"/> had before it was renamed, read once
    /// and then discarded so an existing profile keeps the state it was saved in.
    /// </summary>
    /// <remarks>
    /// The rename changed the name, not the meaning, so the value carries across
    /// untouched. A profile that never had the key leaves this null and keeps the
    /// initialiser, which is processing on, so an install that predates the switch
    /// is not left muted.
    /// </remarks>
    [JsonPropertyName("BypassEnabled")]
    public bool? LegacyBypassEnabled { get; set; }

    /// <summary>
    /// Hardware backlight control over DDC/CI. Off by default and opt-in on
    /// purpose: it talks to the monitor over I2C, which is the one thing in this
    /// app that can upset hardware. The software gamma ramp is unaffected either
    /// way and remains the fallback everywhere.
    /// </summary>
    public bool HardwareBrightnessEnabled { get; set; }

    /// <summary>
    /// Displays that have been put off limits after misbehaving, by device name.
    /// They are skipped rather than probed, so one bad display cannot be asked
    /// again on every launch.
    /// </summary>
    public List<string> ExcludedDdcMonitors { get; set; } = new();

    /// <summary>
    /// Consecutive probe rounds in which no display could be reached.
    /// <para>
    /// Persisted, and that is the whole point. The probe runs at most once per
    /// launch, so a counter living in memory can only ever reach one, and a rule
    /// that says "give up after two" is a rule that never fires. Carried across
    /// launches, the second round is next time the user opens the Display tab,
    /// which is also a more honest measure: a feature that failed on two separate
    /// occasions with the machine freshly started both times is not working.
    /// </para>
    /// </summary>
    public int HardwareBrightnessFailedRounds { get; set; }

    /// <summary>
    /// True when the app turned the feature off by itself, as opposed to the user
    /// having turned it off.
    /// <para>
    /// Persisted for the same reason as the counter, and because the settings tab
    /// has to be able to explain a switch that is already off before anything has
    /// been probed this session. A switch that changes itself silently is
    /// indistinguishable from a bug.
    /// </para>
    /// </summary>
    public bool HardwareBrightnessRetired { get; set; }

    /// <summary>
    /// The brightness each display was at before this app first touched it, so it
    /// can be put back if the app is killed. A monitor left at 5% because
    /// something crashed is a bad afternoon, and this is the cheapest guard
    /// against it.
    /// </summary>
    public Dictionary<string, uint> OriginalHardwareBrightness { get; set; } = new();

    /// <summary>0 = off, 1 = warm, 2 = extra warm.</summary>
    public int BlueLightFilter { get; set; }

    /// <summary>
    /// Whether the blue light filter should come on by itself between two hours
    /// of the day.
    /// <para>
    /// Times are stored as minutes since midnight rather than as "8 PM", because
    /// they are arithmetic: the question every tick asks is "is the current time
    /// inside this window", and midnight is the seam that question is usually
    /// wrong about. The pickers say "8 PM" and convert on the way in and out.
    /// </para>
    /// </summary>
    public bool NightBlueLight { get; set; }

    public int NightStartMinutes { get; set; } = NightSchedule.DefaultStartMinutes;

    public int NightEndMinutes { get; set; } = NightSchedule.DefaultEndMinutes;

    /// <summary>
    /// How long the filter takes to reach full strength at the start of the
    /// window, and to fade back out at the end of it.
    /// <para>
    /// Zero means no fade: the filter arrives all at once, which is what this did
    /// before the fade existed and what makes turning the feature off a complete
    /// return to the old behaviour rather than an approximation of it.
    /// </para>
    /// <para>
    /// A setting rather than a control on purpose. The app's rule is that a
    /// preference nobody asks for does not get a row, and a fade length is a
    /// preference almost nobody has an opinion about - thirty minutes is right for
    /// everyone or close enough. It is here so a profile that wants a different one,
    /// or none, can say so in settings.json.
    /// </para>
    /// </summary>
    public int NightFadeMinutes { get; set; } = NightFade.DefaultFadeMinutes;

    /// <summary>
    /// Set once the user has been offered the FxSound install on launch. Stops a
    /// machine with no FxSound from being nagged on every single start.
    /// </summary>
    public bool FxPromptDisabled { get; set; }

    public List<AppProfile> AppProfiles { get; set; } = new();

    public List<HotkeySlot> Slots { get; set; } = new();

    public List<DisplayPreset> CustomDisplayPresets { get; set; } = new();

    public List<AudioPreset> CustomAudioPresets { get; set; } = new();

    public List<ComboPreset> CustomCombos { get; set; } = new();

    /// <summary>
    /// Bumped whenever a change has to be pushed into settings that already exist
    /// on disk. The property initialisers above only ever apply to a brand new
    /// profile, so anything that changes a default needs a matching step here or
    /// existing installs keep the old value forever.
    /// </summary>
    public int Schema { get; set; }

    /// <summary>The version this build writes.</summary>
    public const int CurrentSchema = 2;

    /// <summary>
    /// Brings settings written by an older build up to date.
    ///
    /// Schema 1 moved the opening tune from one of the game presets to the two
    /// neutral ones. Without this an existing install would carry on opening on
    /// the old tune, because a changed property initialiser says nothing about
    /// values that were already saved.
    /// <para>
    /// Schema 2 is the <c>BypassEnabled</c> to <see cref="EffectsEnabled"/>
    /// rename. Nothing about the value changed, so this only has to move it across
    /// under the new name.
    /// </para>
    /// </summary>
    public void Migrate()
    {
        if (Schema >= CurrentSchema)
        {
            return;
        }

        if (Schema < 1)
        {
            ActiveDisplayPresetId = "flat";
            ActiveAudioPresetId = "flat";
        }

        if (Schema < 2)
        {
            AdoptLegacyBypass();
        }

        Schema = CurrentSchema;
    }

    /// <summary>
    /// Folds a pre-rename <c>BypassEnabled</c> key into <see cref="EffectsEnabled"/>,
    /// then forgets it so it is not written back out on every save.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once, and safe to call on a profile that never had
    /// the key. <see cref="ProfileManager.Normalize"/> calls it on every load as
    /// well as from here, because restoring a backup replaces the whole profile
    /// without passing through the startup migration, and a restored profile has
    /// to keep its bypass state for the same reason a loaded one does.
    /// </remarks>
    public void AdoptLegacyBypass()
    {
        if (!LegacyBypassEnabled.HasValue)
        {
            return;
        }

        // Straight copy, not an inversion. The old name was wrong and the value was
        // not: BypassEnabled == true already meant the bands and effects were
        // live, which is what EffectsEnabled == true means.
        EffectsEnabled = LegacyBypassEnabled.Value;
        LegacyBypassEnabled = null;
    }


}

