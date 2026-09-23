using System.Windows;
using System.Windows.Input;
using GamerTool.Services;

namespace GamerTool.UI;

public partial class HotkeyCaptureWindow : Window
{
    public uint CapturedModifiers { get; private set; }
    public uint CapturedVirtualKey { get; private set; }
    public string CapturedLabel { get; private set; } = "";

    public HotkeyCaptureWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore bare modifier presses; we want modifier+key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        uint mods = 0;
        var labelParts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { mods |= HotkeyService.MOD_CONTROL; labelParts.Add("Ctrl"); }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { mods |= HotkeyService.MOD_ALT; labelParts.Add("Alt"); }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { mods |= HotkeyService.MOD_SHIFT; labelParts.Add("Shift"); }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { mods |= HotkeyService.MOD_WIN; labelParts.Add("Win"); }

        if (mods == 0)
        {
            CapturedText.Text = "Add at least one modifier (Ctrl/Alt/Shift/Win) + a key";
            return;
        }

        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        labelParts.Add(key.ToString());

        CapturedModifiers = mods;
        CapturedVirtualKey = (uint)vk;
        CapturedLabel = string.Join("+", labelParts);
        CapturedText.Text = CapturedLabel;
        ConfirmButton.IsEnabled = true;

        e.Handled = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
