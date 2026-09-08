using System;

namespace GamerTool.Models;

/// <summary>
/// A "Comp Mode" combo: a single master hotkey that activates one Display
/// preset and one Audio preset together. DisplayPresetName/AudioPresetName
/// are denormalized copies of the linked presets' names, captured at combo
/// creation time purely so the Combos tab can display them without the view
/// needing to cross-reference two other collections by id.
/// </summary>
public sealed class ComboPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    public string DisplayPresetId { get; set; } = string.Empty;
    public string AudioPresetId { get; set; } = string.Empty;

    public string DisplayPresetName { get; set; } = string.Empty;
    public string AudioPresetName { get; set; } = string.Empty;

    public int? HotkeyId { get; set; }
    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }

    public ComboPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        DisplayPresetId = DisplayPresetId,
        AudioPresetId = AudioPresetId,
        DisplayPresetName = DisplayPresetName,
        AudioPresetName = AudioPresetName,
        HotkeyId = HotkeyId,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };
}
