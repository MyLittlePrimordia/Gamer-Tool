using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Drives the built-in GamerToolAPO engine: writes the live 10-band config
/// into the shared memory section the APO instances read in audiodg.exe,
/// and performs one-time enablement (DLL extraction + registry registration
/// + audio graph reload) through an elevated relaunch of GamerTool itself.
///
/// Layout freeze: the native side (GamerToolAPO.h struct EqConfig) mirrors
/// this exact 4 + 40 + 8-byte shape. Never change one without the other.
/// </summary>
public sealed class NativeEqEngine
{
    private static readonly Lazy<NativeEqEngine> _instance = new(() => new NativeEqEngine());
    public static NativeEqEngine Instance => _instance.Value;

    // Must match CLSID_GamerToolAPO in GamerToolAPO.h byte for byte.
    public static readonly Guid ApoClsid = new("B7C1B0CB-4D2A-4E48-9B3B-42C39EB0F316");

    // Must match GAMERTOOL_SHARED_MEMORY_NAME in GamerToolAPO.cpp.
    private const string SharedMemoryName = "Local\\GamerToolEqConfig";

    private const int ConfigSize = 4 /*version:LONG64*/ + 4 /*enabled:int*/ + 40 /*10 floats*/;
    // Actually LONG64 = 8 bytes. Version alignment: 8 + 4 + 40 = 52. The
    // native struct is #pragma pack(1) so no padding - mirror that exactly.
    private const int ConfigSizePacked = 8 + 4 + 40;

