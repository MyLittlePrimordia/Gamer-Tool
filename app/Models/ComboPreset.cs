using System;
using System.Collections.Generic;

namespace GamerTool.Models;

/// <summary>
/// A pairing of a screen preset and a sound preset that an older build kept as
/// its own list. Retained only so a settings file written by such a build can
/// still be read: <see cref="Services.SlotService.Migrate"/> turns any custom
/// combos a user made into slots, and nothing creates new ones.
/// </summary>
public sealed class ComboPreset
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public string Hotkey { get; set; } = string.Empty;

    public string DisplayPresetId { get; set; } = string.Empty;

    public string AudioPresetId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? AudioName { get; set; }

    public ComboPreset Copy()
    {
        return new ComboPreset
        {
            Id = Id,
            Name = Name,
            Tag = Tag,
            Hotkey = Hotkey,
            DisplayPresetId = DisplayPresetId,
            AudioPresetId = AudioPresetId
        };
    }

}

