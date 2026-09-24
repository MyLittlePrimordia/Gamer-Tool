using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
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
    // SourceForge's "latest" redirector always resolves to the current
    // Windows installer for this project, so we don't have to scrape a
    // version number or hardcode a URL that goes stale.
    private const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";

    private const string EngineRegistryCheckKey = @"SOFTWARE\EqualizerAPO";
    private const string PostMixEfxClsid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    private const string FxPropertiesEndpointEffectValueName = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";

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
                bool ranCleanly = SilentInstallEngine(installerPath, out int exitCode);
                Log($"Installer process finished (ranCleanly={ranCleanly}, exitCode={exitCode}).");

                TryDeleteFile(installerPath);

                if (!IsEngineInstalled())
                {
                    Log("Engine still not detected in registry after install — treating as failed.");
                    return SetupOutcome.InstallFailed;
                }
                Log("Engine installed successfully.");
            }
            else
            {
                Log("Equalizer APO engine already installed, skipping download/install.");
            }

            string? endpointGuid = GetDefaultRenderEndpointGuid();
            if (endpointGuid == null)
            {
                Log("Could not resolve default playback device GUID; opening Configurator for manual selection.");
                LaunchConfiguratorElevated();
                return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
            }

            var existing = ReadExistingEffectConfig(endpointGuid);
            if (existing.HasOtherEffects)
            {
                Log($"Endpoint {endpointGuid} already has non-Equalizer-APO effects configured " +
                    "(another audio enhancement tool is active) — not overwriting. Opening Configurator " +
                    "so the user can decide.");
                LaunchConfiguratorElevated();
                return SetupOutcome.DeviceAlreadyHasOtherEffects_ConfiguratorOpened;
            }

            if (existing.AlreadyHasEqualizerApo)
            {
                Log($"Endpoint {endpointGuid} already has Equalizer APO registered. Nothing to do.");
                RestartAudioService();
                return SetupOutcome.AlreadyInstalled;
            }

            Log($"Registering Equalizer APO EFX for endpoint {endpointGuid}...");
            bool registered = TryRegisterEfxForEndpoint(endpointGuid);
            if (!registered)
            {
                Log("Direct registry registration failed; opening Configurator for manual completion.");
                LaunchConfiguratorElevated();
                return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
            }

            RestartAudioService();
            Log("Setup complete.");
            return SetupOutcome.Success;
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex}");
            // Last resort: give the user the official, always-safe path.
            try { LaunchConfiguratorElevated(); } catch { /* nothing more we can do headlessly */ }
            return SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened;
        }
    }

    public static bool IsEngineInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(EngineRegistryCheckKey);
        if (key != null) return true;

        // 32-bit installers get redirected away from the 64-bit registry view
        // by WOW6432Node unless read with the right view explicitly — check
        // both, since this exact gap is a known source of false negatives.
        using var baseKey64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key64 = baseKey64.OpenSubKey(EngineRegistryCheckKey);
        if (key64 != null) return true;

        using var baseKey32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key32 = baseKey32.OpenSubKey(EngineRegistryCheckKey);
        return key32 != null;
    }

    private static string? DownloadInstaller()
    {
        try
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"EqualizerAPO-Setup-{Guid.NewGuid():N}.exe");

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GamerTool/1.0");

            using var response = http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using (var fileStream = File.Create(tempPath))
            using (var httpStream = response.Content.ReadAsStream())
            {
                httpStream.CopyTo(fileStream);
            }

            // Sanity checks: a real installer is a multi-MB PE executable.
            // Anything smaller is almost certainly an error page or redirect
            // stub, not the actual installer — bail rather than "installing" garbage.
            var info = new FileInfo(tempPath);
            if (info.Length < 500_000)
            {
                TryDeleteFile(tempPath);
                return null;
            }

            byte[] header = new byte[2];
            using (var fs = File.OpenRead(tempPath))
                fs.ReadExactly(header, 0, 2);
            if (header[0] != 'M' || header[1] != 'Z') // PE header magic
            {
                TryDeleteFile(tempPath);
                return null;
            }

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static bool SilentInstallEngine(string installerPath, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo(installerPath, "/S")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // The NSIS installer's silent mode is normally quick, but give it
            // a generous ceiling rather than hanging GamerTool's setup forever
            // if something about the target machine makes it slow.
            bool exited = proc.WaitForExit(120_000);
            exitCode = exited ? proc.ExitCode : -1;

            // Give the installer's own child processes (it launches a few) a
            // moment to finish writing registry entries after the main
            // process reports exit — avoids a race where IsEngineInstalled()
            // checks a split second too early.
            if (exited) Thread.Sleep(2000);

            return exited;
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
        var defaultDevice = devices.Find(d => d.IsDefault);
        string? endpointId = defaultDevice?.Id;
        if (string.IsNullOrEmpty(endpointId)) return null;

        // Endpoint IDs look like "{0.0.0.00000000}.{5D16E8DA-...}" — the
        // GUID we need for the registry path is the segment after the last dot.
        int lastDot = endpointId.LastIndexOf('.');
        return lastDot >= 0 && lastDot < endpointId.Length - 1
            ? endpointId[(lastDot + 1)..]
            : null;
    }

    private readonly record struct ExistingEffectConfig(bool AlreadyHasEqualizerApo, bool HasOtherEffects);

    private static ExistingEffectConfig ReadExistingEffectConfig(string endpointGuid)
    {
        string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}\FxProperties";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key == null) return new ExistingEffectConfig(false, false);

        object? efx = key.GetValue(FxPropertiesEndpointEffectValueName);
        if (efx is string efxClsid)
        {
            bool isOurs = string.Equals(efxClsid.Trim(), PostMixEfxClsid, StringComparison.OrdinalIgnoreCase);
            return new ExistingEffectConfig(isOurs, !isOurs);
        }

        // No EFX set, but check the legacy GFX slot too — some older driver
        // configs still use it and we don't want to fight with them either.
        object? gfx = key.GetValue(@"{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2");
        if (gfx is string gfxClsid && !string.IsNullOrWhiteSpace(gfxClsid))
            return new ExistingEffectConfig(false, true);

        return new ExistingEffectConfig(false, false);
    }

    private static bool TryRegisterEfxForEndpoint(string endpointGuid)
    {
        string endpointKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}";
        string fxPropertiesPath = $@"{endpointKeyPath}\FxProperties";

        try
        {
            WriteEfxValue(fxPropertiesPath);
            if (VerifyEfxValue(fxPropertiesPath)) return true;
        }
        catch (UnauthorizedAccessException) { /* fall through to ownership takeover below */ }
        catch (System.Security.SecurityException) { /* fall through to ownership takeover below */ }

        // Two distinct cases need two distinct fixes:
        //  - FxProperties already exists (e.g. Windows created it with some
        //    default) but we can't write to it: take ownership of
        //    FxProperties itself. Taking ownership of the PARENT key and
        //    relying on ACL inheritance to cascade down to an EXISTING child
        //    is not reliable — Windows only auto-propagates inheritable ACEs
        //    to children created AFTER the parent's ACL changes.
        //  - FxProperties doesn't exist yet: we need CreateSubKey rights on
        //    the parent ({endpointGuid}) key instead, since that's what's
        //    actually missing.
        bool fxPropertiesExists;
        using (var probe = Registry.LocalMachine.OpenSubKey(fxPropertiesPath))
            fxPropertiesExists = probe != null;

        try
        {
            RegistryOwnershipHelper.TakeOwnershipAndGrantAdmins(
                fxPropertiesExists ? fxPropertiesPath : endpointKeyPath);

            WriteEfxValue(fxPropertiesPath);
            return VerifyEfxValue(fxPropertiesPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the value straight back to confirm the write actually
    /// landed — a registry call that "succeeds" but silently no-ops (e.g.
    /// virtualized write redirection) would otherwise look identical to a
    /// real success and leave the endpoint un-EQ'd with no error shown.</summary>
    private static bool VerifyEfxValue(string fxPropertiesPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(fxPropertiesPath);
        return key?.GetValue(FxPropertiesEndpointEffectValueName) is string v &&
               string.Equals(v.Trim(), PostMixEfxClsid, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteEfxValue(string fxPropertiesPath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(fxPropertiesPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create/open {fxPropertiesPath}");

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
