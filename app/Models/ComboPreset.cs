using System;
using System.Collections.Generic;

namespace GamerTool.Models;

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

    public string PairText => (DisplayName ?? "?") + "  +  " + (AudioName ?? "?");

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

    public static IReadOnlyList<ComboPreset> Defaults { get; } = new List<ComboPreset>
    {
        new ComboPreset
        {
            Id = "combo_tactical",
            Name = "TACTICAL SHOOTER",
            Tag = "WASD AIM",
            Hotkey = "ALT+1",
            DisplayPresetId = "camper",
            AudioPresetId = "footstep"
        },
        new ComboPreset
        {
            Id = "combo_royale",
            Name = "BATTLE ROYALE",
            Tag = "DROP IN",
            Hotkey = "ALT+2",
            DisplayPresetId = "daylight",
            AudioPresetId = "royale"
        },
        new ComboPreset
        {
            Id = "combo_story",
            Name = "CINEMATIC",
            Tag = "WATCH IT",
            Hotkey = "ALT+3",
            DisplayPresetId = "cinematic",
            AudioPresetId = "cinematic"
        },
        new ComboPreset
        {
            Id = "combo_night",
            Name = "LATE NIGHT",
            Tag = "QUIET RUN",
            Hotkey = "ALT+4",
            DisplayPresetId = "night",
            AudioPresetId = "lofi"
        }
    };
}
