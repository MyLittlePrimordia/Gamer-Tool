using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace GamerTool.Core;

public sealed class HotkeyConflictException : Exception
{
    public int RequestedId { get; }

    public HotkeyConflictException(int requestedId, string message) : base(message)
    {
        RequestedId = requestedId;
    }
}

public sealed class RegisteredHotkey
{
    public int Id { get; init; }
    public uint Modifiers { get; init; }
    public uint VirtualKey { get; init; }
    public Action Action { get; init; } = () => { };
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// Pure win32 hotkey bits -> display string ("Ctrl + Alt + F1"), shared by
/// the three preset model classes for their card captions.
/// </summary>
public static class HotkeyFormatting
{
    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & User32Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & User32Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & User32Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & User32Native.MOD_WIN) != 0) parts.Add("Win");

        // MOD_NOREPEAT is a behavioral flag, not a modifier - never display it.
        var keyName = NativeKeyName(virtualKey);
        if (keyName is not null)
            parts.Add(keyName);

        return parts.Count > 0 ? string.Join(" + ", parts) : "None";
    }

    private static string? NativeKeyName(uint virtualKey)
    {
        // MapVirtualKey with MAPVK_VK_TO_CHAR gives the unshifted character
        // for letter/digit keys, which is exactly what users expect to see.
        const uint MAPVK_VK_TO_CHAR = 2;
        uint character = User32Native.MapVirtualKey(virtualKey, MAPVK_VK_TO_CHAR);
        if (character is >= (uint)'A' and <= (uint)'Z' or >= (uint)'0' and <= (uint)'9')
            return ((char)character).ToString();

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
            0x90 => "Num Lock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Win",
            0xA5 => "Right Win",
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => null
        };
    }
}

