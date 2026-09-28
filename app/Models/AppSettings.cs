using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using GamerTool.Services;

namespace GamerTool.Models;

public sealed class AppSettings
{
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

    public bool StartWithWindows { get; set; } = false;

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
    /// Auto preamp: trims master gain by the largest EQ boost so boosted bands
    /// cannot hit 0 dBFS and hard-clip.
    /// </summary>
    public bool AntiClip { get; set; } = true;

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
    /// The brightness each display was at before this app first touched it, so it
    /// can be put back if the app is killed. A monitor left at 5% because
    /// something crashed is a bad afternoon, and this is the cheapest guard
    /// against it.
    /// </summary>
    public Dictionary<string, uint> OriginalHardwareBrightness { get; set; } = new();

    /// <summary>0 = off, 1 = warm, 2 = extra warm.</summary>
    public int BlueLightFilter { get; set; }

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