    private const string ProgramDataDir = @"GamerTool";
    private static string ApoDllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProgramDataDir, "GamerToolAPO.dll");

    private MemoryMappedFile? _mapping;
    private long _lastVersionWritten;

    private NativeEqEngine() { }

    #region State

    /// <summary>True when the extracted APO file and its registration both exist.</summary>
    public bool IsEngineEnabled()
    {
        if (!File.Exists(ApoDllPath))
            return false;

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"AudioEngine\AudioProcessingObjects");
            if (key is null)
                return false;

            var clsid = "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";
            return key.GetSubKeyNames().Contains(clsid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when at least one active render endpoint has GamerToolAPO wired in.</summary>
    public bool IsEngineAttachedToAnyDevice()
    {
        try
        {
            var renderRoot = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (renderRoot is null)
                return false;

            var clsidString = "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";
            foreach (var endpointGuid in renderRoot.GetSubKeyNames())
            {
                using var fx = renderRoot.OpenSubKey(endpointGuid + @"\FxProperties");
                if (fx is null)
                    continue;

                // LFX slot (pre-mix) is where we install - check the LFX value name.
                var lfx = fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1") as string;
                if (string.Equals(lfx, clsidString, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Registry perms vary by OS build - "not attached" is the safe answer.
        }

        return false;
    }

    /// <summary>
    /// True when GamerToolAPO is specifically wired into the given render
    /// endpoint (its GUID in braces, e.g. "{a1b2c3d4-...}") - lets the UI
    /// answer "is my *current* output device covered" instead of "is ANY
    /// device covered", so the reattach prompt reflects the actual device
    /// in use rather than a stale, disconnected-from-reality snapshot.
    /// </summary>
    public bool IsEngineAttachedToEndpoint(string endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid))
            return false;

        try
        {
            using var fx = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpointGuid}\FxProperties");
            if (fx is null)
                return false;

            var clsidString = "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";
            var lfx = fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1") as string;
            return string.Equals(lfx, clsidString, StringComparison.OrdinalIgnoreCase);
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
                using var view = _mapping.CreateViewAccessor();
                view.Write(0, 0L);          // version
                view.Write(8, 0);           // enabled = 0
                for (int i = 0; i < 10; i++)
                    view.Write(12 + i * 4, 0f);
            }
            finally
            {
                Marshal.FreeHGlobal(sd);
            }
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
        public uint OffsetOwner;
        public uint OffsetGroup;
        public uint OffsetSacl;
        public uint OffsetDacl;
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
    /// ProgramData, call the DLL's own registration export (byte-exact
    /// APO_REG_PROPERTIES serialization via the SDK's RegisterAPO), set
    /// DisableProtectedAudioDG (unsigned APOs require it), wire every active
    /// render endpoint's LFX slot, and restart the audio service so the graph
    /// rebuilds with the APO loaded. MUST run elevated.
    /// </summary>
    public static bool RunElevatedEnable(bool enable)
    {
        try
        {
            if (enable)
            {
                // 1) Extract embedded APO to ProgramData (permanent home).
                var dir = Path.GetDirectoryName(ApoDllPath)!;
                Directory.CreateDirectory(dir);
                using var resource = typeof(NativeEqEngine).Assembly
                    .GetManifestResourceStream("GamerTool.Resources.GamerToolAPO.dll");
                if (resource is null)
                    return false;

                using var target = File.Create(ApoDllPath);
                resource.CopyTo(target);
            }

            // 2) Register/unregister via the DLL's own export (SDK-exact blob).
            //    LoadLibrary from our extraction path - works whether or not the
            //    audio service currently has an older copy mapped.
            var register = GetProcAddress<ApoRegisterDelegate>("GamerToolApoRegister");
            var unregister = GetProcAddress<ApoUnregisterDelegate>("GamerToolApoUnregister");

            if (enable)
            {
                int hr = register!(ApoDllPath);
                if (hr != 0)
                    return false;
            }
            else
            {
                unregister!();
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

                // 4) Wire the active render endpoints' LFX slot.
                if (!AttachToAllRenderEndpoints())
                    return false;
            }
            else
            {
                DetachFromAllRenderEndpoints();
            }

            // 5) Restart audio service so the graph rebuilds with/without us.
            RestartAudioService();
            return true;
        }
        catch
        {
            return false;
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
    /// Writes our CLSID into the LFX value of every ACTIVE render endpoint.
    /// EqualizerAPO backs up existing values under its own Child APOs key;
    /// we take the same approach so an uninstall can restore the original.
    /// </summary>
    private static bool AttachToAllRenderEndpoints()
    {
        try
        {
            var clsidString = "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";
            using var renderRoot = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render", writable: true);

            foreach (var endpointGuid in renderRoot.GetSubKeyNames())
            {
                // Only ACTIVE endpoints (state DWORD == 1); plugging in a new
                // device later re-runs this via the C# side's device watcher.
                using var endpointKey = renderRoot.OpenSubKey(endpointGuid, writable: true);
                if (endpointKey is null)
                    continue;

                var state = endpointKey.GetValue("DeviceState") as int?;
                if (state != 1)
                    continue;

                using var fx = endpointKey.CreateSubKey("FxProperties");

                // Backup the existing LFX value (or record its absence) so
                // Disable can restore exactly what was there before us.
                var existing = fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1") as string;
                using (var backupKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\GamerTool\EndpointBackup"))
                {
                    if (existing is null)
                        backupKey.SetValue(endpointGuid, "\0none\0", RegistryValueKind.String);
                    else
                        backupKey.SetValue(endpointGuid, existing, RegistryValueKind.String);
                }

                fx.SetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", clsidString, RegistryValueKind.String);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DetachFromAllRenderEndpoints()
    {
        try
        {
            var clsidString = "{" + ApoClsid.ToString("D").ToUpperInvariant() + "}";
            using var renderRoot = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render", writable: true);

            using var backupKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\GamerTool\EndpointBackup", writable: true);

            foreach (var endpointGuid in renderRoot.GetSubKeyNames())
            {
                using var fx = renderRoot.OpenSubKey(endpointGuid + @"\FxProperties", writable: true);
                if (fx is null)
                    continue;

                var current = fx.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1") as string;
                if (!string.Equals(current, clsidString, StringComparison.OrdinalIgnoreCase))
                    continue; // someone else's APO - leave untouched

                // Restore what was there before us (or delete if there was nothing).
                var backup = backupKey.GetValue(endpointGuid) as string;
                if (backup is null || backup == "\0none\0")
                    fx.DeleteValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", throwOnMissingValue: false);
                else
                    fx.SetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1", backup, RegistryValueKind.String);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Cycle the Windows audio endpoints so the graph reloads and picks up the
    /// new APO. Endpoint-clone/enable dance avoids killing the whole service
    /// (which Windows 10+ dislikes doing programmatically).
    /// </summary>
    private static void RestartAudioService()
    {
        // audiodg reloads APOs when an endpoint is disabled/re-enabled. The
        // PnP approach via the MMDevices registry requires an actual graph
        // rebuild trigger; the accepted programmatic trigger is restarting
        // the "Audiosrv" service. Do it via a detached process so the shell
        // doesn't flap our own audio mid-operation.
        Process.Start(new ProcessStartInfo
        {
            FileName = "net.exe",
            Arguments = "stop audiosrv",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!.WaitForExit();

        Process.Start(new ProcessStartInfo
        {
            FileName = "net.exe",
            Arguments = "start audiosrv",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!.WaitForExit();
    }

    #endregion
}
