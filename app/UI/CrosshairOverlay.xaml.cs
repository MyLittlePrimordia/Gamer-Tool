using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;

namespace GamerTool.UI;

/// <summary>
/// The crosshair as it appears over a game: a small, click-through window that
/// never takes focus.
/// <para>
/// Small on purpose, and that is most of the lightness. The window is the size
/// of the shape plus a margin for its glow, centred on the monitor, rather than a
/// transparent layer the size of the screen. A full-screen layered window makes
/// the compositor blend a monitor's worth of pixels for a few dozen that are
/// drawn, and it is the thing that makes some overlays show up in a profiler.
/// This one is under a hundred pixels on a side.
/// </para>
/// <para>
/// It is an ordinary window that draws pixels. It does not touch another process,
/// does not hook anything and does not read the game's memory, which is the line
/// anti-cheat software cares about. It also cannot be drawn over a game running
/// in true exclusive fullscreen, because no ordinary window can; borderless
/// windowed, which is what most games default to, works.
/// </para>
/// <para>
/// Created once and shown and hidden, never closed and rebuilt, the same as
/// <see cref="RetroOsd"/>. While hidden it holds a window handle and nothing
/// else: an animated crosshair's clock stops with the window, so an animation
/// that is off the screen costs nothing.
/// </para>
/// </summary>
public partial class CrosshairOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    private IntPtr _handle;
    private int _centreX;
    private int _centreY;

    public CrosshairOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnOverlaySourceInitialized;
    }

    /// <summary>The crosshair now drawn, or null if none has been shown yet.</summary>
    public CrosshairPreset? Current => View.Preset;

    /// <summary>
    /// Draw <paramref name="preset"/> with its middle on the given screen pixel.
    /// Showing a crosshair that is already up just redraws and re-places it, so a
    /// live edit on the tab is one call.
    /// </summary>
    public void ShowAt(CrosshairPreset preset, CrosshairPicture? picture, int centreX, int centreY)
    {
        _centreX = centreX;
        _centreY = centreY;

        View.Picture = picture;
        View.Preset = preset;
        View.InvalidateVisual();

        if (!IsVisible)
        {
            // An approximate position first, so the window is not created at a
            // default spot on the wrong monitor. The exact pixel placement below
            // replaces it before anything has been painted.
            int side = CrosshairGeometry.WindowSide(preset);
            Left = centreX - (side / 2.0);
            Top = centreY - (side / 2.0);
            Width = side;
            Height = side;
            Show();
        }

        Place();
    }

    /// <summary>Take it off the screen. The window stays alive for the next show.</summary>
    public void HideOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Move to a new screen without redrawing, for when the monitor layout
    /// changes underneath a crosshair that is up.
    /// </summary>
    public void Recentre(int centreX, int centreY)
    {
        _centreX = centreX;
        _centreY = centreY;
        if (IsVisible)
        {
            Place();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Windows resizes the window to keep its size in device independent units
        // when it crosses onto a monitor with a different scale. The crosshair is
        // measured in real pixels, so put the pixel size and position back.
        View.InvalidateVisual();
        if (IsVisible)
        {
            Place();
        }
    }

    private void OnOverlaySourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // Click-through, never activated and kept out of Alt+Tab. The same three
        // bits the toast uses, for the same reason: a window that takes focus from
        // a game is worse than no crosshair.
        int style = GetWindowLongPtr(_handle, GwlExStyle).ToInt32();
        style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(style));
    }

    private void Place()
    {
        if (_handle == IntPtr.Zero || View.Preset is not { } preset)
        {
            return;
        }

        int side = CrosshairGeometry.WindowSide(preset);
        int x = _centreX - (side / 2);
        int y = _centreY - (side / 2);

        // Straight to pixels. Left and Top are in device independent units of
        // whichever monitor the window was last on, which is the wrong ruler for
        // putting a window on exact pixel of a monitor with a different scale.
        // Topmost is re-asserted each time, because a game that starts after the
        // overlay can otherwise end up above it.
        SetWindowPos(_handle, HwndTopmost, x, y, side, side, SwpNoActivate | SwpNoOwnerZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, index, value);
        }
        else
        {
            SetWindowLong32(hwnd, index, value.ToInt32());
        }
    }
}
