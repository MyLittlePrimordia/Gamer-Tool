using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace GamerTool.Services;

/// <summary>
/// The usable rectangle of a screen - its bounds minus whatever the taskbar and
/// any docked app bar have taken - in physical pixels.
/// <para>
/// Physical pixels rather than device independent units, because that is the only
/// ruler that means the same thing on two screens with different scale factors.
/// <see cref="SystemParameters.WorkArea"/> is neither: it is the primary screen's
/// work area converted into the units of the system DPI, so on a two-monitor
/// machine it answers about the wrong screen even when the question was about the
/// right one. That is what a toast used to be placed with.
/// </para>
/// <para>
/// The screen is chosen by the foreground window, which is the game: true when a
/// hotkey is pressed in-game, and true when a slot loads itself as a game comes to
/// the front. Nothing here reads the pointer, because a crosshair or a toast is
/// about what the user is playing rather than where the mouse happens to rest.
/// </para>
/// </summary>
public static class MonitorWorkArea
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;

    /// <summary>
    /// What is answered when Windows will not name a single screen. A desktop with
    /// no monitor is not a configuration anybody runs a game on, so a plate in the
    /// top middle of a sensible rectangle is a better outcome than no toast at all.
    /// </summary>
    private static readonly Rect Assumed = new(0, 0, 1920, 1080);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        /// <summary><c>szDevice</c>, the <c>\\.\DISPLAYn</c> name the app stores.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>
    /// The work area of the screen holding the foreground window, falling back to
    /// the primary screen and then to a fixed rectangle. Never throws, and never
    /// returns an empty rectangle.
    /// </summary>
    public static Rect ForForeground()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            IntPtr monitor = MonitorFromWindow(
                foreground,
                foreground == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);

            if (monitor != IntPtr.Zero && WorkAreaOf(monitor) is { } area)
            {
                return area;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("TOAST PLACEMENT", ex);
        }

        return Primary() ?? Assumed;
    }

    /// <summary>
    /// The work area of the screen with this device name, or null when no attached
    /// screen answers to it.
    /// <para>
    /// Null rather than a fallback, because the caller's next question is different
    /// from this one: a slot whose screen has been unplugged should get the toast
    /// on the screen the user is looking at, not on the primary screen by
    /// coincidence.
    /// </para>
    /// </summary>
    public static Rect? ForDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return null;
        }

        string wanted = device.Trim();
        Rect? found = null;

        try
        {
            // Stops at the first match: one device name belongs to one screen, so
            // continuing would only be able to overwrite a correct answer.
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                    Device = string.Empty
                };

                if (GetMonitorInfoEx(monitor, ref info)
                    && string.Equals(info.Device, wanted, StringComparison.OrdinalIgnoreCase)
                    && WorkAreaOf(info.Work) is { } area)
                {
                    found = area;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Error("TOAST PLACEMENT", ex);
        }

        return found;
    }

    private static Rect? Primary()
    {
        try
        {
            IntPtr monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
            return monitor == IntPtr.Zero ? null : WorkAreaOf(monitor);
        }
        catch (Exception ex)
        {
            AppLog.Error("TOAST PLACEMENT", ex);
            return null;
        }
    }

    private static Rect? WorkAreaOf(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? WorkAreaOf(info.Work) : null;
    }

    private static Rect? WorkAreaOf(NativeRect work)
    {
        int width = work.Right - work.Left;
        int height = work.Bottom - work.Top;

        // A collapsed or negative rectangle is a screen that is not really there,
        // and placing a window inside it would put the toast where it cannot be
        // seen. Null sends the caller to a screen that is.
        return width > 0 && height > 0 ? new Rect(work.Left, work.Top, width, height) : null;
    }
}

/// <summary>
/// Where the toast sits inside a work area: centred across it, just below the top
/// edge, in the same physical pixels the area is measured in.
/// <para>
/// Out here rather than in the window because this is the part that can be wrong
/// without anything throwing. The version this replaces was three lines of
/// arithmetic against the primary screen only, and no test could see the
/// difference between "centred on the primary screen" and "centred on the screen
/// in use" - which is exactly the bug.
/// </para>
/// </summary>
public static class ToastPlacement
{
    /// <summary>Gap above the plate, in device independent pixels.</summary>
    public const double TopMargin = 10.0;

    public static (int X, int Y) TopLeft(Rect area, double width, double height, double margin)
    {
        double x = area.Left + ((area.Width - width) / 2.0);
        double y = area.Top + margin;

        // Clamped rather than assumed to fit, in both directions. A plate wider
        // than the work area - a portrait screen, a scaled-down virtual desktop, a
        // long slot name - would otherwise be pushed off the left edge by the
        // centring sum alone, and one taller than the area would hang under the
        // taskbar. Neither can happen on a normal desktop and both are cheap.
        if (x + width > area.Right)
        {
            x = area.Right - width;
        }

        if (x < area.Left)
        {
            x = area.Left;
        }

        if (y + height > area.Bottom)
        {
            y = area.Bottom - height;
        }

        if (y < area.Top)
        {
            y = area.Top;
        }

        return (
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }
}
