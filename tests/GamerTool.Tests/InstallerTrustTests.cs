using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GamerTool.Services;
using Xunit;
using Skip = Xunit.Skip;

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


    private static string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(path, "MZ this is not really a program");
        return path;
    }

    /// <summary>
    /// Creates the throwaway signed binary, or reports that this machine has no
    /// way to sign.
    /// <para>
    /// Authenticode signing is a platform capability, not something a test can
    /// assume. It needs PowerShell's Microsoft.PowerShell.Security module, and on
    /// some Windows images that module will not load, so on those machines these
    /// three tests stand aside instead of reporting a defect in the reader that is
    /// not there. The positive case they exist to protect does not go with them:
    /// it is covered separately against a signature Microsoft already put on a
    /// real binary, which needs no signing at all.
    /// </para>
    /// </summary>
    private static SignedExecutable CreateSignedExecutableOrSkip(string publisher)
    {
        try
        {
            return SignedExecutable.Create(publisher);
        }
        catch (SignedExecutable.SkipBecauseHostCannotSign ex)
        {
            // Written to stderr as well as skipped, because a test that quietly
            // does nothing is worse than one that fails. This lands in the CI log
            // next to the test results.
            Console.Error.WriteLine(
                "[skipped] " + nameof(InstallerTrustTests) + ": the host cannot produce an"
                + " Authenticode signature, so the generated-certificate assertions did not run."
                + Environment.NewLine + "          " + ex.Message);

            Skip.If(true, ex.Message);

            // Skip.If always throws, so this is unreachable. It is here because the
            // compiler cannot know that, and a null return would be worse.
            throw;
        }
    }


    /// <summary>
    /// The exact text the GitHub runner produced when this fixture could not sign,
    /// pasted verbatim. It is the whole reason the helper distinguishes a machine
    /// that cannot sign from a signature that was refused, so the recognition is
    /// pinned against it rather than against a paraphrase that could drift.
    /// </summary>
    private const string RunnerModuleFailure =
        "ConvertTo-SecureString : The 'ConvertTo-SecureString' command was found in the module"
        + " 'Microsoft.PowerShell.Security', but the module could not be loaded. For more information, run"
        + " 'Import-Module Microsoft.PowerShell.Security'.\r\nAt line:1 char:36\r\n+ $ErrorActionPreference='Stop';$sec=ConvertTo-SecureString 'tests' -As ...\r\n"
        + "                                    ~~~~~~~~~~~~~~~~~~~~~~\r\n    + CategoryInfo          : ObjectNotFound: (ConvertTo-SecureString:String) [], CommaSeparatedArgumentNotFoundException\r\n"
        + "    + FullyQualifiedErrorId : CouldNotAutoloadMatchingModule";

    [Fact]
    public void A_module_that_will_not_load_is_recognised_as_such()
    {
        Assert.True(SignedExecutable.LooksLikeModuleUnavailable(RunnerModuleFailure));
    }

    [Fact]
    public void A_refused_signature_is_not_mistaken_for_a_missing_module()
    {
        // The distinction that decides whether a test fails or stands aside. An
        // ordinary refusal must keep failing the build.
        const string refused =
            "exit code 1 from C:\\temp\\x.exe\r\n"
            + "Status=UnknownError\r\n"
            + "StatusMessage=A certificate chain processed, but terminated in a root certificate"
            + " which is not trusted by the trust provider\r\n"
            + "SignatureType=Authenticode\r\n"
            + "SignerSubject=CN=GamerTool Test Publisher";

        Assert.False(SignedExecutable.LooksLikeModuleUnavailable(refused));
    }

    [Fact]
    public void A_signer_is_recognised_as_signed_even_though_its_chain_is_not_trusted()
    {
        // The other half of the same confusion: an untrusted chain is a perfectly
        // good signature for our purposes and must not be read as a failure to
        // sign. This is the string the first version of this test produced on CI.
        const string untrustedButSigned =
            "exit code 1 from C:\\temp\\x.exe\r\n"
            + "Status=UnknownError\r\n"
            + "StatusMessage=A certificate chain processed, but terminated in a root certificate"
            + " which is not trusted by the trust provider\r\n"
            + "SignatureType=Authenticode\r\n"
            + "SignerSubject=CN=GamerTool Test Publisher";

        Assert.False(SignedExecutable.LooksLikeModuleUnavailable(untrustedButSigned));
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

    [SkippableFact]
    public void A_real_signed_executable_is_recognised_as_signed()
    {
        // The positive case the other tests here could not make. A copy of a real
        // PE is signed with a throwaway code signing certificate, and the reader
        // has to come back with true. A reader that answers false to everything
        // fails this, which is the point: it is the exact shape of the bug where
        // the certificate cast threw for every input and the install could never
        // run.
        using SignedExecutable signed = CreateSignedExecutableOrSkip(TestPublisher);

        Assert.True(
            SetupService.HasEmbeddedSignature(signed.FilePath),
            "a genuinely signed executable was reported as unsigned");
    }

    [SkippableFact]
    public void A_signed_executable_is_accepted_for_its_own_publisher()
    {
        using SignedExecutable signed = CreateSignedExecutableOrSkip(TestPublisher);

        Assert.True(
            SetupService.IsSignedByPublisher(signed.FilePath, TestPublisher),
            "a signature from the expected publisher was refused");
    }

    [SkippableFact]
    public void A_signed_executable_is_refused_for_a_different_publisher()
    {
        // The other half of the gate. Accepting any valid signature would let
        // anything the network handed us run.
        using SignedExecutable signed = CreateSignedExecutableOrSkip(TestPublisher);

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
    /// Files on every Windows box that carry a real EMBEDDED Authenticode
    /// signature, so the positive test has something to read without having to
    /// sign anything.
    /// <para>
    /// Embedded matters. A catalog signed file, which is what most binaries in
    /// System32 are, has no signature in the file at all: the signature lives in
    /// a driver catalog, so reading the file with CreateFromSignedFile finds
    /// nothing and throws. That is not a bug, it is how those files work, and it
    /// is the reason notepad.exe is no use here even though it is signed.
    /// </para>
    /// </summary>
    private static IEnumerable<string> KnownSignedBinaries()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // The .NET Framework runtime library. Signed by Microsoft, and present on
        // every machine that has ever run PowerShell.
        yield return Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319", "System.dll");

        // The .NET host, which is embedded signed rather than catalog signed. Only
        // present where .NET is installed, which is everywhere this test runs.
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")))
        {
            yield return Path.Combine(dotnetRoot, "dotnet.exe");
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
    }

    /// <summary>
    /// The first known binary that really does carry an embedded signature, or
    /// null if this machine has none. Checked rather than assumed, because a file
    /// losing its signature across a Windows update would otherwise read as the
    /// code under test being broken.
    /// </summary>
    private static string? FindEmbeddedSignedBinary()
    {
        foreach (string candidate in KnownSignedBinaries())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                // The same call SetupService makes, suppressed the same way and
                // for the same reason. Here it is not a workaround, it is the
                // point: the test is asking whether a real file with a real
                // embedded signature reads back through this exact call.
#pragma warning disable SYSLIB0057
                using X509Certificate raw = X509Certificate.CreateFromSignedFile(candidate);
#pragma warning restore SYSLIB0057
                if (raw.GetRawCertData().Length > 0)
                {
                    return candidate;
                }
            }
            catch (CryptographicException)
            {
                // No embedded signature. Expected for catalog signed files.
            }
        }

        return null;
    }

    [Fact]
    public void A_real_embedded_signature_is_read_back_without_being_signed_first()
    {
        // The positive case, and the one that matters, with nothing signed and
        // nothing mocked.
        //
        // This is the test that fails if the reader regresses to the bug it was
        // written around: CreateFromSignedFile hands back a plain X509Certificate
        // that is NOT an X509Certificate2, so casting it throws, the throw is
        // swallowed, and every signed file reads as unsigned. Signing our own
        // binary to prove that needs a working PowerShell, which is not something
        // every machine has, so this one reads a signature Microsoft already put
        // there. It cannot be faked and it cannot be skipped by a missing cmdlet.
        string? signed = FindEmbeddedSignedBinary();

        Assert.NotNull(signed);
        Assert.True(
            SetupService.HasEmbeddedSignature(signed!),
            "a genuinely signed system binary was reported as unsigned: " + signed);
    }

    [Fact]
    public void A_real_embedded_signature_is_refused_for_a_different_publisher()
    {
        // The refusal half against a real signature, so it does not depend on a
        // generated certificate either.
        string? signed = FindEmbeddedSignedBinary();

        Assert.NotNull(signed);
        Assert.False(
            SetupService.IsSignedByPublisher(signed!, SetupService.PublisherHint),
            "a Microsoft signature was accepted as FxSound's");
    }

    [Fact]
    public void A_real_embedded_signature_is_accepted_for_its_own_publisher()
    {
        // The acceptance half, also without signing anything: the publisher asked
        // for is lifted out of the file's own certificate, so this passes only if
        // the reader really returned that certificate rather than swallowing an
        // error. Asking for the publisher the file was actually signed by is the
        // whole assertion.
        string? signed = FindEmbeddedSignedBinary();

        Assert.NotNull(signed);

#pragma warning disable SYSLIB0057
        using X509Certificate raw = X509Certificate.CreateFromSignedFile(signed!);
#pragma warning restore SYSLIB0057

        // Loaded the way the app loads it, which is the point: if this is what
        // SetupService ends up comparing against, then asking with the publisher
        // out of the same certificate only passes if the reader returned it.
        using X509Certificate2 loaded = X509CertificateLoader.LoadCertificate(raw.GetRawCertData());
        string subject = loaded.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        Assert.False(string.IsNullOrWhiteSpace(subject));

        Assert.True(
            SetupService.IsSignedByPublisher(signed!, subject),
            "a signature from the publisher it names was refused: " + subject);
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

            // Exported with an EMPTY password on purpose, because the pfx is handed
            // to Set-AuthenticodeSignature as a path and a path with a password on
            // it makes the cmdlet prompt for one. Under -NonInteractive that is a
            // hang or a failure, and a throwaway test certificate in %TEMP% is not
            // protecting anything. This is also what removes the need for
            // ConvertTo-SecureString, which is a whole class of failure on its own.
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, string.Empty));

            List<string> failures = new();
            bool everyAttemptHitTheMissingModule = true;

            foreach (string source in PeSources())
            {
                File.Copy(source, target, overwrite: true);

                SignOutcome outcome = Sign(target, pfx, publisher);

                if (outcome.Signed)
                {
                    return new SignedExecutable(target, pfx);
                }

                // Every attempt is kept. Overwriting this with the last one is what
                // made a CI failure report only the final fallback's error, when the
                // interesting one was the first.
                failures.Add(source + ": " + outcome.Why);
                everyAttemptHitTheMissingModule &= outcome.ModuleUnavailable;

                // If the machine cannot sign at all, retrying other binaries is
                // pointless: they all go through the same missing module.
                if (outcome.ModuleUnavailable)
                {
                    break;
                }
            }

            File.Delete(target);
            File.Delete(pfx);

            // A platform that cannot load the PowerShell security module cannot
            // sign anything, and that is a fact about the machine rather than a
            // failure of the code under test. It is reported as its own thing so it
            // is never confused with a real regression, and so the log says why the
            // generated-certificate coverage is missing.
            if (failures.Count > 0 && everyAttemptHitTheMissingModule)
            {
                throw new SkipBecauseHostCannotSign(
                    "This machine cannot sign: " + failures[0]
                    + ". The positive case is still covered by the tests that read a"
                    + " signature Microsoft already put on a real binary.");
            }


            throw new InvalidOperationException(
                "could not sign the test executable: " + string.Join(" | ", failures));
        }

        /// <summary>
        /// Tells "this machine cannot sign anything" apart from "the signature was
        /// refused", because the two need opposite handling.
        /// <para>
        /// Matched on the shape of the message rather than on one identifier.
        /// PowerShell words the autoload failure as "The 'Set-AuthenticodeSignature'
        /// command was found in the module 'Microsoft.PowerShell.Security', but the
        /// module could not be loaded", and an explicit Import-Module that cannot
        /// find the manifest says "no valid module file was found" instead. The
        /// FullyQualifiedErrorId has already been seen to differ between cmdlet and
        /// host, so it is matched too but never relied on alone.
        /// </para>
        /// <para>
        /// The module name is required in every case, which is what stops an
        /// ordinary refusal from being classified this way: a genuine signing
        /// failure names the file and the cmdlet, not the module.
        /// </para>
        /// </summary>
        internal static bool LooksLikeModuleUnavailable(string text) =>
            text.Contains("Microsoft.PowerShell.Security", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("could not be loaded", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no valid module file was found", StringComparison.OrdinalIgnoreCase)
                || text.Contains(ModuleUnavailableMarker, StringComparison.Ordinal));

        /// <summary>
        /// A marker that appears in the PowerShell output when the security module
        /// itself will not load, which is a different problem from a monitor, a
        /// certificate or a signature being refused.
        /// </summary>
        internal const string ModuleUnavailableMarker = "CouldNotAutoloadMatchingModule";


        /// <summary>
        /// Thrown when nothing on this machine can produce an Authenticode
        /// signature, so that the generated-certificate tests step aside instead of
        /// reporting a defect in the reader that is not there.
        /// </summary>
        internal sealed class SkipBecauseHostCannotSign : Exception
        {
            public SkipBecauseHostCannotSign(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Portable executables that can stand in for the real installer, best
        /// first.
        /// <para>
        /// The choice matters much more than it looks. Windows treats a file
        /// that has a driver catalog signature as an OS binary, and for one of
        /// those Set-AuthenticodeSignature reports the catalog entry rather than
        /// anything it just wrote: Status comes back Valid and SignerCertificate
        /// reads "CN=Microsoft Windows", because that is who signed notepad.exe,
        /// not us. Signing a copy of notepad.exe therefore went green while
        /// proving nothing at all about the signature the test had just made,
        /// and on a build image where notepad.exe is missing or a Store stub the
        /// same code signed an ordinary PE instead and got a real signature,
        /// which then failed the Valid check below. So: never start from a
        /// system binary, and reject a Catalog result outright rather than
        /// reading it as success.
        /// </para>
        /// </summary>
        private static IEnumerable<string> PeSources()
        {
            // The test host, a plain .NET application, so never an OS binary.
            if (Environment.ProcessPath is { Length: > 0 } self && File.Exists(self))
            {
                yield return self;
            }

            // Kept as fallbacks for the case where the test host turns out to be
            // catalog signed too. dotnet.exe first because it is signed rather
            // than stubbed on every image; the loop tolerates either failing.
            foreach (string name in new[] { "dotnet.exe", "notepad.exe" })
            {
                string candidate = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32",
                    name);

                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        /// Signs <paramref name="target"/> with the throwaway certificate, or
        /// reports why it could not.
        /// <para>
        /// One cmdlet, and that is the point. This used to run three:
        /// ConvertTo-SecureString to build a password, Import-PfxCertificate to
        /// put the certificate into the user's own store, and only then
        /// Set-AuthenticodeSignature. Every one of those is a way to fail that has
        /// nothing to do with signing. On a GitHub runner the first of them fails
        /// outright, because the Microsoft.PowerShell.Security module will not
        /// autoload, and the build goes red on a machine that is perfectly capable
        /// of signing.
        /// <para>
        /// Set-AuthenticodeSignature takes the pfx path directly, so the other two
        /// are not needed, and the user's certificate store is never touched, which
        /// also removes the cleanup step and the risk of leaving a code signing
        /// certificate lying around. The module is still imported by full path
        /// first, because autoloading is what fails and an explicit load does not
        /// care.
        /// </para>
        /// <para>
        /// Note what is deliberately NOT asserted: Status -eq 'Valid'. Status
        /// reports whether the chain builds up to a trusted root, and a
        /// certificate that exists only for the length of a test never can.
        /// Demanding Valid is what made this fail on CI, while passing locally
        /// purely because the catalog signature described in
        /// <see cref="PeSources"/> was being mistaken for ours. What has to be
        /// true is that an Authenticode signature was written and that it carries
        /// our publisher, because that is the only thing SetupService reads. The
        /// app does not validate the chain either, for reasons given on
        /// IsSignedByPublisher.
        /// </para>
        /// </summary>
        private static SignOutcome Sign(string target, string pfx, string publisher)
        {
            string moduleManifest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "Modules",
                "Microsoft.PowerShell.Security",
                "Microsoft.PowerShell.Security.psd1");

            string signing =
                  "$s=Set-AuthenticodeSignature -FilePath '" + target + "' -Certificate '" + pfx + "';"
                // Echoed rather than tested here, so the reason an attempt failed
                // reaches the test output instead of being flattened into one
                // opaque message. This was the reason the CI failure was not
                // diagnosable from the log at all.
                + "Write-Output ('Status='+$s.Status);"
                + "Write-Output ('StatusMessage='+$s.StatusMessage);"
                + "Write-Output ('SignatureType='+$s.SignatureType);"
                + "Write-Output ('SignerSubject='+$s.SignerCertificate.Subject);"
                // Windows answering about a catalog entry is not a signature we
                // made, so tell the caller to move on to the next candidate.
                + "if($s.SignatureType -ne 'Authenticode'){exit 2}"
                + "if($s.SignerCertificate.Subject -notlike '*" + publisher + "*'){exit 3}";

            // Best effort first: the module is nearly always already available and
            // the import costs nothing. If the manifest is not where it is
            // expected the import is simply skipped rather than fatal.
            string script =
                  "$ErrorActionPreference='Stop';"
                + "Import-Module '" + moduleManifest + "' -ErrorAction SilentlyContinue;"
                + signing;

            // Second attempt, explicitly: this is the one that matters when
            // autoloading is what is broken, which is the case this whole
            // arrangement exists for.
            string scriptWithRequiredImport =
                  "$ErrorActionPreference='Stop';"
                + "Import-Module '" + moduleManifest + "' -ErrorAction Stop;"
                + signing;

            SignOutcome first = RunPowerShell(script, target);
            if (first.Signed || !first.ModuleUnavailable)
            {
                return first;
            }

            SignOutcome second = RunPowerShell(scriptWithRequiredImport, target);
            if (second.Signed)
            {
                return second;
            }

            // The real question is not what the error says, it is whether
            // Set-AuthenticodeSignature ever ran. It echoes SignatureType on
            // every completed call, so its absence means the module never loaded
            // and the wording of the failure is whatever the loader happened to
            // say. Without this, a runner where the manifest sits somewhere else
            // reports a plain file-not-found, which reads as a real failure and
            // turns the build red for a machine that merely cannot sign.
            //
            // Only reachable once the first attempt has already established that
            // the module is the problem, so it cannot paper over a genuine
            // signing failure on a healthy machine.
            if (!second.CmdletRan)
            {
                return new SignOutcome
                {
                    Why = "the Microsoft.PowerShell.Security module could not be loaded on this machine,"
                        + " so Set-AuthenticodeSignature never ran"
                        + Environment.NewLine + second.Why,
                    ModuleUnavailable = true
                };
            }

            return second;
        }

        /// <summary>The two things a signing attempt can come back with.</summary>
        private readonly struct SignOutcome
        {
            public bool Signed { get; init; }

            public string? Why { get; init; }

            /// <summary>True when PowerShell could not load its own security module.</summary>
            public bool ModuleUnavailable { get; init; }

            /// <summary>
            /// True when Set-AuthenticodeSignature actually executed, which is the
            /// only reliable proof that the module was usable.
            /// </summary>
            public bool CmdletRan { get; init; }
        }


        private static SignOutcome RunPowerShell(string script, string target)
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
            info.ArgumentList.Add(script);

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (Win32Exception ex)
            {
                // powershell.exe missing, or blocked by policy. Worth a sentence
                // in the failure rather than a bare Win32Exception from a helper.
                return new SignOutcome { Why = "powershell.exe could not be started: " + ex.Message };
            }

            if (process is null)
            {
                return new SignOutcome { Why = "powershell.exe could not be started" };
            }

            using (process)
            {
                // Both streams have to be drained, not just one. A signing attempt
                // writes enough to stderr to fill the pipe buffer, and a process
                // blocked on a full buffer never exits, which would turn a clear
                // failure into a hung test run.
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(120_000))
                {
                    // Reading ExitCode after a wait that timed out throws, so say what
                    // happened instead. The child is killed either way: left running
                    // it would outlive the test.
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone, which is the outcome that was wanted.
                    }

                    return new SignOutcome
                    {
                        Why = "powershell.exe did not finish within 120s" + Environment.NewLine
                            + Describe(outputTask, errorTask)
                    };
                }

                if (process.ExitCode == 0)
                {
                    return new SignOutcome { Signed = true };
                }

                string detail = "exit code " + process.ExitCode + " from " + target
                    + Environment.NewLine + Describe(outputTask, errorTask);

                // The cmdlet echoes this on every call it completes, so it is the
                // proof that the module was usable and the signing actually began.
                bool cmdletRan = detail.Contains("SignatureType=", StringComparison.Ordinal);

                return new SignOutcome
                {
                    Why = detail,
                    ModuleUnavailable = LooksLikeModuleUnavailable(detail),
                    CmdletRan = cmdletRan
                };

            }
        }

        /// <summary>Whatever the signing attempt managed to say about itself.</summary>
        private static string Describe(Task<string> output, Task<string> error) =>
            output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();

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
