using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GamerTool.Services;

public sealed class SetupStage
{
    public int Percent { get; set; }

    public string Text { get; set; } = string.Empty;
}

public sealed class SetupService
{
    public const string DownloadUrl = "https://download.fxsound.com/fxsoundlatest";

    public const string WingetId = "FxSound.FxSound";

    public static string InstallerPath => Path.Combine(Path.GetTempPath(), "fxsound_setup.exe");

    public event Action<string>? StatusChanged;

    public bool IsWingetAvailable()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "winget.exe");
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
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

            string winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "winget.exe");

            ProcessStartInfo info = new()
            {
                FileName = winget,
                Arguments = "install --id " + WingetId + " -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            progress.Report(new SetupStage { Percent = 40, Text = "DOWNLOADING" });
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                progress.Report(new SetupStage { Percent = 0, Text = "STOPPED" });
                return false;
            }

            string log = output.Result + error.Result;
            TraceLog.Write("WINGET " + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + log.Trim());

            progress.Report(new SetupStage { Percent = 90, Text = "FINISHING" });
            foreach (string path in audio.KnownPaths())
            {
                if (File.Exists(path))
                {
                    audio.ExePath = path;
                    break;
                }
            }

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

    public bool IsInstalled(AudioService audio)
    {
        if (audio.IsInstalled)
        {
            return true;
        }

        foreach (string path in audio.KnownPaths())
        {
            if (File.Exists(path))
            {
                audio.ExePath = path;
                return true;
            }
        }

        return false;
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

            foreach (string path in audio.KnownPaths())
            {
                if (File.Exists(path))
                {
                    audio.ExePath = path;
                    break;
                }
            }

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
