using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GamerTool.Services;

public sealed class SetupStage
{
    public int Percent { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>What a launch-time winget query found for FxSound.</summary>
public sealed class FxUpdateResult
{
    public bool UpdateAvailable { get; set; }

    public string InstalledVersion { get; set; } = string.Empty;

    public string AvailableVersion { get; set; } = string.Empty;
}

public sealed class SetupService
{
    public const string DownloadUrl = "https://download.fxsound.com/fxsoundlatest";

    public const string WingetId = "FxSound.FxSound";

    public static string InstallerPath => Path.Combine(Path.GetTempPath(), "fxsound_setup.exe");

    public event Action<string>? StatusChanged;

    public bool IsWingetAvailable()
    {
        return File.Exists(WingetPath());
    }

    private static string WingetPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");
    }

    public async Task<bool> InstallBestAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        if (IsInstalled(audio))
        {
            progress.Report(new SetupStage { Percent = 100, Text = "READY" });
            StartEngine(audio);
            return true;
        }

        if (IsWingetAvailable() && await InstallWithWingetAsync(audio, progress, token).ConfigureAwait(false))
        {
            return true;
        }

        return await DownloadAndInstallAsync(audio, progress, token).ConfigureAwait(false);
    }

    public async Task<bool> InstallWithWingetAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        try
        {
            progress.Report(new SetupStage { Percent = 5, Text = "WINGET" });
            StatusChanged?.Invoke("INSTALLING FXSOUND");

            string arguments =
                "install --id " + WingetId
                + " -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

            (int exitCode, string log) = await RunWingetAsync(arguments, TimeSpan.FromMinutes(15), token).ConfigureAwait(false);
            TraceLog.Write("WINGET " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + log.Trim());

            progress.Report(new SetupStage { Percent = 90, Text = "FINISHING" });
            AdoptInstalledPath(audio);

            bool ok = IsInstalled(audio);
            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                StartEngine(audio);
            }

