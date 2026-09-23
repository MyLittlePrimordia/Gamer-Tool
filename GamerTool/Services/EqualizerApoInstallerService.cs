using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;

namespace GamerTool.Services;

/// <summary>
/// Handles the parts of Equalizer APO setup that were previously manual:
/// downloading the engine installer, running it silently, and wiring it to
/// the default playback device by writing the FxProperties registry entry
/// Configurator.exe would otherwise write when you tick its checkbox.
///
/// This must run from an already-elevated process (see
/// AudioService.RunElevatedSetupEntryPoint) — installing the engine and
/// writing MMDevices registry keys both require admin.
///
/// Safety posture: every step is verified before the next runs, and if the
/// direct registry registration fails or looks unsafe (endpoint already has
/// a *different* sAPO configured — i.e. the user is already using their own
/// audio enhancement software), we back off and open Equalizer APO's own
/// Configurator instead of guessing. That mirrors how other tools in this
/// space (e.g. FluidEQ) handle the same problem, for the same reason: a
/// wrong guess here reproduces exactly the "Access is denied" / muted-audio
/// failure this whole project exists to avoid.
/// </summary>
public static class EqualizerApoInstallerService
{
    // Primary: SourceForge "latest" redirect. Fallback: pinned 1.4.2 x64 direct path.
    private const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";
    private const string DownloadUrlFallback =
        "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";

    private const string EngineRegistryCheckKey = @"SOFTWARE\EqualizerAPO";
    // Equalizer APO Post-Mix (GFX / EFX) CLSID — used for SFX/EFX install mode.
    private const string PostMixEfxClsid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    // Property keys under FxProperties (PKEY_FX_*).
    private const string FxStreamEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";   // SFX
    private const string FxModeEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";     // MFX
    private const string FxEndpointEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7"; // EFX
    private const string FxPropertiesEndpointEffectValueName = FxEndpointEffect;

    public static string DefaultInstallDir =>
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\EqualizerAPO");

    public enum SetupOutcome
    {
        Success,
        AlreadyInstalled,
        DownloadFailed,
        InstallFailed,
        DeviceAutoRegisterFailed_ConfiguratorOpened,
        DeviceAlreadyHasOtherEffects_ConfiguratorOpened
    }

    /// <summary>
    /// Full orchestration: download engine if missing, silent-install,
    /// register the default render endpoint, restart audiosrv. Call only
    /// from an elevated process. Never throws — always returns an outcome
    /// so the caller can show the user something concrete.
    /// </summary>
    public static SetupOutcome RunFullAutoSetup(string? logPath = null)
    {
        void Log(string msg)
        {
            if (logPath != null)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
                catch { /* logging is best-effort */ }
            }
        }

