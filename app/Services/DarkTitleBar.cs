using System;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>
/// Turns the Windows caption buttons (minimise / maximise / close) dark so the
/// title bar strip matches the OLED black window instead of the default light grey.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;

        // Windows 11 / recent Windows 10 build.
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // Older Windows 10 builds only know the legacy attribute.
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }
}
