using System.Diagnostics;
using System.IO;
using System.Text;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Drives system-wide audio EQ through Equalizer APO (a user-mode COM DLL that
/// hosts inside audiodg.exe — no WHQL kernel driver needed, unlike FxSound).
///
/// ROOT CAUSE FIX for the "Access is denied" / muted-audio bug from the
/// previous attempt: audiodg.exe runs as NT AUTHORITY\LOCAL SERVICE. If the
/// EQ config file lives under %USERPROFILE% or %APPDATA%, LOCAL SERVICE has
/// no read access to it, the APO faults on its real-time callback, and
/// Windows mutes the endpoint as a failsafe. The fix is to store the config
/// under C:\ProgramData\GamerTool\EQ and explicitly ICACLS-grant read/execute
/// to "NT SERVICE\Audiosrv" and "LOCAL SERVICE". That single directory choice
/// is what makes this reliable.
///
/// Because ICACLS + the one-time Equalizer APO installer/registry hook need
/// admin, that step is done by relaunching this exe with verb "runas" and a
/// hidden CLI flag, NOT by making the whole app run elevated all the time.
/// </summary>
public static class AudioService
{
    // GamerTool keeps its own copy under ProgramData (ACL-protected for LOCAL SERVICE).
    // Equalizer APO itself always loads from its install dir. During elevated setup we
    // write an Include line into Equalizer APO's config.txt so both stay in sync.
    public static readonly string EqDirectory = @"C:\ProgramData\GamerTool\EQ";
    public static readonly string ConfigPath = Path.Combine(EqDirectory, "config.txt");

    public static string EqualizerApoConfigPath =>
        Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\EqualizerAPO\config"),
            "config.txt");

    private const string ElevatedSetupFlag = "--elevated-audio-setup";

    /// <summary>
    /// True only when Equalizer APO is installed AND registered on the current
    /// default playback device AND DisableProtectedAudioDG is set.
    /// A weak check (folder exists + installer present) was falsely reporting
    /// "Ready" while EQ had no effect — this is the stricter version.
    /// </summary>
    public static bool IsEngineReady()
    {
        try
        {
            if (!EqualizerApoInstallerService.IsEngineInstalled())
                return false;

            // Unsigned APOs require this flag on modern Windows.
            using (var audioKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"))
            {
                object? v = audioKey?.GetValue("DisableProtectedAudioDG");
                if (v is not int i || i != 1)
                    return false;
            }

            // APO must be attached to the default render endpoint.
            return EqualizerApoInstallerService.IsApoRegisteredOnDefaultDevice();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches THIS SAME exe elevated with a hidden setup flag so the user
    /// sees exactly one UAC prompt, only when they opt in to audio EQ. The
    /// elevated process does everything: the ProgramData ACL fix, downloading
    /// and silently installing Equalizer APO if it's missing, and registering
    /// it against the current default playback device. Returns the outcome
    /// so the UI can say something more useful than a plain pass/fail.
    /// </summary>
    public static EqualizerApoInstallerService.SetupOutcome? RunElevatedSetup()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not resolve executable path.");

            var psi = new ProcessStartInfo(exePath, ElevatedSetupFlag)
            {
                UseShellExecute = true,
                Verb = "runas" // triggers the UAC prompt
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit();

            int code = proc?.ExitCode ?? -1;
            return code >= 0 && Enum.IsDefined(typeof(EqualizerApoInstallerService.SetupOutcome), code)
                ? (EqualizerApoInstallerService.SetupOutcome)code
                : null; // negative/unrecognized = setup process itself failed (e.g. UAC declined)
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User clicked "No" on the UAC prompt.
            return null;
        }
    }

    /// <summary>
    /// Entry point when GamerTool.exe is relaunched with --elevated-audio-setup.
    /// Call this from App.xaml.cs / Main before any WPF window is created.
    /// Does the ACL grant + directory bootstrap, then hands off to
    /// EqualizerApoInstallerService for the download/install/device-registration
    /// steps. Returns the outcome as an int (cast of SetupOutcome) for the
    /// non-elevated parent process to read via exit code.
    /// </summary>
    public static int RunElevatedSetupEntryPoint()
    {
        string logPath = Path.Combine(EqDirectory, "setup.log");
        try
        {
            Directory.CreateDirectory(EqDirectory);

            // Grant audiodg.exe (running as these two identities) read+execute
            // on the whole EQ folder, recursively, for current and future files.
            RunIcacls($"\"{EqDirectory}\" /grant \"NT SERVICE\\Audiosrv\":(OI)(CI)RX /T /C /Q");
            RunIcacls($"\"{EqDirectory}\" /grant \"LOCAL SERVICE\":(OI)(CI)RX /T /C /Q");

            // Seed a flat config so the folder is never empty/invalid.
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, BuildConfigText(AudioPreset.Flat), Encoding.UTF8);

            // Critical: allow unsigned APOs (Equalizer APO is unsigned).
            // Without this, audiodg.exe silently refuses to load the DLL on
            // modern Windows 10/11.
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", writable: true);
                key?.SetValue("DisableProtectedAudioDG", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { /* best-effort; installer service also tries */ }

            var outcome = EqualizerApoInstallerService.RunFullAutoSetup(logPath);

            // After engine install, point Equalizer APO's own config at ours
            // so live slider changes take effect.
            TryWireIncludeConfig();

            return (int)outcome;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Makes Equalizer APO load GamerTool's config by writing a single Include
    /// line into its standard config.txt. Safe to call repeatedly.
    /// </summary>
    public static void TryWireIncludeConfig()
    {
        try
        {
            string apoConfigDir = Path.GetDirectoryName(EqualizerApoConfigPath)!;
            if (!Directory.Exists(apoConfigDir)) return;

            // Ensure LOCAL SERVICE can also read the ProgramData config
            // (already done in setup, but re-assert here for safety).
            string includeLine = $"Include: {ConfigPath}";
            string content =
                "# Managed by GamerTool — do not edit by hand\r\n" +
                "# All EQ settings live in the Include file below.\r\n" +
                includeLine + "\r\n";

            File.WriteAllText(EqualizerApoConfigPath, content, Encoding.UTF8);

            // Also grant the APO install folder read access just in case
            RunIcacls($"\"{apoConfigDir}\" /grant \"NT SERVICE\\Audiosrv\":(OI)(CI)RX /T /C /Q");
            RunIcacls($"\"{apoConfigDir}\" /grant \"LOCAL SERVICE\":(OI)(CI)RX /T /C /Q");
        }
        catch
        {
            // Non-fatal: user can still open Configurator and tick the device.
        }
    }

    private static void RunIcacls(string arguments)
    {
        var psi = new ProcessStartInfo("icacls.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
        if (proc != null && proc.ExitCode != 0)
        {
            string err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"icacls failed ({proc.ExitCode}): {err}");
        }
    }

    /// <summary>
    /// Writes the 10-band EQ + preamp as Equalizer APO filter syntax, with
    /// anti-clip preamp compensation. Write is a full-file overwrite (not an
    /// append) since Equalizer APO hot-reloads the whole file on change and a
    /// partial/appended write could be read mid-write and crash the parser.
    /// </summary>
    public static void ApplyPreset(AudioPreset preset)
    {
        if (!IsEngineReady())
            throw new InvalidOperationException(
                "Audio engine isn't set up yet. Click \"Enable Audio EQ\" first (one-time, needs admin).");

        string text = BuildConfigText(preset);

        // Write to a temp file then move-replace, so audiodg.exe's file watcher
        // never observes a half-written config (the earlier root cause of
        // filter-parse failures and sudden mutes).
        Directory.CreateDirectory(EqDirectory);
        string tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, text, Encoding.UTF8);
        File.Copy(tempPath, ConfigPath, overwrite: true);
        try { File.Delete(tempPath); } catch { }

        // Keep the Include wire-up healthy in case the user (or another tool)
        // overwrote Equalizer APO's config.txt.
        TryWireIncludeConfig();
    }

    private static string BuildConfigText(AudioPreset preset)
    {
        double maxGain = 0;
        foreach (var g in preset.Bands)
            if (g > maxGain) maxGain = g;

        double effectivePreamp = preset.Preamp;
        if (preset.AntiClip && effectivePreamp + maxGain > 0)
            effectivePreamp -= effectivePreamp + maxGain;

        var sb = new StringBuilder();
        sb.AppendLine("# Generated by GamerTool - do not edit by hand, changes are overwritten");
        sb.AppendLine($"Preamp: {effectivePreamp:F1} dB");

        for (int i = 0; i < AudioPreset.Frequencies.Length && i < preset.Bands.Length; i++)
        {
            sb.AppendLine(
                $"Filter {i + 1}: ON PK Fc {AudioPreset.Frequencies[i]} Hz Gain {preset.Bands[i]:F1} dB Q 1.41");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The "Panic / Reset Audio" button. Writes a flat/bypassed config
    /// (0 dB everywhere) rather than deleting the file, which is enough to
    /// restore normal sound instantly without touching the registry — safer
    /// than a live registry edit while audiodg.exe is running. Also restarts
    /// the Windows Audio service as a last-resort stream-recovery kick.
    /// </summary>
    public static void PanicReset()
    {
        try
        {
            if (Directory.Exists(EqDirectory))
                File.WriteAllText(ConfigPath, BuildConfigText(AudioPreset.Flat), Encoding.UTF8);
        }
        catch { /* best effort: even if the write fails, still try the service restart */ }

        RunElevatedServiceRestart();
    }

    private static void RunElevatedServiceRestart()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var psi = new ProcessStartInfo(exePath, "--elevated-panic-reset")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { /* user declined UAC; flat config write above already helps */ }
    }

    /// <summary>Entry point for --elevated-panic-reset: restarts audiosrv.</summary>
    public static int RunElevatedPanicResetEntryPoint()
    {
        try
        {
            RunSc("stop audiosrv");
            RunSc("start audiosrv");
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void RunSc(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
    }

    public const string ElevatedSetupArg = ElevatedSetupFlag;
    public const string ElevatedPanicArg = "--elevated-panic-reset";
}