        try
        {
            // Required for any unsigned APO (including Equalizer APO).
            try
            {
                using var audioKey = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", writable: true);
                audioKey?.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
                Log("DisableProtectedAudioDG set to 1.");
            }
            catch (Exception ex)
            {
                Log($"Could not set DisableProtectedAudioDG: {ex.Message}");
            }

            if (!IsEngineInstalled())
            {
                Log("Equalizer APO not found, downloading installer...");
                string? installerPath = DownloadInstaller();
                if (installerPath == null)
                {
                    Log("Download failed.");
                    return SetupOutcome.DownloadFailed;
                }

                Log($"Downloaded to {installerPath}, running silent install...");
                if (!SilentInstallEngine(installerPath))
                {
                    Log("Silent install failed or timed out.");
                    return SetupOutcome.InstallFailed;
                }

                TryDeleteFile(installerPath);

                if (!IsEngineInstalled())
                {
                    Log("Install reported success but engine still not detected in registry.");
                    return SetupOutcome.InstallFailed;
                }
                Log("Engine installed successfully.");
            }
            else
            {
                Log("Equalizer APO engine already installed, skipping download/install.");
            }

            // Register on EVERY active playback device so speakers + headphones both
            // get EQ without the user opening Configurator. Skip endpoints that
            // already have a *different* third-party enhancement (don't stomp).
            var devices = AudioDeviceService.EnumerateRenderDevices();
            if (devices.Count == 0)
            {
                Log("No active render endpoints found; opening Configurator.");
                LaunchConfiguratorElevated();
                return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
            }

            int registeredCount = 0;
            int skippedOther = 0;
            int alreadyOurs = 0;
            int failed = 0;

            foreach (var dev in devices)
            {
                string? guid = ExtractEndpointGuid(dev.Id);
                if (string.IsNullOrEmpty(guid))
                {
                    Log($"Skip device '{dev.FriendlyName}': could not parse endpoint GUID from {dev.Id}");
                    failed++;
                    continue;
                }

                var existing = ReadExistingEffectConfig(guid);
                if (existing.HasOtherEffects)
                {
                    Log($"Skip '{dev.FriendlyName}' ({guid}): other enhancements present.");
                    skippedOther++;
                    continue;
                }
                if (existing.AlreadyHasEqualizerApo)
                {
                    Log($"'{dev.FriendlyName}' already has Equalizer APO.");
                    alreadyOurs++;
                    registeredCount++;
                    continue;
                }

                Log($"Registering Equalizer APO on '{dev.FriendlyName}' ({guid})...");
                if (TryRegisterEfxForEndpoint(guid))
                {
                    registeredCount++;
                    Log($"  OK: registered {guid}");
                }
                else
                {
                    failed++;
                    Log($"  FAILED: registry write for {guid}");
                }
            }

            RestartAudioService();
            Log($"Done. registered/kept={registeredCount}, skippedOther={skippedOther}, failed={failed}");

            if (registeredCount > 0)
                return alreadyOurs == devices.Count && failed == 0 && skippedOther == 0
                    ? SetupOutcome.AlreadyInstalled
                    : SetupOutcome.Success;

            // Nothing registered — fall back to official UI.
            Log("No endpoints registered; opening Configurator for manual selection.");
            LaunchConfiguratorElevated();
            return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex}");
            // Last resort: give the user the official, always-safe path.
            try { LaunchConfiguratorElevated(); } catch { /* nothing more we can do headlessly */ }
            return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
        }
    }

    /// <summary>
    /// True only when EqualizerAPO.dll is actually on disk.
    /// A leftover HKLM\SOFTWARE\EqualizerAPO key (partial/failed install) used
    /// to make this return true and skip the download — that is exactly the
    /// bug the user hit. Always require the real DLL file.
    /// </summary>
    public static bool IsEngineInstalled()
    {
        string[] candidateDlls =
        {
            Path.Combine(DefaultInstallDir, "EqualizerAPO.dll"),
            Path.Combine(Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\EqualizerAPO"), "EqualizerAPO.dll"),
        };
        foreach (string dll in candidateDlls)
        {
            if (File.Exists(dll))
                return true;
        }

        // Registry InstallPath only counts if it points at a real DLL.
        try
        {
            string? installPath = null;
            using (var key = Registry.LocalMachine.OpenSubKey(EngineRegistryCheckKey))
                installPath = key?.GetValue("InstallPath") as string;

            if (string.IsNullOrEmpty(installPath))
            {
                using var baseKey64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key64 = baseKey64.OpenSubKey(EngineRegistryCheckKey);
                installPath = key64?.GetValue("InstallPath") as string;
            }

            if (!string.IsNullOrEmpty(installPath))
            {
                string dll = Path.Combine(installPath, "EqualizerAPO.dll");
                if (File.Exists(dll))
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>
    /// True if the current default playback device has Equalizer APO's Post-Mix
    /// CLSID written into its FxProperties. This is what actually puts EQ in
    /// the audio path — merely having the DLL installed is not enough.
    /// </summary>
    public static bool IsApoRegisteredOnDefaultDevice()
    {
        try
        {
            string? endpointGuid = GetDefaultRenderEndpointGuid();
            if (string.IsNullOrEmpty(endpointGuid)) return false;

            var existing = ReadExistingEffectConfig(endpointGuid);
            return existing.AlreadyHasEqualizerApo;
        }
        catch
        {
            return false;
        }
    }

    private static string? DownloadInstaller()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"EqualizerAPO-Setup-{Guid.NewGuid():N}.exe");
        string[] urls = { DownloadUrl, DownloadUrlFallback };

        foreach (string url in urls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GamerTool/1.0");

                using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) continue;

                using (var fileStream = File.Create(tempPath))
                using (var httpStream = response.Content.ReadAsStream())
                    httpStream.CopyTo(fileStream);

                var info = new FileInfo(tempPath);
                // Real installer is several MB; reject HTML error pages / stubs.
                if (info.Length < 1_000_000) { TryDeleteFile(tempPath); continue; }

                byte[] header = new byte[2];
                using (var fs = File.OpenRead(tempPath))
                    fs.ReadExactly(header, 0, 2);
                if (header[0] != 'M' || header[1] != 'Z') { TryDeleteFile(tempPath); continue; }

                return tempPath;
            }
            catch
            {
                TryDeleteFile(tempPath);
            }
        }

        return null;
    }

    private static bool SilentInstallEngine(string installerPath)
    {
        try
        {
            // /S = NSIS silent. Already running elevated from the parent setup,
            // so no extra UAC needed for the installer itself.
            var psi = new ProcessStartInfo(installerPath, "/S")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // Allow up to 3 minutes — first-time extract + reg writes can be slow.
            bool exited = proc.WaitForExit(180_000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            // Give the filesystem a moment, then require the real DLL.
            System.Threading.Thread.Sleep(1500);
            return IsEngineInstalled();
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort cleanup */ }
    }

    /// <summary>Endpoint GUID (e.g. "{5D16E8DA-...}") of the current default
    /// playback device, or null if it couldn't be determined.</summary>
    private static string? GetDefaultRenderEndpointGuid()
    {
        var devices = AudioDeviceService.EnumerateRenderDevices();
        var defaultDevice = devices.Find(d => d.IsDefault) ?? devices.FirstOrDefault();
        return defaultDevice == null ? null : ExtractEndpointGuid(defaultDevice.Id);
    }

    /// <summary>
    /// Endpoint IDs look like "{0.0.0.00000000}.{5D16E8DA-...}" — registry
    /// needs the segment after the last dot, including braces.
    /// </summary>
    private static string? ExtractEndpointGuid(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return null;
        int lastDot = endpointId.LastIndexOf('.');
        if (lastDot < 0 || lastDot >= endpointId.Length - 1) return null;
        string guid = endpointId[(lastDot + 1)..];
        if (!guid.StartsWith('{')) guid = "{" + guid.Trim('{', '}') + "}";
        return guid;
    }

    private readonly record struct ExistingEffectConfig(bool AlreadyHasEqualizerApo, bool HasOtherEffects);

    private static ExistingEffectConfig ReadExistingEffectConfig(string endpointGuid)
    {
        string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}\FxProperties";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key == null) return new ExistingEffectConfig(false, false);

        string[] slots =
        {
            FxStreamEffect, FxModeEffect, FxEndpointEffect,
            "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", // legacy LFX
            "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2", // legacy GFX
        };

        bool foundOurs = false;
        bool foundOther = false;
        foreach (string slot in slots)
        {
            if (key.GetValue(slot) is not string clsid || string.IsNullOrWhiteSpace(clsid))
                continue;
            if (string.Equals(clsid.Trim(), PostMixEfxClsid, StringComparison.OrdinalIgnoreCase))
                foundOurs = true;
            else
                foundOther = true;
        }

        // "Ours" wins if Equalizer APO is in any slot, even if OEM APO is also present
        // (Equalizer APO is designed to chain). Only treat pure-other as HasOtherEffects.
        if (foundOurs) return new ExistingEffectConfig(true, false);
        if (foundOther) return new ExistingEffectConfig(false, true);
        return new ExistingEffectConfig(false, false);
    }

    private static bool TryRegisterEfxForEndpoint(string endpointGuid)
    {
        string endpointKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}";
        string fxPropertiesPath = $@"{endpointKeyPath}\FxProperties";

        try
        {
            WriteEfxValue(fxPropertiesPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Expected on a fresh endpoint: FxProperties doesn't exist yet and
            // the parent key is TrustedInstaller-owned. Take ownership (the
            // scripted equivalent of the documented regedit workaround) and retry once.
        }
        catch (System.Security.SecurityException)
        {
        }

        try
        {
            RegistryOwnershipHelper.TakeOwnershipAndGrantAdmins(endpointKeyPath);
            WriteEfxValue(fxPropertiesPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteEfxValue(string fxPropertiesPath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(fxPropertiesPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create/open {fxPropertiesPath}");

        // SFX/EFX mode (preferred on Win10/11): put Equalizer APO in stream + endpoint slots.
        key.SetValue(FxStreamEffect, PostMixEfxClsid, RegistryValueKind.String);
        key.SetValue(FxEndpointEffect, PostMixEfxClsid, RegistryValueKind.String);
        // Also set EFX under the name we used for detection.
        key.SetValue(FxPropertiesEndpointEffectValueName, PostMixEfxClsid, RegistryValueKind.String);
    }

    /// <summary>Removes GamerTool's EFX registration (used by an "Uninstall
    /// Audio Engine" action), leaving everything else on the endpoint alone.</summary>
    public static void UnregisterEfxForEndpoint(string endpointGuid)
    {
        string fxPropertiesPath =
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}\FxProperties";

        using var key = Registry.LocalMachine.OpenSubKey(fxPropertiesPath, writable: true);
        if (key?.GetValue(FxPropertiesEndpointEffectValueName) is string clsid &&
            string.Equals(clsid.Trim(), PostMixEfxClsid, StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue(FxPropertiesEndpointEffectValueName, throwOnMissingValue: false);
        }

        RestartAudioService();
    }

    public static void LaunchConfiguratorElevated()
    {
        string configuratorPath = Path.Combine(DefaultInstallDir, "Configurator.exe");
        if (!File.Exists(configuratorPath)) return;

        try
        {
            var psi = new ProcessStartInfo(configuratorPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
        }
        catch
        {
            // User declined the UAC prompt or Configurator failed to launch;
            // nothing more we can do without blocking headlessly.
        }
    }

    private static void RestartAudioService()
    {
        RunSc("stop audiosrv");
        RunSc("start audiosrv");
    }

    private static void RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);
        }
        catch { /* best effort */ }
    }
}
