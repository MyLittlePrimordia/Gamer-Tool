using System.Windows.Input;

namespace GamerTool.Models;

/// <summary>What a hotkey should trigger when pressed.</summary>
public enum BindingAction
{
    DisplayOnly,
    AudioOnly,
    Combo,
    PanicReset
}

/// <summary>
/// A combo preset simply pairs the name of a DisplayPreset with the name of an
/// AudioPreset so both can be applied atomically from a single hotkey press.
/// </summary>
public class ComboPreset
{
    public string Name { get; set; } = "New Combo";
    public string DisplayPresetName { get; set; } = "";
    public string AudioPresetName { get; set; } = "";
}

/// <summary>
/// One row in the hotkey table: a key combination bound to an action.
/// Modifiers use the Win32 MOD_* bit values (see HotkeyService).
/// </summary>
public class HotkeyBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public uint Modifiers { get; set; }
    public uint Key { get; set; } // Win32 virtual-key code
    public BindingAction Action { get; set; } = BindingAction.DisplayOnly;
    public string TargetName { get; set; } = ""; // preset/combo name this binding applies

    /// <summary>Human-readable label like "Ctrl+Shift+1", rebuilt on load, not persisted logic.</summary>
    public string DisplayText { get; set; } = "";
}
