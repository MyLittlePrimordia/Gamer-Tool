using System.Runtime.InteropServices;

namespace GamerTool.Services;

public record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault);

/// <summary>
/// Minimal Core Audio (MMDevice) COM interop for listing render endpoints.
/// All HRESULTs are checked — failed calls must never throw AccessViolation
/// into the UI thread (that was the dropdown crash).
/// </summary>
public static class AudioDeviceService
{
    private const int eRender = 0;
    private const int eConsole = 0;
    private const int eMultimedia = 1;
    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int S_OK = 0;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr endpoint);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore? properties);
        [PreserveSig] int GetId(out IntPtr id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    // PROPVARIANT is variable-sized; we only need VT_LPWSTR (31).
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
    }

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

            string defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out var defDev) == S_OK && defDev != null)
                defaultId = SafeGetDeviceId(defDev);
            else if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out defDev) == S_OK && defDev != null)
                defaultId = SafeGetDeviceId(defDev);

            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != S_OK
                || collection == null)
                return results;

            if (collection.GetCount(out int count) != S_OK)
                return results;

            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != S_OK || device == null)
                    continue;

                string id = SafeGetDeviceId(device);
                string name = SafeGetFriendlyName(device);
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    continue;

                results.Add(new AudioDeviceInfo(id, name, id == defaultId));
            }
        }
        catch
        {
            // Never let COM failures crash the UI — return empty / partial list.
        }
        return results;
    }

    private static string SafeGetDeviceId(IMMDevice device)
    {
        try
        {
            if (device.GetId(out IntPtr idPtr) != S_OK || idPtr == IntPtr.Zero)
                return "";
            string id = Marshal.PtrToStringUni(idPtr) ?? "";
            Marshal.FreeCoTaskMem(idPtr);
            return id;
        }
        catch { return ""; }
    }

    private static string SafeGetFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0, out var store) != S_OK || store == null)
                return "Unknown Device";

            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var variant) != S_OK)
                return "Unknown Device";

            // VT_LPWSTR = 31
            if (variant.vt == 31 && variant.p != IntPtr.Zero)
                return Marshal.PtrToStringUni(variant.p) ?? "Unknown Device";

            return "Unknown Device";
        }
        catch { return "Unknown Device"; }
    }
}
