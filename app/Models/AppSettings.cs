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

    /// <summary>
    /// The screen the Display tab's Apply changes, by device name, or empty for all
    /// of them.
    /// <para>
    /// Empty is the default and the old behaviour, so a profile written before the
    /// picker existed keeps applying everywhere. It is deliberately not a property
    /// of the preset: a slot already carries its own screen, and a preset that
    /// pinned itself to a monitor would follow the monitor rather than the user's
    /// intention when the desk changes.
    /// </para>
    /// </summary>
    public string DisplayScope { get; set; } = string.Empty;

    public string ActiveAudioPresetId { get; set; } = "flat";

    public string OutputDeviceId { get; set; } = string.Empty;

    public string OutputDeviceName { get; set; } = string.Empty;

    public string FxSoundPath { get; set; } = AudioService.DefaultFxSoundPath;

    public bool GammaLock { get; set; } = true;

    public bool ShowOsd { get; set; } = true;

    public bool StartHidden { get; set; } = false;

    public bool CloseToTray { get; set; } = false;

    /// <summary>
    /// Master switch for automatic loading: when on, a slot bound to a game loads
    /// when that game launches or comes to the front, and (if
    /// <see cref="AutoPauseOnFocusLoss"/> is on) pauses and resumes with focus.
    /// <para>
    /// Not to be confused with starting Gamer Tool with Windows. That is not a
    /// setting at all: it lives in the per-user Run key and
    /// <see cref="StartupService"/> is its only reader.
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
    /// The crosshair loaded on the Crosshair tab: a built in id or one of
    /// <see cref="CustomCrosshairs"/>. Remembered so the tab opens on what was
    /// last used. Having one selected does not put anything on the screen.
    /// </summary>
    public string ActiveCrosshairId { get; set; } = "xh-classic";

    /// <summary>
    /// Global key that shows and hides the crosshair. Empty by default, like the
    /// bypass key, because a key nobody chose could be one a game already uses.
    /// </summary>
    public string CrosshairHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Global key that steps to the next crosshair in the list, so one can be
    /// swapped for another in the middle of a game. Empty by default for the same
    /// reason as <see cref="CrosshairHotkey"/>.
    /// </summary>
    public string CrosshairCycleHotkey { get; set; } = string.Empty;

    /// <summary>Crosshairs the user saved. The built in ones are not stored here.</summary>
    public List<CrosshairPreset> CustomCrosshairs { get; set; } = new();

    /// <summary>
    /// Whether a crosshair that is on the screen steps aside after a quarter of an hour
    /// without keyboard or mouse input, and comes back on the next one.
    /// <para>
    /// Off by default. Most panels are not OLED, and for everyone else a crosshair that
    /// disappeared while the mouse sat still would be a fault rather than a feature.
    /// It is not per crosshair, either: it is a fact about the monitor, and the monitor
    /// does not change when the crosshair does.
    /// </para>
    /// </summary>
    public bool OledGuard { get; set; }

    /// <summary>
    /// Bumped whenever a change has to be pushed into settings that already exist
    /// on disk. The property initialisers above only ever apply to a brand new
    /// profile, so anything that changes a default needs a matching step here or
    /// existing installs keep the old value forever.
    /// </summary>
    public int Schema { get; set; }

    /// <summary>
    /// The version this build writes.
    /// <para>
    /// Three is deliberately skipped. A development build that was never released
    /// wrote a schema-3 file, and it is still on at least one machine. Reusing the
    /// number would make that profile look up to date and it would never receive
    /// the step added in four.
    /// </para>
    /// </summary>
    public const int CurrentSchema = 4;

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
    /// <summary>
    /// Whether every setting is still the value a fresh install starts with.
    /// </summary>
    /// <remarks>
    /// Read once at launch to decide whether to explain the panic key, and it is
    /// deliberately a measurement rather than a remembered flag. A stored "have I
    /// shown the hint" boolean is another field in the profile, another thing to
    /// migrate, and one more piece of state that can be wrong - and this question
    /// can simply be asked of the settings that are already there.
    /// <para>
    /// The definition is "nothing has been changed yet", which is a narrower set
    /// than "never launched": a user who has been running this for a year without
    /// touching a single setting still reads as untouched. That is the right way
    /// round for a one-line hint. The worst case is a long-time user seeing a tip
    /// about a key that is genuinely still on its default, once - and the moment
    /// they change anything, including the panic key itself, it stops.
    /// </para>
    /// <para>
    /// Compared against a fresh instance rather than against a list of numbers, so
    /// a default that is changed later moves this with it instead of silently
    /// leaving the comparison wrong.
    /// </para>
    /// </remarks>
    public bool IsUntouched()
    {
        AppSettings fresh = new();

        return Schema == fresh.Schema
            && ActiveDisplayPresetId == fresh.ActiveDisplayPresetId
            && DisplayScope == fresh.DisplayScope
            && ActiveAudioPresetId == fresh.ActiveAudioPresetId
            && OutputDeviceId == fresh.OutputDeviceId
            && OutputDeviceName == fresh.OutputDeviceName
            && FxSoundPath == fresh.FxSoundPath
            && GammaLock == fresh.GammaLock
            && ShowOsd == fresh.ShowOsd
            && StartHidden == fresh.StartHidden
            && CloseToTray == fresh.CloseToTray
            && AutoSwitch == fresh.AutoSwitch
            && AutoRevertOnExit == fresh.AutoRevertOnExit
            && EmergencyHotkey == fresh.EmergencyHotkey
            && AutoPauseOnFocusLoss == fresh.AutoPauseOnFocusLoss
            && BypassHotkey == fresh.BypassHotkey
            && AntiClip == fresh.AntiClip
            && LoudGuard == fresh.LoudGuard
            && EffectsEnabled == fresh.EffectsEnabled
            && HardwareBrightnessEnabled == fresh.HardwareBrightnessEnabled
            && BlueLightFilter == fresh.BlueLightFilter
            && NightBlueLight == fresh.NightBlueLight
            && NightStartMinutes == fresh.NightStartMinutes
            && NightEndMinutes == fresh.NightEndMinutes
            && NightFadeMinutes == fresh.NightFadeMinutes
            && FxPromptDisabled == fresh.FxPromptDisabled
            && CustomDisplayPresets.Count == 0
            && CustomAudioPresets.Count == 0
            && CustomCombos.Count == 0
            && CustomCrosshairs.Count == 0
            && ActiveCrosshairId == fresh.ActiveCrosshairId
            && CrosshairHotkey == fresh.CrosshairHotkey
            && CrosshairCycleHotkey == fresh.CrosshairCycleHotkey
            && AppProfiles.Count == 0

            // Empty rather than compared against the shipped slots, and the
            // difference matters. A profile that has been through the engine has its
            // shipped slots materialised into the list, so comparing counts would
            // call a long-used install untouched; an empty list means no profile has
            // ever been saved, which is the thing actually being asked about. The
            // caller pairs this with a check that the slots are still the factory
            // ones, so a hand-written profile with no slots is not mistaken for new.
            && Slots.Count == 0;
    }

    /// <param name="monitorKey">
    /// Turns a saved display device name into the panel's stable key. Injected so
    /// the step that reads it can be tested without a monitor attached; the app
    /// passes the same enumeration the display service uses.
    /// </param>
    public void Migrate(Func<string, string>? monitorKey = null)
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

        if (Schema < 4)
        {
            AdoptMonitorKeys(monitorKey ?? DisplayService.MonitorKeyFor);
        }

        Schema = CurrentSchema;
    }

    /// <summary>
    /// Gives every slot that names a screen the panel key behind that name.
    /// <para>
    /// A profile written before keys existed only carries <c>\\.\DISPLAY2</c>, and
    /// the whole point of the key is to stop following a slot number. Filling it
    /// here means the next scan can resolve the screen even if Windows has
    /// renumbered it in the meantime.
    /// </para>
    /// <para>
    /// Skips a device that is not attached right now. The enumeration falls back
    /// to the device name in that case, and storing that would be storing the very
    /// thing the key exists to replace - so an absent screen is left keyless for a
    /// later scan rather than given a key that means nothing.
    /// </para>
    /// </summary>
    internal void AdoptMonitorKeys(Func<string, string> resolve)
    {
        foreach (HotkeySlot slot in Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.MonitorKey) || string.IsNullOrWhiteSpace(slot.MonitorDevice))
            {
                continue;
            }

            string key = resolve(slot.MonitorDevice);
            if (!string.IsNullOrWhiteSpace(key)
                && !string.Equals(key, slot.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            {
                slot.MonitorKey = key;
            }
        }
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

