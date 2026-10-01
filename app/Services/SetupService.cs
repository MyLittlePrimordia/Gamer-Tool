using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace GamerTool.Services;

public sealed class SetupStage
{
    public int Percent { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// True when the percentage is a placeholder rather than a measurement.
    /// <para>
    /// winget reports nothing until it is finished, so a bar wired to Percent
    /// would sit at 5% for the whole install and look more broken than no bar at
    /// all. The direct download does know its percentage, so the same bar is a
    /// real readout there.
    /// </para>
    /// </summary>
    public bool Indeterminate { get; set; }
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

    /// <summary>Who the downloaded installer has to be signed by.</summary>
    public const string PublisherHint = "FxSound";

    /// <summary>
    /// Where the downloaded installer is put while it is fetched and run.
    /// <para>
    /// One name per run of the app rather than a fixed one. A fixed name in a
    /// shared folder is a name any other process can predict and get to first, and
    /// a file left locked by a previous run that was killed part way through an
    /// install would stop this run opening it at all.
    /// </para>
    /// <para>
    /// It has to be worked out once and held, not recomputed per access: the
    /// download writes to this path and the installer is later started from it,
    /// and those two have to be the same file. A property returning a fresh name
    /// each time would hand the installer a path that does not exist.
    /// </para>
    /// </summary>
    public static readonly string InstallerPath = Path.Combine(
        Path.GetTempPath(),
        "GamerTool-fxsound-" + Guid.NewGuid().ToString("N")[..8] + ".exe");

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

    /// <summary>
    /// Installs FxSound, preferring winget, and never escalates past a method that
    /// was actually tried and failed.
    /// <para>
    /// The two cases are deliberately not treated the same. If winget is not on the
    /// machine there is no alternative, so the direct download is the only way to do
    /// what was asked and it goes ahead. If winget is there and failed, that is
    /// information: it almost always means the network, the package source or winget
    /// itself, and quietly retrying with a different installer that has no hash
    /// check against it would hide that behind a second, more invasive action the
    /// user never agreed to. So the failure is passed back and the direct route is
    /// offered as a choice instead of taken on their behalf.
    /// </para>
    /// </summary>
    public async Task<bool> InstallBestAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        if (IsInstalled(audio))
        {
            progress.Report(new SetupStage { Percent = 100, Text = "READY" });
            StartEngine(audio);
            return true;
        }

        if (!IsWingetAvailable())
        {
            TraceLog.Write("INSTALL winget is not on this machine, using the direct download");
            progress.Report(new SetupStage { Percent = 0, Text = "NO WINGET" });
            return await DownloadAndInstallAsync(audio, progress, token).ConfigureAwait(false);
        }

        bool ok = await InstallWithWingetAsync(audio, progress, token).ConfigureAwait(false);
        if (!ok)
        {
            TraceLog.Write("INSTALL winget did not complete, not falling through to the direct download");
        }

        return ok;
    }

