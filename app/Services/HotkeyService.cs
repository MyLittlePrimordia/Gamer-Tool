using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace GamerTool.Services;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public sealed class HotkeyBinding
{
    public int Id { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;

    private const uint ModControl = 0x0002;

    private const uint ModShift = 0x0004;

    private const uint ModWin = 0x0008;

    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;

    private readonly Dictionary<int, HotkeyBinding> _bindings = new();

    private bool _disposed;

    public event Action<HotkeyBinding>? Pressed;

    public event Action<string>? Failed;

    public HotkeyService()
    {
        HwndSourceParameters parameters = new("GamerToolHotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowHook);
    }

    public IReadOnlyList<HotkeyBinding> Bindings
    {
        get
        {
            List<HotkeyBinding> list = new();
            foreach (HotkeyBinding binding in _bindings.Values)
            {
                list.Add(binding);
            }

            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }
    }

    public void Clear()
    {
        foreach (int id in new List<int>(_bindings.Keys))
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _bindings.Clear();
    }

    public bool Register(int id, string targetId, string text)
    {
        if (_disposed)
        {
            return false;
        }

        UnregisterHotKey(_source.Handle, id);
        _bindings.Remove(id);

        if (!TryParse(text, out HotkeyModifiers mods, out Key key))
        {
            return false;
        }

        if (key == Key.None)
        {
            return false;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return false;
        }

        uint flags = ToNative(mods) | ModNoRepeat;
        if (!RegisterHotKey(_source.Handle, id, flags, virtualKey))
        {
            Failed?.Invoke(text);
            return false;
        }

        _bindings[id] = new HotkeyBinding { Id = id, TargetId = targetId, Text = Normalize(text) };
        return true;
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_bindings.TryGetValue(id, out HotkeyBinding? binding))
            {
                handled = true;
                Pressed?.Invoke(binding);
            }
        }

        return IntPtr.Zero;
    }

    public static string Normalize(string text)
    {
        if (!TryParse(text, out HotkeyModifiers mods, out Key key))
        {
            return text.ToUpperInvariant();
        }

        StringBuilder builder = new();
        if ((mods & HotkeyModifiers.Control) != 0)
        {
            builder.Append("CTRL+");
        }

        if ((mods & HotkeyModifiers.Alt) != 0)
        {
            builder.Append("ALT+");
        }

        if ((mods & HotkeyModifiers.Shift) != 0)
        {
            builder.Append("SHIFT+");
        }

        if ((mods & HotkeyModifiers.Win) != 0)
        {
            builder.Append("WIN+");
        }

        builder.Append(KeyToken(key));
        return builder.ToString();
    }

    public static string FromInput(Key key, HotkeyModifiers mods)
    {
        StringBuilder builder = new();
        if ((mods & HotkeyModifiers.Control) != 0)
        {
            builder.Append("CTRL+");
        }

        if ((mods & HotkeyModifiers.Alt) != 0)
        {
            builder.Append("ALT+");
        }

        if ((mods & HotkeyModifiers.Shift) != 0)
        {
            builder.Append("SHIFT+");
        }

        if ((mods & HotkeyModifiers.Win) != 0)
        {
            builder.Append("WIN+");
        }

        builder.Append(KeyToken(key));
        return builder.ToString();
    }

    public static HotkeyModifiers CurrentModifiers()
    {
        HotkeyModifiers mods = HotkeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            mods |= HotkeyModifiers.Control;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            mods |= HotkeyModifiers.Alt;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            mods |= HotkeyModifiers.Shift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
        {
            mods |= HotkeyModifiers.Win;
        }

        return mods;
    }

    public static string KeyToken(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return "NUM" + ((int)key - (int)Key.NumPad0).ToString();
        }

        if (key >= Key.A && key <= Key.Z)
        {
            return key.ToString();
        }

        if (key >= Key.F1 && key <= Key.F24)
        {
            return key.ToString();
        }

        return key.ToString().ToUpperInvariant();
    }

    public static Key KeyFromToken(string token)
    {
        string trimmed = token.Trim().ToUpperInvariant();
        if (trimmed.Length == 0)
        {
            return Key.None;
        }

        if (trimmed.Length == 1 && trimmed[0] >= '0' && trimmed[0] <= '9')
        {
            return (Key)((int)Key.D0 + (int)(trimmed[0] - '0'));
        }

        if (trimmed.StartsWith("NUM", StringComparison.Ordinal) && trimmed.Length == 4 && trimmed[3] >= '0' && trimmed[3] <= '9')
        {
            return (Key)((int)Key.NumPad0 + (int)(trimmed[3] - '0'));
        }

        if (Enum.TryParse(trimmed, ignoreCase: true, out Key parsed) && parsed != Key.None)
        {
            return parsed;
        }

        return Key.None;
    }

    public static bool TryParse(string text, out HotkeyModifiers mods, out Key key)
    {
        mods = HotkeyModifiers.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    mods |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    mods |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    mods |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    mods |= HotkeyModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        key = KeyFromToken(parts[^1]);
        return key != Key.None;
    }

    private static uint ToNative(HotkeyModifiers mods)
    {
        uint flags = 0;
        if ((mods & HotkeyModifiers.Alt) != 0)
        {
            flags |= ModAlt;
        }

        if ((mods & HotkeyModifiers.Control) != 0)
        {
            flags |= ModControl;
        }

        if ((mods & HotkeyModifiers.Shift) != 0)
        {
            flags |= ModShift;
        }

        if ((mods & HotkeyModifiers.Win) != 0)
        {
            flags |= ModWin;
        }

        return flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _source.RemoveHook(WindowHook);
        _source.Dispose();
    }
}
