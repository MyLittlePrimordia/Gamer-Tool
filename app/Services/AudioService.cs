using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class AudioService
{
    public const string DefaultFxSoundPath = @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe";

    /// <summary>How long a normal engine call is given before it is left alone.</summary>
    private const int ExitWaitMs = 4000;

    /// <summary>
    /// The shorter budget used on the way out, where nothing is left to show a
    /// wait and the process is about to be torn down. The emergency reset makes
    /// two of these calls, so at the full budget quitting could sit there for
    /// eight seconds with no window on screen and no sign anything was happening.
    /// </summary>
    private const int ShutdownWaitMs = 1200;

    private string _exePath = DefaultFxSoundPath;

    private FxSoundState? _cached;

    private DateTime _cachedAt;

    public event Action<string>? StatusChanged;

    public string ExePath
    {
        get => _exePath;

        // Validated rather than taken, because this string ends up as the
        // FileName of a process the app launches, and it arrives from two places
        // the user did not type: settings.json, and a backup file they may well
        // have been sent or downloaded. Without a check, restoring a profile
        // shared between two machines runs whatever the other machine's path
        // names, as this user, once per engine call.
        //
        // The rule is the file name, not the folder. FxSound is installable
        // anywhere, and this app already looks in four places for it, so pinning
        // the folder would break every custom install to close a hole that a name
        // check already closes. What must not be possible is pointing the engine
        // at something that is not the engine.
        set => _exePath = IsPlausibleFxSoundPath(value) ? value : DefaultFxSoundPath;
    }

    /// <summary>
    /// Whether a path could be the audio engine: absolute, and naming the engine
    /// itself. Pure, and static, so the schema guard and the tests can ask the
    /// same question the setter asks.
    /// </summary>
    public static bool IsPlausibleFxSoundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetFileName(path), "FxSound.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // A path the runtime will not even accept, which is the same answer.
            return false;
        }
    }

    public bool IsInstalled
    {
        get => File.Exists(_exePath);
    }

    public bool IsRunning
    {
        get
        {
            Process[] found;
            try
            {
                found = Process.GetProcessesByName("FxSound");
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                return found.Length > 0;
            }
            finally
            {
                // Every element holds an open handle to a running process. Only
                // the array itself is collectable, so dropping the array on the
                // floor leaks one handle per call, and this runs on every audio
                // apply. The watcher service disposes these for exactly this
                // reason.
                foreach (Process process in found)
                {
                    process.Dispose();
                }
            }
        }
    }

    public static string Round(double value, double step)
    {
        if (step <= 0.0)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        double rounded = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
        return rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Clean(string text)
    {
        return text.Replace("\"", string.Empty).Trim();
    }

    public void StartEngine()
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        if (IsRunning)
        {
            StatusChanged?.Invoke("SOUND ON");
            return;
        }

        if (Run("--run_minimized"))
        {
            StatusChanged?.Invoke("SOUND ON");
        }
    }

    public void SetOutputDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        Run("--output=\"" + Clean(deviceName) + "\"");
    }

    public void SavePresetInFxSound(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        Run("--save_preset=\"" + Clean(presetName) + "\"");
    }

    // Seven one-line command wrappers used to sit here: SetPower, SelectPreset,
    // OverwritePresetInFxSound, DeleteCurrentPreset, SetBandCount, SetBands and
    // SetEffects. Every one of them had a caller count of zero.
    //
    // They are not unused features - the same flags are all still reachable,
    // because an apply goes out through BuildApplyCommand and a bypass through
    // BuildBypassCommand, and EmergencyReset asks for --power=0 by calling Run
    // directly. What they were was a second, parallel spelling of how to reach
    // the engine, and a parallel spelling is one more thing to keep true. A
    // reader comparing a wrapper against the command builder had no way to tell
    // which of the two the app actually used.
    //
    // Where a scalar still has to be pushed on its own, the call is one line at
    // the point of use, which is where it can be seen to be used.

    /// <summary>
    /// The effect list for the engine, honouring the bypass.
    /// </summary>
    /// <remarks>
    /// An instance method rather than a static one, and that is the point: the
    /// bypass is a state of this service, so the full apply has to respect it too.
    /// While this was static, applying a preset while bypassed would have sent the
    /// real effect values and quietly switched the bypass back off without the
    /// switch ever moving.
    /// </remarks>
    public string EffectString(AudioPreset preset)
    {
        IReadOnlyList<double> wanted = ExpectedEffectValues(preset);
        List<string> parts = new(wanted.Count);
        for (int i = 0; i < wanted.Count; i++)
        {
            parts.Add(EffectNames[i] + ":" + wanted[i].ToString("0.0", CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }

    public static string BandString(IReadOnlyList<double> gains)
    {
        List<string> parts = new(gains.Count);
        for (int i = 0; i < gains.Count; i++)
        {
            // Two decimals, because a band moves in quarter steps. At one decimal a
            // quarter turned into 0.2 on the wire while the preset file beside it
            // said 0.25, so the live value and the saved one disagreed and the
            // apply came back reporting a drift that was really just rounding.
            parts.Add(i.ToString(CultureInfo.InvariantCulture) + ":" + gains[i].ToString("0.00", CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }



    /// <summary>
    /// The same list shape as <see cref="BandString"/>, for centre frequencies.
    /// A pair whose frequency falls outside the band's own allowed range is
    /// ignored by the engine without a word, which is why the values sent are the
    /// ones already resolved through <see cref="BandFrequencies"/> and why
    /// <see cref="Verify"/> reads the result back rather than assuming.
    /// </summary>
    public static string FrequencyString(IReadOnlyList<double> freqs)
    {
        List<string> parts = new(freqs.Count);
        for (int i = 0; i < freqs.Count; i++)
        {
            parts.Add(i.ToString(CultureInfo.InvariantCulture) + ":" + freqs[i].ToString("0.0", CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Headroom (in dB) that must be given back to the preamp so the largest EQ
    /// boost cannot push the sum past 0 dBFS. Industry-standard preset practice
    /// (Oratory1990 PEQ, RME ADI-2 AutoRef) is to offset by the largest boost;
    /// 0.5 dB is added as safety margin.
    /// </summary>
    public static double RequiredHeadroom(AudioPreset preset)
    {
        // The count the preset is really for, not the length of the array. The
        // band grid grows the array but never trims it, so a tune taken from a
        // 31-band preset and dropped to 10 kept a 31-long array, and this folded
        // band 25's boost into the preamp offset that is subtracted from the
        // master gain actually sent. The result was a tune that was several
        // decibels quieter than the curve asked for, for as long as the stale tail
        // survived - which Copy preserves, so every copy and every reapply kept it.
        int count = Math.Min(preset.Bands.Length, Math.Max(preset.NumBands, 1));
        double largest = 0.0;
        for (int i = 0; i < count; i++)
        {
            double value = preset.Band(i);
            if (value > largest)
            {
                largest = value;
            }
        }

        if (largest <= 0.0)
        {
            return 0.0;
        }

        return Math.Min(AudioPreset.GainMax, largest + 0.5);
    }

    public static double EffectiveMasterGain(AudioPreset preset, bool antiClip)
    {
        double gain = Math.Clamp(preset.MasterGain, AudioPreset.MasterGainMin, AudioPreset.MasterGainMax);
        if (!antiClip)
        {
            return gain;
        }

        return Math.Clamp(
            gain - RequiredHeadroom(preset),
            AudioPreset.MasterGainMin,
            AudioPreset.MasterGainMax);
    }

    public bool AntiClipEnabled { get; set; } = true;

    /// <summary>
    /// Whether a game that is louder than the rest is to be held down.
    /// <para>
    /// Off by default, because it is a change to what the sound is like rather
    /// than a correction to it, and a utility that quietly alters audio behind a
    /// switch nobody pressed is worse than one that does not offer it at all.
    /// </para>
    /// <para>
    /// It works through the engine's own volume levelling rather than by moving
    /// the Windows output level, which is the difference between a guard and a
    /// hazard. Touching the endpoint volume means fighting the mixer, the game's
    /// own loudness slider and whatever else has an opinion, and a meter that
    /// reacts to what it is measuring can run away on its own - a game at a
    /// transient would pull the master down and nothing would ever push it back.
    /// Levelling is the engine doing the thing it is for.
    /// </para>
    /// </summary>
    public bool LoudGuardEnabled { get; set; }

    /// <summary>
    /// The levelling the guard guarantees, which is the engine's own midpoint.
    /// <para>
    /// The built-in presets already range from none to the maximum of four, so
    /// this is a floor rather than a replacement: it lifts the presets that ask
    /// for nothing and leaves alone the ones that already want more or more than
    /// this. <see cref="AudioPreset.LevelingMax"/> is four.
    /// </para>
    /// </summary>
    public const double GuardLeveling = 3.0;

    /// <summary>
    /// The tune that will actually be sent, with the guard folded in.
    /// <para>
    /// Applied to a copy, always. The preset handed in is the one held in the
    /// settings, and it is what the panel is showing; if this modified it in
    /// place then turning the guard off would leave the saved tune quietly lifted,
    /// and the faders would disagree with the engine.
    /// </para>
    /// </summary>
    internal static AudioPreset WithLoudGuard(AudioPreset preset, bool enabled)
    {
        if (!enabled)
        {
            return preset;
        }

        AudioPreset guarded = preset.Copy();
        guarded.VolumeLeveling = Math.Max(guarded.VolumeLeveling, GuardLeveling);
        return guarded;
    }

    /// <summary>
    /// Whether the app's processing reaches the output at all: the equaliser bands
    /// and every effect, or none of them. <c>true</c> is the normal, live state.
    /// </summary>
    /// <remarks>
    /// It began as an equaliser-only switch, and became a whole-chain one the moment
    /// Clarity and Dynamic Boost were left running while it read "EQ off". Someone
    /// who throws a bypass and still hears the bass boosted has been told a lie by
    /// the label, and a control that looks broken is worse than a vague one. So it
    /// silences the whole chain.
    /// <para>
    /// It is a global state rather than part of a saved tune, so it lives beside
    /// AntiClip and not on the preset: someone who wants to hear the unprocessed
    /// signal wants that to hold for whatever tune they load next.
    /// </para>
    /// <para>
    /// Named for what it holds, not for the switch. This used to be called
    /// <c>BypassEnabled</c> and behaved the same way, so the name read as the
    /// opposite of the value and the BYPASS switch on the audio tab, wired straight
    /// to it, came up showing on while the equaliser was running. <see cref="BypassToggle"/>
    /// now holds the one inversion between the two.
    /// </para>
    /// </remarks>
    public bool EffectsEnabled { get; set; } = true;

    /// <summary>The effect names in the order the engine expects them.</summary>
    private static readonly string[] EffectNames = { "clarity", "ambience", "surround", "dynamicboost", "bass" };

    /// <summary>
    /// The five effect values, in the order the engine names them.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="ExpectedEffectValues"/>, which is what the engine is
    /// actually sent and what the read-back grades it against. Between them they
    /// decide what "correct" means for an effect, so the command and the check
    /// cannot drift apart and start arguing with each other.
    /// </remarks>
    private static double[] PresetEffectValues(AudioPreset preset) => new[]
    {
        preset.Clarity,
        preset.Ambience,
        preset.Surround,
        preset.DynamicBoost,
        preset.BassBoost,
    };

    /// <summary>
    /// What each effect is expected to read as, which while the bypass is thrown is
    /// not what the preset says.
    /// </summary>
    /// <remarks>
    /// Zero rather than "off", because the engine has no off for an effect: zero
    /// is where an effect does nothing, which is what off has to mean here.
    /// </remarks>
    public IReadOnlyList<double> ExpectedEffectValues(AudioPreset preset)
    {
        double[] wanted = PresetEffectValues(preset);
        if (EffectsEnabled)
        {
            return wanted;
        }

        return new double[wanted.Length];
    }

    /// <summary>
    /// The master gain the engine is expected to be on, which while the bypass is
    /// thrown is not the one the preset asks for.
    /// <para>
    /// Zero rather than the preset's, for the same reason the bands and the
    /// effects flatten: a bypass that leaves a track six decibels hot is not a
    /// bypass, and the loudness trim is as much a part of the tune as the curve
    /// is. Round tripped through the switch it comes back exactly as it was,
    /// because this is the same function the release path goes through.
    /// </para>
    /// </summary>
    public double ExpectedMasterGain(AudioPreset preset) =>
        EffectsEnabled ? EffectiveMasterGain(preset, AntiClipEnabled) : 0.0;

    /// <summary>
    /// The balance the engine is expected to be on, which while the bypass is
    /// thrown is centred rather than the preset's.
    /// <para>
    /// Zero is the engine's own default, so this is the setting going back to
    /// where it was found rather than a new value being invented. Balance is
    /// where a user is most likely to have drifted off centre without noticing,
    /// which is exactly the sort of thing a "let me hear it straight" switch is
    /// for.
    /// </para>
    /// </summary>
    public double ExpectedBalance(AudioPreset preset) =>
        EffectsEnabled ? Math.Clamp(preset.Balance, -20.0, 20.0) : 0.0;

    public string BuildApplyCommand(AudioPreset preset, string deviceName, bool selectPresetFile = true)
    {
        int count = ResolveBandCount(preset);

        // The band gains and the effects are not sent from here, and not because
        // they are unimportant: selecting a preset re-applies both of them, so
        // anything bundled in here is discarded. They go out in a command of their
        // own, once this has landed. BuildBypassCommand explains why.

        StringBuilderHelper helper = new();
        helper.Add("--power=1");
        helper.Add("--num_bands=" + count.ToString(CultureInfo.InvariantCulture));
        helper.Add("--master_gain=" + Round(ExpectedMasterGain(preset), 1.0));
        helper.Add("--volume_leveling=" + Round(Math.Clamp(preset.VolumeLeveling, AudioPreset.LevelingMin, AudioPreset.LevelingMax), 0.5));
        helper.Add("--filter_q=" + Round(Math.Clamp(preset.FilterQ, AudioPreset.FilterQMin, AudioPreset.FilterQMax), 0.5));
        helper.Add("--balance=" + Round(ExpectedBalance(preset), 1.0));

        // Centre frequencies travel as a documented running instance command, the
        // same as the gains below. Verified against FxSound 1.2.15: the values
        // land in the engine and read back out of status.json unchanged. The
        // frequency dials used to be the one control that could only reach the
        // engine by way of a preset file, because this flag was never sent.
        helper.Add("--set_band_freq=\"" + FrequencyString(BandFrequencies(preset)) + "\"");
        helper.Add("--set_effect=\"" + EffectString(preset) + "\"");

        // The preset is still selected, because the file written just before this
        // carries the curve itself and the engine has to pick that file up. Only
        // the equals form is sent: the documentation is explicit that a space
        // between an option and its value is parsed as two unrelated arguments
        // and the value is silently ignored, so the older space form was dead
        // weight. Selecting the same preset twice is harmless.
        //
        // Unless there is no file to select, which is the failed-write case above:
        // then this names a preset that is either absent or left over from an
        // earlier tune, and the engine applies that one's state over everything
        // else in this command line.
        if (selectPresetFile)
        {
            helper.Add("--preset=" + FxPresetFile.PresetName);
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            helper.Add("--output=\"" + Clean(deviceName) + "\"");
        }

        return helper.ToString();
    }

    /// <summary>
    /// The commands an apply takes, in the order they must be run.
    /// </summary>
    /// <remarks>
    /// Two of them, not one, and the split is not cosmetic. The engine applies a
    /// selected preset's band gains after it has finished parsing the command
    /// line, so gains sent in the same breath as the preset are discarded no
    /// matter where they are written. They have to go out in an invocation of
    /// their own once the preset has landed. Verified on FxSound 1.2.15 by
    /// replaying both shapes against the running engine: one invocation came back
    /// holding the curve regardless of order, two came back as sent.
    /// <para>
    /// The second command is the same one the bypass switch throws, so an apply
    /// while bypassed stays bypassed instead of quietly switching it back on.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> BuildApplyCommands(AudioPreset preset, string deviceName, bool selectPresetFile = true)
    {
        return new List<string>(2)
        {
            BuildApplyCommand(preset, deviceName, selectPresetFile),
            BuildBypassCommand(preset),
        };
    }

    public void Apply(AudioPreset preset, string deviceName)
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        // The guard is folded in here rather than at each caller because this is
        // the one place every push goes through: the Apply button, a slot hotkey,
        // a game launching, saving into FxSound, and the reset. It is deliberately
        // not in BuildApplyCommand, because that is what the reset and the exit
        // path use, and a guard that fought an attempt to put things back would be
        // indefensible.
        //
        // Both the curve written to the preset file and the values verified
        // afterwards come from the guarded copy, so the read-back compares what
        // was sent against what the engine reports rather than reporting a drift
        // that is really just the guard.
        AudioPreset sent = WithLoudGuard(preset, LoudGuardEnabled);

        // The curve cannot go across as a command.
        //
        // The return value is checked, which it was not. Write documents that a
        // null means "only the curve is lost, so the caller carries on" - and then
        // the caller sent --preset=GamerTool anyway. Selecting a preset makes the
        // engine apply that file's state after it finishes parsing the command
        // line, which is exactly why the band gains are already a command of their
        // own. So a failed write did not merely lose the curve: it made the engine
        // select a stale or absent GamerTool.fac, and the stale one's state won.
        // Run then returned true and the caller raised "SOUND ON".
        //
        // Carrying on is still right - the scalar values and the bypass command
        // that follow are unaffected - but selecting a file this call did not write
        // is not, so it is left out.
        bool wroteCurve = FxPresetFile.Write(sent, BandFrequencies(sent), EffectsEnabled) is not null;

        IReadOnlyList<string> commands = BuildApplyCommands(sent, deviceName, wroteCurve);
        for (int i = 0; i < commands.Count; i++)
        {
            string command = commands[i];
            if (i == 0 && !IsRunning)
            {
                command += " --run_minimized";
            }

            if (Run(command) && i == 0)
            {
                StatusChanged?.Invoke("SOUND ON");
            }
        }

        InvalidateCache();
    }


    /// <summary>
    /// The centre frequency for each band: the preset's own if it carries them,
    /// otherwise the engine's measured table for that band count.
    /// </summary>
    /// <summary>
    /// The centre frequencies a preset's gains belong to, resolved once.
    /// <para>
    /// Public because the exit path has to write the same curve file the apply
    /// path writes before it selects it, and it cannot reach a private helper from
    /// outside. Both halves of that are load-bearing: selecting a preset makes the
    /// engine apply the file's state after it parses the command line, so a reset
    /// that selects a file it did not write re-applies the tune that wrote it last.
    /// </para>
    /// </summary>
    public static IReadOnlyList<double> BandFrequencies(AudioPreset preset)
    {
        int count = preset.NumBands <= 0 ? AudioPreset.PresetBandCount : preset.NumBands;
        var freqs = new double[count];
        for (int i = 0; i < count; i++)
        {
            freqs[i] = AudioPreset.BandFrequency(count, preset.Frequencies, i);
        }

        return freqs;
    }

    /// <summary>
    /// Puts the engine back to flat and powers it back on. The band count is
    /// read back from the engine itself rather than assumed, so a reset never
    /// changes the filter layout: someone on thirty one bands who presses reset
    /// expects the gains zeroed, not the faders dropping to ten.
    /// </summary>
    public async System.Threading.Tasks.Task ResetSoundAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        // Through ReadState, which asks the engine to refresh and waits for the
        // file to change. Reading the file cold here used to pick up whatever was
        // last written, so a reset could flatten the wrong number of bands.
        //
        // The count is resolved rather than passed through. status.json is another
        // program's output, and nothing bounds num_bands in it - a value of two
        // billion reached AudioPreset.Flat, which sizes an array from it, and the
        // attempt was a sixteen gigabyte allocation on the pool thread. Flat now
        // refuses it too, but this is the boundary where an untrusted number enters
        // the app, so it is bounded here as well as downstream.
        int reported = ReadState()?.Equalizer.NumBands ?? AudioPreset.PresetBandCount;
        int bands = ResolveBandCount(reported);

        Run("--power=0");

        // Cancelled from here on, and every one of these points sits *after* the
        // engine has been powered down. That is what makes cancelling safe rather
        // than a new way to leave the engine in a half state: the caller wants the
        // engine off, and stopping here leaves it off having done strictly less than
        // it would otherwise have done.
        //
        // The gain over waiting for the reset to finish is that the wait is real.
        // A reset is three engine calls and two delays, and the exit path's own
        // budget is about a second per call - so joining a reset in flight would
        // have meant either a visible hang on quit or a timeout that reintroduces
        // the interleaving it was meant to avoid.
        if (!await SettleAsync(350, cancel))
        {
            TraceLog.Write("SOUND RESET stood down early: engine left powered down");
            return;
        }

        AudioPreset flat = AudioPreset.Flat(bands);
        flat.MasterGain = 0.0;
        flat.VolumeLeveling = 0.0;
        flat.FilterQ = 1.0;
        flat.Balance = 0.0;

        // The curve is written, and the gains are sent as a command of their own,
        // because a reset that did neither left the equaliser exactly where the
        // game had it. Both halves were missing here.
        //
        // Selecting a preset makes the engine apply that file's state after it
        // finishes parsing the command line - which is why Apply sends the gains
        // separately at all. So --preset=GamerTool below was not selecting the flat
        // curve built two lines up; it was selecting whatever GamerTool.fac last
        // held, and the last thing to write it was the game's tune. The reset
        // flattened master gain, volume levelling, filter Q, balance and the
        // effects, reported SOUND RESET, and left a ten band EQ in place - which
        // is a reset that looks like it worked and does not sound like one.
        //
        // Both halves are needed rather than one. The file is what --preset
        // selects, so without it the engine re-applies the game's curve. The
        // explicit gains are what actually zero the bands, so they still land when
        // the write fails - and a write that fails is the case where the engine is
        // left holding the stale file, which is the one that must not win.
        //
        // The loud guard is still not applied, and deliberately: this is an attempt
        // to put things back, and a guard that fought one would be indefensible.
        bool wroteCurve = FxPresetFile.Write(flat, BandFrequencies(flat), EffectsEnabled) is not null;

        IReadOnlyList<string> reset = BuildApplyCommands(flat, string.Empty, wroteCurve);
        for (int i = 0; i < reset.Count; i++)
        {
            if (cancel.IsCancellationRequested)
            {
                TraceLog.Write("SOUND RESET stood down early: engine left powered down");
                return;
            }

            // Spaced, not sent together. The second command carries the band gains
            // and has to land after the preset selection it is correcting.
            if (i > 0 && !await SettleAsync(200, cancel))
            {
                TraceLog.Write("SOUND RESET stood down early: engine left powered down");
                return;
            }

            Run(reset[i]);
        }

        // Before the power back on, and this is the boundary that matters most.
        // Reaching here means the engine is flat and switched off; powering it on
        // would undo what the caller is about to ask for.
        if (!await SettleAsync(200, cancel))
        {
            TraceLog.Write("SOUND RESET stood down before power on: engine left powered down");
            return;
        }

        Run("--power=1");
        InvalidateCache();
        StatusChanged?.Invoke("SOUND RESET");
    }

    /// <summary>
    /// A delay that reports whether it ran to completion rather than throwing when
    /// it does not.
    /// <para>
    /// <c>Task.Delay(ms, token)</c> throws <see cref="TaskCanceledException"/>, and
    /// a reset has four of these in a row. Letting the first one throw would skip
    /// the engine write that follows it, which is the opposite of what standing
    /// down means.
    /// </para>
    /// </summary>
    private static async Task<bool> SettleAsync(int ms, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            await System.Threading.Tasks.Task.Delay(ms, cancel);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>How an apply compared against what the engine went on to report.</summary>
    public enum ApplyOutcome
    {
        /// <summary>The engine is doing everything that was asked of it.</summary>
        Applied,

        /// <summary>Some of it took and some of it did not. The detail says which.</summary>
        Drifted,

        /// <summary>Nothing came back at all, so nothing can be claimed.</summary>
        Failed
    }


    /// <summary>
    /// What an apply actually did, as opposed to what was asked of it.
    /// </summary>
    public sealed class ApplyReport
    {
        public ApplyOutcome Outcome { get; init; }

        /// <summary>One line per value the engine did not take, phrased for a tooltip.</summary>
        public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    }


    /// <summary>
    /// The gain a band is expected to be sitting at, which is not always the gain
    /// in the preset: while bypassed, every band is expected to be flat.
    /// </summary>
    /// <remarks>
    /// This is the one place that decides what "correct" means for a band, so the
    /// read-back and the command that is sent cannot drift apart. It is also what
    /// makes the bypass verifiable. The bands used to be skipped entirely while
    /// bypassed, on the reasoning that a bypassed equaliser reports nothing - but
    /// it reports ten zeros, and ten zeros is precisely what was asked for, so the
    /// check runs in both states and can catch a bypass that did not take.
    /// </remarks>
    public double ExpectedBandGain(AudioPreset preset, int index)
    {
        if (!EffectsEnabled)
        {
            return 0.0;
        }

        return Math.Clamp(preset.Band(index), AudioPreset.GainMin, AudioPreset.GainMax);
    }

    /// <summary>
    /// The whole bypass in one command: the bands flat, the effects at zero, the
    /// master gain and the balance back to where the engine found them, or the
    /// lot back again.
    /// <para>
    /// Master gain and balance were missing from here, and the switch flattened
    /// the curve while leaving a track running six decibels hot and swung eight
    /// to one side. It read as a curve switch, so that is what everybody expected
    /// it to be, but everything the preset did to the signal was fair game and
    /// the two loudest parts of it were quietly not.
    /// </para>
    /// <para>
    /// They belong here rather than being left to the apply because this command
    /// carries no <c>--preset</c>, and that is the only arrangement the engine
    /// honours: it applies a selected preset's state after it finishes parsing,
    /// which is why the band gains are already a command of their own.
    /// </para>
    /// <para>
    /// Loud guard, deliberately, is not in the list. It is a safety control
    /// rather than part of the tune, and a switch that quietly turned off the
    /// thing holding a loud game down is not one anybody wants to find out about
    /// afterwards. The same reason the panic key does not re-arm it.
    /// </para>
    /// <para>
    /// Still not a full apply. The switch is meant to feel like a switch, so it
    /// must not drag the preset file, the band count, the filter shape or the
    /// output device along with it - a user who has a fader moved but not applied
    /// would find that move committed by an unrelated click.
    /// </para>
    /// </summary>
    public string BuildBypassCommand(AudioPreset preset)
    {
        int count = ResolveBandCount(preset);

        List<double> gains = new(count);
        for (int i = 0; i < count; i++)
        {
            gains.Add(ExpectedBandGain(preset, i));
        }

        return "--master_gain=" + Round(ExpectedMasterGain(preset), 1.0)
            + " --balance=" + Round(ExpectedBalance(preset), 1.0)
            + " --set_band_gain=\"" + BandString(gains) + "\""
            + " --set_effect=\"" + EffectString(preset) + "\"";
    }

    /// <summary>
    /// How many bands this preset is really for.
    /// <para>
    /// One answer, from one place, because this was written five times with two
    /// different rules and the disagreement was visible in adjacent lines of this
    /// one file. Three of the five checked the value against
    /// <see cref="AudioPreset.BandCounts"/> and two did not, so a preset naming
    /// thirteen bands was sent as <c>--num_bands=10</c> alongside thirteen
    /// <c>--set_band_freq</c> pairs - two different band layouts in a single command
    /// line - and then graded against thirteen when ten had been sent.
    /// </para>
    /// <para>
    /// The engine is told ten bands but handed thirteen frequencies, so the extras
    /// have nowhere to go and are silently dropped. That is a wrong sound rather
    /// than a failure, which is why it survived: the read-back agreed with what it
    /// had been told to expect rather than with what the engine could actually hold.
    /// </para>
    /// </summary>
    public static int ResolveBandCount(AudioPreset preset) => ResolveBandCount(preset.NumBands);

    /// <summary>
    /// The band count to actually use, given a number that came from somewhere
    /// else.
    /// <para>
    /// Takes the count rather than only the preset because two of the three
    /// callers do not have one: this file reads the count back off the engine
    /// during a reset, and <see cref="ProfileManager"/> normalises a saved preset.
    /// Both used to carry their own copy of the rule, which is how a profile
    /// naming thirteen bands was sent ten and then checked against thirteen.
    /// </para>
    /// </summary>
    public static int ResolveBandCount(int requested)
    {
        return requested <= 0 || !AudioPreset.BandCounts.Contains(requested)
            ? AudioPreset.PresetBandCount
            : requested;
    }

    /// <summary>Throws the bypass, or takes it off. See <see cref="BuildBypassCommand"/>.</summary>
    public bool PushBypass(AudioPreset preset)
    {
        return Run(BuildBypassCommand(preset));
    }

    /// <summary>
    /// Checks the bands, the effects, the master gain and the balance, and
    /// nothing else.
    /// <para>
    /// The full <see cref="Verify"/> cannot be used, because it also reports on
    /// the loud guard and the output device. The guard is a safety control the
    /// bypass deliberately leaves alone, so checking it here would grade the
    /// engine against a value the switch never asked to change. The output
    /// device is frequently mid-edit and unapplied when someone is fiddling with
    /// the switch, so a full check would answer a question nobody asked and turn
    /// a normal state into a spurious warning.
    /// </para>
    /// <para>
    /// Master gain and balance are the other way round from that: the switch
    /// *does* set them now, so a bypass that flattened the curve and left the
    /// track six decibels hot is exactly the drift this is here to catch, and it
    /// was not being caught because nothing compared them.
    /// </para>
    /// </summary>
    public ApplyReport VerifyBypass(AudioPreset preset)
    {
        if (!IsInstalled)
        {
            return new ApplyReport { Outcome = ApplyOutcome.Failed, Mismatches = new[] { "FxSound is not installed" } };
        }

        FxSoundState? state = ReadState(true);
        if (state is null)
        {
            return new ApplyReport { Outcome = ApplyOutcome.Failed, Mismatches = new[] { "The engine did not report its state" } };
        }

        List<string> off = new();

        // The same band count the command used, resolved the same way.
        // <c>BuildBypassCommand</c> falls back to the shipped default when the
        // count is not one of the layouts the engine has a table for, and this
        // used to fall back to a different default for anything at or below zero.
        // So a profile that named a count of, say, thirteen was sent ten bands
        // and then checked against thirteen, and the switch reported a drift on
        // bands that had never been asked for - which is the one thing a bypass
        // that is meant to be verifiable cannot afford.
        int count = ResolveBandCount(preset);
        for (int i = 0; i < count; i++)
        {
            if (i >= state.Equalizer.Bands.Count)
            {
                off.Add("band " + (i + 1) + " was not reported at all");
                continue;
            }

            Near(off, "band " + (i + 1) + " gain", state.Equalizer.Bands[i].Gain, ExpectedBandGain(preset, i), 0.1);
        }

        // The effects too, and this is the half that would have caught the original
        // problem. A switch labelled EQ that left the bass boosted was wrong, and
        // nothing was looking, because nothing here ever compared an effect while
        // the bypass was thrown.
        IReadOnlyList<double> wantedEffects = ExpectedEffectValues(preset);
        for (int i = 0; i < wantedEffects.Count && i < EffectNames.Length; i++)
        {
            Near(off, EffectNames[i], EffectValue(state, EffectNames[i]), wantedEffects[i], 0.2);
        }

        // The two the command also sets, and the two a bypass used to leave
        // alone. Both come from the same Expected methods the command does, so
        // the read-back and the wire cannot disagree about what bypassed means.
        Near(off, "master gain", state.Equalizer.MasterGain, ExpectedMasterGain(preset), 0.1);
        Near(off, "balance", state.Equalizer.Balance, ExpectedBalance(preset), 0.1);

        return off.Count == 0
            ? new ApplyReport { Outcome = ApplyOutcome.Applied, Mismatches = Array.Empty<string>() }
            : new ApplyReport { Outcome = ApplyOutcome.Drifted, Mismatches = off };
    }

    /// <summary>One effect as the engine is currently reporting it.</summary>
    private static double EffectValue(FxSoundState state, string name)
    {
        return name switch
        {
            "clarity" => state.Effects.Clarity,
            "ambience" => state.Effects.Ambience,
            "surround" => state.Effects.Surround,
            "dynamicboost" => state.Effects.DynamicBoost,
            "bass" => state.Effects.Bass,
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Reads the engine back and compares it with the tune that was sent.
    /// <para>
    /// This is the difference between "SOUND ON", which only means a process was
    /// launched, and something worth believing. Every value the apply command
    /// carries is checked against status.json, and anything that did not land is
    /// named. Bands are compared by index, and a band the engine did not report
    /// at all is a mismatch rather than something to shrug off, because a silent
    /// out of range pair is exactly how a frequency dial ends up doing nothing.
    /// </para>
    /// </summary>
    public ApplyReport Verify(AudioPreset preset, string deviceName)
    {
        if (!IsInstalled)
        {
            return new ApplyReport { Outcome = ApplyOutcome.Failed, Mismatches = new[] { "FxSound is not installed" } };
        }

        // Graded against the same tune that was actually sent, so a guard that is
        // switched on is not then reported as the engine failing to take the tune.
        // Applying it twice is harmless: it only ever raises a value.
        preset = WithLoudGuard(preset, LoudGuardEnabled);

        FxSoundState? state = ReadState(true);
        if (state is null)
        {
            return new ApplyReport { Outcome = ApplyOutcome.Failed, Mismatches = new[] { "The engine did not report its state" } };
        }

        List<string> off = new();

        if (!state.Power)
        {
            off.Add("DFX power is off, so nothing is being processed");
        }

        // The bands are checked in both states, against ExpectedBandGain: the
        // preset's own gain normally, and a flat zero while bypassed. Ten zeros is
        // what a bypassed equaliser is supposed to look like, so there is no longer
        // any reason to skip the comparison - and skipping it is what would have
        // let a bypass that quietly did nothing pass unremarked.
        int count = ResolveBandCount(preset);
        if (state.Equalizer.NumBands != count)
        {
            off.Add("bands " + state.Equalizer.NumBands + ", asked for " + count);
        }


        // ExpectedMasterGain, not EffectiveMasterGain. While the bypass is thrown
        // the apply sends a master gain of zero, so grading against the un-bypassed
        // value reported drift on every apply the user had deliberately bypassed -
        // and the amber "the engine did not take this sound" then sat on a preset
        // that was exactly right. VerifyBypass already used the Expected form;
        // this is the same fix on the other half.
        Near(off, "master gain", state.Equalizer.MasterGain, ExpectedMasterGain(preset), 0.1);
        Near(off, "leveling", state.Equalizer.VolumeLeveling, Math.Clamp(preset.VolumeLeveling, AudioPreset.LevelingMin, AudioPreset.LevelingMax), 0.3);
        Near(off, "filter Q", state.Equalizer.FilterQ, Math.Clamp(preset.FilterQ, AudioPreset.FilterQMin, AudioPreset.FilterQMax), 0.3);
        Near(off, "balance", state.Equalizer.Balance, Math.Clamp(preset.Balance, -20.0, 20.0), 0.1);

        IReadOnlyList<double> wantedFreqs = BandFrequencies(preset);
        for (int i = 0; i < count; i++)
        {
            if (i >= state.Equalizer.Bands.Count)
            {
                off.Add("band " + (i + 1) + " was not reported at all");
                continue;
            }

            FxBandState band = state.Equalizer.Bands[i];
            Near(off, "band " + (i + 1) + " gain", band.Gain, ExpectedBandGain(preset, i), 0.1);

            if (i < wantedFreqs.Count)
            {
                // Generous, because a monitor reports a rounded centre and the
                // engine snaps to its own table. Only a real miss shows up here.
                Near(off, "band " + (i + 1) + " centre", band.Frequency, wantedFreqs[i], Math.Max(2.0, wantedFreqs[i] * 0.02));
            }
        }


        // Against ExpectedEffectValues, so a bypassed apply is graded as five
        // correct zeros rather than as five effects that failed to land.
        IReadOnlyList<double> wantedEffects = ExpectedEffectValues(preset);
        for (int i = 0; i < wantedEffects.Count && i < EffectNames.Length; i++)
        {
            Near(off, EffectNames[i], EffectValue(state, EffectNames[i]), wantedEffects[i], 0.2);
        }

        if (!string.IsNullOrWhiteSpace(deviceName)
            && !string.IsNullOrWhiteSpace(state.SelectedOutput)
            && !string.Equals(deviceName.Trim(), state.SelectedOutput.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            off.Add("output is \"" + state.SelectedOutput + "\", not \"" + deviceName.Trim() + "\"");
        }

        return new ApplyReport
        {
            Outcome = off.Count == 0 ? ApplyOutcome.Applied : ApplyOutcome.Drifted,
            Mismatches = off
        };
    }


    private static void Near(List<string> off, string what, double actual, double wanted, double tolerance)
    {
        if (Math.Abs(actual - wanted) > tolerance)
        {
            off.Add(what + " is " + Fmt(actual) + ", asked for " + Fmt(wanted));
        }
    }


    private static string Fmt(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);


    public FxSoundState? ReadState(bool force = false)
    {
        if (!IsInstalled)
        {
            return null;
        }

        if (!force && _cached is not null && (DateTime.Now - _cachedAt).TotalSeconds < 3.0)
        {
            return _cached;
        }

        string path = FxSoundState.StatusPath;
        DateTime before = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;

        Run("--status");

        for (int attempt = 0; attempt < 12; attempt++)
        {
            Thread.Sleep(150);
            if (!File.Exists(path))
            {
                continue;
            }

            DateTime now = File.GetLastWriteTime(path);
            if (now <= before)
            {
                continue;
            }

            FxSoundState? state = FxSoundState.TryRead();
            if (state is not null)
            {
                _cached = state;
                _cachedAt = DateTime.Now;
                return state;
            }
        }

        FxSoundState? fallback = FxSoundState.TryRead();
        if (fallback is not null)
        {
            // Keep the values readable for diagnostics, but do not present a stale
            // snapshot as live engine state: mark it by zeroing the freshness stamp.
            fallback.FileWrittenUtc = DateTime.MinValue;
            _cached = fallback;
            _cachedAt = DateTime.Now;
        }

        return fallback;
    }

    public void InvalidateCache()
    {
        _cached = null;
        _cachedAt = DateTime.MinValue;
    }

    public IReadOnlyList<string> GetOutputDevices()
    {
        FxSoundState? state = ReadState();
        if (state is not null && state.OutputDevices.Count > 0)
        {
            return state.OutputDevices;
        }

        return Array.Empty<string>();
    }

    public string[] KnownPaths()
    {
        return new[]
        {
            DefaultFxSoundPath,
            @"C:\Program Files (x86)\FxSound LLC\FxSound\FxSound.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxSound", "FxSound.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FxSound", "FxSound.exe")
        }.Where(File.Exists).ToArray();
    }

    public bool Run(string arguments) => Run(arguments, ExitWaitMs);

    /// <summary>
    /// Hands a command to the engine and waits for it. The wait is bounded and
    /// the process is simply abandoned if it overruns, because the point of the
    /// call is to have started the engine, not to supervise it.
    /// </summary>
    public bool Run(string arguments, int waitMs)
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return false;
        }

        try
        {
            ProcessStartInfo info = new()
            {
                FileName = _exePath,

                // The quotes are left exactly as written, and that is deliberate.
                // ProcessStartInfo.Arguments is parsed with the C runtime rules,
                // which is what keeps --output="Some Device" in one piece, and the
                // engine wants each value to arrive as a single argument.
                //
                // This used to blanket-escape every quote to \" first, on the
                // understanding that the engine needed to see literal quote
                // characters. That is backwards, and it was quietly breaking the
                // two most important commands the app sends. Verified on FxSound
                // 1.2.15, from an identical starting state: with the escapes a
                // ten band list arrived as one band, and a device name came back
                // from the engine as "\". The backslashes reach the engine as
                // part of the value, and it stops reading at the first one. No
                // value can contain a quote of its own - Clean strips them from
                // device names - so passing the arguments through is safe.
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(info);
            if (process is null)
            {
                // Starting the engine failed outright - an antivirus that blocked
                // it, a path that stopped being the exe between the check and the
                // launch. It used to fall through and report success, so the
                // caller raised "SOUND ON" for a command that never ran.
                TraceLog.Write("FX could not start: " + arguments);
                return false;
            }

            // Bounded, because this runs on the way out of the app where
            // nothing is left to show a wait. The engine is given up on
            // rather than letting the process linger after the window has
            // already gone.
            bool exited = process.WaitForExit(waitMs);

            // The exit code is read, which it was not: a non-zero code from the
            // engine means it rejected the command line, and reporting that as
            // success is what let a failed apply go on to say "SOUND ON" while
            // the read-back - the only honest signal - came back a failure
            // separately. So the two halves of the UI disagreed.
            int code = exited ? process.ExitCode : -1;

            TraceLog.Write("FX " + arguments + (exited ? " exit=" + code : " no exit within " + waitMs + "ms"));
            return exited && code == 0;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("SOUND FAIL");
            TraceLog.Write("FX", ex);
            return false;
        }
    }


    private sealed class StringBuilderHelper
    {
        private readonly List<string> _parts = new();

        public void Add(string part)
        {
            _parts.Add(part);
        }

        public override string ToString()
        {
            return string.Join(" ", _parts);
        }
    }
}
