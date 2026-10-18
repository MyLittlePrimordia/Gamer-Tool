using System;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>
/// Which monitor a crosshair goes on, and where its middle is in screen pixels.
/// <para>
/// The monitor holding the foreground window, because the foreground window is
/// the game: that is true when a hotkey is pressed in-game and when a slot loads
/// itself as a game comes to the front. With no usable foreground window the
/// primary monitor is used. The answer is in physical pixels of the virtual
/// screen, which is what the overlay window is positioned in, so the DPI of any
/// monitor never enters into it.
/// </para>
/// </summary>
public static class CrosshairPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// The middle of the monitor the user is looking at. Never fails: if Windows
    /// will not say, the answer is the middle of a 1920 x 1080 screen, which puts
    /// the crosshair somewhere sensible instead of nowhere.
    /// </summary>
    public static (int X, int Y) ForegroundMonitorCentre()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            IntPtr monitor = MonitorFromWindow(
                foreground,
                foreground == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);

            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return (
                        info.Monitor.Left + ((info.Monitor.Right - info.Monitor.Left) / 2),
                        info.Monitor.Top + ((info.Monitor.Bottom - info.Monitor.Top) / 2));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PLACEMENT", ex);
        }

        return (960, 540);
    }
}
