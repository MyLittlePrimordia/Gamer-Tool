using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Result of applying an audio preset, reported per-backend so the UI/tray can
/// show an accurate "native loudness EQ unavailable, using EqualizerAPO only"
/// (or vice versa) state instead of a misleading blanket success/failure.
/// </summary>
public sealed class AudioApplyResult
{
    public bool EqualizerApoApplied { get; init; }
    public bool NativeLoudnessApplied { get; init; }
    public bool AnyBackendSucceeded => EqualizerApoApplied || NativeLoudnessApplied;
}

/// <summary>
/// Bridges GamerTool's 10-band EQ and native loudness toggle to two independent
/// backends: EqualizerAPO (when installed, for true parametric filtering) and
/// Windows' own per-endpoint property store (for the built-in "Loudness
/// Equalization" driver enhancement, reached through undocumented CoreAudio COM
/// interfaces). Every native/undocumented call is wrapped so a failure degrades
/// the relevant feature to "unavailable" rather than crashing the app - the COM
/// GUIDs and property keys in this space are not published Microsoft contracts
/// and have shifted across Windows builds before, per the Phase 1 risk note.
/// </summary>
public sealed class AudioManager
{
    private static readonly Lazy<AudioManager> _instance = new(() => new AudioManager());
    public static AudioManager Instance => _instance.Value;

    public static readonly int[] BandFrequenciesHz =
    {
        31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };

    private const string EqApoRegistryKey = @"SOFTWARE\EqualizerAPO";
    private const string GamerToolBlockBegin = "# --- GamerTool Managed Block: DO NOT EDIT BELOW THIS LINE ---";
    private const string GamerToolBlockEnd = "# --- GamerTool Managed Block: END ---";

    private const uint STGM_READWRITE = 0x00000002;

    private AudioManager() { }

    public bool IsEqualizerApoInstalled() => TryGetEqApoConfigPath(out _);

    public bool TryGetEqApoConfigPath(out string configPath)
    {
        configPath = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EqApoRegistryKey);
            var installPath = key?.GetValue("InstallPath") as string;
            if (string.IsNullOrWhiteSpace(installPath))
                return false;

            var candidate = Path.Combine(installPath, "config", "config.txt");
            configPath = candidate;
            return File.Exists(candidate);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the 10-band gain values into EqualizerAPO's config.txt as a
    /// single GraphicEQ line, confined to a delimited GamerTool-owned block so
    /// any other filters the user has configured elsewhere in the file are
    /// preserved untouched.
    /// </summary>
    public bool ApplyEqualizerApoBands(double[] gainsDb)
    {
        if (gainsDb.Length != BandFrequenciesHz.Length)
            throw new ArgumentException($"Expected {BandFrequenciesHz.Length} gain values, got {gainsDb.Length}.");

        if (!TryGetEqApoConfigPath(out var configPath))
            return false;

        try
        {
            var lines = File.Exists(configPath)
                ? File.ReadAllLines(configPath).ToList()
                : new List<string>();

            RemoveExistingManagedBlock(lines);

            var points = string.Join("; ", BandFrequenciesHz.Select((freq, i) =>
                $"{freq} {gainsDb[i].ToString("0.0", CultureInfo.InvariantCulture)}"));

            lines.Add(GamerToolBlockBegin);
            lines.Add($"GraphicEQ: {points}");
            lines.Add(GamerToolBlockEnd);

            File.WriteAllLines(configPath, lines);
            return true;
        }
        catch
        {
            // Best-effort: EqualizerAPO's config file can be locked, permissions
            // can be restrictive, or the path can vanish between the check
            // above and the write. None of that should crash GamerTool.
            return false;
        }
    }

