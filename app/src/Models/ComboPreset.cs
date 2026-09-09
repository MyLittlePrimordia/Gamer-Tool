using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamerTool.Models;

/// <summary>
/// A "Comp Mode" combo: a single master hotkey that activates one Display
/// preset and one Audio preset together. DisplayPresetName/AudioPresetName
/// are denormalized copies of the linked presets' names, captured at combo
/// creation time purely so the Combos tab can display them without the view
/// needing to cross-reference two other collections by id. Only the HotkeyId
/// field notifies - the card caption must update live after recording.
/// </summary>
public sealed class ComboPreset : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }
    private string _name = string.Empty;

    /// <summary>Emoji glyph identifier shown next to the name - never part of Name itself.</summary>
    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconAndName)); }
    }
    private string _icon = string.Empty;

    /// <summary>"{name}" for text-only displays - icons render as separate Images now.</summary>
    public string IconAndName => Name;

    /// <summary>Sonar-style favorite: starred combos appear in Home/favorites + top of tray.</summary>
    public bool IsFavorite { get; set; }

    public string DisplayPresetId { get; set; } = string.Empty;
    public string AudioPresetId { get; set; } = string.Empty;

    public string DisplayPresetName { get; set; } = string.Empty;
    public string AudioPresetName { get; set; } = string.Empty;

    /// <summary>Id returned by HotkeyManager.RegisterHotkey, or null if this combo has no bound hotkey.</summary>
    public int? HotkeyId
    {
        get => _hotkeyId;
        set { _hotkeyId = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private int? _hotkeyId;

    public uint HotkeyModifiers
    {
        get => _hotkeyModifiers;
        set { _hotkeyModifiers = value; OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private uint _hotkeyModifiers;

    public uint HotkeyVirtualKey
    {
        get => _hotkeyVirtualKey;
        set { _hotkeyVirtualKey = value; OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private uint _hotkeyVirtualKey;

    /// <summary>Human-readable "Ctrl+Alt+F1" style label, or null when unbound.</summary>
    public string? HotkeyDisplayLabel =>
        HotkeyId.HasValue ? GamerTool.Core.HotkeyFormatting.Format(HotkeyModifiers, HotkeyVirtualKey) : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ComboPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        IsFavorite = IsFavorite,
        DisplayPresetId = DisplayPresetId,
        AudioPresetId = AudioPresetId,
        DisplayPresetName = DisplayPresetName,
        AudioPresetName = AudioPresetName,
        HotkeyId = HotkeyId,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };
}
