using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamerTool.Models;

/// <summary>
/// The full persisted state of GamerTool: every Display/Audio/Combo preset
/// (built-in and user-saved alike) and which two are currently active.
/// Serialized as plain JSON via System.Text.Json - part of the BCL for
/// net8.0-windows, so persistence stays zero-dependency.
/// </summary>
public sealed class AppSettings
{
    public bool RunOnStartup { get; set; }

    public string? ActiveDisplayPresetId { get; set; }
    public string? ActiveAudioPresetId { get; set; }

    public List<DisplayPreset> DisplayPresets { get; set; } = new();
    public List<AudioPreset> AudioPresets { get; set; } = new();
    public List<ComboPreset> ComboPresets { get; set; } = new();

    private static readonly string SettingsFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamerTool");

    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads settings.json if present and valid; otherwise builds a fresh
    /// AppSettings seeded with all 7 built-in Display presets and all 7
    /// built-in Audio presets (and no combos) - exactly the state a
    /// first-ever launch should present.
    /// </summary>
    public static AppSettings LoadOrCreateDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    EnsureBuiltInsPresent(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall through to a fresh
            // default rather than crashing startup over a bad settings.json.
        }

        return CreateDefault();
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            RunOnStartup = false,
            DisplayPresets = DisplayPreset.CreateBuiltIns(),
            AudioPresets = AudioPreset.CreateBuiltIns(),
            ComboPresets = new List<ComboPreset>(),
            ActiveDisplayPresetId = "builtin-default",
            ActiveAudioPresetId = "builtin-default"
        };
    }

    /// <summary>
    /// Defensive merge: if a future GamerTool version adds a built-in preset
    /// that isn't in an older saved settings.json, add it rather than
    /// silently leaving it missing from the UI. Built-ins whose ids match an
    /// older save also get their display names refreshed, so renamed
    /// built-ins (genre names + emojis) propagate to existing installs.
    /// </summary>
    private static void EnsureBuiltInsPresent(AppSettings settings)
    {
        foreach (var preset in DisplayPreset.CreateBuiltIns())
        {
            var existing = settings.DisplayPresets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing is null)
                settings.DisplayPresets.Add(preset);
            else if (existing.IsBuiltIn)
            {
                existing.Name = preset.Name;
                existing.Icon = preset.Icon;
            }
        }

        foreach (var preset in AudioPreset.CreateBuiltIns())
        {
            var existing = settings.AudioPresets.FirstOrDefault(p => p.Id == preset.Id);
            if (existing is null)
                settings.AudioPresets.Add(preset);
            else if (existing.IsBuiltIn)
            {
                existing.Name = preset.Name;
                existing.Icon = preset.Icon;
            }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort: a failed save should never crash the app - the
            // user simply keeps their in-memory state for the rest of this
            // session.
        }
    }
}
