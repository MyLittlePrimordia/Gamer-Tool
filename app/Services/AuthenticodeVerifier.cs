using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>What Windows made of a file's Authenticode signature.</summary>
internal enum SignatureVerdict
{
    /// <summary>Signature valid, file unaltered, certificate chains to a trusted root.</summary>
    Trusted,

    /// <summary>
    /// Signature valid and file unaltered, but the certificate does not chain to a
    /// root this machine trusts. Self-signed and test certificates land here.
    /// </summary>
    IntactButUntrusted,

    /// <summary>No signature, a broken one, an altered file, or anything else wrong.</summary>
    Invalid,
}

/// <summary>
/// Full Authenticode verification of a file through WinVerifyTrust: the signature
/// is checked against the file's hash, so a signature copied onto a different file
/// fails, and the certificate chain is built to a trusted root.
/// <para>
/// Revocation checking is switched off and URL retrieval is cache-only, so a
/// machine that cannot reach a revocation endpoint still verifies a file that is
/// genuinely signed. That offline failure is what made an earlier version of the
/// installer check give up on WinVerifyTrust altogether; it was the revocation
/// lookup, not the signature.
/// </para>
/// <para>
/// This answers "is the signature real". Who signed it is a separate question and
/// stays with the caller.
/// </para>
/// </summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    // The signature and the file hash are fine; only trust in the chain is missing.
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int CertEUntrustedTestRoot = unchecked((int)0x800B010D);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WintrustData data);

    /// <summary>Asks Windows to verify the file's embedded signature.</summary>
    public static SignatureVerdict Verify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SignatureVerdict.Invalid;
        }

        IntPtr fileInfoPtr = IntPtr.Zero;
        WintrustData data = default;
        bool opened = false;

        try
        {
            WintrustFileInfo fileInfo = new()
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = path,
            };

            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };

            // INVALID_HANDLE_VALUE as the owner window: never show UI.
            int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            opened = true;

            if (result == 0)
            {
                return SignatureVerdict.Trusted;
            }

            TraceLog.Write("INSTALLER WinVerifyTrust 0x"
                + result.ToString("X8", CultureInfo.InvariantCulture));

            return result is CertEUntrustedRoot or CertEChaining or CertEUntrustedTestRoot
                ? SignatureVerdict.IntactButUntrusted
                : SignatureVerdict.Invalid;
        }
        catch (Exception ex)
        {
            TraceLog.Write("INSTALLER WinVerifyTrust", ex);
            return SignatureVerdict.Invalid;
        }
        finally
        {
            if (opened)
            {
                // The state that WinVerifyTrust kept for the verify call has to be
                // released with a second call, or it is held for the process's life.
                data.dwStateAction = WtdStateActionClose;
                try
                {
                    _ = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                }
                catch (Exception ex)
                {
                    TraceLog.Write("INSTALLER WinVerifyTrust close", ex);
                }
            }

            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WintrustFileInfo>(fileInfoPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }
    }
}
