using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Where the in-game popup goes, and on which screen.
/// <para>
/// It used to be placed from <see cref="SystemParameters.WorkArea"/>, which answers
/// about the primary screen whatever the user is doing. On one monitor that is
/// indistinguishable from correct, which is why it survived: the plate was centred,
/// it was at the top, and the only screen it could have been on was the one in
/// front of the user. With two, a hotkey pressed in a game on the second screen put
/// the confirmation on the first - at the moment the user could least afford to
/// look away from the game to find it.
/// </para>
/// <para>
/// The arithmetic is separated from the window so the second-screen case can be
/// asserted without a second screen: a work area is just a rectangle, and where a
/// plate belongs inside it is a question with one right answer.
/// </para>
/// </summary>
public class ToastPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void A_toast_is_centred_across_the_screen_it_is_given()
    {
        (int x, int y) = ToastPlacement.TopLeft(Primary, 320, 44, 10);

        Assert.Equal(800, x);
        Assert.Equal(10, y);
    }

    [Fact]
    public void A_toast_on_the_second_screen_is_centred_on_that_screen()
    {
        // The regression. A screen to the right of the primary starts where the
        // primary ends, so centring inside it is nowhere near centring in the
        // primary - and the old arithmetic could only ever produce the second
        // answer.
        var second = new Rect(1920, 0, 2560, 1440);

        (int x, int y) = ToastPlacement.TopLeft(second, 320, 44, 10);

        Assert.Equal(3040, x);
        Assert.Equal(10, y);

        Assert.NotEqual(800, x);
    }

    [Fact]
    public void A_screen_to_the_left_of_the_primary_uses_negative_coordinates()
    {
        // Windows places a screen left of the primary at negative x, and a
        // placement helper that assumed zero was the left edge would put the plate
        // on the primary instead.
        var left = new Rect(-1920, 0, 1920, 1080);

        (int x, int y) = ToastPlacement.TopLeft(left, 320, 44, 10);

        Assert.Equal(-1120, x);
        Assert.Equal(10, y);
    }

    [Fact]
    public void The_gap_above_the_plate_follows_the_scale_factor()
    {
        // On a 150% screen the ten device independent pixels are fifteen real ones,
        // which is the caller's conversion to make - the arithmetic takes the gap in
        // the same units as the rectangle.
        (int x, int y) = ToastPlacement.TopLeft(new Rect(0, 0, 2560, 1440), 480, 66, 15);

        Assert.Equal(15, y);
        Assert.Equal(1040, x);
    }

    [Fact]
    public void A_plate_wider_than_the_screen_starts_at_its_left_edge()
    {
        // A portrait screen, a scaled-down virtual desktop, or a long slot name on a
        // narrow display. The centring sum alone is negative here, and the old code
        // clamped the left edge but not the right, so a plate that did not fit went
        // off the side it had been pushed towards.
        (int x, int y) = ToastPlacement.TopLeft(new Rect(100, 0, 200, 1080), 320, 44, 10);

        Assert.Equal(100, x);
        Assert.Equal(10, y);
    }

    [Fact]
    public void A_plate_on_a_screen_too_short_for_it_is_lifted_to_the_top_edge()
    {
        (int x, int y) = ToastPlacement.TopLeft(new Rect(0, 0, 1920, 40), 320, 44, 10);

        Assert.Equal(800, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void An_odd_leftover_is_rounded_rather_than_truncated()
    {
        // 1920 - 321 = 1599, whose half is 799.5. Truncation would bias every odd
        // case half a pixel to the left, which is invisible on its own and wrong
        // whenever it is measured.
        (int x, _) = ToastPlacement.TopLeft(Primary, 321, 44, 10);

        Assert.Equal(800, x);
    }

    [Fact]
    public void A_slot_with_no_screen_asks_about_nothing()
    {
        // Null and blank both mean "no screen of its own", and asking Windows for
        // one would enum every monitor to answer a question nobody asked. The caller
        // falls back to the foreground window on null.
        Assert.Null(MonitorWorkArea.ForDevice(null));
        Assert.Null(MonitorWorkArea.ForDevice(string.Empty));
        Assert.Null(MonitorWorkArea.ForDevice("   "));
    }

    [Fact]
    public void A_toast_reaches_the_window_at_the_screen_it_was_given()
    {
        // The arithmetic above can be right while the window is not following it, so
        // this one reads the real window handle back. Two toasts, two screens, and
        // the second must move back to the first - which also pins that the screen
        // is taken from the call rather than remembered from the first toast.
        var (onSecond, onPrimary) = WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            try
            {
                osd.ShowToast("Slot loaded", false, new Rect(1920, 0, 2560, 1440));
                NativeRect second = RectOf(osd);

                osd.ShowToast("Slot loaded", false, new Rect(0, 0, 1920, 1080));
                NativeRect primary = RectOf(osd);

                return (second, primary);
            }
            finally
            {
                osd.HideTimer.Stop();
                osd.Close();
            }
        });

        Assert.True(onSecond.Left >= 1920, "the plate was not on the second screen: left " + onSecond.Left);
        Assert.True(onSecond.Right <= 1920 + 2560, "the plate ran past the second screen: right " + onSecond.Right);
        Assert.InRange(onSecond.Top, 0, 60);

        Assert.True(onPrimary.Left >= 0, "the plate was not on the primary screen: left " + onPrimary.Left);
        Assert.True(onPrimary.Right <= 1920, "the plate ran past the primary screen: right " + onPrimary.Right);
        Assert.InRange(onPrimary.Top, 0, 60);
    }

    [Fact]
    public void A_toast_with_no_screen_of_its_own_lands_on_one_that_exists()
    {
        // The path every non-slot toast takes: ask Windows which screen the
        // foreground window is on. There is no fake for this, so the assertion is
        // the weak but real one - the plate must end up somewhere inside the
        // desktop - and what it is really guarding is that the native lookup answers
        // a usable rectangle rather than an empty one.
        var (placed, virtualLeft, virtualTop, virtualWidth, virtualHeight) = WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            try
            {
                osd.ShowToast("Slot loaded");
                NativeRect rect = RectOf(osd);
                return (rect, GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
                    GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
            }
            finally
            {
                osd.HideTimer.Stop();
                osd.Close();
            }
        });

        Assert.True(placed.Right > placed.Left, "the plate has no width");
        Assert.True(placed.Bottom > placed.Top, "the plate has no height");
        Assert.True(placed.Left >= virtualLeft, "the plate is left of the desktop: " + placed.Left);
        Assert.True(placed.Top >= virtualTop, "the plate is above the desktop: " + placed.Top);
        Assert.True(placed.Right <= virtualLeft + virtualWidth, "the plate is right of the desktop: " + placed.Right);
        Assert.True(placed.Bottom <= virtualTop + virtualHeight, "the plate is below the desktop: " + placed.Bottom);
    }

    [Fact]
    public void A_slot_load_asks_for_the_screen_the_slot_is_keyed_to()
    {
        // The window test above proves the plate follows the rectangle it is given.
        // This is the other half: that a slot load gives it that rectangle instead
        // of leaving it to the foreground window, which at Play time is still the
        // launcher or this app. Losing the argument here would put the toast back
        // on the screen the game is not about to open on, and nothing else would
        // notice.
        string source = File.ReadAllText(RepoFile(Path.Combine("app", "MainWindow.Slots.cs")));

        Assert.Contains("Flash(slot.Name + \" loaded\", ScreenForSlot(slot));", source);
        Assert.Contains("Flash(message, warn, ScreenForSlot(slot));", source);
        Assert.Contains("Flash(slot.Name + \" loaded, screen did not take it\", true, ScreenForSlot(slot));", source);
    }

    // ------------------------------------------------------------------ helpers

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static NativeRect RectOf(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(GetWindowRect(handle, out NativeRect rect), "the window has no rectangle");
        return rect;
    }
}
