using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

public sealed class AudioEndpoint
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; }

    /// <summary>
    /// HDMI/DisplayPort audio attached to a monitor. These endpoints have no
    /// speakers, so routing game audio here is silent.
    /// </summary>
    public bool LooksLikeDisplay => AudioDeviceService.LooksLikeDisplayOutput(Name);
}

public sealed class AudioDeviceService
{
    private const int ErEof = unchecked((int)0x80070490);
    private const int MmDeviceStateActive = 0x1;
    private const int DataFlowRender = 0;

    private static readonly string[] DisplayHints =
    {
        "AG276", "AMD High Definition", "Display Audio", "Monitor",
        "HDMI", "DisplayPort", "DP ", "TV", "Intel Display"
    };

    public static bool LooksLikeDisplayOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (string hint in DisplayHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public string DefaultOutputName()
    {
        try
        {
            return GetDefault(DataFlowRender, 1) ?? string.Empty;
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIODEV", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Active playback endpoints with the default one flagged. Falls back to an
    /// empty list when Core Audio is unavailable.
    /// </summary>
    public IReadOnlyList<AudioEndpoint> ListOutputs()
    {
        List<AudioEndpoint> list = new();
        try
        {
            IMMDeviceEnumerator? enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                return list;
            }

            string defaultId = string.Empty;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, 1, out IMMDevice? def) == 0 && def is not null)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }

            if (enumerator.EnumAudioEndpoints(DataFlowRender, MmDeviceStateActive, IntPtr.Zero, out IntPtr collection) != 0
                || collection == IntPtr.Zero)
            {
                return list;
            }

            var devices = (IEnumAudioDevices)Marshal.GetObjectForIUnknown(collection);
            try
            {
                while (devices.Next(1, out IntPtr item, out int fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var device = (IMMDevice)Marshal.GetObjectForIUnknown(item);
                        try
                        {
                            device.GetId(out string id);
                            string name = FriendlyName(device);
                            if (name.Length == 0)
                            {
                                name = id;
                            }

                            list.Add(new AudioEndpoint
                            {
                                Id = id,
                                Name = name,
                                IsDefault = id.Length > 0 && id.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
                                IsActive = true
                            });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                    finally
                    {
                        Marshal.Release(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIODEV", ex);
        }

        return list;
    }

    /// <summary>
    /// Active, non-display playback endpoints.
    /// </summary>
    public IReadOnlyList<AudioEndpoint> ListSpeakers()
    {
        return ListOutputs().Where(endpoint => !endpoint.LooksLikeDisplay).ToList();
    }

    private static string? GetDefault(int dataFlow, int role)
    {
        IMMDeviceEnumerator? enumerator = CreateEnumerator();
        if (enumerator is null)
        {
            return null;
        }

        if (enumerator.GetDefaultAudioEndpoint(dataFlow, role, out IMMDevice? device) != 0 || device is null)
        {
            return null;
        }

        try
        {
            return FriendlyName(device);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out IPropertyStore? opened) != 0 || opened is null)
            {
                return string.Empty;
            }

            store = opened;
            PropKey key = new()
            {
                FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                PropertyId = 14
            };

            if (store.GetValue(ref key, out PropVariant value) != 0)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(value.Pointer) ?? string.Empty;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIODEV", ex);
            return string.Empty;
        }
        finally
        {
            if (store is not null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        Type? type = Type.GetTypeFromCLSID(EnumeratorClsid);
        if (type is null)
        {
            return null;
        }

        return Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, IntPtr options, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        int GetDeviceCount(int includeDisconnected, out int count);
        int GetDeviceId(int index, [MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAudioDevices
    {
        int GetCount(out int count);
        int Item(int index, out IntPtr device);
        int Next(int count, out IntPtr device, out int fetched);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PropKey key);
        int GetValue(ref PropKey key, out PropVariant value);
        int SetValue(ref PropKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
