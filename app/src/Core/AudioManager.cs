using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Result of applying an audio preset. EqApplied covers the EqualizerAPO
/// config-file path; NativeLoudnessApplied is the optional Windows
/// per-endpoint "Loudness Equalization" enhancement toggle, which is
/// complementary (dynamic-range compression, not EQ) and works entirely
/// independently of EqualizerAPO.
/// </summary>
public sealed class AudioApplyResult
{
    public bool EqApplied { get; init; }
    public bool NativeLoudnessApplied { get; init; }
    public bool AnyBackendSucceeded => EqApplied || NativeLoudnessApplied;
}

/// <summary>
/// Audio pipeline: presets are rendered by EqualizerAPO (see
/// EqualizerApoManager for why - a from-scratch unsigned audio plugin is
/// silently refused by Windows on most modern PCs). This class handles the
/// per-endpoint property-store side (native loudness toggle, unchanged from
/// earlier versions) and delegates the actual EQ to EqualizerApoManager.
/// </summary>
public sealed class AudioManager
{
    private static readonly Lazy<AudioManager> _instance = new(() => new AudioManager());
    public static AudioManager Instance => _instance.Value;

    public static readonly int[] BandFrequenciesHz =
    {
        31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };

    private const uint STGM_READWRITE = 0x00000002;

    private AudioManager() { }

    public bool IsEngineEnabled => EqualizerApoManager.IsInstalled();

    /// <summary>
    /// Applies a 10-band preset: writes EqualizerAPO's config file (instant,
    /// all devices, no elevation) and toggles the optional native loudness
    /// enhancement via the endpoint property store.
    ///
    /// Anti-clip headroom: EqualizerAPO has no automatic gain-safety net
    /// either, so when the stored curve peaks above +3dB we write the whole
    /// curve shifted down so the peak lands at +3dB. Relative shape (what
    /// you hear as "footsteps forward") is bit-identical, absolute level
    /// drops a few dB (compensate with the Volume slider) instead of
    /// clipping transients at the DAC. Stored presets are never modified -
    /// only the written config reflects the shift.
    /// </summary>
    public AudioApplyResult ApplyPreset(double[] gainsDb, bool enableNativeLoudness, double preampDb = 0.0)
    {
        double peak = double.NegativeInfinity;
        for (int i = 0; i < gainsDb.Length; i++)
            if (gainsDb[i] > peak) peak = gainsDb[i];
        double headroomShift = peak > 3.0 ? peak - 3.0 : 0.0;

        var shiftedGains = new double[gainsDb.Length];
        for (int i = 0; i < gainsDb.Length; i++)
            shiftedGains[i] = gainsDb[i] - headroomShift;

        bool eqApplied = false;
        if (EqualizerApoManager.IsInstalled())
        {
            EqualizerApoManager.EnsureIncluded();
            eqApplied = EqualizerApoManager.WriteConfig(
                shiftedGains,
                Array.ConvertAll(BandFrequenciesHz, f => (double)f),
                preampDb - headroomShift);
        }

        bool loudnessApplied = TrySetNativeLoudnessEqualization(enableNativeLoudness);

        return new AudioApplyResult
        {
            EqApplied = eqApplied,
            NativeLoudnessApplied = loudnessApplied
        };
    }

    /// <summary>Flat-lines our EQ contribution (Default preset / panic reset).</summary>
    public void ClearEq()
    {
        if (EqualizerApoManager.IsInstalled())
            EqualizerApoManager.WriteFlatConfig(Array.ConvertAll(BandFrequenciesHz, f => (double)f));
        TrySetNativeLoudnessEqualization(false);
    }

    #region Native loudness endpoint toggle (unchanged from V1)

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
    /// The current default render endpoint's device ID string (e.g.
    /// "{0.0.0.00000000}.{guid}"), or null if it can't be determined.
    /// Used to detect real output-device changes so the "re-run Enable"
    /// prompt reflects the device actually in use, not a stale snapshot.
    /// </summary>
    public string? TryGetDefaultRenderEndpointId()
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;

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

            hr = device.GetId(out var id);
            return hr == 0 ? id : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device is not null && Marshal.IsComObject(device))
                Marshal.ReleaseComObject(device);
            if (enumeratorObj is not null && Marshal.IsComObject(enumeratorObj))
                Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    #endregion
}
