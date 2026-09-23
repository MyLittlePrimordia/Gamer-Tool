using System.IO;
using System.Text.Json;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Persists AppSettings (presets, hotkeys, last-applied state) to
/// %AppData%\GamerTool\profiles.json. Deliberately NOT under ProgramData —
/// this file is only ever read by GamerTool.exe itself (never by
/// audiodg.exe), so ordinary per-user AppData permissions are fine here.
/// Only the Equalizer APO config.txt needs the ProgramData/ACL treatment.
/// </summary>
public static class ProfileManager
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamerTool");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            string json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file: don't crash the app over it,
            // just fall back to defaults (user's custom presets are unfortunately
            // lost in this edge case, but a broken launch is worse).
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        // Atomic-ish write: temp file + replace, avoids truncated JSON if the
        // app is killed mid-save.
        string tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, SettingsPath, overwrite: true);
        File.Delete(tempPath);
    }
}
