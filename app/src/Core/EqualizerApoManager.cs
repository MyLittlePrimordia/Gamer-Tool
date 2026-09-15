using System;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// GamerTool's built-in EQ is a thin, friendly front-end over EqualizerAPO
/// (https://sourceforge.net/projects/equalizerapo/, GPL v2, by Jonas
/// Thedering) rather than a homemade audio plugin. A from-scratch, unsigned
/// Audio Processing Object is silently refused by Windows on most modern PCs
/// (Memory Integrity / Core Isolation, on by default since Windows 11) - no
/// crash, no error, it just never loads. EqualizerAPO's own binary is
/// properly signed and has solved this exact problem for over a decade, so
/// GamerTool detects it, offers a one-time guided install if it's missing,
/// and then just writes plain text config files that EqualizerAPO watches
/// and hot-reloads automatically - no shared memory, no COM, no elevation,
/// no native code of our own at all.
///
/// EqualizerAPO's installer grants the Windows "Users" group full access to
/// its config folder specifically (verified in their own Setup.nsi:
/// AccessControl::GrantOnFile "$INSTDIR\config" "(S-1-5-32-545)" "FullAccess"),
/// so once it's installed, GamerTool never needs administrator rights for
/// anything, ever - the elevation dance is entirely EqualizerAPO's own
/// installer's problem, one time, solved by someone who already solved it.
/// </summary>
public static class EqualizerApoManager
{
    private const string RegPath = @"Software\EqualizerAPO";
    private const string ConfigFileName = "GamerTool.txt";

    /// <summary>SourceForge's "latest" alias - always resolves to the current release, no version number to keep updated.</summary>
    public const string DownloadPageUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";

    public static bool IsInstalled() => GetConfigDirectory() is not null;

    /// <summary>
    /// The folder EqualizerAPO watches for config changes (normally
    /// "C:\Program Files\EqualizerAPO\config"), read from the registry key
    /// their installer writes rather than assuming a fixed path.
    /// </summary>
    public static string? GetConfigDirectory()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegPath);
            if (key is null)
                return null;

            var configPath = key.GetValue("ConfigPath") as string;
            if (!string.IsNullOrEmpty(configPath) && Directory.Exists(configPath))
                return configPath;

            var installPath = key.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath))
            {
                var fallback = Path.Combine(installPath, "config");
                if (Directory.Exists(fallback))
                    return fallback;
            }
        }
        catch
        {
            // Registry perms/corruption - "not installed" is the safe answer.
        }
        return null;
    }

    /// <summary>
    /// Downloads the official installer to a temp file and launches it
    /// (visibly - not silently). EqualizerAPO's installer includes a device-
    /// selection step, and rather than guess how that behaves unattended, a
    /// normal person is far better served clicking through 4-5 obvious
    /// dialog pages once than hitting a silently-wrong default.
    /// </summary>
    public static async System.Threading.Tasks.Task<string?> DownloadInstallerAsync()
    {
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), "EqualizerAPO-Setup.exe");
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(3);

            using var response = await http.GetAsync(DownloadPageUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using (var fileStream = File.Create(tempFile))
                await response.Content.CopyToAsync(fileStream);

            return tempFile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ensures the main config.txt includes GamerTool's own config file,
    /// WITHOUT touching anything else already in it - other tools (or the
    /// user by hand) may already be using EqualizerAPO for their own filters,
    /// and this must never clobber that.
    /// </summary>
    public static bool EnsureIncluded()
    {
        var dir = GetConfigDirectory();
        if (dir is null)
            return false;

        try
        {
            var mainConfigPath = Path.Combine(dir, "config.txt");
            var includeLine = $"Include: {ConfigFileName}";

            string existing = File.Exists(mainConfigPath) ? File.ReadAllText(mainConfigPath) : string.Empty;
            if (existing.Contains(includeLine, StringComparison.OrdinalIgnoreCase))
                return true;

            var separator = existing.Length > 0 && !existing.EndsWith("\n") ? "\r\n" : "";
            File.AppendAllText(mainConfigPath, separator + includeLine + "\r\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes GamerTool's own include file: a 10-band graphic EQ plus
    /// preamp, translated directly from the same band frequencies and gains
    /// the UI already shows. EqualizerAPO watches this file and hot-reloads
    /// within a fraction of a second of it changing - no signal, no restart,
    /// no version-bump protocol needed, just a plain text file write.
    /// </summary>
    public static bool WriteConfig(double[] bandGainsDb, double[] bandFrequenciesHz, double preampDb)
    {
        var dir = GetConfigDirectory();
        if (dir is null || bandGainsDb.Length != bandFrequenciesHz.Length)
            return false;

        try
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("# Written automatically by Gamer Tool - do not edit by hand, your changes will be overwritten.");
            lines.AppendLine($"Preamp: {preampDb:0.0} dB");

            var bands = new string[bandGainsDb.Length];
            for (int i = 0; i < bandGainsDb.Length; i++)
                bands[i] = $"{bandFrequenciesHz[i]:0} {bandGainsDb[i]:0.0}";
            lines.AppendLine("GraphicEQ: " + string.Join("; ", bands));

            File.WriteAllText(Path.Combine(dir, ConfigFileName), lines.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True bypass: flat EQ, no preamp - audio passes through EqualizerAPO unmodified.</summary>
    public static bool WriteFlatConfig(double[] bandFrequenciesHz)
    {
        var flat = new double[bandFrequenciesHz.Length];
        return WriteConfig(flat, bandFrequenciesHz, 0.0);
    }
}
