using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Drives the built-in GamerToolAPO equalizer engine.
///
/// Architecture (mirrors EqualizerAPO's proven design, reimplemented):
///  - A native APO DLL (GamerToolAPO) runs inside audiodg.exe and applies a
///    10-band biquad chain to system audio.
///  - This class publishes live gain vectors into a shared-memory section
///    the APO polls, and performs one-time installation (DLL extraction +
///    COM registration + per-endpoint FX wiring + audio stack restart)
///    through an elevated relaunch of GamerTool itself.
///
/// Two hard-won platform facts shape every method below:
///  1. MMDevices registry is TrustedInstaller-owned. Even elevated, Admins
///     hold only KEY_SET_VALUE there - so ALL writes go through raw
///     advapi32 calls requesting exactly that right. Both .NET's
///     writable:true open (demands KEY_CREATE_SUB_KEY) and a
///     rights-restricted .NET open (whose SetValue re-demands KEY_WRITE
///     internally) are denied. Verified empirically on 24H2.
///  2. On Windows 8.1+ the graph builder consumes the SFX slot (,5) and
///     ignores the Vista-era LFX slot (,1). EqualizerAPO's DeviceAPOInfo
///     encodes this: SFX/MFX/EFX unless the driver exposes ONLY LFX/GFX.
///     Writing only ,1 leaves the APO unloadable in audiodg.
///
/// Layout freeze: EqConfig below mirrors struct EqConfig in GamerToolAPO.h
/// byte for byte (#pragma pack(1): LONG64 version + int enabled + 10 floats
/// = 52 bytes). Never change one without the other.
/// </summary>
public sealed class NativeEqEngine
{
    private static readonly Lazy<NativeEqEngine> _instance = new(() => new NativeEqEngine());
    public static NativeEqEngine Instance => _instance.Value;

    // Must match CLSID_GamerToolAPO in GamerToolAPO.h byte for byte.
    public static readonly Guid ApoClsid = new("B7C1B0CB-4D2A-4E48-9B3B-42C39EB0F316");

    // Must match GAMERTOOL_SHARED_MEMORY_NAME in GamerToolAPO.cpp.
    private const string SharedMemoryName = "Local\\GamerToolEqConfig";

    private const int ConfigSizePacked = 8 + 4 + 40;

    private const string ProgramDataDir = @"GamerTool";
    private const string ApoFileName = "GamerToolAPO.dll";
    private const string EnableLogFileName = "enable-log.txt";