            return ok;
        }
        catch (Exception ex)
        {
            TraceLog.Write("WINGET", ex);
            progress.Report(new SetupStage { Percent = 0, Text = "RETRY" });
            return false;
        }
    }

    /// <summary>
    /// Asks winget what the newest published FxSound is, then compares it with the
    /// version of the exe on this PC. Runs off the UI thread at launch; returns
    /// null when winget is missing or the query fails, which simply means no
    /// upgrade badge is shown rather than an error state.
    /// </summary>
    public FxUpdateResult? CheckForUpdate()
    {
        try
        {
            if (!IsWingetAvailable())
            {
                return null;
            }

            string installed = InstalledVersion();
            if (installed.Length == 0)
            {
                return null;
            }

            (int exitCode, string log) = RunWingetAsync(
                "show --id " + WingetId + " -e --source winget --accept-source-agreements --disable-interactivity",
                TimeSpan.FromSeconds(45),
                CancellationToken.None).GetAwaiter().GetResult();

            if (exitCode != 0)
            {
                return null;
            }

            string available = ParsePublishedVersion(log);
            if (available.Length == 0)
            {
                return null;
            }

            return new FxUpdateResult
            {
                InstalledVersion = installed,
                AvailableVersion = available,
                UpdateAvailable = Compare(installed, available) < 0
            };
        }
        catch (Exception ex)
        {
            TraceLog.Write("UPDATE CHECK", ex);
            return null;
        }
    }

    /// <summary>Runs the actual winget upgrade for FxSound.</summary>
    public async Task<bool> UpgradeAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        try
        {
            if (!IsWingetAvailable())
            {
                progress.Report(new SetupStage { Percent = 0, Text = "NO WINGET" });
                return false;
            }

            progress.Report(new SetupStage { Percent = 10, Text = "CHECKING" });
            StatusChanged?.Invoke("CHECKING FXSOUND");

            string arguments =
                "upgrade --id " + WingetId
                + " -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

            (int exitCode, string log) = await RunWingetAsync(arguments, TimeSpan.FromMinutes(15), token).ConfigureAwait(false);
            TraceLog.Write("WINGET UPGRADE " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + log.Trim());

            progress.Report(new SetupStage { Percent = 90, Text = "FINISHING" });
            AdoptInstalledPath(audio);

            bool ok = IsInstalled(audio);
            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                StartEngine(audio);
            }

            return ok;
        }
        catch (Exception ex)
        {
            TraceLog.Write("UPGRADE", ex);
            progress.Report(new SetupStage { Percent = 0, Text = "RETRY" });
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunWingetAsync(
        string arguments,
        TimeSpan timeout,
        CancellationToken token)
    {
        ProcessStartInfo info = new()
        {
            FileName = WingetPath(),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process? process = Process.Start(info);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            return (-1, string.Empty);
        }

        return (process.ExitCode, output.Result + Environment.NewLine + error.Result);
    }

    /// <summary>
    /// winget show prints the release number on a line of its own just above the
    /// installer block. Taking the last line that is nothing but digits and dots
    /// avoids depending on the output being in English.
    /// </summary>
    private static string ParsePublishedVersion(string output)
    {
        string found = string.Empty;
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^\d+(\.\d+)+$"))
            {
                found = trimmed;
            }
        }

        return found;
    }

    private static string InstalledVersion()
    {
        string[] candidates =
        {
            @"C:\Program Files\FxSound LLC\FxSound\FxSound.exe",
            @"C:\Program Files (x86)\FxSound LLC\FxSound\FxSound.exe"
        };

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string version = (info.ProductVersion ?? string.Empty).Split(' ')[0].Trim();
                if (version.Length > 0)
                {
                    return version;
                }
            }
            catch (Exception ex)
            {
                TraceLog.Write("FX VERSION", ex);
            }
        }

        return string.Empty;
    }

    /// <summary>-1 older, 0 same, 1 newer. Missing components count as zero.</summary>
    private static int Compare(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int count = Math.Max(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int x = i < a.Length && int.TryParse(a[i], out int p) ? p : 0;
            int y = i < b.Length && int.TryParse(b[i], out int q) ? q : 0;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }

    private static void AdoptInstalledPath(AudioService audio)
    {
        foreach (string path in audio.KnownPaths())
        {
            if (File.Exists(path))
            {
                audio.ExePath = path;
                return;
            }
        }
    }

    public bool IsInstalled(AudioService audio)
    {
        if (audio.IsInstalled)
        {
            return true;
        }

        AdoptInstalledPath(audio);
        return audio.IsInstalled;
    }

    public async Task<bool> DownloadAndInstallAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        try
        {
            if (IsInstalled(audio))
            {
                progress.Report(new SetupStage { Percent = 100, Text = "READY" });
                StartEngine(audio);
                return true;
            }

            progress.Report(new SetupStage { Percent = 0, Text = "DOWNLOAD" });
            StatusChanged?.Invoke("GETTING FXSOUND");

            using HttpClient client = new();
            client.Timeout = TimeSpan.FromMinutes(10);
            using HttpRequestMessage request = new(HttpMethod.Get, DownloadUrl);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using FileStream target = new(InstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920];
            long read = 0;
            int taken;
            while ((taken = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, taken), token).ConfigureAwait(false);
                read += taken;
                int percent = total.HasValue && total.Value > 0 ? (int)Math.Clamp(read * 100 / total.Value, 0, 100) : 0;
                progress.Report(new SetupStage { Percent = percent, Text = "DOWNLOAD " + percent + "%" });
            }

            await target.FlushAsync(token).ConfigureAwait(false);
            progress.Report(new SetupStage { Percent = 100, Text = "INSTALLING" });
            StatusChanged?.Invoke("INSTALLING FXSOUND");

            await RunInstallerAsync(token).ConfigureAwait(false);

            AdoptInstalledPath(audio);

            bool ok = audio.IsInstalled;
            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                StartEngine(audio);
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new SetupStage { Percent = 0, Text = "STOPPED" });
            return false;
        }
        catch (Exception ex)
        {
            progress.Report(new SetupStage { Percent = 0, Text = "FAILED" });
            StatusChanged?.Invoke("INSTALL FAILED");
            Debug.WriteLine(ex.Message);
            return false;
        }
    }

    private static async Task RunInstallerAsync(CancellationToken token)
    {
        ProcessStartInfo info = new()
        {
            FileName = InstallerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? process = Process.Start(info);
        if (process is null)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    public static void StartEngine(AudioService audio)
    {
        if (audio.IsInstalled)
        {
            audio.StartEngine();
        }
    }
}
