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
        set => _exePath = string.IsNullOrWhiteSpace(value) ? DefaultFxSoundPath : value;
    }

    public bool IsInstalled
    {
        get => File.Exists(_exePath);
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName("FxSound").Length > 0;
            }
            catch (Exception)
            {
                return false;
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

    public void SetPower(bool on)
    {
        Run(on ? "--power=1" : "--power=0");
    }

    public void SetOutputDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        Run("--output=\"" + Clean(deviceName) + "\"");
    }

    public void SelectPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        Run("--preset=\"" + Clean(presetName) + "\"");
    }

    public void SavePresetInFxSound(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        Run("--save_preset=\"" + Clean(presetName) + "\"");
    }

    public void OverwritePresetInFxSound()
    {
        Run("--overwrite_preset");
    }

    public void DeleteCurrentPreset()
    {
        Run("--delete_preset");
    }

    public void SetBandCount(int count)
    {
        Run("--num_bands=" + count.ToString(CultureInfo.InvariantCulture));
    }

    public void SetBands(IReadOnlyList<double> gains)
    {
        if (gains.Count == 0)
        {
            return;
        }

        List<string> parts = new(gains.Count);
        for (int i = 0; i < gains.Count; i++)
        {
            double gain = Math.Clamp(gains[i], AudioPreset.GainMin, AudioPreset.GainMax);
            parts.Add(i.ToString(CultureInfo.InvariantCulture) + ":" + gain.ToString("0.0", CultureInfo.InvariantCulture));
        }

        Run("--set_band_gain=\"" + string.Join(",", parts) + "\"");
    }

    public void SetEffects(AudioPreset preset)
    {
        Run("--set_effect=\"" + EffectString(preset) + "\"");
    }

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
        int count = preset.Bands.Length;
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

    public string BuildApplyCommand(AudioPreset preset, string deviceName)
    {
        int count = preset.NumBands;
        if (count <= 0 || !AudioPreset.BandCounts.Contains(count))
        {
            count = AudioPreset.PresetBandCount;
        }

        // The band gains and the effects are not sent from here, and not because
        // they are unimportant: selecting a preset re-applies both of them, so
        // anything bundled in here is discarded. They go out in a command of their
        // own, once this has landed. BuildBypassCommand explains why.

        StringBuilderHelper helper = new();
        helper.Add("--power=1");
        helper.Add("--num_bands=" + count.ToString(CultureInfo.InvariantCulture));
        helper.Add("--master_gain=" + Round(EffectiveMasterGain(preset, AntiClipEnabled), 1.0));
        helper.Add("--volume_leveling=" + Round(Math.Clamp(preset.VolumeLeveling, AudioPreset.LevelingMin, AudioPreset.LevelingMax), 0.5));
        helper.Add("--filter_q=" + Round(Math.Clamp(preset.FilterQ, AudioPreset.FilterQMin, AudioPreset.FilterQMax), 0.5));
        helper.Add("--balance=" + Round(Math.Clamp(preset.Balance, -20.0, 20.0), 1.0));

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
        helper.Add("--preset=" + FxPresetFile.PresetName);

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
    public IReadOnlyList<string> BuildApplyCommands(AudioPreset preset, string deviceName)
    {
        return new List<string>(2)
        {
            BuildApplyCommand(preset, deviceName),
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

        // The curve cannot go across as a command.
        FxPresetFile.Write(preset, BandFrequencies(preset), EffectsEnabled);

        IReadOnlyList<string> commands = BuildApplyCommands(preset, deviceName);
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
    private static IReadOnlyList<double> BandFrequencies(AudioPreset preset)
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
    public async System.Threading.Tasks.Task ResetSoundAsync()
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        // Through ReadState, which asks the engine to refresh and waits for the
        // file to change. Reading the file cold here used to pick up whatever was
        // last written, so a reset could flatten the wrong number of bands.
        int bands = ReadState()?.Equalizer.NumBands ?? AudioPreset.PresetBandCount;

        Run("--power=0");
        await System.Threading.Tasks.Task.Delay(350);

        AudioPreset flat = AudioPreset.Flat(bands);
        flat.MasterGain = 0.0;
        flat.VolumeLeveling = 0.0;
        flat.FilterQ = 1.0;
        flat.Balance = 0.0;
        Run(BuildApplyCommand(flat, string.Empty));
        await System.Threading.Tasks.Task.Delay(200);
        Run("--power=1");
        InvalidateCache();
        StatusChanged?.Invoke("SOUND RESET");
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

        public bool IsClean => Outcome == ApplyOutcome.Applied;
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
    /// The whole bypass in one command: every band flat and every effect at zero,
    /// or the lot back again.
    /// </summary>
    /// <remarks>
    /// One invocation, because it is one idea, and because the engine applies a
    /// selected preset after parsing the whole line - so anything bundled with
    /// <c>--preset</c> is discarded. Deliberately not a full apply, though: the
    /// switch is meant to feel like a switch, so it must not drag the preset file,
    /// the master gain or the output device along with it. A user who has a fader
    /// moved but not applied would find that move silently committed by an
    /// unrelated click.
    /// </remarks>
    public string BuildBypassCommand(AudioPreset preset)
    {
        int count = preset.NumBands <= 0 || !AudioPreset.BandCounts.Contains(preset.NumBands)
            ? AudioPreset.PresetBandCount
            : preset.NumBands;

        List<double> gains = new(count);
        for (int i = 0; i < count; i++)
        {
            gains.Add(ExpectedBandGain(preset, i));
        }

        return "--set_band_gain=\"" + BandString(gains) + "\""
            + " --set_effect=\"" + EffectString(preset) + "\"";
    }

    /// <summary>Throws the bypass, or takes it off. See <see cref="BuildBypassCommand"/>.</summary>
    public bool PushBypass(AudioPreset preset)
    {
        return Run(BuildBypassCommand(preset));
    }

    /// <summary>
    /// Checks the bands and the effects, and nothing else.
    /// </summary>
    /// <remarks>
    /// The full <see cref="Verify"/> cannot be used to confirm a bypass, because it
    /// also reports on the master gain and the output device. Those are frequently
    /// mid-edit and unapplied when someone is fiddling with the switch, so a full
    /// check would answer a question nobody asked and turn a normal state into a
    /// spurious warning.
    /// </remarks>
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
        int count = preset.NumBands <= 0 ? AudioPreset.PresetBandCount : preset.NumBands;
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
        int count = preset.NumBands <= 0 ? AudioPreset.PresetBandCount : preset.NumBands;
        if (state.Equalizer.NumBands != count)
        {
            off.Add("bands " + state.Equalizer.NumBands + ", asked for " + count);
        }


        Near(off, "master gain", state.Equalizer.MasterGain, EffectiveMasterGain(preset, AntiClipEnabled), 0.1);
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
            if (process is not null)
            {
                // Bounded, because this runs on the way out of the app where
                // nothing is left to show a wait. The engine is given up on
                // rather than letting the process linger after the window has
                // already gone.
                process.WaitForExit(waitMs);
            }

            TraceLog.Write("FX " + arguments);
            return true;
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
