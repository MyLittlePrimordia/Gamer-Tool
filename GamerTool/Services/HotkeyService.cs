using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Registers global hotkeys via Win32 RegisterHotKey against the main
/// window's handle, and hooks WM_HOTKEY through an HwndSource so games
/// running fullscreen still receive the keypress broadcast.
/// </summary>
public class HotkeyService : IDisposable
{
    // MOD_* values (Win32 fsModifiers for RegisterHotKey)
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, HotkeyBinding> _registeredById = new();
    private HwndSource? _source;
    private int _nextId = 1;

    public event Action<HotkeyBinding>? HotkeyPressed;

    public void AttachToWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        // Window must already have a handle (call after window is shown, or
        // force handle creation via helper.EnsureHandle()).
        IntPtr handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredById.TryGetValue(id, out var binding))
            {
                HotkeyPressed?.Invoke(binding);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Registers a single binding. Returns false (and leaves it
    /// unregistered) if the combo is already claimed by another app.</summary>
    public bool Register(HotkeyBinding binding)
    {
        if (_source == null)
            throw new InvalidOperationException("Call AttachToWindow before registering hotkeys.");

        int id = _nextId++;
        IntPtr handle = _source.Handle;

        bool ok = RegisterHotKey(handle, id, binding.Modifiers | MOD_NOREPEAT, binding.Key);
        if (ok)
            _registeredById[id] = binding;

        return ok;
    }

    public void UnregisterAll()
    {
        if (_source == null) return;
        foreach (var id in _registeredById.Keys)
            UnregisterHotKey(_source.Handle, id);
        _registeredById.Clear();
    }

    /// <summary>Convenience for re-reading bindings after the user edits the table.</summary>
    public void ReloadAll(IEnumerable<HotkeyBinding> bindings, out List<HotkeyBinding> failed)
    {
        UnregisterAll();
        failed = new List<HotkeyBinding>();
        foreach (var b in bindings)
        {
            if (!Register(b))
                failed.Add(b);
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }
}
