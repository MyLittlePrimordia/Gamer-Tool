using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GamerTool.Services;

/// <summary>How a display's EDID looks, as a cheap pre-flight check.</summary>
public enum EdidVerdict
{
    /// <summary>Nothing about the EDID looks wrong.</summary>
    Plausible,

    /// <summary>Something about the EDID is not credible, so it is not probed.</summary>
    Suspicious,

    /// <summary>There was no usable EDID to judge.</summary>
    Unreadable
}


public sealed class EdidReading
{
    public string Manufacturer { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public int? Year { get; init; }

    public double? DiagonalInches { get; init; }

    public int? Serial { get; init; }

    public EdidVerdict Verdict { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public string Summary =>
        Verdict == EdidVerdict.Unreadable
            ? "no EDID"
            : (Manufacturer.Length == 0 ? "unknown" : Manufacturer)
                + (ModelName.Length == 0 ? "" : " " + ModelName)
                + (PlausibleYear is null ? "" : ", " + PlausibleYear)
                + (DiagonalInches is null ? "" : ", " + DiagonalInches.Value.ToString("0") + "\"")
                + (Verdict == EdidVerdict.Suspicious ? "  [" + string.Join("; ", Reasons) + "]" : "");


    /// <summary>
    /// The year, but only when it decodes to something believable. A healthy AOC
    /// AG276QZ reports a year field that works out as 40722, so printing the raw
    /// decode would put a nonsense number in front of the user every time.
    /// </summary>
    public int? PlausibleYear =>
        Year is int y && y >= 1990 && y <= DateTime.Now.Year + 1 ? y : null;
}


public sealed class BrightnessReading
{
    public uint Minimum { get; init; }

    public uint Current { get; init; }

    public uint Maximum { get; init; }

    /// <summary>0 to 1 against the monitor's own reported range, not against 0 to 100.</summary>
    public double Normalised => Maximum > Minimum
        ? Math.Clamp((Current - Minimum) / (double)(Maximum - Minimum), 0.0, 1.0)
        : 0.0;

    public override string ToString() =>
        $"{Current} of {Maximum} (range {Minimum}-{Maximum}), {(Normalised * 100).ToString("0")}%";
}


public sealed class MonitorProbe
{
    public string DeviceName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    public bool IsExternal { get; init; }

    public EdidReading Edid { get; init; } = new();

    /// <summary>
    /// The last known reading. Settable rather than fixed because it is a live
    /// value: it changes as the app writes, and it is cleared when a display
    /// refuses a write so the row greys out instead of failing every drag.
    /// </summary>
    public BrightnessReading? Brightness { get; set; }

    public string? BlockedReason { get; init; }

    /// <summary>
    /// Whether THIS monitor can be driven, decided from THIS monitor's own probe
    /// and nothing else. Deliberately per monitor and never a shared setting: two
    /// displays on the same machine routinely differ, one answering VCP 0x10 and
    /// the other having no DDC/CI pathway at all, and the row has to go live for
    /// one while staying greyed out for the other. A single global flag here would
    /// quietly disable hardware brightness for a good monitor because a bad one
    /// was plugged in.
    /// </summary>
    public bool CanControlBacklight => BlockedReason is null && Brightness is not null;

    /// <summary>
    /// Why there is no reading, when there is not one. "No reply" on its own is
    /// useless: failing to get a handle, the monitor declining the code, and a
    /// timeout all look identical from the outside and have nothing in common.
    /// </summary>
    public string? NoReplyBecause { get; set; }

    public bool Probed => Brightness is not null;

    public string StatusLine
    {
        get
        {
            if (BlockedReason is not null)
            {
                return FriendlyName + " - not probed: " + BlockedReason;
            }

            if (Brightness is null)
            {
                return FriendlyName + " - no DDC/CI brightness reply";
            }

            return FriendlyName + " - brightness " + Brightness;
        }
    }
}


/// <summary>
/// Reads hardware brightness, and deliberately refuses to do very much else.
/// <para>
/// The one genuinely dangerous operation in this whole area is asking a monitor
/// for its DDC/CI capabilities string. A monitor that returns a malformed one
/// makes the kernel copy it into an undersized stack buffer, and Windows
/// bugchecks with KERNEL_SECURITY_CHECK_FAILURE 0x139, subcode 0x2. That was
/// reported against Power Display on a monitor whose EDID was itself malformed,
/// and the faulting frame is win32kfull!CPhysicalMonitorHandle::
/// DdcciGetCapabilitiesStringFromMonitor. So nothing here ever calls
/// GetMonitorCapabilitiesString or GetMonitorCapabilities, and nothing reads any
/// VCP code other than 0x10, the brightness one. Contrast, colour temperature,
/// rotation, input source and power all need the capabilities string, and are
/// therefore out of scope by design rather than by oversight.
/// </para>
/// <para>
/// Everything else here exists to make a monitor that misbehaves a boring
/// non-event: an EDID plausibility check before any bus traffic, one lock so two
/// threads never share the I2C channel, a hard timeout so a wedged monitor is
/// dropped instead of hanging a thread, and a persistent exclusion list so a
/// display that has already caused trouble is never asked again.
/// </para>
/// </summary>
public static class HardwareBrightness
{
    /// <summary>
    /// VCP 0x10 is the brightness code. It is the only code this class will
    /// ever read or write.
    /// </summary>
    private const byte VcpBrightness = 0x10;

    private const int ProbeTimeoutMs = 2000;

    /// <summary>
    /// A dropped first packet is normal on a lot of scalers, so one refusal is
    /// never treated as final.
    /// </summary>
    private const int RetryAttempts = 3;

    private const int RetryDelayMs = 80;

    /// <summary>
    /// The MCCS rule that matters most in practice: no more than one transaction
    /// per physical handle every 40ms. Some scalers drop packets that arrive too
    /// closely together, which looks exactly like a monitor that does not
    /// implement the code.
    /// </summary>
    private const int MinimumGapMs = 45;

    /// <summary>
    /// The gap the UI is asked to debounce to. A drag must not turn into a
    /// hundred writes a second: slow scalers drop those, and the symptom is a
    /// slider that appears to do nothing.
    /// </summary>
    public const int MinimumWriteGapMs = 120;

    /// <summary>One lock for the whole bus. DDC/CI is a shared, slow resource.</summary>
    private static readonly object Gate = new();

    private static readonly Dictionary<IntPtr, long> LastCallUtc = new();

    public static IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys = null)
    {
        List<MonitorProbe> found = new();
        HashSet<string> skip = new(
            excludedDeviceKeys ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach ((IntPtr hMonitor, string deviceName, string friendly, bool external, EdidReading edid) in EnumerateMonitors())
        {
            if (skip.Count > 0 && skip.Contains(deviceName))
            {
                found.Add(new MonitorProbe
                {
                    DeviceName = deviceName,
                    FriendlyName = friendly,
                    IsExternal = external,
                    Edid = edid,
                    BlockedReason = "on your exclusion list"
                });
                continue;
            }

            if (edid.Verdict != EdidVerdict.Plausible)
            {
                // The whole point of the pre-flight check. A display whose EDID is
                // not credible is exactly the display that took Power Display's
                // kernel down, and it is refused before a single byte goes over
                // the bus rather than after.
                found.Add(new MonitorProbe
                {
                    DeviceName = deviceName,
                    FriendlyName = friendly,
                    IsExternal = external,
                    Edid = edid,
                    BlockedReason = edid.Verdict == EdidVerdict.Unreadable
                        ? "no EDID to check"
                        : "EDID not credible: " + string.Join("; ", edid.Reasons)
                });
                continue;
            }

            if (!external)
            {
                // Deliberately NOT a block. Whether a panel counts as built in is
                // not something the display APIs can answer: the flag that looks
                // like it says so, DISPLAY_DEVICE_ATTACHED_TO_DESKTOP, is set on
                // any active display, which is how a perfectly good desktop
                // monitor ended up labelled internal here. A desktop monitor
                // behind WMI would simply fail, and a laptop panel that answers
                // DDC/CI is perfectly happy to be driven by it. So the only
                // question that matters is whether the monitor replies, and that
                // is asked directly.
            }

            (BrightnessReading? reading, string? why) = ReadBrightness(hMonitor);
            found.Add(new MonitorProbe
            {
                DeviceName = deviceName,
                FriendlyName = friendly,
                IsExternal = external,
                Edid = edid,
                Brightness = reading,
                NoReplyBecause = why
            });
        }

        return found;
    }


    /// <summary>
    /// Writes VCP 0x10 on one display. Like the read, the handle is taken, used
    /// and dropped inside a single call so no handle is ever held across a sleep,
    /// a monitor waking up or a topology change.
    /// <para>
    /// The value is clamped to the range the monitor itself reported rather than
    /// to 0-100, because plenty of panels do not use that range and writing
    /// outside it is how a slider ends up doing nothing.
    /// </para>
    /// </summary>
    public static bool TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why)
    {
        why = null;

        uint clamped = Math.Clamp(value, minimum, maximum);

        // Callers debounce, so this rarely fires. It is here so that a burst from
        // anywhere else still cannot outrun the scaler.
        if (LastWriteUtc.TryGetValue(deviceName, out long last))
        {
            long sinceMs = (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond;
            if (sinceMs < MinimumWriteGapMs)
            {
                Thread.Sleep(MinimumWriteGapMs - (int)sinceMs);
            }
        }

        LastWriteUtc[deviceName] = DateTime.UtcNow.Ticks;

        IntPtr? handle = FindMonitorHandle(deviceName);
        if (handle is null)
        {
            why = "the display is no longer connected";
            return false;
        }

        Task<bool> write = Task.Run(() =>
        {
            lock (Gate)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle.Value, out uint count) || count == 0)
                {
                    return false;
                }

                int stride = Marshal.SizeOf<PHYSICAL_MONITOR>();
                IntPtr buffer = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    if (!GetPhysicalMonitorsFromHMONITOR(handle.Value, count, buffer))
                    {
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        PHYSICAL_MONITOR monitor = Marshal.PtrToStructure<PHYSICAL_MONITOR>(IntPtr.Add(buffer, i * stride));
                        if (monitor.hPhysicalMonitor == IntPtr.Zero)
                        {
                            continue;
                        }

                        bool ok = false;
                        for (int attempt = 0; attempt < RetryAttempts && !ok; attempt++)
                        {
                            if (attempt > 0)
                            {
                                Thread.Sleep(RetryDelayMs);
                            }

                            RespectMinimumGap(monitor.hPhysicalMonitor);
                            ok = SetVCPFeature(monitor.hPhysicalMonitor, VcpBrightness, clamped);
                        }

                        if (ok)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    DestroyPhysicalMonitors(count, buffer);
                    Marshal.FreeHGlobal(buffer);
                }
            }
        });