    /// <summary>
    /// Removes GamerTool's own EQ contribution from config.txt, leaving any of
    /// the user's other filters intact. Used when the "Default" preset is
    /// applied.
    /// </summary>
    public bool ClearEqualizerApoBands()
    {
        if (!TryGetEqApoConfigPath(out var configPath) || !File.Exists(configPath))
            return false;

        try
        {
            var lines = File.ReadAllLines(configPath).ToList();
            RemoveExistingManagedBlock(lines);
            File.WriteAllLines(configPath, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveExistingManagedBlock(List<string> lines)
    {
        int start = lines.FindIndex(l => l.Trim() == GamerToolBlockBegin);
        if (start < 0)
            return;

        int end = lines.FindIndex(start, l => l.Trim() == GamerToolBlockEnd);
        if (end < 0)
            end = lines.Count - 1;

        lines.RemoveRange(start, end - start + 1);
    }

    /// <summary>
    /// Attempts to read the native "Loudness Equalization" driver enhancement
    /// state from the current default audio render endpoint's property store.
    /// Returns null (not just false) when the property is unsupported on this
    /// endpoint/driver, so callers can distinguish "off" from "unavailable".
    /// </summary>
    public bool? TryGetNativeLoudnessEqualization()
    {
        return WithDefaultEndpointPropertyStore(store =>
        {
            var key = AudioPropertyKeys.PKEY_AudioEndpoint_Enable_Loudness_Equalization;
            int hr = store.GetValue(ref key, out var value);
            if (hr != 0 || value.vt != PROPVARIANT.VT_BOOL)
                return (bool?)null;
            return value.AsBool();
        });
    }

    /// <summary>
    /// Attempts to toggle the native "Loudness Equalization" driver
    /// enhancement. Returns false on any failure (unsupported driver, COM
    /// interface unavailable, permission denied) - GamerTool falls back to the
    /// EqualizerAPO path or surfaces "native loudness EQ unavailable" in the UI
    /// rather than throwing.
    /// </summary>
    public bool TrySetNativeLoudnessEqualization(bool enabled)
    {
        var result = WithDefaultEndpointPropertyStore<bool>(store =>
        {
            var key = AudioPropertyKeys.PKEY_AudioEndpoint_Enable_Loudness_Equalization;
            var value = PROPVARIANT.FromBool(enabled);
            int hr = store.SetValue(ref key, ref value);
            if (hr != 0)
                return false;

            hr = store.Commit();
            return hr == 0;
        });

        return result ?? false;
    }

    private T? WithDefaultEndpointPropertyStore<T>(Func<IPropertyStore, T?> action) where T : struct
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;

        try
        {
            var clsid = ComGuids.MMDeviceEnumerator;
            var iid = ComGuids.IID_IMMDeviceEnumerator;

            int hr = Ole32Native.CoCreateInstance(
                ref clsid, IntPtr.Zero, Ole32Native.CLSCTX_INPROC_SERVER, ref iid, out var enumeratorPtr);

            if (hr != 0 || enumeratorPtr == IntPtr.Zero)
                return null;

            enumeratorObj = Marshal.GetObjectForIUnknown(enumeratorPtr);
            Marshal.Release(enumeratorPtr);

            if (enumeratorObj is not IMMDeviceEnumerator enumerator)
                return null;

            hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev);
            if (hr != 0 || dev is null)
                return null;

            device = dev;

            hr = device.OpenPropertyStore(STGM_READWRITE, out var propStore);
            if (hr != 0 || propStore is null)
                return null;

            store = propStore;
            return action(store);
        }
        catch
        {
            // Any COM/marshaling failure here means this Windows build/driver
            // doesn't expose the interface the way GamerTool expects - treat
            // it as "feature unavailable", never as a crash.
            return null;
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store))
                Marshal.ReleaseComObject(store);
            if (device is not null && Marshal.IsComObject(device))
                Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null && Marshal.IsComObject(enumeratorObj))
                Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    /// <summary>
    /// High-level entry point used by PresetService: applies a 10-band audio
    /// preset via whichever backend is available, using EqualizerAPO for true
    /// parametric accuracy when installed and always attempting the native
    /// loudness toggle as well (the two are complementary, not mutually
    /// exclusive - EqualizerAPO shapes frequency response, native loudness EQ
    /// affects dynamic range compression).
    /// </summary>
    public AudioApplyResult ApplyPreset(double[] gainsDb, bool enableNativeLoudness)
    {
        bool apoApplied = IsEqualizerApoInstalled() && ApplyEqualizerApoBands(gainsDb);
        bool nativeApplied = TrySetNativeLoudnessEqualization(enableNativeLoudness);

        return new AudioApplyResult
        {
            EqualizerApoApplied = apoApplied,
            NativeLoudnessApplied = nativeApplied
        };
    }
}