/// <summary>
/// Owns all global hotkey registration for GamerTool. Hotkeys must keep working
/// while another application (a game) has exclusive or borderless-fullscreen
/// focus, which is exactly what Win32 RegisterHotKey/WM_HOTKEY guarantees -
/// unlike WPF's normal input events, which only fire when the WPF window itself
/// has focus. A hidden message-only window exists purely to receive WM_HOTKEY
/// and session-end messages; it is never shown and has no visual presence.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private static readonly Lazy<HotkeyManager> _instance = new(() => new HotkeyManager());
    public static HotkeyManager Instance => _instance.Value;

    /// <summary>Reserved id for the panic reset hotkey - never used for a user-bindable preset.</summary>
    public const int ID_PANIC = 0xFFFF;

    /// <summary>Reserved scratch id used only by IsCombinationAvailable's probe-and-release check.</summary>
    private const int ID_PROBE = 0x7FFE;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly Dictionary<int, RegisteredHotkey> _registered = new();
    private readonly object _lock = new();
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// Raised on WM_QUERYENDSESSION/WM_ENDSESSION (system shutdown, logoff, or
    /// restart). SafetyWatchdog subscribes to this to trigger a factory gamma
    /// restore before Windows tears the process down.
    /// </summary>
    public event Action? SessionEnding;

    private HotkeyManager() { }

    /// <summary>
    /// Creates the hidden message-only window and installs the WM_HOTKEY /
    /// WM_QUERYENDSESSION hook. Must be called once, early in App startup,
    /// before any hotkey (including the panic hotkey) is registered.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_hwndSource is not null)
                return;

            var parameters = new HwndSourceParameters("GamerToolMessageWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0, // no visible style bits - this window is never shown
                ParentWindow = IntPtr.Zero
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);
            _hwnd = _hwndSource.Handle;
        }
    }

    /// <summary>
    /// Registers the panic reset hotkey (Ctrl+Alt+R by spec). This is
    /// intentionally the very first hotkey registered by the application, and
    /// is never exposed as user-remappable in this build.
    /// </summary>
    public void RegisterPanicHotkey(Action action)
    {
        RegisterHotkeyInternal(
            ID_PANIC,
            User32Native.MOD_CONTROL | User32Native.MOD_ALT | User32Native.MOD_NOREPEAT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.R),
            action,
            "Panic Reset (Ctrl+Alt+R)");
    }

    /// <summary>
    /// Registers a new preset/combo hotkey and allocates a fresh id. Throws
    /// HotkeyConflictException if another application already owns that exact
    /// modifier+key combination, so the UI can surface a clear conflict warning
    /// instead of the binding silently doing nothing.
    /// </summary>
    public int RegisterHotkey(uint modifiers, uint virtualKey, Action action, string displayName)
    {
        lock (_lock)
        {
            int id = _nextId++;
            RegisterHotkeyInternal(id, modifiers | User32Native.MOD_NOREPEAT, virtualKey, action, displayName);
            return id;
        }
    }

    /// <summary>
    /// Registers a hotkey under a caller-supplied, persisted id - used when
    /// restoring saved bindings on startup, so ids remain stable across
    /// restarts and rebinding one preset never shifts another preset's id.
    /// </summary>
    public void RegisterHotkeyWithId(int id, uint modifiers, uint virtualKey, Action action, string displayName)
    {
        lock (_lock)
        {
            RegisterHotkeyInternal(id, modifiers | User32Native.MOD_NOREPEAT, virtualKey, action, displayName);
            if (id >= _nextId)
                _nextId = id + 1;
        }
    }

    private void RegisterHotkeyInternal(int id, uint modifiers, uint virtualKey, Action action, string displayName)
    {
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("HotkeyManager.Initialize() must be called before registering hotkeys.");

        // Rebinding: release our own prior claim on this id first, so a
        // rebind of an existing preset doesn't spuriously report a conflict
        // against itself.
        if (_registered.ContainsKey(id))
        {
            User32Native.UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
        }

        bool ok = User32Native.RegisterHotKey(_hwnd, id, modifiers, virtualKey);
        if (!ok)
        {
            throw new HotkeyConflictException(id,
                $"Hotkey '{displayName}' could not be registered - the combination is already owned by another application.");
        }

        _registered[id] = new RegisteredHotkey
        {
            Id = id,
            Modifiers = modifiers,
            VirtualKey = virtualKey,
            Action = action,
            DisplayName = displayName
        };
    }

    /// <summary>
    /// Checks whether a modifier+key combination is free without permanently
    /// claiming it - used by the hotkey-recording UI to show a live conflict
    /// warning as the user types a new binding. Registers and immediately
    /// unregisters a reserved scratch id.
    /// </summary>
    public bool IsCombinationAvailable(uint modifiers, uint virtualKey)
    {
        lock (_lock)
        {
            if (_hwnd == IntPtr.Zero)
                return false;

            bool ok = User32Native.RegisterHotKey(_hwnd, ID_PROBE, modifiers | User32Native.MOD_NOREPEAT, virtualKey);
            if (ok)
                User32Native.UnregisterHotKey(_hwnd, ID_PROBE);
            return ok;
        }
    }

    public void UnregisterHotkey(int id)
    {
        lock (_lock)
        {
            if (_registered.Remove(id))
                User32Native.UnregisterHotKey(_hwnd, id);
        }
    }

    public void UnregisterAll()
    {
        lock (_lock)
        {
            foreach (var id in _registered.Keys)
                User32Native.UnregisterHotKey(_hwnd, id);
            _registered.Clear();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case User32Native.WM_HOTKEY:
                int id = wParam.ToInt32();
                RegisteredHotkey? hotkey;
                lock (_lock)
                {
                    _registered.TryGetValue(id, out hotkey);
                }
                hotkey?.Action?.Invoke();
                handled = true;
                break;

            case User32Native.WM_QUERYENDSESSION:
            case User32Native.WM_ENDSESSION:
                // System shutdown/logoff/restart in progress - give
                // SafetyWatchdog a chance to restore factory gamma before
                // Windows tears the process down.
                SessionEnding?.Invoke();
                handled = false; // never veto/block the session end
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        UnregisterAll();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
        _disposed = true;
    }
}
