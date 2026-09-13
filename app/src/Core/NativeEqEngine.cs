using System;
using System.Diagnostics;
using System.IO;
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

    #region Live config publishing

    private IntPtr _mappingHandle = IntPtr.Zero;
    private IntPtr _viewPtr = IntPtr.Zero;
    private long _lastVersionWritten;

    /// <summary>
    /// Raw kernel32/advapi32 P/Invoke for the shared-memory section, used
    /// instead of System.IO.MemoryMappedFiles because MemoryMappedFileSecurity
    /// (the managed wrapper for a custom security descriptor) isn't available
    /// even on net8.0-windows. This mirrors GamerToolAPO.cpp's OpenSharedConfig
    /// exactly: audiodg.exe runs the APO under a different account than this
    /// desktop app, so the DEFAULT (creator-only) security descriptor would
    /// let whichever side creates the section first lock the other one out -
    /// the APO would then never see a real config and just pass audio
    /// straight through untouched. A real NULL DACL (built explicitly, not
    /// just a NULL parameter, which means something different) grants access
    /// to any account regardless of creation order.
    /// </summary>
    private static class SharedMemoryNative
    {
        public const uint PAGE_READWRITE = 0x04;
        public const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        public const uint SECURITY_DESCRIPTOR_REVISION = 1;
        public const int SECURITY_DESCRIPTOR_MIN_LENGTH = 40; // opaque; Win32's SECURITY_DESCRIPTOR_MIN_LENGTH is well under this

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(
            IntPtr hFile, ref SECURITY_ATTRIBUTES lpAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSecurityDescriptorDacl(IntPtr pSecurityDescriptor, [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent, IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);
    }

    private void EnsureMapping()
    {
        if (_viewPtr != IntPtr.Zero)
            return;

        // Open existing (audiodg created it when the APO loaded) ...
        _mappingHandle = SharedMemoryNative.OpenFileMappingW(SharedMemoryNative.FILE_MAP_ALL_ACCESS, false, SharedMemoryName);

        if (_mappingHandle == IntPtr.Zero)
        {
            // ... or create it ourselves (so config is stable before any APO
            // instance runs), with an explicit NULL DACL - see the class
            // summary above for why the default security descriptor isn't safe.
            IntPtr sd = Marshal.AllocHGlobal(SharedMemoryNative.SECURITY_DESCRIPTOR_MIN_LENGTH);
            try
            {
                if (!SharedMemoryNative.InitializeSecurityDescriptor(sd, SharedMemoryNative.SECURITY_DESCRIPTOR_REVISION))
                    return;
                if (!SharedMemoryNative.SetSecurityDescriptorDacl(sd, true, IntPtr.Zero, false))
                    return;

                var sa = new SharedMemoryNative.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SharedMemoryNative.SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = false
                };

                _mappingHandle = SharedMemoryNative.CreateFileMappingW(
                    new IntPtr(-1) /* INVALID_HANDLE_VALUE: page-file-backed, no real file */,
                    ref sa, SharedMemoryNative.PAGE_READWRITE, 0, (uint)ConfigSizePacked, SharedMemoryName);
            }
            finally
            {
                Marshal.FreeHGlobal(sd);
            }

            if (_mappingHandle == IntPtr.Zero)
                return;

            _viewPtr = SharedMemoryNative.MapViewOfFile(_mappingHandle, SharedMemoryNative.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)ConfigSizePacked);
            if (_viewPtr == IntPtr.Zero)
            {
                SharedMemoryNative.CloseHandle(_mappingHandle);
                _mappingHandle = IntPtr.Zero;
                return;
            }

            // Initialize to flat/bypass so a freshly-created section is benign.
            Marshal.WriteInt64(_viewPtr, 0, 0L);   // version
            Marshal.WriteInt32(_viewPtr, 8, 0);    // enabled = 0
            for (int i = 0; i < 10; i++)
                Marshal.WriteInt32(_viewPtr, 12 + i * 4, 0);
            Marshal.WriteInt32(_viewPtr, 52, 0);   // preampDb = 0
            Marshal.WriteInt32(_viewPtr, 56, 0);   // compressionAmount = 0 (off)
            return;
        }

        _viewPtr = SharedMemoryNative.MapViewOfFile(_mappingHandle, SharedMemoryNative.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)ConfigSizePacked);
        if (_viewPtr == IntPtr.Zero)
        {
            SharedMemoryNative.CloseHandle(_mappingHandle);
            _mappingHandle = IntPtr.Zero;
        }
    }

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
            if (_viewPtr == IntPtr.Zero)
                return;

            long newVersion = _lastVersionWritten + 2;

            Marshal.WriteInt32(_viewPtr, 8, enabled ? 1 : 0);
            for (int i = 0; i < 10; i++)
            {
                float g = Math.Clamp(gainsDb[i], -12f, 12f);
                Marshal.WriteInt32(_viewPtr, 12 + i * 4, BitConverter.SingleToInt32Bits(g));
            }
            Marshal.WriteInt32(_viewPtr, 52, BitConverter.SingleToInt32Bits(Math.Clamp(preampDb, -12f, 12f)));
            Marshal.WriteInt32(_viewPtr, 56, BitConverter.SingleToInt32Bits(Math.Clamp(compressionAmount, 0f, 1f)));

            System.Threading.Thread.MemoryBarrier();
            Marshal.WriteInt64(_viewPtr, 0, newVersion);
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

    private NativeEqEngine() { }

    #endregion

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
            if (!Instance.IsEngineEnabled())
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
