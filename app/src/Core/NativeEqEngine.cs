using System;
using System.ComponentModel;
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
/// this exact 8 + 4 + 40 + 4 + 4-byte shape. Never change one without the other.
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
    // Actually LONG64 = 8 bytes. Version alignment: 8 + 4 + 40 = 52, plus
    // 4 (preampDb) + 4 (compressionAmount) = 60. The native struct is
    // #pragma pack(1) so no padding - mirror that exactly.
    private const int ConfigSizePacked = 8 + 4 + 40 + 4 + 4;

    private const string ProgramDataDir = @"GamerTool";
    private static string ApoDllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProgramDataDir, "GamerToolAPO.dll");

    private MemoryMappedFile? _mapping;
    private long _lastVersionWritten;

    private NativeEqEngine() { }

    #region State

    /// <summary>True when the extracted APO file and its registration both exist.</summary>
    public static bool IsEngineEnabled()
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
            // Mirror OpenSharedConfig on the C++ side with raw P/Invoke
            // (MemoryMappedFileSecurity, the managed route, is .NET
            // Framework-only): an explicitly built NULL DACL (not just a
            // null SA parameter, which would be the default descriptor
            // again) grants access to any account regardless of
            // creation order.
            _mapping = CreateSectionWithNullDacl(out bool weCreatedIt);

            if (weCreatedIt)
            {
                // Initialize to flat/bypass so a freshly-created section is benign.
                using var view = _mapping.CreateViewAccessor();
                view.Write(0, 0L);          // version
                view.Write(8, 0);           // enabled = 0
                for (int i = 0; i < 10; i++)
                    view.Write(12 + i * 4, 0f);
                view.Write(52, 0f);         // preampDb = 0
                view.Write(56, 0f);         // compressionAmount = 0 (off)
            }
        }
    }

    // --- kernel32/advapi32 P/Invokes mirroring GamerToolAPO.cpp OpenSharedConfig ---
    // The managed MemoryMappedFileSecurity type is .NET Framework-only, so
    // the "Local\GamerToolEqConfig" section's security is declared by hand,
    // exactly like the native side does.

    /// <summary>
    /// Create-or-open of the shared config section with an explicit NULL
    /// DACL - exact mirror of GamerToolAPO.cpp OpenSharedConfig. Returns a
    /// managed wrapper. weCreatedIt is false when the section already
    /// existed (e.g. audiodg created it), in which case its contents are
    /// NOT initialized - the APO owns them. The raw CreateFileMappingW
    /// handle is released as soon as the managed re-open succeeds; keeping
    /// it alive longer would serve no purpose (the section object itself
    /// persists as long as ANY handle to it is open anywhere).
    /// </summary>
    private static MemoryMappedFile CreateSectionWithNullDacl(out bool weCreatedIt)
    {
        // SECURITY_DESCRIPTOR (absolute format): revision byte + control
        // byte + control word + 4 pointer-width slots (owner/group/sacl/
        // dacl). Sized via the mirrored struct (40 bytes on x64) rather
        // than the legacy SECURITY_DESCRIPTOR_MIN_LENGTH (20, a 32-bit-era
        // constant) so InitializeSecurityDescriptor can never write past
        // the buffer while nulling the owner/group slots. The native side
        // gets the same guarantee from its full stack SECURITY_DESCRIPTOR.
        // A null DACL set as "present" (SetSecurityDescriptorDacl TRUE,
        // NULL, FALSE) means "grant everyone everything" - the strongest
        // possible openness, matching the native side.
        var sd = new byte[Marshal.SizeOf<SECURITY_DESCRIPTOR>()];
        if (!InitializeSecurityDescriptor(sd, SecurityDescriptorRevision))
            throw new Win32Exception("InitializeSecurityDescriptor failed");

        if (!SetSecurityDescriptorDacl(sd, bDaclPresent: true, pDacl: IntPtr.Zero, bDaclDefaulted: false))
            throw new Win32Exception("SetSecurityDescriptorDacl failed");

        // SECURITY_ATTRIBUTES wraps the descriptor - CreateFileMappingW
        // wants a pointer to this struct, not to the descriptor itself.
        // Blittable by-hand layout, fields in Win32 order.
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = Marshal.AllocHGlobal(sd.Length),
            bInheritHandle = false
        };
        try
        {
            Marshal.Copy(sd, 0, sa.lpSecurityDescriptor, sd.Length);

            IntPtr section = CreateFileMappingW(
                InvalidHandleValue, ref sa, PageReadWrite,
                0, (uint)ConfigSizePacked, SharedMemoryName);
            if (section == IntPtr.Zero)
                throw new Win32Exception("CreateFileMappingW failed");

            bool existed = Marshal.GetLastWin32Error() == ErrorAlreadyExists;
            weCreatedIt = !existed;

            try
            {
                // Re-open via the managed API so the MemoryMappedFile owns
                // its own handle. CreateFromHandle doesn't exist in this
                // TFM, so this is the only route to a managed wrapper. The
                // re-open cannot fail with "name not found" while the raw
                // handle above is still open.
                return MemoryMappedFile.OpenExisting(
                    SharedMemoryName, MemoryMappedFileRights.ReadWrite);
            }
            finally
            {
                CloseHandle(section);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sa.lpSecurityDescriptor);
        }
    }

    private const uint SecurityDescriptorRevision = 1;    // SECURITY_DESCRIPTOR_REVISION
    private const uint PageReadWrite = 0x04;              // PAGE_READWRITE
    private const int ErrorAlreadyExists = 183;           // ERROR_ALREADY_EXISTS

    private static readonly IntPtr InvalidHandleValue = new(-1); // INVALID_HANDLE_VALUE

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    // Absolute-format SECURITY_DESCRIPTOR: 1-byte revision, 1-byte control,
    // 2-byte control word, then owner/group/sacl/dacl pointer slots.
    // Used only for size - InitializeSecurityDescriptor zeroes it and
    // SetSecurityDescriptorDacl stamps the null DACL flag.
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_DESCRIPTOR
    {
        public byte Revision;
        public byte Sbz1;
        public ushort Control;
        public IntPtr Owner;
        public IntPtr Group;
        public IntPtr Sacl;
        public IntPtr Dacl;
    }

    [DllImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(
        byte[] pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(
        byte[] pSecurityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent,
        IntPtr pDacl,
        [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr hFile,
        ref SECURITY_ATTRIBUTES lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string lpName);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Publishes a 10-band gain vector plus preamp/compression to every APO
    /// instance. Bump-version protocol: write payload, memory barrier, then
    /// flip the version - the RT reader snapshots only when the version
    /// moved, so a torn read of a stable payload is impossible.
    /// </summary>
    public void PublishGains(float[] gainsDb, bool enabled, float preampDb = 0f, float compressionAmount = 0f)
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
            view.Write(52, Math.Clamp(preampDb, -12f, 12f));
            view.Write(56, Math.Clamp(compressionAmount, 0f, 1f));

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
        PublishGains(zeros, enabled: true, preampDb: 0f, compressionAmount: 0f); // identity gains, still processing = flat output
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

                // 4b) Register the periodic reattach task (see RegisterReattachTask)
                //     so a device plugged in AFTER today still gets wired in
                //     automatically, without a UAC prompt every time.
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    RegisterReattachTask(exePath);
            }
            else
            {
                DetachFromAllRenderEndpoints();
                UnregisterReattachTask();
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

    private const string ReattachTaskName = "GamerToolEqReattach";

    /// <summary>
    /// Lightweight periodic re-attach: just re-wires any newly-active render
    /// endpoints into our LFX slot, without touching DLL registration or
    /// restarting the audio service (a service restart would audibly
    /// interrupt whatever is currently playing). Invoked via the
    /// "GamerToolEqReattach" scheduled task (see RegisterReattachTask), so a
    /// brand-new device (one that didn't exist the last time Enable ran) gets
    /// picked up within ~15 minutes without a UAC prompt every time - not
    /// instant, but no further elevation needed after the one-time Enable.
    /// A device Windows has already seen before today needs none of this:
    /// AttachToAllRenderEndpoints already wired every ACTIVE endpoint when
    /// Enable last ran, so switching between known devices already works
    /// immediately with no extra step at all.
    /// </summary>
    public static bool RunElevatedReattach()
    {
        try
        {
            if (!IsEngineEnabled())
                return true; // engine off - nothing to do, not a failure

            return AttachToAllRenderEndpoints();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers a Task Scheduler task (via schtasks.exe - no extra
    /// dependency, ships with every Windows install) that reruns
    /// "GamerTool.exe --reattach-eq" every 15 minutes under this user's own
    /// account with the "highest privileges" flag. Creating the task
    /// requires admin ONCE (we're already elevated here); the task itself
    /// then runs elevated on its own schedule with no further UAC prompt.
    /// Best-effort: if schtasks.exe is missing or blocked by policy, Enable
    /// still succeeds - the user just falls back to the manual "re-run
    /// Enable" banner for brand-new devices.
    /// </summary>
    private static void RegisterReattachTask(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{ReattachTaskName}\" /TR \"\\\"{exePath}\\\" --reattach-eq\" " +
                            "/SC MINUTE /MO 15 /RL HIGHEST /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch
        {
            // Best-effort - see summary above.
        }
    }

    private static void UnregisterReattachTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{ReattachTaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch
        {
            // Best-effort - an orphaned task just keeps silently re-wiring
            // an engine that's now disabled, which is harmless (RunElevatedReattach
            // no-ops when !IsEngineEnabled()).
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
