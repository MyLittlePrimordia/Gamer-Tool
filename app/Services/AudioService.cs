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

    public static string EffectString(AudioPreset preset)
    {
        return "clarity:" + preset.Clarity.ToString("0.0", CultureInfo.InvariantCulture)
            + ",ambience:" + preset.Ambience.ToString("0.0", CultureInfo.InvariantCulture)
            + ",surround:" + preset.Surround.ToString("0.0", CultureInfo.InvariantCulture)
            + ",dynamicboost:" + preset.DynamicBoost.ToString("0.0", CultureInfo.InvariantCulture)
            + ",bass:" + preset.BassBoost.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public static string BandString(IReadOnlyList<double> gains)
    {
        List<string> parts = new(gains.Count);
        for (int i = 0; i < gains.Count; i++)
        {
            parts.Add(i.ToString(CultureInfo.InvariantCulture) + ":" + gains[i].ToString("0.0", CultureInfo.InvariantCulture));
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

    public string BuildApplyCommand(AudioPreset preset, string deviceName)
    {
        int count = preset.NumBands;
        if (count <= 0 || !AudioPreset.BandCounts.Contains(count))
        {
            count = AudioPreset.PresetBandCount;
        }

        List<double> gains = new(count);
        for (int i = 0; i < count; i++)
        {
            gains.Add(Math.Clamp(preset.Band(i), AudioPreset.GainMin, AudioPreset.GainMax));
        }

        StringBuilderHelper helper = new();
        helper.Add("--power=1");
        helper.Add("--num_bands=" + count.ToString(CultureInfo.InvariantCulture));
        helper.Add("--master_gain=" + Round(EffectiveMasterGain(preset, AntiClipEnabled), 1.0));
        helper.Add("--volume_leveling=" + Round(Math.Clamp(preset.VolumeLeveling, AudioPreset.LevelingMin, AudioPreset.LevelingMax), 0.5));
        helper.Add("--filter_q=" + Round(Math.Clamp(preset.FilterQ, AudioPreset.FilterQMin, AudioPreset.FilterQMax), 0.5));
        helper.Add("--balance=" + Round(Math.Clamp(preset.Balance, -20.0, 20.0), 1.0));
        helper.Add("--set_band_gain=\"" + BandString(gains) + "\"");

        // Centre frequencies travel as a documented running instance command, the
        // same as the gains above. Verified against FxSound 1.2.15: the values
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

    public void Apply(AudioPreset preset, string deviceName)
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        // The curve cannot go across as a command.
        FxPresetFile.Write(preset, BandFrequencies(preset));

        string command = BuildApplyCommand(preset, deviceName);
        if (!IsRunning)
        {
            command += " --run_minimized";
        }

        if (Run(command))
        {
            StatusChanged?.Invoke("SOUND ON");
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

        int bands = FxSoundState.TryRead()?.Equalizer.NumBands ?? AudioPreset.PresetBandCount;

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
            Near(off, "band " + (i + 1) + " gain", band.Gain, Math.Clamp(preset.Band(i), AudioPreset.GainMin, AudioPreset.GainMax), 0.1);

            if (i < wantedFreqs.Count)
            {
                // Generous, because a monitor reports a rounded centre and the
                // engine snaps to its own table. Only a real miss shows up here.
                Near(off, "band " + (i + 1) + " centre", band.Frequency, wantedFreqs[i], Math.Max(2.0, wantedFreqs[i] * 0.02));
            }
        }

        Near(off, "clarity", state.Effects.Clarity, preset.Clarity, 0.2);
        Near(off, "ambience", state.Effects.Ambience, preset.Ambience, 0.2);
        Near(off, "surround", state.Effects.Surround, preset.Surround, 0.2);
        Near(off, "dynamic boost", state.Effects.DynamicBoost, preset.DynamicBoost, 0.2);
        Near(off, "bass boost", state.Effects.Bass, preset.BassBoost, 0.2);

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


    public FxSoundState? ReadState(bool force = false)    {
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

    public bool Run(string arguments)
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

                // The quotes have to survive the trip. FxSound parses its own
                // command line and only accepts a value that is still quoted when
                // it arrives, but ProcessStartInfo.Arguments is itself parsed with
                // the C runtime rules, which eat a plain double quote pair. So
                // --set_band_gain="0:6.0" reaches FxSound as --set_band_gain=0:6.0
                // and is silently ignored, and every band gain, effect and output
                // name in this app was going nowhere. Verified both ways against
                // the running engine: unquoted leaves the EQ untouched, quoted
                // changes it.
                Arguments = EscapeArgumentQuotes(arguments),
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(info);
            if (process is not null)
            {
                process.WaitForExit(4000);
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


    /// <summary>
    /// Turns every plain double quote in a command line into an escaped one, so
    /// the value keeps its quotes when the target process parses its arguments.
    /// </summary>
    private static string EscapeArgumentQuotes(string arguments)
    {
        return arguments.IndexOf('"') < 0 ? arguments : arguments.Replace("\"", "\\\"");
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
