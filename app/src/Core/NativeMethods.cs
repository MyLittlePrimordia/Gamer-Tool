using System;
using System.Runtime.InteropServices;

namespace GamerTool.Core;

#region Shared structs

/// <summary>
/// Maps 1:1 onto GDI32's native RAMP struct: three contiguous WORD[256] arrays
/// (Red, Green, Blue). Sequential layout with fixed-size marshaled arrays gives
/// the exact 1536-byte memory shape GetDeviceGammaRamp/SetDeviceGammaRamp expect.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RAMP
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Red;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Green;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public ushort[] Blue;

    /// <summary>Builds a linear 1:1 identity ramp (0..65535 in 257-unit steps).</summary>
    public static RAMP CreateIdentity()
    {
        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * 257); // 257 = 65535 / 255, exact integer step
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
        }

        return ramp;
    }

    public RAMP Clone()
    {
        return new RAMP
        {
            Red = (ushort[])Red.Clone(),
            Green = (ushort[])Green.Clone(),
            Blue = (ushort[])Blue.Clone()
        };
    }
}

#endregion

#region GDI32 - Gamma ramp

public static class Gdi32Native
{
    private const string Gdi32Dll = "gdi32.dll";

    [DllImport(Gdi32Dll, SetLastError = true)]
    public static extern bool GetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);

    [DllImport(Gdi32Dll, SetLastError = true)]
    public static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);
}

#endregion

#region USER32 - Device context, hotkeys, window messages, icon loading

public static class User32Native
{
    private const string User32Dll = "user32.dll";

    [DllImport(User32Dll)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32Dll)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport(User32Dll, SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32Dll, SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport(User32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport(User32Dll)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport(User32Dll)]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport(User32Dll)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // RegisterHotKey modifier flags
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Window messages
    public const int WM_HOTKEY = 0x0312;
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const int WM_DESTROY = 0x0002;

    public const int ENDSESSION_CLOSEAPP = 0x00000001;
    public const int ENDSESSION_CRITICAL = 0x40000000;
    public const int ENDSESSION_LOGOFF = unchecked((int)0x80000000);

    // LoadImage constants (used to load .ico files without any System.Drawing
    // dependency, keeping icon loading zero-dependency)
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;
}

#endregion

#region KERNEL32 - Console control handler (defense-in-depth exit hook)

public static class Kernel32Native
{
    private const string Kernel32Dll = "kernel32.dll";

    public delegate bool ConsoleCtrlHandlerDelegate(int ctrlType);

    [DllImport(Kernel32Dll, SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate handler, bool add);

    public const int CTRL_C_EVENT = 0;
    public const int CTRL_BREAK_EVENT = 1;
    public const int CTRL_CLOSE_EVENT = 2;
    public const int CTRL_LOGOFF_EVENT = 5;
    public const int CTRL_SHUTDOWN_EVENT = 6;
}

#endregion

#region SHELL32 - System tray icon

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NOTIFYICONDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public int uID;
    public int uFlags;
    public int uCallbackMessage;
    public IntPtr hIcon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;

    public int dwState;
    public int dwStateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;

    public int uTimeoutOrVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;

    public int dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

public static class Shell32Native
{
    private const string Shell32Dll = "shell32.dll";

    [DllImport(Shell32Dll, CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;

    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int NIF_INFO = 0x00000010;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
}

#endregion

#region CoreAudio COM interop

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(string guid, uint pid)
    {
        fmtid = new Guid(guid);
        this.pid = pid;
    }
}

/// <summary>
/// Minimal PROPVARIANT sufficient for the boolean/UI4 toggles this app needs
/// (native "Loudness Equalization" enhancement). The real PROPVARIANT is a much
/// larger tagged union; we deliberately do not model the rest of it since we
/// never read or write anything beyond VT_BOOL/VT_UI4 here.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public short boolVal;
    [FieldOffset(8)] public uint ulVal;
    [FieldOffset(8)] public IntPtr pointerValue;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_UI4 = 19;
    public const ushort VT_BOOL = 11;

    public static PROPVARIANT FromBool(bool value) => new() { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) };

    public static PROPVARIANT FromUInt32(uint value) => new() { vt = VT_UI4, ulVal = value };

    public bool AsBool() => boolVal != 0;
    public uint AsUInt32() => ulVal;
}

public static class Ole32Native
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    public const uint CLSCTX_INPROC_SERVER = 0x1;
}

public enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

public enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    int RegisterEndpointNotificationCallback(IntPtr pClient);

    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);

    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    int GetState(out uint pdwState);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    int GetCount(out uint cProps);

    int GetAt(uint iProp, out PROPERTYKEY pkey);

    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    int Commit();
}

/// <summary>
/// Undocumented COM interface used by many community sound-control utilities to
/// query/set per-endpoint driver behavior and default roles. Its GUID and vtable
/// order are reverse-engineered by the community, are NOT published by
/// Microsoft, and have shifted across major Windows builds before. AudioManager
/// treats every call through this interface as best-effort and never assumes
/// success - see AudioManager.cs for the fallback strategy.
/// </summary>
[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, out PROPVARIANT pv);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
}

public static class ComGuids
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IPolicyConfig = new("f8679f50-850a-41cf-9c72-430f290290c8");
    public static readonly Guid CLSID_PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
}

/// <summary>
/// Property keys used for the native "Sound enhancements / Loudness
/// Equalization" endpoint checkbox. PKEY_AudioEndpoint_Disable_SysFx is a
/// publicly documented DEVPKEY; the loudness-specific key is vendor/APO
/// dependent and treated as best-effort by AudioManager - see the comment on
/// IPolicyConfig above for why.
/// </summary>
public static class AudioPropertyKeys
{
    public static readonly PROPERTYKEY PKEY_AudioEndpoint_Enable_Loudness_Equalization =
        new("D04E05A6-594B-4FB6-A80D-01AF5EED7D1D", 8);

    public static readonly PROPERTYKEY PKEY_AudioEndpoint_Disable_SysFx =
        new("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E", 5);
}

#endregion
