using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The gate in front of running a downloaded installer.
/// <para>
/// The refusal cases matter, but on their own they are not enough. A reader that
/// simply always returns "unsigned" passes every refusal test and refuses every
/// real download, which is what happened: the certificate cast in the reader threw
/// for every input, the throw was swallowed, and the direct install could never
/// run. The positive case below signs a real executable so that failure mode has
/// something to fail against.
/// </para>
/// </summary>
public class InstallerTrustTests
{
        private const string TestPublisher = "GamerTool Test Publisher";

        /// <summary>Password for the throwaway pfx. Never anything real.</summary>
        private const string Password = "tests";


    private static string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(path, "MZ this is not really a program");
        return path;
    }

    [Fact]
    public void An_unsigned_file_is_not_signed()
    {
        string path = TempFile();

        try
        {
            Assert.False(SetupService.HasEmbeddedSignature(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_is_not_signed()
    {
        Assert.False(SetupService.HasEmbeddedSignature(
            Path.Combine(Path.GetTempPath(), "GamerToolTests-definitely-not-here.exe")));
    }

    [Fact]
    public void A_file_that_is_not_a_program_at_all_is_not_signed()
    {
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        try
        {
            Assert.False(SetupService.HasEmbeddedSignature(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unsigned_download_is_refused_before_it_can_run()
    {
        string path = TempFile();

        try
        {
            Assert.False(SetupService.IsSignedByPublisher(path, SetupService.PublisherHint));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_download_is_refused_rather_than_crashing()
    {
        Assert.False(SetupService.IsSignedByPublisher(
            Path.Combine(Path.GetTempPath(), "GamerToolTests-nope.exe"), SetupService.PublisherHint));
    }

    [Fact]
    public void A_real_signed_executable_is_recognised_as_signed()
    {
        // The positive case the other tests here could not make. A copy of a real
        // PE is signed with a throwaway code signing certificate, and the reader
        // has to come back with true. A reader that answers false to everything
        // fails this, which is the point: it is the exact shape of the bug where
        // the certificate cast threw for every input and the install could never
        // run.
        using SignedExecutable signed = SignedExecutable.Create(TestPublisher);

        Assert.True(
            SetupService.HasEmbeddedSignature(signed.FilePath),
            "a genuinely signed executable was reported as unsigned");
    }

    [Fact]
    public void A_signed_executable_is_accepted_for_its_own_publisher()
    {
        using SignedExecutable signed = SignedExecutable.Create(TestPublisher);

        Assert.True(
            SetupService.IsSignedByPublisher(signed.FilePath, TestPublisher),
            "a signature from the expected publisher was refused");
    }

    [Fact]
    public void A_signed_executable_is_refused_for_a_different_publisher()
    {
        // The other half of the gate. Accepting any valid signature would let
        // anything the network handed us run.
        using SignedExecutable signed = SignedExecutable.Create(TestPublisher);

        Assert.False(
            SetupService.IsSignedByPublisher(signed.FilePath, SetupService.PublisherHint));
    }

    [Fact]
    public void The_check_answers_about_a_real_signed_system_binary_without_throwing()
    {
        string? real = Directory
            .EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), "*.dll")
            .FirstOrDefault(File.Exists);

        Assert.NotNull(real);
        bool answer = SetupService.HasEmbeddedSignature(real!);
        Assert.Equal(answer, SetupService.HasEmbeddedSignature(real!));
    }

    /// <summary>
    /// A throwaway signed executable: a real PE with an Authenticode signature
    /// made by a certificate that exists only for the length of the test.
    /// <para>
    /// .NET has no managed API for signing, so the signing step shells out to
    /// Set-AuthenticodeSignature. The certificate is created in process, exported
    /// to a pfx, and removed from the user store on dispose, because leaving
    /// code signing certificates lying around is not a thing a test should do.
    /// </para>
    /// </summary>
    private sealed class SignedExecutable : IDisposable
    {
        private readonly string? _pfx;

        private SignedExecutable(string filePath, string? pfx)
        {
            FilePath = filePath;
            _pfx = pfx;
        }

        /// <summary>
        /// Named FilePath rather than Path, because a member called Path would
        /// shadow System.IO.Path for every use of it inside this class.
        /// </summary>
        public string FilePath { get; }

        public static SignedExecutable Create(string publisher)
        {
            using RSA key = RSA.Create(2048);

            var request = new CertificateRequest(
                "CN=" + publisher,
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Without this the certificate is not a code signing certificate and
            // Set-AuthenticodeSignature refuses to use it.
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.3") },
                    critical: false));

            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));

            string id = Guid.NewGuid().ToString("N")[..8];
            string pfx = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + id + ".pfx");
            string target = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + id + ".exe");

            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, Password));

            // Authenticode needs a real PE to attach itself to, so use one that
            // is definitely on a Windows box rather than synthesising a header.
            string source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "notepad.exe");

            if (!File.Exists(source))
            {
                source = Environment.ProcessPath ?? throw new InvalidOperationException("no PE to copy");
            }

            File.Copy(source, target, overwrite: true);

            if (!Sign(target, pfx))
            {
                File.Delete(target);
                File.Delete(pfx);
                throw new InvalidOperationException("could not sign the test executable");
            }

            return new SignedExecutable(target, pfx);
        }

        private static bool Sign(string target, string pfx)
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(
                "$ErrorActionPreference='Stop';"
                // The pfx is written with a password, so the import has to be given
                // one. Import-PfxCertificate without it fails, and the failure looks
                // like "the certificate is no good" rather than "you forgot the
                // password", which is an easy hour to lose.
                + "$sec=ConvertTo-SecureString '" + Password + "' -AsPlainText -Force;"
                + "$c=Import-PfxCertificate -FilePath '" + pfx + "' -CertStoreLocation Cert:\\CurrentUser\\My -Password $sec;"
                + "try{"
                + "$s=Set-AuthenticodeSignature -FilePath '" + target + "' -Certificate $c;"
                + "if($s.Status -ne 'Valid'){Write-Output ('STATUS='+$s.Status);exit 1}"
                + "}finally{Remove-Item -LiteralPath ('Cert:\\CurrentUser\\My\\'+$c.Thumbprint) -Force}");

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(60_000);
            return process.ExitCode == 0;
        }

        public void Dispose()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // A locked temp file is not worth failing a test over.
            }

            if (_pfx is not null)
            {
                try
                {
                    File.Delete(_pfx);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
