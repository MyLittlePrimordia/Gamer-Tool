using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Result of applying an audio preset through the built-in engine.
/// NativeEqEngineApplied covers the DSP path; NativeLoudnessApplied is the
/// optional Windows per-endpoint "Loudness Equalization" enhancement toggle,
/// which is complementary (dynamic-range compression, not EQ).
/// </summary>
public sealed class AudioApplyResult
{
    public bool NativeEqApplied { get; init; }
    public bool NativeLoudnessApplied { get; init; }
    public bool AnyBackendSucceeded => NativeEqApplied || NativeLoudnessApplied;
}

/// <summary>
/// V2 audio pipeline: presets are rendered by GamerTool's OWN APO
/// (GamerToolAPO.dll) running inside audiodg.exe - no external app required.
/// This class only handles the per-endpoint property-store side (native
/// loudness toggle) and delegates the EQ itself to NativeEqEngine.
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

    public bool IsEngineEnabled => NativeEqEngine.Instance.IsEngineEnabled();

    /// <summary>
    /// Applies a 10-band preset: publishes gains to the built-in APO's shared
    /// config (instant, all devices) and toggles the optional native loudness
    /// enhancement via the endpoint property store.
    ///
    /// Anti-clip headroom (no protocol change): the APO struct has no preamp
    /// field, so when the stored curve peaks above +3dB we publish the whole
    /// curve shifted down so the peak lands at +3dB. Relative shape (what you
    /// hear as "footsteps forward") is bit-identical, absolute level drops a
    /// few dB (compensate with volume) instead of clipping transients at the
    /// DAC. Stored presets are never modified - only the published vector.
    /// </summary>
    public AudioApplyResult ApplyPreset(double[] gainsDb, bool enableNativeLoudness, double preampDb = 0.0, double compressionAmount = 0.0)
    {
        var floatGains = new float[gainsDb.Length];
        double peak = double.NegativeInfinity;
        for (int i = 0; i < gainsDb.Length; i++)
            if (gainsDb[i] > peak) peak = gainsDb[i];
        double headroomShift = peak > 3.0 ? peak - 3.0 : 0.0;

        for (int i = 0; i < gainsDb.Length; i++)
            floatGains[i] = (float)(gainsDb[i] - headroomShift);

        bool eqApplied = false;
        if (NativeEqEngine.Instance.IsEngineEnabled())
        {
            NativeEqEngine.Instance.PublishGains(floatGains, enabled: true, (float)preampDb, (float)compressionAmount);
            eqApplied = true;
        }

        bool loudnessApplied = TrySetNativeLoudnessEqualization(enableNativeLoudness);

        return new AudioApplyResult
        {
            NativeEqApplied = eqApplied,
            NativeLoudnessApplied = loudnessApplied
        };
    }

    /// <summary>Flat-lines our EQ contribution (Default preset / panic reset).</summary>
    public void ClearEq()
    {
        NativeEqEngine.Instance.ClearGains();
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