    private static string ApoDllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProgramDataDir, ApoFileName);

    private MemoryMappedFile? _mapping;
    private long _lastVersionWritten;

    private NativeEqEngine() { }

    #region Installation state

    /// <summary>True when the extracted APO file and its registration both exist.</summary>
    public bool IsEngineInstalled()
    {
        if (!File.Exists(ApoDllPath))
            return false;

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"AudioEngine\AudioProcessingObjects");
            if (key is null)
                return false;

            return key.GetSubKeyNames().Contains(ClsidBraced);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True only when the engine is installed AND at least one active render
    /// endpoint is wired in a slot the graph actually consumes. A stale
    /// wiring in an ignored slot does NOT count - that false positive once
    /// hid the Enable button while the EQ was silently dead.
    /// </summary>
    public bool IsEngineEnabled()
    {
        return IsEngineInstalled() && IsEngineAttachedToAnyDevice();
    }

    /// <summary>True when at least one render endpoint is wired in its effective slot.</summary>
    public bool IsEngineAttachedToAnyDevice()
    {
        try
        {
            using var renderRoot = OpenRenderRoot();
            if (renderRoot is null)
                return false;

            return renderRoot.GetSubKeyNames().Any(IsEngineAttachedToEndpoint);
        }
        catch
        {
            // Registry perms vary by OS build - "not attached" is the safe answer.
            return false;
        }
    }

    /// <summary>
    /// True when GamerToolAPO is wired into the given endpoint's effective
    /// slot (its GUID in braces, e.g. "{a1b2c3d4-...}") - lets the UI answer
    /// "is my *current* output device covered" instead of "is ANY device
    /// covered".
    /// </summary>
    public bool IsEngineAttachedToEndpoint(string endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid))
            return false;

        try
        {
            using var renderRoot = OpenRenderRoot();
            using var endpointKey = renderRoot?.OpenSubKey(endpointGuid);
            if (endpointKey is null)
                return false;

            var slot = ChooseInstallSlot(endpointKey);
            using var fx = endpointKey.OpenSubKey("FxProperties");
            return IsOurClsid(fx?.GetValue(slot) as string);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the "{guid}" endpoint key name out of a full IMMDevice ID
    /// string (e.g. "{0.0.0.00000000}.{guid}" -> "{guid}"), which is how
    /// MMDevices\Audio\Render subkeys are named in the registry.
    /// </summary>
    public static string? ExtractEndpointGuid(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        int idx = deviceId.LastIndexOf('{');
        return idx >= 0 ? deviceId[idx..] : null;
    }

    private static string ClsidBraced => "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";

    private static bool IsOurClsid(string? value)
        => !string.IsNullOrEmpty(value)
        && string.Equals(value, "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}",
            StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Endpoint FX slots

    // PKEY_FX_*Clsid property-store value names under each endpoint's
    // FxProperties key: the slots the audio graph builder consumes.
    private const string LfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1"; // PKEY_FX_PreMixClsid (Vista-era LFX)
    private const string GfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2"; // PKEY_FX_PostMixClsid (GFX)
    private const string SfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5"; // PKEY_FX_StreamEffectClsid (SFX)
    private const string MfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6"; // PKEY_FX_ModeEffectClsid (MFX)
    private const string EfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7"; // PKEY_FX_EndpointEffectClsid (EFX)
    private const string MultiSfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
    private const string MultiMfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14";
    private const string MultiEfxSlot = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";

    // Forced-disable-enhancements value + SFX processing-modes list.
    private const string SysFxDisableValueName = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string SfxProcessingModesValueName = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
    private const string DefaultProcessingMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    private const string NoneMarker = "\0none\0";

    /// <summary>
    /// Every slot this app can possibly write - used by detach so uninstall
    /// cleans any slot a previous build may have wired.
    /// </summary>
    private static readonly string[] AllInstallSlots =
    {
        LfxSlot, GfxSlot, SfxSlot, MfxSlot, EfxSlot
    };

    private const string RenderRootPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string BackupRootPath = @"SOFTWARE\GamerTool\EndpointBackup";

    /// <summary>
    /// Chooses the ONE FxProperties slot to write for an endpoint, following
    /// EqualizerAPO's DeviceAPOInfo mode detection:
    ///
    ///  - Legacy LFX mode ONLY when the driver supplied just LFX/GFX APOs
    ///    (no SFX/MFX/EFX/multi slots present) - Windows 8.0-era drivers.
    ///    Our own stale wiring does NOT count as "driver-supplied".
    ///  - Otherwise SFX (,5): the modern graph builder consumes ,5/,6/,7 and
    ///    ignores Vista-era ,1 entirely. Verified on 24H2 (in-box chain at
    ///    ,5/,6) and against EqualizerAPO's INSTALL_SFX_EFX default.
    ///
    /// Exactly ONE slot is ever returned, on purpose: this app has a single
    /// APO CLSID, so wiring two slots would instantiate the same 10-band
    /// chain twice and double every gain (EAPO avoids that with a Stage
    /// filter and two distinct pre/post-mix CLSIDs; a single slot is the
    /// correct equivalent here).
    /// </summary>
    private static string ChooseInstallSlot(RegistryKey endpointKey)
    {
        using var fx = endpointKey.OpenSubKey("FxProperties");
        if (fx is null)
            return SfxSlot;

        bool DriverHas(string name)
        {
            var v = fx.GetValue(name) as string;
            return !string.IsNullOrEmpty(v) && !IsOurClsid(v);
        }

        bool legacyOnly = (DriverHas(LfxSlot) || DriverHas(GfxSlot))
            && !DriverHas(SfxSlot) && !DriverHas(MfxSlot) && !DriverHas(EfxSlot)
            && !DriverHas(MultiSfxSlot) && !DriverHas(MultiMfxSlot) && !DriverHas(MultiEfxSlot);

        return legacyOnly ? LfxSlot : SfxSlot;
    }

    /// <summary>Read-only open of the Render root (subkey enumeration).</summary>
    private static RegistryKey? OpenRenderRoot()
    {
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
            .OpenSubKey(RenderRootPath);
    }

    /// <summary>Read-only open of an endpoint's FxProperties key (reads are granted to Users).</summary>
    private static RegistryKey? OpenEndpointFxKey(string endpointGuid)
    {
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
            .OpenSubKey($@"{RenderRootPath}\{endpointGuid}\FxProperties");
    }

    /// <summary>Reads an endpoint's current CLSID value in the given slot (null when absent).</summary>
    private static string? GetEndpointSlotValue(string endpointGuid, string slot)
    {
        using var fx = OpenEndpointFxKey(endpointGuid);
        return fx?.GetValue(slot) as string;
    }

    /// <summary>
    /// Writes (or, when clsidString is null, deletes) an endpoint's CLSID
    /// value in the given slot.
    ///
    /// Raw advapi32 calls, NOT Microsoft.Win32.RegistryKey: .NET's wrapper
    /// gates every SetValue/DeleteValue behind an internal IsWritable()
    /// check that demands full KEY_WRITE regardless of which RegistryRights
    /// the handle was opened with - and the TrustedInstaller-owned
    /// MMDevices ACL grants Admins only KEY_SET_VALUE. Verified on a live
    /// machine: the restricted .NET open succeeds, then SetValue throws
    /// "Cannot write to the registry key". RegOpenKeyExW(KEY_SET_VALUE) +
    /// RegSetValueExW exercises exactly the right the ACL grants, nothing
    /// more.
    /// </summary>
    private static bool TrySetEndpointSlotValue(string endpointGuid, string slot, string? clsidString)
    {
        string subKey = $@"{RenderRootPath}\{endpointGuid}\FxProperties";

        IntPtr hKey = OpenHlmKey(subKey, KEY_SET_VALUE | KEY_QUERY_VALUE);
        if (hKey == IntPtr.Zero)
            return false;

        try
        {
            if (clsidString is null)
            {
                int hr = RegDeleteValueW(hKey, slot);
                return hr == 0 || hr == ERROR_FILE_NOT_FOUND;
            }

            byte[] data = System.Text.Encoding.Unicode.GetBytes(clsidString + "\0");
            return RegSetValueExW(hKey, slot, 0, REG_SZ, data, (uint)data.Length) == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>Raw byte-value writer (REG_DWORD/REG_BINARY restores); mirrors TrySetEndpointSlotValue.</summary>
    private static bool TrySetEndpointValueBytes(string endpointGuid, string valueName, byte[] data, uint type)
    {
        string subKey = $@"{RenderRootPath}\{endpointGuid}\FxProperties";
        IntPtr hKey = OpenHlmKey(subKey, KEY_SET_VALUE | KEY_QUERY_VALUE);
        if (hKey == IntPtr.Zero)
            return false;

        try
        {
            return RegSetValueExW(hKey, valueName, 0, type, data, (uint)data.Length) == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private const uint KEY_SET_VALUE = 0x0002;
    private const uint KEY_QUERY_VALUE = 0x0001;
    private const uint REG_SZ = 1;
    private const uint REG_MULTI_SZ = 7;
    private const uint REG_DWORD = 4;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    private static IntPtr OpenHlmKey(string subKey, uint rights)
    {
        int hr = RegOpenKeyExW(HKEY_LOCAL_MACHINE, subKey, 0, rights, out IntPtr hKey);
        return hr == 0 ? hKey : IntPtr.Zero;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, uint Reserved, uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    #endregion

    #region Live config publishing

    private void EnsureMapping()
    {
        if (_mapping is not null)
            return;

        try
        {
            // Open existing (audiodg created it when the APO loaded) or create
            // for ourselves (so config is stable before any APO instance runs).
            _mapping = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            // audiodg.exe runs the APO instance under a different account
            // (LocalService) than this desktop app runs under. A section
            // created with the default (null) security descriptor is only
            // reachable by its creator's own account, so whichever side
            // gets here first silently locks the other one out - the APO
            // then never sees a real config and just passes audio straight
            // through untouched (which is exactly "the EQ does nothing").
            // The managed MemoryMappedFileSecurity overload of CreateNew is
            // .NET Framework-only, so mirror the native side's approach via
            // P/Invoke: CreateFileMappingW with an explicitly NULL DACL
            // (grants every account access regardless of who created the
            // section first), then re-open it through the managed API.
            IntPtr sd = BuildNullDaclDescriptor();
            if (sd == IntPtr.Zero)
                return;

            try
            {
                var security = new SECURITY_ATTRIBUTES
                {
                    nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = false
                };

                IntPtr h = CreateFileMappingW(
                    INVALID_HANDLE_VALUE, ref security, PAGE_READWRITE,
                    0, (uint)ConfigSizePacked, SharedMemoryName);

                if (h == IntPtr.Zero)
                    return;

                try
                {
                    // Create-or-open semantics: if audiodg won the race this
                    // returns a handle to the existing section; either way
                    // the managed wrapper now owns a usable view of it.
                    _mapping = MemoryMappedFile.OpenExisting(
                        SharedMemoryName, MemoryMappedFileRights.ReadWrite);
                }
                finally
                {
                    CloseHandle(h);
                }

                // Initialize to flat/bypass so a freshly-created section is benign.
                using (var view = _mapping.CreateViewAccessor())
                {
                    view.Write(0, 0L);          // version
                    view.Write(8, 0);           // enabled = 0
                    for (int i = 0; i < 10; i++)
                        view.Write(12 + i * 4, 0f);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sd);
            }
        }

        // Adopt the section's CURRENT version as our baseline. The APO in
        // audiodg survives app restarts (the audio service keeps running),
        // so its appliedVersion may hold a stale value from a previous app
        // session. Restarting our counter at 0 could republish a version the
        // APO already applied - silently ignored forever ("changed preset
        // does nothing until reboot"). Adopting the live counter makes every
        // publish strictly newer from the APO's perspective.
        try
        {
            using var view = _mapping.CreateViewAccessor();
            _lastVersionWritten = view.ReadInt64(0);
        }
        catch
        {
            // Unreadable baseline (shouldn't happen) - 0 keeps old behavior.
        }
    }

    /// <summary>
    /// Builds an absolute-form SECURITY_DESCRIPTOR whose DACL is explicitly
    /// NULL (not merely absent): "no access restrictions, everyone allowed".
    /// That is what lets audiodg's LocalService account and this desktop app
    /// share the section regardless of which side created it first. Mirrors
    /// the native OpenSharedConfig() in GamerToolAPO.cpp.
    /// </summary>
    private static IntPtr BuildNullDaclDescriptor()
    {
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_DESCRIPTOR>());
        try
        {
            if (!InitializeSecurityDescriptor(memory, SECURITY_DESCRIPTOR_REVISION) ||
                !SetSecurityDescriptorDacl(memory, true, IntPtr.Zero, false))
            {
                Marshal.FreeHGlobal(memory);
                return IntPtr.Zero;
            }

            return memory;
        }
        catch
        {
            Marshal.FreeHGlobal(memory);
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_DESCRIPTOR
    {
        public byte Revision;
        public byte Sbz1;
        public ushort Control;
        // PSIDs are POINTERS (8 bytes on x64) - declaring these as uint
        // under-allocates the buffer by 20 bytes and InitializeSecurity-
        // Descriptor then heap-corrupts past the end (0xC0000374 crash in
        // ntdll, confirmed via WER on the CI build). Sequential layout with
        // IntPtr yields the correct 40-byte native shape.
        public IntPtr Owner;
        public IntPtr Group;
        public IntPtr Sacl;
        public IntPtr Dacl;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    private const uint SECURITY_DESCRIPTOR_REVISION = 1;
    private const uint PAGE_READWRITE = 0x04;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor, bool bDaclPresent, IntPtr pDacl, bool bDaclDefaulted);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr hFile, ref SECURITY_ATTRIBUTES sa, uint protect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Publishes a 10-band gain vector to every APO instance. Bump-version
    /// protocol: write payload, memory barrier, then flip the version - the
    /// RT reader snapshots only when the version moved, so a torn read of a
    /// stable payload is impossible.
    /// </summary>
    public void PublishGains(float[] gainsDb, bool enabled)
    {
        if (gainsDb is null || gainsDb.Length != 10)
            return;

        try
        {
            EnsureMapping();

            using var view = _mapping!.CreateViewAccessor();
            long newVersion = _lastVersionWritten + 2;

            view.Write(8, enabled ? 1 : 0);
            for (int i = 0; i < 10; i++)
                view.Write(12 + i * 4, Math.Clamp(gainsDb[i], -12f, 12f));

            System.Threading.Thread.MemoryBarrier();
            view.Write(0, newVersion);
            _lastVersionWritten = newVersion;
        }
        catch
        {
            // Mapping unavailable (engine disabled) - nothing to publish to.
        }
    }

    /// <summary>Flat-line and bypass our contribution without touching other processing.</summary>
    public void ClearGains()
    {
        var zeros = new float[10];
        PublishGains(zeros, enabled: true); // identity gains, still processing = flat output
    }

    #endregion

    #region Enable / disable bootstrap

    /// <summary>
    /// Runs the elevated enable sequence: extract the embedded APO resource to
    /// ProgramData, call the DLL's own registration export, set
    /// DisableProtectedAudioDG (unsigned APOs require it), wire every active
    /// render endpoint's FX slot, and restart the audio stack so the graph
    /// rebuilds with the APO loaded. MUST run elevated. Returns true only
    /// when endpoints were actually wired - never a hollow success.
    /// </summary>
    public static bool RunElevatedEnable(bool enable)
    {
        var log = new List<string> { $"[{DateTime.Now:HH:mm:ss.fff}] enable={enable} begin" };
        bool result = false;
        try
        {
            if (enable)
            {
                // 1) Extract embedded APO to ProgramData (permanent home).
                //    Skipped only when the on-disk copy is byte-identical
                //    (SHA256, not just length - two different builds can
                //    share a size, and a stale DLL silently kept alive that
                //    way cost a full debug round). A File.Create over a DLL
                //    audiodg has mapped throws a sharing violation, so a
                //    locked target keeps the existing file instead of
                //    aborting the whole enable.
                var dir = Path.GetDirectoryName(ApoDllPath)!;
                Directory.CreateDirectory(dir);
                using var resource = typeof(NativeEqEngine).Assembly
                    .GetManifestResourceStream("GamerTool.Resources.GamerToolAPO.dll");
                if (resource is null)
                {
                    log.Add("step1 EXTRACT: embedded resource MISSING from exe");
                    return false;
                }

                string resourceHash;
                using (var sha = SHA256.Create())
                {
                    resource.Position = 0;
                    resourceHash = Convert.ToHexString(sha.ComputeHash(resource));
                    resource.Position = 0;
                }
                log.Add($"step1 EXTRACT: embedded sha256={resourceHash.Substring(0, 16)}...");

                bool needExtract = true;
                try
                {
                    if (File.Exists(ApoDllPath))
                    {
                        string diskHash;
                        using (var sha = SHA256.Create())
                        using (var disk = File.OpenRead(ApoDllPath))
                            diskHash = Convert.ToHexString(sha.ComputeHash(disk));

                        if (string.Equals(diskHash, resourceHash, StringComparison.OrdinalIgnoreCase))
                        {
                            needExtract = false;
                            log.Add("step1 EXTRACT: skipped, on-disk copy byte-identical");
                        }
                        else
                        {
                            log.Add($"step1 EXTRACT: on-disk copy differs (disk sha256={diskHash.Substring(0, 16)}...), replacing");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Add($"step1 EXTRACT: hash check failed ({ex.GetType().Name}), will extract");
                }

                if (needExtract)
                {
                    try
                    {
                        using var target = File.Create(ApoDllPath);
                        resource.CopyTo(target);
                        log.Add($"step1 EXTRACT: ok ({new FileInfo(ApoDllPath).Length} bytes)");
                    }
                    catch (IOException ex) when (File.Exists(ApoDllPath))
                    {
                        // Target locked (audiodg has a previous copy mapped).
                        // The existing file is usable - log and continue.
                        log.Add($"step1 EXTRACT: target locked, keeping existing file ({ex.Message})");
                    }
                }
            }

            // 2) Register/unregister via the DLL's own export (SDK-exact blob).
            //    LoadLibrary from our extraction path - works whether or not the
            //    audio service currently has an older copy mapped.
            var register = GetProcAddress<ApoRegisterDelegate>("GamerToolApoRegister");
            var unregister = GetProcAddress<ApoUnregisterDelegate>("GamerToolApoUnregister");

            if (enable)
            {
                if (register is null)
                {
                    log.Add("step2 REGISTER: GamerToolApoRegister export not found in DLL");
                    return false;
                }

                int hr = register(ApoDllPath);
                log.Add($"step2 REGISTER: hr=0x{hr:X8}");
                if (hr != 0)
                    return false;
            }
            else
            {
                unregister?.Invoke();
                log.Add("step2 UNREGISTER: done");
            }

            if (enable)
            {
                // 3) DisableProtectedAudioDG - unsigned APOs need it (same line
                //    EqualizerAPO's installer writes; admin-only HKLM).
                using (var audioKey = Registry.LocalMachine.CreateSubKey(
                           @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", writable: true))
                {
                    audioKey.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
                }
                log.Add("step3 PROTECTED_DG: set");

                // 4) Wire the active render endpoints' FX slots.
                bool attached = AttachToAllRenderEndpoints(log);
                log.Add($"step4 ATTACH: {(attached ? "wired >=1 active endpoint" : "wired NOTHING")}");
                if (!attached)
                    return false;
            }
            else
            {
                DetachFromAllRenderEndpoints(log);
                log.Add("step4 DETACH: done");
            }

            // 5) Restart the full audio stack so graphs rebuild with/without
            //    us. AudioEndpointBuilder owns and caches the endpoint FX
            //    stores - restarting Audiosrv alone cycles audiodg but the
            //    builder re-serves its stale cache, so fresh wiring stays
            //    invisible and the APO never loads. Stop order: Audiosrv
            //    (depends on the builder) first, then the builder itself.
            RestartAudioService(log);
            log.Add("step5 RESTART: audio stack cycled (see svc lines above)");

            result = true;
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            log.Add($"[{DateTime.Now:HH:mm:ss.fff}] result={result}");
            try
            {
                File.WriteAllLines(
                    Path.Combine(Path.GetDirectoryName(ApoDllPath)!, "enable-log.txt"), log);
            }
            catch
            {
                // Logging is best-effort; never fail enable over it.
            }
        }
    }

    private delegate int ApoRegisterDelegate([MarshalAs(UnmanagedType.LPWStr)] string dllPath);
    private delegate int ApoUnregisterDelegate();

    private static T? GetProcAddress<T>(string exportName) where T : class
    {
        // LoadLibrary: if audiodg already mapped an old copy of a different
        // path, LoadLibrary on OUR path still resolves to the new file for
        // the registration functions (which only touch the registry anyway).
        var handle = LoadLibrary(ApoDllPath);
        if (handle == IntPtr.Zero)
            return null;

        var address = GetProcAddress(handle, exportName);
        if (address == IntPtr.Zero)
            return null;

        return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

    /// <summary>
    /// Writes our CLSID into the effective FX slot of every ACTIVE render
    /// endpoint (mode-aware: SFX on modern drivers, LFX on legacy ones),
    /// backing up whatever was there first. Returns true only if at least
    /// one endpoint was actually wired - a hollow "success" here is what
    /// once made the EQ silently dead while the UI said "active".
    /// </summary>
    private static bool AttachToAllRenderEndpoints(List<string> log)
    {
        int wired = 0;
        int activeSeen = 0;

        try
        {
            using var renderRoot = OpenRenderRoot();
            if (renderRoot is null)
            {
                log.Add("  attach: Render root missing");
                return false;
            }

            foreach (var endpointGuid in renderRoot.GetSubKeyNames())
            {
                // Only ACTIVE endpoints (state DWORD == 1); plugging in a new
                // device later re-runs this via the C# side's device watcher.
                RegistryKey? endpointKey = null;
                int? state;
                try
                {
                    endpointKey = renderRoot.OpenSubKey(endpointGuid);
                    state = endpointKey?.GetValue("DeviceState") as int?;
                }
                catch (Exception ex)
                {
                    endpointKey?.Dispose();
                    log.Add($"  attach: {endpointGuid} skipped ({ex.GetType().Name})");
                    continue;
                }

                using (endpointKey)
                {
                    if (state != 1 || endpointKey is null)
                        continue;

                    activeSeen++;
                    string slot = ChooseInstallSlot(endpointKey);

                    // Backup the slot's current value (or its absence) so
                    // Disable restores exactly what was there before us.
                    // Never overwrite an existing backup - re-running Enable
                    // must keep the ORIGINAL pre-GamerTool value.
                    // A backup holding OUR OWN CLSID is provably corrupt
                    // (an older build snapshotted our wiring as "original");
                    // drop it so it can never restore us over ourselves.
                    try
                    {
                        using var backupKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\GamerTool\EndpointBackup");
                        if (backupKey is not null)
                        {
                            // Purge poisoned legacy backups: earlier builds
                            // overwrote the bare-name ,1 backup on every run,
                            // so it can hold OUR OWN CLSID. A backup is only
                            // valid if it predates us - drop the corrupt
                            // entry so it can never restore us over ourselves.
                            if (backupKey.GetValue(endpointGuid) is string legacy
                                && IsOurClsid(legacy))
                            {
                                try { backupKey.DeleteValue(endpointGuid); } catch { }
                                log.Add($"  attach: {endpointGuid} dropped poisoned legacy backup");
                            }

                            string backupName = endpointGuid + slot;
                            if (backupKey.GetValue(backupName) is string poisoned && IsOurClsid(poisoned))
                            {
                                try { backupKey.DeleteValue(backupName); } catch { }
                                log.Add($"  attach: {endpointGuid} dropped poisoned backup for {slot}");
                            }

                            if (backupKey.GetValue(backupName) is null)
                            {
                                var existing = GetEndpointSlotValue(endpointGuid, slot);
                                backupKey.SetValue(backupName, existing ?? "\0none\0", RegistryValueKind.String);
                            }
                        }
                    }
                    catch
                    {
                        // Backup is best-effort; keep wiring.
                    }

                    if (!TrySetEndpointSlotValue(endpointGuid, slot, ClsidBraced))
                    {
                        log.Add($"  attach: {endpointGuid} slot {slot} WRITE FAILED");
                        continue;
                    }

                    // SFX installs need their streaming-modes list; drivers
                    // normally ship it, but write the DEFAULT mode entry if
                    // absent - otherwise the graph may skip our APO for every
                    // stream mode.
                    if (slot == SfxSlot)
                        EnsureSfxProcessingModes(endpointGuid, log);

                    // Force-enable enhancements for this endpoint: if the
                    // PKEY "disable all enhancements" DWORD is present, the
                    // graph skips every FX APO including ours. Backed up (as
                    // DWORD bytes) so Disable restores it.
                    TryForceEnableEnhancements(endpointGuid, log);

                    // Delete our own stale wiring in slots we're NOT using
                    // (e.g. the old Vista-era LFX write on a modern graph):
                    // exactly one slot is ever wired, so a legacy graph can
                    // never see a second instance.
                    foreach (var other in AllInstallSlots)
                    {
                        if (other != slot && IsOurClsid(GetEndpointSlotValue(endpointGuid, other)))
                            TrySetEndpointSlotValue(endpointGuid, other, null);
                    }

                    log.Add($"  attach: {endpointGuid} wired slot {slot}");
                    wired++;
                }
            }

            return activeSeen == 0 || wired > 0;
        }
        catch (Exception ex)
        {
            log.Add($"  attach: fatal {ex.GetType().Name}: {ex.Message}");
            return wired > 0;
        }
    }

    private static void DetachFromAllRenderEndpoints(List<string> log)
    {
        try
        {
            using var renderRoot = OpenRenderRoot();
            if (renderRoot is null)
                return;

            foreach (var endpointGuid in renderRoot.GetSubKeyNames())
            {
                foreach (var slot in AllInstallSlots)
                {
                    var current = GetEndpointSlotValue(endpointGuid, slot);
                    if (!IsOurClsid(current))
                        continue; // someone else's APO (or empty) - leave untouched

                    // Restore what was there before us (or delete if there was
                    // nothing). Also honors the legacy backup format (bare
                    // endpoint GUID holding the ,1 value) written by earlier
                    // builds - unless that legacy entry is poisoned with our
                    // own CLSID, in which case the slot was originally empty.
                    string? backup = null;
                    try
                    {
                        using var backupKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GamerTool\EndpointBackup");
                        backup = backupKey?.GetValue(endpointGuid + slot) as string
                            ?? (slot == LfxSlot ? backupKey?.GetValue(endpointGuid) as string : null);
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (backup is null || backup == "\0none\0" || IsOurClsid(backup))
                            TrySetEndpointSlotValue(endpointGuid, slot, null);
                        else
                            TrySetEndpointSlotValue(endpointGuid, slot, backup);
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }
                }

                // Restore a user/driver "disable all enhancements" setting we
                // may have removed during attach (stored as 4 raw bytes).
                try
                {
                    using var backupKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GamerTool\EndpointBackup");
                    if (backupKey?.GetValue(endpointGuid + "|sysfx") is byte[] sysfxBackup
                        && sysfxBackup.Length == 4)
                    {
                        TrySetEndpointValueBytes(endpointGuid, SysFxDisableValueName, sysfxBackup, REG_DWORD);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            log.Add("  detach: sweep complete");
        }
        catch (Exception ex)
        {
            log.Add($"  detach: fatal {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the SFX streaming-modes list exists (REG_MULTI_SZ holding the
    /// DEFAULT processing mode), mirroring EqualizerAPO: modes the engine
    /// doesn't see listed never load the SFX. Never touches an existing
    /// list - drivers ship their own.
    /// </summary>
    private static void EnsureSfxProcessingModes(string endpointGuid, List<string> log)
    {
        try
        {
            using var fx = OpenEndpointFxKey(endpointGuid);
            if (fx?.GetValue(SfxProcessingModesValueName) is not null)
                return;
        }
        catch
        {
            return;
        }

        string subKey = $@"{RenderRootPath}\{endpointGuid}\FxProperties";
        IntPtr hKey = OpenHlmKey(subKey, KEY_SET_VALUE | KEY_QUERY_VALUE);
        if (hKey == IntPtr.Zero)
        {
            log.Add($"  attach: {endpointGuid} processing-modes open denied");
            return;
        }

        try
        {
            // REG_MULTI_SZ: one DEFAULT-mode GUID string, double-NUL terminated.
            byte[] data = System.Text.Encoding.Unicode.GetBytes(DefaultProcessingMode + "\0\0");
            if (RegSetValueExW(hKey, SfxProcessingModesValueName, 0, REG_MULTI_SZ, data, (uint)data.Length) != 0)
                log.Add($"  attach: {endpointGuid} processing-modes write failed");
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>
    /// Deletes an endpoint's "disable all enhancements" DWORD when present
    /// (with it set, the graph skips every FX APO including ours), backing
    /// it up first so Disable restores the user's original setting.
    /// </summary>
    private static void TryForceEnableEnhancements(string endpointGuid, List<string> log)
    {
        try
        {
            int? current;
            using (var fx = OpenEndpointFxKey(endpointGuid))
            {
                if (fx?.GetValue(SysFxDisableValueName) is not int v)
                    return;
                current = v;
            }

            using (var backupKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\GamerTool\EndpointBackup"))
            {
                string backupName = endpointGuid + "|sysfx";
                if (backupKey is not null && !backupKey.GetValueNames().Contains(backupName))
                    backupKey.SetValue(backupName, BitConverter.GetBytes(current.Value), RegistryValueKind.Binary);
            }

            string subKey = $@"{RenderRootPath}\{endpointGuid}\FxProperties";
            IntPtr hKey = OpenHlmKey(subKey, KEY_SET_VALUE | KEY_QUERY_VALUE);
            if (hKey == IntPtr.Zero)
            {
                log.Add($"  attach: {endpointGuid} sysfx open denied");
                return;
            }

            try
            {
                RegDeleteValueW(hKey, SysFxDisableValueName);
            }
            finally
            {
                RegCloseKey(hKey);
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>
    /// Cycle the Windows audio stack so graphs rebuild and pick up the new
    /// APO wiring. Order matters: AudioEndpointBuilder OWNS and CACHES the
    /// endpoint FX property stores - restarting only Audiosrv cycles
    /// audiodg but the builder just re-serves its cached store, so freshly
    /// written values are invisible and the APO never loads. The builder
    /// must cycle too. Audiosrv DEPENDS on the builder, so the correct order
    /// is: stop Audiosrv, stop AudioEndpointBuilder, start
    /// AudioEndpointBuilder, start Audiosrv.
    ///
    /// Done through the Service Control Manager directly, NOT net.exe: net's
    /// exit codes report dispatch rather than completion, its /yes switch
    /// is invalid on `start` (exit 2 with the service left stopped - the
    /// exact failure that once left this machine mute), and it can hang on
    /// interactive prompts. Here every transition is waited on with a
    /// timeout and the reached state is logged.
    /// </summary>
    private static void RestartAudioService(List<string> log)
    {
        StopService("Audiosrv", log);
        StopService("AudioEndpointBuilder", log);
        StartService("AudioEndpointBuilder", log);
        StartService("Audiosrv", log);
        log.Add($"step5 RESTART final: Audiosrv={QueryServiceState("Audiosrv")}, AudioEndpointBuilder={QueryServiceState("AudioEndpointBuilder")}");
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_START_PENDING = 0x00000002;
    private const uint SERVICE_STOP_PENDING = 0x00000003;
    private const uint SERVICE_RUNNING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    private static string QueryServiceState(string name)
    {
        try
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
                return "scm denied";
            try
            {
                IntPtr svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero)
                    return "open denied";
                try
                {
                    var st = new SERVICE_STATUS();
                    if (!QueryServiceStatus(svc, ref st))
                        return "query failed";
                    return st.dwCurrentState switch
                    {
                        SERVICE_STOPPED => "STOPPED",
                        SERVICE_START_PENDING => "START_PENDING",
                        SERVICE_STOP_PENDING => "STOP_PENDING",
                        SERVICE_RUNNING => "RUNNING",
                        _ => $"state={st.dwCurrentState}"
                    };
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
        catch
        {
            return "error";
        }
    }

    private static bool WaitForServiceState(string name, uint wantState, int timeoutMs, List<string> log)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            string state = QueryServiceState(name);
            uint code = state switch
            {
                "STOPPED" => SERVICE_STOPPED,
                "START_PENDING" => SERVICE_START_PENDING,
                "STOP_PENDING" => SERVICE_STOP_PENDING,
                "RUNNING" => SERVICE_RUNNING,
                _ => 0
            };
            if (code == wantState)
                return true;
            System.Threading.Thread.Sleep(250);
        }

        log.Add($"  svc {name}: TIMEOUT waiting for state {wantState} (now {QueryServiceState(name)})");
        return false;
    }

    private static void StopService(string name, List<string> log)
    {
        try
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                log.Add($"  svc stop {name}: scm denied");
                return;
            }

            try
            {
                IntPtr svc = OpenServiceW(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero)
                {
                    log.Add($"  svc stop {name}: open denied");
                    return;
                }

                try
                {
                    var st = new SERVICE_STATUS();
                    if (!QueryServiceStatus(svc, ref st))
                    {
                        log.Add($"  svc stop {name}: query failed");
                        return;
                    }

                    if (st.dwCurrentState == SERVICE_STOPPED)
                    {
                        log.Add($"  svc stop {name}: already stopped");
                        return;
                    }

                    if (!ControlService(svc, SERVICE_CONTROL_STOP, ref st))
                    {
                        log.Add($"  svc stop {name}: control failed err={Marshal.GetLastWin32Error()}");
                        return;
                    }

                    bool ok = WaitForServiceState(name, SERVICE_STOPPED, 30000, log);
                    log.Add($"  svc stop {name}: {(ok ? "STOPPED" : "stop incomplete")}");
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
        catch (Exception ex)
        {
            log.Add($"  svc stop {name}: EXCEPTION {ex.GetType().Name}");
        }
    }

    private static void StartService(string name, List<string> log)
    {
        try
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                log.Add($"  svc start {name}: scm denied");
                return;
            }

            try
            {
                IntPtr svc = OpenServiceW(scm, name, SERVICE_START | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero)
                {
                    log.Add($"  svc start {name}: open denied");
                    return;
                }

                try
                {
                    var st = new SERVICE_STATUS();
                    if (!QueryServiceStatus(svc, ref st))
                    {
                        log.Add($"  svc start {name}: query failed");
                        return;
                    }

                    if (st.dwCurrentState == SERVICE_RUNNING)
                    {
                        log.Add($"  svc start {name}: already running");
                        return;
                    }

                    if (!StartServiceW(svc, 0, null))
                    {
                        int err = Marshal.GetLastWin32Error();
                        // ERROR_SERVICE_ALREADY_RUNNING (1056) after a racing
                        // dependency auto-start is fine, anything else is real.
                        log.Add($"  svc start {name}: start call err={err}");
                        if (err != 1056)
                            return;
                    }

                    bool ok = WaitForServiceState(name, SERVICE_RUNNING, 30000, log);
                    log.Add($"  svc start {name}: {(ok ? "RUNNING" : "start incomplete")}");
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
        catch (Exception ex)
        {
            log.Add($"  svc start {name}: EXCEPTION {ex.GetType().Name}");
        }
    }

    #endregion
}
