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

/// <summary>
/// What a keystroke means while a keycap is listening for a new binding.
/// <para>
/// The decision lives here rather than in the key handler so it can be tested
/// without a window. Every value except <see cref="Bind"/> keeps the keycap
/// listening.
/// </para>
/// </summary>
public enum HotkeyCapture
{
    /// <summary>A modifier arriving on its own, or a bare key that needs one. Keep listening.</summary>
    NeedsModifier,

    /// <summary>Escape. Abandon the attempt and put the old binding back.</summary>
    Cancel,

    /// <summary>Backspace or Delete. Drop this slot's binding.</summary>
    Clear,

    /// <summary>A complete binding. Take <see cref="HotkeyModifiers"/> and a key.</summary>
    Bind
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

        _bindings[id] = new HotkeyBinding { Id = id, TargetId = targetId, Text = Normalise(text) };
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

    /// <summary>
    /// Reduces a key string to the one form two of them can be compared by.
    /// <para>
    /// Keys reach the app from three places that all format them differently: the
    /// live capture writes "ALT+1", a restored backup can hold "alt+1" or
    /// "ALT + 1" or "CONTROL+ALT+1", and a hand edited file can hold anything.
    /// Comparing the raw strings would let a duplicate through on spacing alone,
    /// or on a modifier spelled out, or on the modifiers being written in a
    /// different order, which is exactly the case the duplicate check exists to
    /// prevent. So this parses the string properly and rebuilds it in one fixed
    /// order, and anything unparseable falls back to a stripped uppercase form so
    /// two identical pieces of nonsense still collide rather than both claiming
    /// the same key.
    /// </para>
    /// <para>
    /// This is the single canonicaliser for the whole app. Duplicate detection at
    /// load time, duplicate detection at registration time and the clash check in
    /// the capture handler all call it, which is what makes those three agree.
    /// </para>
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (TryParse(text, out HotkeyModifiers mods, out Key key))
        {
            return FromInput(key, mods);
        }

        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }


    /// <summary>
    /// The key the user actually pressed, given the pair WPF reports.
    /// <para>
    /// Holding Alt makes WPF report <see cref="Key.System"/> and put the real key
    /// in <paramref name="systemKey"/>. Passing the first one through blindly is
    /// how "ALT+SYSTEM" ended up as a live binding: it looks like a chord, it
    /// survives a save and reload, and it never fires. Every caller that turns a
    /// key event into a binding has to go through here instead.
    /// </para>
    /// </summary>
    public static Key ResolveKey(Key key, Key systemKey) =>
        key == Key.System && systemKey != Key.System ? systemKey : key;

    /// <summary>True for a key that only modifies another key, never one that binds.</summary>
    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System
        or Key.None;

    /// <summary>
    /// F1 to F24. These are the one family that binds without a modifier, which
    /// is what every launcher and overlay does: they are not characters, so
    /// binding one cannot swallow anything the user was typing.
    /// </summary>
    public static bool IsFunctionKey(Key key) => key >= Key.F1 && key <= Key.F24;

    /// <summary>
    /// Decides what a keystroke means mid capture. Escape cancels, Backspace and
    /// Delete clear the slot, a lone modifier or an unmodified character key is
    /// refused politely and the keycap keeps listening, and anything else binds.
    /// </summary>
    public static HotkeyCapture ClassifyCapture(Key key, HotkeyModifiers mods)
    {
        if (IsModifierKey(key))
        {
            return HotkeyCapture.NeedsModifier;
        }

        if (key == Key.Escape)
        {
            return HotkeyCapture.Cancel;
        }

        if (key is Key.Back or Key.Delete)
        {
            return HotkeyCapture.Clear;
        }

        // Bare characters would eat typing everywhere, so they need a modifier.
        // F-keys are the deliberate exception.
        if (mods == HotkeyModifiers.None && !IsFunctionKey(key))
        {
            return HotkeyCapture.NeedsModifier;
        }

        return HotkeyCapture.Bind;
    }

    /// <summary>
    /// Renders a binding, or an empty string when the key cannot be one. A
    /// modifier or an unresolved <see cref="Key.System"/> produces nothing rather
    /// than a plausible looking binding, so a mistake upstream cannot be written
    /// into a slot and then persisted.
    /// </summary>
    public static string FromInput(Key key, HotkeyModifiers mods)
    {
        if (IsModifierKey(key))
        {
            return string.Empty;
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

        // "SYSTEM" and the modifier names all name real members of Key, so the
        // lookup above happily returns them. None of them can be pressed as a
        // chord, and accepting one is how "ALT+SYSTEM" came to be a saved binding
        // that displayed fine and never fired.
        return key != Key.None && !IsModifierKey(key);
    }

    /// <summary>
    /// True when a stored string names a binding that can actually be pressed.
    /// Used to drop the ones that cannot, rather than showing them as if they
    /// worked.
    /// </summary>
    public static bool IsBindable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && TryParse(text, out _, out _);

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
