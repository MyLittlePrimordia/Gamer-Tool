using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace GamerTool.Core;

public sealed class TrayMenuItemDefinition
{
    public string Header { get; init; } = string.Empty;
    public Action? OnClick { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsCheckable { get; init; }
    public bool IsChecked { get; init; }
    /// <summary>Nested items render as a submenu (used for preset quick-apply lists).</summary>
    public IReadOnlyList<TrayMenuItemDefinition>? Children { get; init; }
}

/// <summary>
/// Owns the Windows system tray icon: creation, tooltip and icon-state updates,
/// the right-click context menu, and double-click-to-restore behavior. Uses its
/// own hidden message-only window so tray callback messages never depend on the
/// main WPF window's lifetime (the main window is routinely hidden/closed-to-tray
/// while GamerTool keeps running in the background). Icons are loaded via raw
/// LoadImage P/Invoke rather than System.Drawing, keeping this class free of any
/// dependency beyond the BCL/WPF.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private static readonly Lazy<TrayManager> _instance = new(() => new TrayManager());
    public static TrayManager Instance => _instance.Value;

    private const int WM_TRAYICON = 0x8000 + 100; // WM_APP + 100, app-defined callback message

    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;
    private NOTIFYICONDATA _iconData;
    private bool _iconAdded;
    private bool _disposed;

    private IntPtr _idleHIcon = IntPtr.Zero;
    private IntPtr _activeHIcon = IntPtr.Zero;

    /// <summary>Raised on left double-click - MainViewModel wires this to "show and restore the main window".</summary>
    public event Action? RestoreRequested;

    /// <summary>Raised on right-click - the caller supplies current menu content via ShowContextMenu, since TrayManager itself stays agnostic of preset/settings state.</summary>
    public event Action? TrayIconRightClicked;

    private TrayManager() { }

    /// <summary>
    /// Creates the hidden window that receives tray callback messages and adds
    /// the icon to the notification area. iconIdlePath/iconActivePath point at
    /// the embedded .ico resources (tray_idle.ico / tray_active.ico) resolved
    /// to on-disk paths by the caller.
    /// </summary>
    public void Initialize(string iconIdlePath, string iconActivePath, string initialTooltip)
    {
        if (_hwndSource is not null)
            return;

        var parameters = new HwndSourceParameters("GamerToolTrayWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = IntPtr.Zero
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        _hwnd = _hwndSource.Handle;

        _idleHIcon = LoadHIcon(iconIdlePath);
        _activeHIcon = LoadHIcon(iconActivePath);

        _iconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Shell32Native.NIF_ICON | Shell32Native.NIF_MESSAGE | Shell32Native.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _idleHIcon != IntPtr.Zero ? _idleHIcon : _activeHIcon,
            szTip = Truncate(initialTooltip, 127)
        };

        _iconAdded = Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_ADD, ref _iconData);
    }

    /// <summary>
    /// Swaps the tray icon to the active preset's/combo's icon (rendered from
    /// the IconCatalog PNG). Falls back to the idle glyph on any failure.
    /// </summary>
    public void SetIconFromPng(System.Drawing.Bitmap? bitmap)
    {
        if (!_iconAdded || bitmap is null)
            return;

        try
        {
            var handle = bitmap.GetHicon();
            _iconData.hIcon = handle;
            _iconData.uFlags = Shell32Native.NIF_ICON;
            Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_MODIFY, ref _iconData);

            // The previous preset icon handle is ours to destroy.
            if (_presetIconHandle != IntPtr.Zero && _presetIconHandle != _idleHIcon && _presetIconHandle != _activeHIcon)
                User32Native.DestroyIcon(_presetIconHandle);
            _presetIconHandle = handle;
        }
        catch
        {
            // Bad bitmap -> keep whatever icon is currently shown.
        }
    }

    private IntPtr _presetIconHandle = IntPtr.Zero;

    /// <summary>Restores the idle/active built-in glyph (used on Standard preset / panic reset).</summary>
    public void SetActiveState(bool anyPresetActive)
    {
        if (!_iconAdded)
            return;

        _iconData.hIcon = anyPresetActive ? _activeHIcon : _idleHIcon;
        _iconData.uFlags = Shell32Native.NIF_ICON;
        Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_MODIFY, ref _iconData);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_iconAdded)
            return;

        _iconData.szTip = Truncate(tooltip, 127);
        _iconData.uFlags = Shell32Native.NIF_TIP;
        Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_MODIFY, ref _iconData);
    }

    /// <summary>
    /// Shows the right-click context menu at the current cursor position. Built
    /// fresh from the supplied definitions on every call so checkmarks (e.g.
    /// "Run on Startup") always reflect current state rather than a stale
    /// cached menu.
    /// </summary>
    public void ShowContextMenu(IEnumerable<TrayMenuItemDefinition> items)
    {
        // WPF popup menus close on the next click anywhere only when their
        // owning window is the foreground window - without this, clicks on
        // other apps' windows are eaten by the popup's capture and it just
        // sits there. This is the standard tray-menu idiom (SetForegroundWindow
        // right before TrackPopupMenu-style display).
        User32Native.SetForegroundWindow(_hwnd);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var menuItem = new MenuItem
            {
                Header = item.Header,
                IsCheckable = item.IsCheckable,
                IsChecked = item.IsChecked
            };

            if (item.Children is not null)
            {
                // Submenu: radio-style quick-apply list (checkable, one checked).
                foreach (var child in item.Children)
                {
                    if (child.IsSeparator)
                    {
                        menuItem.Items.Add(new Separator());
                        continue;
                    }

                    var childItem = new MenuItem
                    {
                        Header = child.Header,
                        IsCheckable = true,
                        IsChecked = child.IsChecked
                    };
                    childItem.Click += (_, _) => child.OnClick?.Invoke();
                    menuItem.Items.Add(childItem);
                }
            }
            else
            {
                menuItem.Click += (_, _) => item.OnClick?.Invoke();
            }

            menu.Items.Add(menuItem);
        }

        // A tray context menu has no owning WPF Window, so it must be driven
        // open explicitly rather than via a control's ContextMenu property.
        menu.IsOpen = true;

        // menus auto-close when they lose activation.
        menu.Focus();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32();
            switch (mouseMsg)
            {
                case Shell32Native.WM_LBUTTONDBLCLK:
                    RestoreRequested?.Invoke();
                    handled = true;
                    break;

                case Shell32Native.WM_RBUTTONUP:
                    TrayIconRightClicked?.Invoke();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr LoadHIcon(string path)
    {
        try
        {
            return User32Native.LoadImage(
                IntPtr.Zero,
                path,
                User32Native.IMAGE_ICON,
                0,
                0,
                User32Native.LR_LOADFROMFILE | User32Native.LR_DEFAULTSIZE);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_iconAdded)
        {
            Shell32Native.Shell_NotifyIcon(Shell32Native.NIM_DELETE, ref _iconData);
            _iconAdded = false;
        }

        if (_idleHIcon != IntPtr.Zero)
            User32Native.DestroyIcon(_idleHIcon);
        if (_activeHIcon != IntPtr.Zero)
            User32Native.DestroyIcon(_activeHIcon);
        if (_presetIconHandle != IntPtr.Zero && _presetIconHandle != _idleHIcon && _presetIconHandle != _activeHIcon)
            User32Native.DestroyIcon(_presetIconHandle);

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
        _disposed = true;
    }
}