    public async Task<bool> InstallWithWingetAsync(AudioService audio, IProgress<SetupStage> progress, CancellationToken token)
    {
        try
        {
            progress.Report(new SetupStage { Percent = 5, Text = "WINGET", Indeterminate = true });
            StatusChanged?.Invoke("INSTALLING FXSOUND");

            string arguments =
                "install --id " + WingetId
                + " -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

            (int exitCode, string log) = await RunWingetAsync(
                arguments,
                TimeSpan.FromMinutes(15),
                token,
                stage => progress.Report(stage)).ConfigureAwait(false);
            TraceLog.Write("WINGET " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + log.Trim());

            progress.Report(new SetupStage { Percent = 90, Text = "FINISHING" });
            AdoptInstalledPath(audio);

            bool ok = IsInstalled(audio);
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                // Ahead of the ready report, not behind it. Starting the engine
                // waits on a process that is not supposed to exit, so it costs the
                // full timeout, and reporting ready first left the bar sitting at
                // full beside a counter still climbing while nothing was happening.
                StartEngine(audio);
            }

            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });

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
                // No badge without winget, and that is deliberate rather than a
                // gap still to be filled.
                //
                // The obvious fallback is to ask the download host what the newest
                // build is, so it is worth recording that this was tried and why
                // it cannot work. download.fxsound.com/fxsoundlatest redirects to
                // a file named fxsound_setup.exe on a branch literally called
                // "latest". There is no version in the path, no Content-Disposition
                // to carry one, and the ETag is a SHA-256 of the bytes. The
                // current version is genuinely not published as anything a client
                // can read.
                //
                // Guessing at it - parsing the installer, or assuming the year is
                // in there - would either be wrong or would mean running the file
                // to find out. So a machine with no winget gets no update badge,
                // and still gets the update button, which takes the direct
                // download. A missing badge is a much smaller problem than an
                // update prompt that lies about there being an update.
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
                // Winget is the preferred route and the one with a hash check
                // against it, but a machine without it has no other way to update,
                // and "NO WINGET" on an upgrade button is not an answer. The
                // direct download verifies the Authenticode publisher before
                // running anything, which is a weaker check than winget's hash
                // but is a real one - and on this machine it is the only check
                // there is.
                TraceLog.Write("UPGRADE winget is not on this machine, using the direct download");
                StatusChanged?.Invoke("GETTING FXSOUND");
                return await DownloadAndInstallAsync(audio, progress, token, force: true).ConfigureAwait(false);
            }

            progress.Report(new SetupStage { Percent = 10, Text = "CHECKING", Indeterminate = true });
            StatusChanged?.Invoke("CHECKING FXSOUND");

            string arguments =
                "upgrade --id " + WingetId
                + " -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

            (int exitCode, string log) = await RunWingetAsync(
                arguments,
                TimeSpan.FromMinutes(15),
                token,
                stage => progress.Report(stage)).ConfigureAwait(false);
            TraceLog.Write("WINGET UPGRADE " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + log.Trim());

            progress.Report(new SetupStage { Percent = 90, Text = "FINISHING" });
            AdoptInstalledPath(audio);

            bool ok = IsInstalled(audio);
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                // Ahead of the ready report, not behind it. Starting the engine
                // waits on a process that is not supposed to exit, so it costs the
                // full timeout, and reporting ready first left the bar sitting at
                // full beside a counter still climbing while nothing was happening.
                StartEngine(audio);
            }

            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });

            return ok;
        }
        catch (Exception ex)
        {
            TraceLog.Write("UPGRADE", ex);
            progress.Report(new SetupStage { Percent = 0, Text = "RETRY" });
            return false;
        }
    }

    /// <summary>
    /// Runs winget and hands back its exit code and everything it printed.
    /// <para>
    /// The output is read as it arrives rather than at the end, purely so the
    /// milestones winget does print can be shown while it is still working. The
    /// full text is still collected for the log. A line that matches nothing is
    /// simply not reported, so if winget ever changes its wording the install
    /// carries on exactly as before with the generic message instead.
    /// </para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunWingetAsync(
        string arguments,
        TimeSpan timeout,
        CancellationToken token,
        Action<SetupStage>? onStage = null)
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

        // Both streams are pumped from a thread pool thread and either can land
        // here at the same time, so the collector is locked rather than trusted.
        StringBuilder log = new();
        void Capture(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                // Null is the end of the stream, not a line.
                return;
            }

            lock (log)
            {
                log.AppendLine(e.Data);
            }

            SetupStage? stage = DescribeWingetLine(e.Data);
            if (stage is not null)
            {
                onStage?.Invoke(stage);
            }
        }

        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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

        try
        {
            // Waiting for exit does not wait for the last of the redirected text
            // to be handed over. Without this the closing lines, which are the
            // ones that say it worked, can be missing from the log.
            process.WaitForExit(2000);
        }
        catch (Exception)
        {
            // The process is gone, so there is nothing left to wait for.
        }

        lock (log)
        {
            return (process.ExitCode, log.ToString());
        }
    }


    /// <summary>
    /// Turns one line of winget's own output into a stage to show, or nothing at
    /// all when the line is not one of the milestones it actually prints.
    /// <para>
    /// winget emits no byte counts and no percentages when its output is
    /// redirected, so there is no rate to show here. What it does print is a
    /// handful of genuine milestones, and naming the one in flight is more use to
    /// a waiting user than a bar that cannot honestly move. The percentages are
    /// only there to place the sweep bar, never to claim a measurement.
    /// </para>
    /// </summary>
    private static SetupStage? DescribeWingetLine(string line)
    {
        // Most specific first, so a line that contains more than one phrase is
        // reported as the later thing it achieved.
        if (line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupStage { Percent = 100, Text = "Installed" };
        }

        if (line.Contains("verified installer hash", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupStage { Percent = 60, Text = "Downloaded, hash verified", Indeterminate = true };
        }

        if (line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupStage { Percent = 65, Text = "Running the FxSound installer", Indeterminate = true };
        }

        if (line.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase))
        {
            int cut = line.LastIndexOfAny(new[] { '/', '\\' });
            string file = cut >= 0 && cut < line.Length - 1
                ? line[(cut + 1)..].Trim()
                : "FxSound";

            return new SetupStage { Percent = 20, Text = "Downloading " + file, Indeterminate = true };
        }

        return null;
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

    /// <summary>
    /// Installs FxSound, or upgrades it.
    /// </summary>
    /// <param name="force">
    /// Skip the "already installed, nothing to do" shortcut and run the installer
    /// anyway. This exists because the shortcut made the direct download unable
    /// to do the one job it was the only route for on a machine with no winget:
    /// an existing install returned READY before a byte was fetched, so the
    /// "update FxSound" button on such a machine reported success while doing
    /// nothing at all.
    /// </param>
    public async Task<bool> DownloadAndInstallAsync(
        AudioService audio,
        IProgress<SetupStage> progress,
        CancellationToken token,
        bool force = false)
    {
        try
        {
            if (!force && IsInstalled(audio))
            {
                progress.Report(new SetupStage { Percent = 100, Text = "READY" });
                StartEngine(audio);
                return true;
            }

            progress.Report(new SetupStage { Percent = 0, Text = "DOWNLOAD", Indeterminate = true });
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
            progress.Report(new SetupStage { Percent = 90, Text = "INSTALLING", Indeterminate = true });
            StatusChanged?.Invoke("INSTALLING FXSOUND");

            if (!await RunInstallerAsync(token).ConfigureAwait(false))
            {
                return false;
            }

            AdoptInstalledPath(audio);

            bool ok = audio.IsInstalled;
            StatusChanged?.Invoke(ok ? "FXSOUND READY" : "FXSOUND MISSING");
            if (ok)
            {
                // Ahead of the ready report, for the same reason as the winget path.
                StartEngine(audio);
            }

            progress.Report(new SetupStage { Percent = 100, Text = ok ? "READY" : "TRY AGAIN" });

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
        finally
        {
            // Runs after the download stream has been closed, and on the early
            // returns too, where there is no file and Delete does nothing.
            TryDeleteInstaller();
        }
    }


    /// <summary>
    /// Best effort removal of the downloaded installer. It will still be locked if
    /// the wait for it was cancelled and the process is running, in which case
    /// this leaves it alone, which is the right answer: the installer copies what
    /// it needs before it exits and Windows clears the temp folder anyway.
    /// </summary>
    private static void TryDeleteInstaller()
    {
        try
        {
            File.Delete(InstallerPath);
        }
        catch (Exception ex)
        {
            TraceLog.Write("INSTALLER CLEANUP", ex);
        }
    }

    /// <summary>
    /// Checks the downloaded installer really is FxSound's before anything runs
    /// it.
    /// <para>
    /// The transport is HTTPS with the normal chain validated, so this is not
    /// about a stranger on the wire. It is about the origin: a compromised
    /// download host, a compromised CDN, or anything sitting in front of the
    /// connection with a certificate the machine already trusts would otherwise
    /// get code execution as this user, silently, because the install is
    /// <c>/VERYSILENT</c> and shows nothing at all. The app already treats the
    /// absence of a hash check as a reason to prefer winget, so the direct route
    /// is the one that most needs a check of its own.
    /// </para>
    /// <para>
    /// What is verified is that the file carries an embedded Authenticode
    /// signature and that FxSound signed it. What is not verified here is the
    /// whole chain up to a trusted root, so this is a check on the origin rather
    /// than a replacement for one. WinVerifyTrust, which does the full job, was
    /// tried first and rejected: on a machine that cannot reach a revocation
    /// endpoint it returns a failure for files that are perfectly well signed,
    /// and treating that as a refusal would have rejected every legitimate
    /// download. The chain is already covered by the HTTPS fetch, and the
    /// publisher name is what stops a valid signature from anyone else passing.
    /// </para>
    /// </summary>
    public static bool IsSignedByPublisher(string path, string publisher)
    {
        X509Certificate2? signer = ReadSigner(path);
        if (signer is null)
        {
            TraceLog.Write("INSTALLER the download carries no readable signature");
            return false;
        }

        using (signer)
        {
            string name = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            if (!name.Contains(publisher, StringComparison.OrdinalIgnoreCase))
            {
                TraceLog.Write("INSTALLER signed by " + name + ", not " + publisher);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// True when the file carries an embedded Authenticode signature at all.
    /// A file with none is refused before anything else is even looked at.
    /// </summary>
    public static bool HasEmbeddedSignature(string path) => ReadSigner(path) is not null;

    /// <summary>
    /// The certificate that signed an executable, or null when there is not one.
    /// <para>
    /// Reading the signer out of a signed file is the one thing the certificate
    /// loader has no supported replacement for, and the deprecation it warns
    /// about is importing into a store without controlling the key's lifetime.
    /// That is not what happens here: the certificate is read, handed to the
    /// caller to dispose, and nothing is kept or persisted.
    /// </para>
    /// </summary>
    private static X509Certificate2? ReadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            X509Certificate raw = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057

            // CreateFromSignedFile hands back a plain X509Certificate wrapping the
            // encoded public key. It is NOT an X509Certificate2, so casting it
            // throws InvalidCastException, the catch below swallows that, and every
            // signed file then reads as unsigned. The practical effect was that the
            // direct install could never run: the download succeeded, the check
            // said "not signed by FxSound", and there was no way past it except
            // winget or installing by hand. The unit tests missed it because they
            // only ever asserted the unsigned answer, which a broken reader also
            // gets right.
            //
            // Loading the same bytes properly is the supported path and carries no
            // deprecation. Nothing is persisted and the caller disposes it.
            return X509CertificateLoader.LoadCertificate(raw.GetRawCertData());
        }
        catch (CryptographicException)
        {
            // The ordinary answer for a file with no signature in it.
            return null;
        }
        catch (Exception ex)
        {
            TraceLog.Write("INSTALLER signature probe: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<bool> RunInstallerAsync(CancellationToken token)
    {
        if (!IsSignedByPublisher(InstallerPath, PublisherHint))
        {
            // Its own stage, because "the download is not from FxSound" and "the
            // network went away" call for completely different reactions and
            // lumping them together as a generic failure is what made this worth
            // reporting properly in the first place.
            TraceLog.Write("INSTALLER refused, not signed by " + PublisherHint);
            return false;
        }

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
            return false;
        }

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    public static void StartEngine(AudioService audio)
    {
        if (audio.IsInstalled)
        {
            audio.StartEngine();
        }
    }
}

