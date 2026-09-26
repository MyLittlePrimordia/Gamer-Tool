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
        helper.Add("--set_effect=\"" + EffectString(preset) + "\"");

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

    public async System.Threading.Tasks.Task ResetSoundAsync()
    {
        if (!IsInstalled)
        {
            StatusChanged?.Invoke("NO FXSOUND");
            return;
        }

        Run("--power=0");
        await System.Threading.Tasks.Task.Delay(350);

        AudioPreset flat = AudioPreset.Flat();
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
                Arguments = arguments,
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