        if (!write.Wait(ProbeTimeoutMs))
        {
            why = "the monitor did not acknowledge the write in time, so the bus is treated as wedged";
            return false;
        }

        if (!write.Result)
        {
            why = "the monitor refused the brightness value";
        }

        return write.Result;
    }


    private static IntPtr? FindMonitorHandle(string deviceName)
    {
        foreach ((IntPtr handle, string device, string friendly, bool external, EdidReading edid) in EnumerateMonitors())
        {
            if (string.Equals(device, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return handle;
            }
        }

        return null;
    }


    private static readonly Dictionary<string, long> LastWriteUtc = new();


    /// <summary>
    /// Waits out whatever is left of the mandatory gap since the last transaction
    /// on this handle. Callers already hold <see cref="Gate"/>, so the table
    /// needs no lock of its own.
    /// </summary>
    private static void RespectMinimumGap(IntPtr handle)
    {
        long now = DateTime.UtcNow.Ticks;
        if (LastCallUtc.TryGetValue(handle, out long last))
        {
            long elapsedMs = (now - last) / TimeSpan.TicksPerMillisecond;
            if (elapsedMs < MinimumGapMs)
            {
                Thread.Sleep(MinimumGapMs - (int)elapsedMs);
            }
        }

        LastCallUtc[handle] = DateTime.UtcNow.Ticks;
    }


    /// <summary>
    /// One VCP 0x10 read, on a worker thread with a hard deadline, reporting why
    /// it came back empty-handed. A monitor whose I2C engine has wedged leaves the
    /// worker blocked and is reported as a timeout, which is what it is. Nothing
    /// is retried in the same session.
    /// </summary>
    private static (BrightnessReading? Reading, string? Why) ReadBrightness(IntPtr hMonitor)
    {
        Task<(BrightnessReading? Reading, string? Why)> read = Task.Run<(BrightnessReading? Reading, string? Why)>(() =>
        {
            lock (Gate)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count))
                {
                    return ((BrightnessReading?)null, "the driver would not list physical monitors (error " + Marshal.GetLastWin32Error() + ")");
                }

                if (count == 0)
                {
                    return ((BrightnessReading?)null, "the driver reports no physical monitor on this display");
                }

                PHYSICAL_MONITOR[] monitors = new PHYSICAL_MONITOR[count];
                int stride = Marshal.SizeOf<PHYSICAL_MONITOR>();
                // The struct carries a by-value string, so it is not blittable and
                // an [Out] array of them silently fails to copy the handles back:
                // every hPhysicalMonitor then reads as zero and the monitor looks
                // like it declined. A raw buffer with the struct read out of it by
                // hand is the only version of this that actually works.
                IntPtr buffer = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                    {
                        return ((BrightnessReading?)null, "no physical monitor handles (error " + Marshal.GetLastWin32Error() + ")");
                    }

                    List<string> declined = new();

                    for (int i = 0; i < count; i++)
                    {
                        PHYSICAL_MONITOR monitor = Marshal.PtrToStructure<PHYSICAL_MONITOR>(IntPtr.Add(buffer, i * stride));
                        monitors[i] = monitor;

                        if (monitor.hPhysicalMonitor == IntPtr.Zero)
                        {
                            // The driver listed a physical monitor but handed back
                            // no handle, which is how it says there is no DDC/CI
                            // pathway to this display. Nothing can be asked of it,
                            // and retrying would be pointless.
                            declined.Add("\"" + monitor.szPhysicalMonitorDescription
                                + "\" has no DDC/CI handle from the driver, so this display has no DDC/CI link (a dock, hub, KVM or"
                                + " adapter in the path will do this, as will a GPU driver with no DDC/CI support)");
                            continue;
                        }

                        // GetLastError has to be read on the line after the call.
                        // Building the message first lets the string allocation
                        // clobber it, and the code that comes back is then from
                        // whatever ran last rather than from the I2C failure.
                        RespectMinimumGap(monitor.hPhysicalMonitor);
                        bool ok = GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor, VcpBrightness, out uint current, out uint max);
                        int error = ok ? 0 : Marshal.GetLastWin32Error();

                        // AOC scalers are documented to drop the first AUX packet
                        // and to be slow to answer, so a single refusal is not
                        // treated as final. Two short retries with a pause settle
                        // the difference between a dropped packet and a monitor
                        // that genuinely does not implement the code.
                        for (int attempt = 1; attempt <= RetryAttempts && !ok; attempt++)
                        {
                            Thread.Sleep(RetryDelayMs);
                            RespectMinimumGap(monitor.hPhysicalMonitor);
                            ok = GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor, VcpBrightness, out current, out max);
                            if (!ok)
                            {
                                error = Marshal.GetLastWin32Error();
                            }
                        }

                        if (ok && max > 0)
                        {
                            uint min = ReadMinimum(monitor.hPhysicalMonitor);
                            return (new BrightnessReading { Minimum = min, Current = current, Maximum = max }, null);
                        }

                        declined.Add("\"" + monitor.szPhysicalMonitorDescription + "\" declined 0x10 after "
                            + RetryAttempts + " attempts, error 0x" + unchecked((uint)error).ToString("X8"));
                    }

                    return ((BrightnessReading?)null, declined.Count == 0
                        ? "no usable physical monitor handle"
                        : string.Join("; ", declined));
                }
                finally
                {
                    DestroyPhysicalMonitors(count, buffer);
                    Marshal.FreeHGlobal(buffer);
                }
            }
        });

        if (!read.Wait(ProbeTimeoutMs))
        {
            TraceLog.Write("HWB probe timed out after " + ProbeTimeoutMs + "ms, monitor will be left alone");
            return (null, "no answer within " + ProbeTimeoutMs + "ms, so the bus is treated as wedged");
        }

        return read.Result;
    }


    /// <summary>
    /// The high level call reports the monitor's own range, which is the number
    /// writes must be clamped to. Monitors that are not 0 to 100 are common, and
    /// rescaling against the wrong range is a real bug in a shipped tool.
    /// </summary>
    private static uint ReadMinimum(IntPtr physicalMonitor)
    {
        return GetMonitorBrightness(physicalMonitor, out uint min, out _, out _) ? min : 0;
    }


    /// <summary>
    /// Every display, as a handle plus whatever the driver will tell us about it
    /// without going near the bus. The handle comes from EnumDisplayMonitors and
    /// is matched back to its name with GetMonitorInfo, which is the only
    /// documented way to get from a \\.\DISPLAYx name to an HMONITOR.
    /// </summary>
    private static IEnumerable<(IntPtr, string, string, bool, EdidReading)> EnumerateMonitors()
    {
        List<(IntPtr Handle, string Device)> displays = new();

        MonitorEnumProc collect = (IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data) =>
        {
            MONITORINFOEX info = default;
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMonitor, ref info))
            {
                displays.Add((hMonitor, info.Device));
            }

            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero))
        {
            yield break;
        }

        foreach ((IntPtr handle, string device) in displays)
        {
            string friendly = device;
            string driverEdid = string.Empty;
            bool external = true;

            for (uint i = 0; ; i++)
            {
                DISPLAY_DEVICE md = NewDisplayDevice();
                if (!EnumDisplayDevices(device, i, ref md, EddGetEdid))
                {
                    break;
                }

                if (i == 0)
                {
                    friendly = md.DeviceString.Length == 0 ? device : md.DeviceString;
                    driverEdid = md.DeviceID;
                    external = (md.StateFlags & DisplayDeviceAttachedToDesktop) == 0;
                }
            }

            // Asked for outside the loop on purpose. This driver refuses the
            // EDD_GET_EDID request outright, so the enumeration above can come
            // back empty, and a fallback that only runs when the driver answered
            // is a fallback that never runs.
            yield return (handle, device, friendly, external, ReadEdid(driverEdid, friendly));
        }
    }


    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);


    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;

        public RECT Monitor;

        public RECT Work;

        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }


    private const uint EddGetEdid = 0x00000001;
    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;


    private static DISPLAY_DEVICE NewDisplayDevice()
    {
        DISPLAY_DEVICE d = default;
        d.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        d.DeviceName = string.Empty;
        d.DeviceString = string.Empty;
        d.DeviceID = string.Empty;
        d.DeviceKey = string.Empty;
        return d;
    }


    private static EdidReading ReadEdid(string hex, string modelName)
    {
        if (TryHex(hex, out byte[]? fromDriver) && fromDriver is not null)
        {
            return ParseEdid(fromDriver, modelName);
        }

        // Plenty of drivers decline the EDD_GET_EDID request and still publish
        // the block in the registry, which is where Windows itself caches it. One
        // real machine here reports "Generic PnP Monitor" through the driver and
        // holds a perfectly good 128 byte EDID under Enum\DISPLAY, so without this
        // the pre-flight check would refuse to look at a perfectly ordinary panel.
        byte[]? fromRegistry = ReadRegistryEdid(modelName);
        return fromRegistry is null
            ? new EdidReading { ModelName = modelName, Verdict = EdidVerdict.Unreadable }
            : ParseEdid(fromRegistry, modelName);
    }


    /// <summary>
    /// The EDID Windows cached for the display panels, straight out of
    /// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY. A registry read, so it costs
    /// nothing on the bus and cannot be the thing that upsets a monitor.
    /// </summary>
    private static byte[]? ReadRegistryEdid(string modelName)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? root =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");

            if (root is null)
            {
                LastRegistryError = "OpenSubKey(Enum\\DISPLAY) returned null";
                return null;
            }

            List<(byte[] Edid, string Name)> found = new();

            foreach (string panel in root.GetSubKeyNames())
            {
                using Microsoft.Win32.RegistryKey? panelKey = root.OpenSubKey(panel);
                if (panelKey is null)
                {
                    continue;
                }

                foreach (string instance in panelKey.GetSubKeyNames())
                {
                    using Microsoft.Win32.RegistryKey? instanceKey = panelKey.OpenSubKey(instance);
                    using Microsoft.Win32.RegistryKey? parameters = instanceKey?.OpenSubKey("Device Parameters");

                    if (parameters is null)
                    {
                        continue;
                    }

                    // The value reads back as byte[] in theory and as Object[] in
                    // practice, which is exactly the sort of thing a silent
                    // `is byte[]` test throws away. Both shapes are accepted.
                    byte[]? bytes = parameters.GetValue("EDID") switch
                    {
                        byte[] direct when direct.Length >= 128 => direct,
                        object[] boxed when boxed.Length >= 128 => boxed
                            .Select(v => v is byte b ? b : (byte)0)
                            .ToArray(),
                        _ => null
                    };

                    if (bytes is null)
                    {
                        continue;
                    }

                    found.Add((bytes, FriendlyNameOf(instanceKey?.GetValue("DeviceDesc") as string)));
                }
            }

            if (found.Count == 0)
            {
                return null;
            }

            // Windows keeps one entry per connection, so a single monitor commonly
            // appears here two or three times over, and the whole EDID is sometimes
            // present on one instance and only the 128 byte base block on another.
            // When every entry names the same panel they are all the same display
            // and the first one will do. Only when there are genuinely different
            // panels does the name have to choose, and that match is loose on
            // purpose: a miss costs us the pre-flight check, never a wrong write.
            string[] distinct = found
                .Select(f => f.Name)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (distinct.Length <= 1)
            {
                return found[0].Edid;
            }

            foreach ((byte[] edid, string name) in found)
            {
                if (name.Length > 0
                    && (modelName.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(modelName, StringComparison.OrdinalIgnoreCase)))
                {
                    return edid;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            LastRegistryError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }


    /// <summary>
    /// A DeviceDesc reads as "@monitor.inf,%pnpmonitor.devicedesc%;Generic PnP
    /// Monitor", so the usable name is whatever follows the last semicolon.
    /// </summary>
    private static string FriendlyNameOf(string? deviceDesc)
    {
        if (string.IsNullOrWhiteSpace(deviceDesc))
        {
            return string.Empty;
        }

        int cut = deviceDesc.LastIndexOf(';');
        return cut >= 0 && cut < deviceDesc.Length - 1
            ? deviceDesc[(cut + 1)..].Trim()
            : deviceDesc.Trim();
    }


    /// <summary>Why the registry fallback gave up, for diagnostics. Empty when it worked.</summary>
    public static string LastRegistryError { get; private set; } = string.Empty;


    private static EdidReading ParseEdid(byte[] edid, string modelName)
    {
        if (edid.Length < 128)
        {
            return new EdidReading { ModelName = modelName, Verdict = EdidVerdict.Unreadable };
        }

        List<string> reasons = new();

        for (int i = 0; i < 8; i++)
        {
            byte want = i == 0 || i == 7 ? (byte)0x00 : (byte)0xFF;
            if (edid[i] != want)
            {
                reasons.Add("bad header");
                break;
            }
        }

        // Bytes 8 and 9 hold the manufacturer id as three five bit letters.
        int packed = (edid[8] << 8) | edid[9];
        string maker = string.Empty;
        for (int shift = 10; shift >= 0; shift -= 5)
        {
            int code = (packed >> shift) & 0x1F;
            maker += code == 0 ? '?' : (char)(code + 64);
            if (code is < 1 or > 26)
            {
                reasons.Add("manufacturer id '" + maker + "' is not letters");
                maker = string.Empty;
                break;
            }
        }

        int yearRaw = (edid[19] << 8) | (edid[12] << 8) | edid[11];
        int? year = yearRaw == 0 ? null : yearRaw < 50 ? 1990 + yearRaw : 1900 + yearRaw;

        // The year is deliberately NOT one of the blocking reasons. A healthy
        // AOC AG276QZ reports a year field that decodes to 2066, and plenty of
        // other panels report nonsense in it, so treating a bad year as a
        // dangerous monitor would throw away working hardware. It is reported
        // because it is interesting, not because it is a verdict.

        int widthCm = edid[21];
        int heightCm = edid[22];
        double? diagonal = widthCm > 0 && heightCm > 0
            ? Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54
            : null;
        if (diagonal is not null && (diagonal < 5 || diagonal > 150))
        {
            reasons.Add("panel size " + diagonal.Value.ToString("0") + "\" is not believable");
        }

        int serial = (edid[15] << 8) | edid[14];
        if (yearRaw == 0 && widthCm == 0 && serial == 0)
        {
            return new EdidReading
            {
                ModelName = modelName,
                Verdict = EdidVerdict.Unreadable,
                Reasons = new[] { "EDID is all zeroes" }
            };
        }

        return new EdidReading
        {
            Manufacturer = maker,
            ModelName = modelName,
            Year = year,
            DiagonalInches = diagonal,
            Serial = serial == 0 ? null : serial,
            Verdict = reasons.Count == 0 ? EdidVerdict.Plausible : EdidVerdict.Suspicious,
            Reasons = reasons
        };
    }


    private static bool TryHex(string hex, out byte[]? bytes)
    {
        bytes = null;
        hex = hex.Trim();
        if (hex.Length < 256 || hex.Length % 2 != 0)
        {
            return false;
        }

        byte[] buffer = new byte[hex.Length / 2];
        for (int i = 0; i < buffer.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out buffer[i]))
            {
                return false;
            }
        }

        bytes = buffer;
        return true;
    }


    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }


    /// <summary>
    /// The real layout puts the handle FIRST and the description after it, which
    /// is the opposite of how it reads in the documentation's prose and is easy
    /// to get backwards: a struct declared description-first puts the handle
    /// 256 bytes in, reads the description's first characters as a pointer, and
    /// every monitor then looks like it declined.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);


    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc callback, IntPtr data);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, IntPtr monitors);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, IntPtr monitors);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, out uint current, out uint max);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint minimum, out uint current, out uint maximum);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
}
