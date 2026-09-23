using System.Runtime.InteropServices;

namespace GamerTool.Services;

public record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault);

/// <summary>
/// Minimal Core Audio (MMDevice) COM interop so we can list render endpoints
/// for the output-device dropdown without pulling in a NuGet audio library
/// just for enumeration. Equalizer APO itself hooks in below this layer per
/// endpoint, so GamerTool only needs to know which endpoint is selected.
/// </summary>
public static class AudioDeviceService
{
    private const int eRender = 0;
    private const int eConsole = 0;       // matches what Sound Settings shows as Default
    private const int eMultimedia = 1;
    private const int DEVICE_STATE_ACTIVE = 0x1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        // remaining methods not needed
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr endpoint);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        int GetId(out IntPtr id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public int p2;
    }

    // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, pid 14
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    public static List<AudioDeviceInfo> EnumerateRenderDevices()
    {
        var results = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            // Prefer eConsole (matches Sound Settings "Default"); fall back to eMultimedia.
            IMMDevice defaultDevice;
            try
            {
                enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out defaultDevice);
            }
            catch
            {
                enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out defaultDevice);
            }
            string defaultId = GetDeviceId(defaultDevice);

            enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection);
            collection.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                string id = GetDeviceId(device);
                string name = GetFriendlyName(device);
                results.Add(new AudioDeviceInfo(id, name, id == defaultId));
            }
        }
        catch
        {
            // COM failures here just mean the dropdown falls back to "System Default"
            // only — never fatal to the rest of the app.
        }
        return results;
    }

    private static string GetDeviceId(IMMDevice device)
    {
        device.GetId(out IntPtr idPtr);
        string id = Marshal.PtrToStringUni(idPtr) ?? "";
        Marshal.FreeCoTaskMem(idPtr);
        return id;
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(0 /* STGM_READ */, out var store);
        var key = PKEY_Device_FriendlyName;
        store.GetValue(ref key, out var variant);
        // VT_LPWSTR = 31
        if (variant.vt == 31 && variant.p != IntPtr.Zero)
            return Marshal.PtrToStringUni(variant.p) ?? "Unknown Device";
        return "Unknown Device";
    }
}
