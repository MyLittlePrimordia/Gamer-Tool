using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using GamerTool.Services.AudioBridge;

namespace GamerTool.Services;

/// <summary>
/// Downloads, verifies, and silently installs VB-CABLE — a free, already
/// WHQL-signed virtual audio driver — which the Bridge uses as the
/// intermediate "sink" apps render to (see AudioBridgeService for why one is
/// needed at all). This must run from an elevated process: driver
/// installation requires admin regardless of signing status.
/// </summary>
public static class VirtualCableInstallerService
{
    // Pinned to a specific, checksum-verified release rather than a "latest"
    // redirector (VB-Audio doesn't publish one). If this ever 404s or the
    // checksum stops matching because VB-Audio shipped a newer pack, we
    // deliberately do NOT fall back to installing an unverified file — see
    // the checksum check below.
    private const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";
    private const string ExpectedSha256 = "66FD0A4D9F4896FF41632B7E3D53892C085C4561F53E8AE8D0F0BC10EEDD1CDD";

    public enum SetupOutcome
    {
        Success,
        AlreadyInstalled,
        DownloadFailed,
        ChecksumMismatch_Aborted,
        InstallFailed,
        InstalledButRebootRequired
    }

    /// <summary>Relaunches GamerTool.exe elevated with the hidden cable-setup
    /// flag, waits for it, and returns the outcome via exit code. Call this
    /// from the normal (non-elevated) UI process.</summary>
    public static SetupOutcome? RunElevated()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not resolve executable path.");

            var psi = new ProcessStartInfo(exePath, App.ElevatedCableSetupArg)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit();

            int code = proc?.ExitCode ?? -1;
            return code >= 0 && Enum.IsDefined(typeof(SetupOutcome), code)
                ? (SetupOutcome)code
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // user declined the UAC prompt
        }
    }

    public static SetupOutcome RunFullSetup(string? logPath = null)
    {
        void Log(string msg)
        {
            if (logPath == null) return;
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
            catch { /* best effort */ }
        }

        try
        {
            if (AudioBridgeService.FindVirtualCableInput() != null)
            {
                Log("VB-CABLE already installed and visible.");
                return SetupOutcome.AlreadyInstalled;
            }

            Log("Downloading VB-CABLE driver pack...");
            string? zipPath = Download();
            if (zipPath == null)
            {
                Log("Download failed.");
                return SetupOutcome.DownloadFailed;
            }

            string actualHash = ComputeSha256(zipPath);
            if (!string.Equals(actualHash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately refuse to proceed with an unverified binary.
                // A mismatch usually just means VB-Audio shipped a newer
                // pack (in which case this constant needs updating) — either
                // way, guessing wrong here means silently running an
                // unverified installer with admin rights, which is worse
                // than asking the user to grab it themselves once.
                Log($"Checksum mismatch: expected {ExpectedSha256}, got {actualHash}. Aborting for safety.");
                TryDeleteFile(zipPath);
                return SetupOutcome.ChecksumMismatch_Aborted;
            }

            string extractDir = Path.Combine(Path.GetTempPath(), $"VBCABLE-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            TryDeleteFile(zipPath);

            string? setupExe = Directory.GetFiles(extractDir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (setupExe == null)
            {
                Log("VBCABLE_Setup_x64.exe not found inside the downloaded pack.");
                return SetupOutcome.InstallFailed;
            }

            Log($"Running silent install: {setupExe} -i -h");
            var psi = new ProcessStartInfo(setupExe, "-i -h")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(setupExe)
            };
            using (var proc = Process.Start(psi))
            {
                proc?.WaitForExit(120_000);
                Log($"Installer exited (code {proc?.ExitCode.ToString() ?? "unknown"}).");
            }

            // Give Windows a moment to enumerate the new device before we check.
            Thread.Sleep(3000);

            if (AudioBridgeService.FindVirtualCableInput() != null)
            {
                Log("VB-CABLE installed and device is visible.");
                return SetupOutcome.Success;
            }

            // VB-Audio's own documentation notes a reboot can be required
            // before the virtual device fully registers with the Windows
            // audio subsystem, even after a successful silent install.
            Log("Install completed but device isn't visible yet — likely needs a reboot.");
            return SetupOutcome.InstalledButRebootRequired;
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex}");
            return SetupOutcome.InstallFailed;
        }
    }

    private static string? Download()
    {
        try
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"VBCABLE-{Guid.NewGuid():N}.zip");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GamerTool/1.0");

            using var response = http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using (var fileStream = File.Create(tempPath))
            using (var httpStream = response.Content.ReadAsStream())
            {
                httpStream.CopyTo(fileStream);
            }

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
