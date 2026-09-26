using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class AudioDeviceManager
{
    private const uint StgmRead = 0x00000000;

    private const int ERender = 0;

    private const int ERoleMultimedia = 1;

    private const uint DeviceStateActive = 0x1;

    private static readonly PropertyKey FriendlyNameKey = new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };

    private static readonly PropertyKey DescriptionKey = new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 2 };

    public event Action<string>? StatusChanged;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        List<AudioDeviceInfo> devices = new();
        try
        {
            MMDeviceEnumeratorClass enumeratorClass = new();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorClass;
            int hr = enumerator.EnumAudioEndpointDataFlow(ERender, DeviceStateActive, out IMMDeviceCollection? collection);
            if (hr != 0 || collection is null)
            {
                StatusChanged?.Invoke("NO DEVICE");
                return devices;
            }

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice? device) != 0 || device is null)
                {
                    continue;
                }

                string id = string.Empty;
                string name = string.Empty;
                try
                {
                    if (device.GetId(out string? found) == 0 && found is not null)
                    {
                        id = found;
                    }

                    if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) == 0 && store is not null)
                    {
                        name = ReadName(store);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }

                devices.Add(new AudioDeviceInfo { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "OUTPUT " + (i + 1).ToString() : name });
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("NO DEVICE");
            Debug.WriteLine(ex.Message);
        }

        return devices;
    }

    public string GetDefaultOutputId()
    {
        try
        {
            MMDeviceEnumeratorClass enumeratorClass = new();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorClass;
            if (enumerator.GetDefaultAudioEndpoint(ERender, ERoleMultimedia, out IMMDevice? device) != 0 || device is null)
            {
                return string.Empty;
            }

            return device.GetId(out string? id) == 0 && id is not null ? id : string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return string.Empty;
        }
    }

    public string GetDefaultOutputName()
    {
        try
        {
            MMDeviceEnumeratorClass enumeratorClass = new();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorClass;
            if (enumerator.GetDefaultAudioEndpoint(ERender, ERoleMultimedia, out IMMDevice? device) != 0 || device is null)
            {
                return string.Empty;
            }

            if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) != 0 || store is null)
            {
                return string.Empty;
            }

            return ReadName(store);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return string.Empty;
        }
    }

    private static string ReadName(IPropertyStore store)
    {
        if (store.GetCount(out uint count) != 0)
        {
            return string.Empty;
        }

        string fallback = string.Empty;
        for (uint i = 0; i < count; i++)
        {
            if (store.GetAt(i, out PropertyKey key) != 0)
            {
                continue;
            }

            PropVariant value = default;
            if (store.GetValue(ref key, ref value) != 0)
            {
                continue;
            }

            string text = value.ReadString();
            value.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (key.FormatId == FriendlyNameKey.FormatId && key.PropertyId == FriendlyNameKey.PropertyId)
            {
                return text;
            }

            if (key.FormatId == DescriptionKey.FormatId && key.PropertyId == DescriptionKey.PropertyId)
            {
                fallback = text;
            }
        }

        return fallback;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorClass
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpointDataFlow(int dataFlow, uint state, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection? devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint count);

        int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid id, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        int OpenPropertyStore(uint stgmAccess, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);

        int GetAt(uint index, out PropertyKey key);

        int GetValue(ref PropertyKey key, ref PropVariant value);

        int SetValue(ref PropertyKey key, ref PropVariant value);

        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;

        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort ValueType;

        [FieldOffset(8)]
        public uint UIntValue;

        [FieldOffset(8)]
        public IntPtr Pointer;

        [FieldOffset(8)]
        public short ShortValue;

        [FieldOffset(16)]
        public IntPtr SecondPointer;

        public string ReadString()
        {
            if (ValueType == 31 && Pointer != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(Pointer) ?? string.Empty;
            }

            return string.Empty;
        }

        public void Clear()
        {
            if (ValueType == 31 && Pointer != IntPtr.Zero)
            {
                PropVariantClear(this);
                Pointer = IntPtr.Zero;
                ValueType = 0;
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PropVariant variant);
}
