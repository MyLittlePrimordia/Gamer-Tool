using System;
using System.Collections.Concurrent;
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

    /// <summary>Bytes 10 and 11, little endian. Together with the maker this is the model's short id.</summary>
    public ushort? ProductCode { get; init; }

    public int? Year { get; init; }

    /// <summary>Manufacture week, 1 to 54. Null when the panel used the model-year flag or a nonsense value.</summary>
    public int? Week { get; init; }

    public double? DiagonalInches { get; init; }

    /// <summary>
    /// Bytes 12 to 15, little endian. Deliberately absent from
    /// <see cref="Summary"/> and from every log line: a serial number is an
    /// identifier, and this text gets pasted into public bug reports.
    /// </summary>
    public int? Serial { get; init; }

    public EdidVerdict Verdict { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public string Summary =>
        Verdict == EdidVerdict.Unreadable
            ? "no EDID"
            : (Manufacturer.Length == 0 ? "unknown" : Manufacturer)
                + (ProductCode is null ? "" : " " + ProductCode.Value.ToString("X4"))
                + (ModelName.Length == 0 ? "" : " " + ModelName)
                + (PlausibleYear is null ? "" : ", " + PlausibleYear)
                + (Week is null ? "" : " w" + Week.Value.ToString("00"))
                + (DiagonalInches is null ? "" : ", " + DiagonalInches.Value.ToString("0") + "\"")
                + (Verdict == EdidVerdict.Suspicious ? "  [" + string.Join("; ", Reasons) + "]" : "");


    /// <summary>
    /// The year, but only when it decodes to something believable. Kept as a
    /// separate step because the raw field is still reported in the struct: a
    /// panel claiming to be from 2044 is worth seeing, as long as it is not
    /// shown to the user as though it were true.
    /// </summary>
    public int? PlausibleYear =>
        Year is int y && y >= 1990 && y <= DateTime.Now.Year + 1 ? y : null;
}


/// <summary>
/// The code type a monitor reports alongside a VCP value, from
/// <c>LPMC_VCP_CODE_TYPE</c>. Declared as uint because that is the underlying
/// type; the values are fixed by MCCS and are not ours to renumber.
/// </summary>
public enum VcpCodeType : uint
{
    Unknown = 0xFFFFFFFF,

    /// <summary>The panel reports the value but will not hold it.</summary>
    Momentary = 0,

    /// <summary>The panel holds a value that can be set and read back.</summary>
    SetParameter = 1,

    /// <summary>As SetParameter, and the original value is restorable by the panel.</summary>
    SetParameterWithOriginal = 2
}


/// <summary>
/// Turns the numeric DDC/CI failures into something a person can act on.
/// <para>
/// A failed I2C transaction does not come back as a Win32 error. The monitor
/// class driver returns its own status block with 0xC026 in the high word, and
/// reading that as a plain error number gives something like "3223725441", which
/// is a value nobody can look up and every reader has to guess at.
/// </para>
/// <para>
/// A correction worth recording, because the obvious assumption is wrong: these
/// four are not in winerror.h. 0xC0262580 through 0xC0262583 are the physical
/// monitor provider's own I2C status codes, and the low word is the specific
/// condition within it. So the mapping keys off the high word and the names
/// describe the I2C condition rather than pretending to be Win32.
/// </para>
/// </summary>
public static class DdcErrors
{
    /// <summary>The high word every I2C status from the monitor provider carries.</summary>
    private const uint I2cStatusHigh = 0xC0260000;

    /// <summary>
    /// A name for the error, or null when it is not one we recognise. Never
    /// invents a name: an unmapped code is reported as hex so it can be looked
    /// up rather than guessed at.
    /// </summary>
    public static string? Name(int error)
    {
        if (error == 0)
        {
            return "SUCCESS";
        }

        uint value = unchecked((uint)error);

        if ((value & 0xFFFF0000) == I2cStatusHigh)
        {
            return (value & 0xFFFF) switch
            {
                0x2580 => "I2C_DEVICE_DOES_NOT_EXIST",
                0x2581 => "I2C_NOT_SUPPORTED",
                0x2582 => "I2C_ERROR_TRANSMITTING_DATA",
                0x2583 => "I2C_ERROR_RECEIVING_DATA",
                _ => null
            };
        }

        return value switch
        {
            0x00000001 => "ERROR_INVALID_FUNCTION",
            0x00000005 => "ERROR_ACCESS_DENIED",
            0x00000006 => "ERROR_INVALID_HANDLE",
            0x00000087 => "ERROR_INVALID_PARAMETER",
            0x0000010E => "ERROR_INVALID_WINDOW_HANDLE",
            0x0000011B => "ERROR_NOT_ENOUGH_MEMORY",
            0x000004D9 => "ERROR_NOT_FOUND",
            0x80004005 => "E_FAIL",
            _ => null
        };
    }

    /// <summary>The error as it should appear in a log: hex, plus a name if known.</summary>
    public static string Describe(int error)
    {
        if (error == 0)
        {
            return "0x00000000 SUCCESS";
        }

        string? name = Name(error);
        return "0x" + unchecked((uint)error).ToString("X8") + (name is null ? string.Empty : " " + name);
    }
}


public sealed class BrightnessReading
{
    public uint Minimum { get; init; }

    public uint Current { get; init; }

    public uint Maximum { get; init; }

    /// <summary>
    /// The VCP code type the monitor reported alongside the values. MCCS
    /// defines momentary, set-parameter and set-parameter-with-original, and
    /// knowing which one answered is the difference between a read that is
    /// trustworthy and one that is reporting a value the monitor never held.
    /// </summary>
    public VcpCodeType CodeType { get; init; } = VcpCodeType.Unknown;

    /// <summary>0 to 1 against the monitor's own reported range, not against 0 to 100.</summary>
    public double Normalised => Maximum > Minimum
        ? Math.Clamp((Current - Minimum) / (double)(Maximum - Minimum), 0.0, 1.0)
        : 0.0;

    public override string ToString() =>
        $"{Current} of {Maximum} (range {Minimum}-{Maximum}), {(Normalised * 100).ToString("0")}%"
        + " type=" + CodeType;
}


/// <summary>
/// What the monitor said when we asked. The point of enumerating these is that
/// "it did not work" is not one answer.
/// <para>
/// Busy used to be folded into the same false as a refusal, which cost the
/// feature twice in one session on real hardware: a second caller that arrived
/// while the first was still on the bus was told the monitor had declined, the
/// decline was counted towards the permanent exclusion, and the row greyed out
/// for a display that had in fact been asked nothing at all.
/// </para>
/// </summary>
public enum BusOutcome
{
    /// <summary>Nothing ran, because another call already held the bus.</summary>
    Busy,

    /// <summary>The driver listed a physical monitor but gave it no DDC/CI handle.</summary>
    NoDdcPathway,

    /// <summary>The monitor answered, and the answer is usable.</summary>
    Ok,

    /// <summary>The monitor answered, but declined VCP 0x10.</summary>
    Refused,

    /// <summary>Nothing came back before the deadline.</summary>
    TimedOut,

    /// <summary>Something below the monitor failed: no physical monitor, bad handle.</summary>
    Failed
}


/// <summary>Everything one bus transaction produced, for the log and the tests.</summary>
public sealed class BusResult
{
    public BusOutcome Outcome { get; init; }

    public BrightnessReading? Reading { get; init; }

    /// <summary>Written in words, for the log and for Copy diagnostics.</summary>
    public string? Why { get; init; }

    /// <summary>Win32 error from the failing call, or 0. Never logged without its name.</summary>
    public int Win32Error { get; init; }

    /// <summary>How many transactions it took to get this answer.</summary>
    public int Attempts { get; init; }

    /// <summary>Which physical monitor within the handle answered, when there was one.</summary>
    public string? PhysicalMonitor { get; init; }

    public int ElapsedMs { get; init; }

    /// <summary>True only for a genuine answer from the display itself.</summary>
    public bool IsRealRefusal => Outcome is BusOutcome.Refused or BusOutcome.NoDdcPathway;
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

    /// <summary>
    /// How the last bus transaction on this display ended. The refusal counter
    /// and the log both need to be able to tell a real answer from a skipped one,
    /// and neither can do that from a reason string.
    /// </summary>
    public BusOutcome Outcome { get; set; } = BusOutcome.Failed;

    /// <summary>Win32 error from the failing call, with a name where one is known.</summary>
    public string? LastError { get; set; }

    /// <summary>Transactions used by the last read, and how long the probe took.</summary>
    public int Attempts { get; set; }

    public int ProbeMs { get; set; }

    /// <summary>The physical monitor description the driver reported, if any.</summary>
    public string? PhysicalMonitor { get; set; }

    /// <summary>
    /// Whether the EDID came from the display driver or from the registry cache.
    /// Worth recording because the two disagree often enough: a driver that
    /// answers "Generic PnP Monitor" still publishes a perfectly good block under
    /// Enum\DISPLAY, and knowing which one was used is the difference between
    /// "the driver gave us nothing" and "the driver gave us the wrong name".
    /// </summary>
    public string EdidSource { get; set; } = "none";

    /// <summary>How the display is wired up, and whether HDR is on. Read only.</summary>
    public DisplayLink Link { get; set; } = new();

    /// <summary>How a display is referred to in the log, given drivers say "Generic PnP Monitor".</summary>
    public string FriendlyOrDevice() =>
        FriendlyName.Length > 0 && !FriendlyName.Equals(DeviceName, StringComparison.OrdinalIgnoreCase)
            ? FriendlyName + " (" + DeviceName + ")"
            : DeviceName;

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
/// What the OS says about how a display is connected. Read only, and never used
/// to decide anything: it exists so the log can say "DisplayPort, HDR off"
/// instead of leaving someone to guess whether the monitor or the cable is at
/// fault.
/// </summary>
public sealed class DisplayLink
{
    /// <summary>As the compositor names the target. Often the same generic string the driver gives.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>DisplayPort, HDMI, DVI and so on, or "unknown".</summary>
    public string Connection { get; init; } = "unknown";

    /// <summary>True when the compositor is driving the panel in HDR.</summary>
    public bool HdrActive { get; init; }

    /// <summary>True when the OS reports the display as advanced colour capable.</summary>
    public bool HdrSupported { get; init; }

    /// <summary>
    /// Whether the compositor would answer at all. It does not always: the
    /// advanced colour query is refused on some driver and build combinations.
    /// Without the flag the log would quietly print "DisplayPort" and read as
    /// though HDR had been ruled out when in fact nobody asked, which is the
    /// exact confusion this whole section exists to remove.
    /// </summary>
    public bool HdrKnown { get; init; }

    /// <summary>
    /// Whether this link belongs to a given display. Name matching is all there
    /// is, because neither API hands out a shared identifier: the dxva2 side
    /// knows the monitor as \\.\DISPLAYx and the compositor side knows it as a
    /// friendly name. A miss is harmless, it only leaves the link unreported.
    /// </summary>
    public bool Matches(string deviceName, string friendlyName) =>
        (FriendlyName.Length > 0
            && (FriendlyName.Equals(friendlyName, StringComparison.OrdinalIgnoreCase)
                || friendlyName.Contains(FriendlyName, StringComparison.OrdinalIgnoreCase)))
        || (FriendlyName.Length > 0 && FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        Connection
        + (HdrKnown
            ? HdrActive ? ", HDR ON" : HdrSupported ? ", HDR capable (off)" : ", SDR only"
            : ", HDR unknown");
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

    /// <summary>
    /// Set while a call is inside its worker, so only ever one is in there.
    /// <para>
    /// The timeout on a wedged monitor abandons the worker but cannot cancel the
    /// I2C call inside it, and that worker goes on holding the bus lock for as
    /// long as the hardware takes to answer, which for a wedged panel is forever.
    /// Without this, every later call started its own worker, and every one of
    /// them blocked on the lock and was itself abandoned, so a drag across the
    /// slider queued a thread pool thread per step and none of them ever came
    /// back. Refusing new work while one is outstanding caps the damage at a
    /// single consumed thread and reports the bus as busy, which is true.
    /// </para>
    /// </summary>
    private static int _busBusy;

    private static bool TryEnterBus()
    {
        return Interlocked.Exchange(ref _busBusy, 1) == 0;
    }

    private static void LeaveBus()
    {
        Interlocked.Exchange(ref _busBusy, 0);
    }

    private static readonly Dictionary<IntPtr, long> LastCallUtc = new();

    public static IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys = null)
    {
        List<MonitorProbe> found = new();
        HashSet<string> skip = new(
            excludedDeviceKeys ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Read once for the whole pass rather than per monitor. It cannot change
        // in the middle of a probe, and asking the compositor per display is
        // asking it the same question twice.
        IReadOnlyList<DisplayLink> links = ReadDisplayLinks();

        foreach ((IntPtr hMonitor, string deviceName, string friendly, bool external, EdidReading edid, string edidSource, string hardwareId) in EnumerateMonitors())
        {
            // A single active display is the overwhelmingly common case and there
            // is nothing to confuse it with, so it is reported rather than
            // dropped. With several, only a name that actually matches is used.
            DisplayLink link = links.FirstOrDefault(l => l.Matches(deviceName, friendly))
                ?? (links.Count == 1 ? links[0] : new DisplayLink());

            if (skip.Count > 0 && skip.Contains(deviceName))
            {
                found.Add(new MonitorProbe
                {
                    DeviceName = deviceName,
                    FriendlyName = friendly,
                    IsExternal = external,
                    Edid = edid,
                    EdidSource = edidSource,
                    Link = link,
                    BlockedReason = "on your exclusion list"
                });
                continue;
            }

            if (edid.Verdict != EdidVerdict.Plausible)
            {
                // The whole point of the pre-flight check. A display whose EDID is
                // not credible is exactly the display that took Power Display's
                // kernel down, and it is refused before a single byte goes over
                // the bus rather than after. The conditions are deliberately
                // narrow and are not loosened on the strength of one odd panel:
                // the year is reported, never used as a verdict.
                found.Add(new MonitorProbe
                {
                    DeviceName = deviceName,
                    FriendlyName = friendly,
                    IsExternal = external,
                    Edid = edid,
                    EdidSource = edidSource,
                    Link = link,
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

            BusResult bus = ReadBrightness(hMonitor);
            found.Add(new MonitorProbe
            {
                DeviceName = deviceName,
                FriendlyName = friendly,
                IsExternal = external,
                Edid = edid,
                EdidSource = edidSource,
                Link = link,
                Brightness = bus.Reading,
                NoReplyBecause = bus.Why,
                Outcome = bus.Outcome,
                LastError = bus.Win32Error == 0 ? null : DdcErrors.Describe(bus.Win32Error),
                Attempts = bus.Attempts,
                ProbeMs = bus.ElapsedMs,
                PhysicalMonitor = bus.PhysicalMonitor
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
    /// <para>
    /// A busy bus is not a refusal. It returns <see cref="BusOutcome.Busy"/> so
    /// nothing counts it against the display, and the caller is expected to send
    /// the value again once the first attempt has finished.
    /// </para>
    /// </summary>
    public static BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error)
    {
        why = null;
        win32Error = 0;

        if (!TryEnterBus())
        {
            why = "skipped: another call was still on the bus";
            return BusOutcome.Busy;
        }

        try
        {
            return WriteBrightness(deviceName, value, minimum, maximum, out why, out win32Error);
        }
        finally
        {
            LeaveBus();
        }
    }

    /// <summary>The body of a write, with the bus already claimed.</summary>
    private static BusOutcome WriteBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error)
    {
        why = null;
        win32Error = 0;

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
            return BusOutcome.Failed;
        }

        Task<(bool Ok, int Error)> write = Task.Run<(bool Ok, int Error)>(() =>
        {
            lock (Gate)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle.Value, out uint count) || count == 0)
                {
                    return (false, Marshal.GetLastWin32Error());
                }

                int stride = Marshal.SizeOf<PHYSICAL_MONITOR>();
                IntPtr buffer = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    if (!GetPhysicalMonitorsFromHMONITOR(handle.Value, count, buffer))
                    {
                        return (false, Marshal.GetLastWin32Error());
                    }

                    int lastError = 0;

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

                            // Read the error on the line after the call, before
                            // anything else can allocate. The write path used to
                            // record nothing at all, so a refused write and a
                            // write that never left the machine looked identical.
                            ok = SetVCPFeature(monitor.hPhysicalMonitor, VcpBrightness, clamped);
                            if (!ok)
                            {
                                lastError = Marshal.GetLastWin32Error();
                            }
                        }

                        if (ok)
                        {
                            return (true, 0);
                        }
                    }

                    return (false, lastError);
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
            return BusOutcome.TimedOut;
        }

        (bool ok, int error) = write.Result;
        if (ok)
        {
            return BusOutcome.Ok;
        }

        win32Error = error;
        why = "the monitor refused the brightness value (" + DdcErrors.Describe(error) + ")";
        return BusOutcome.Refused;
    }


    /// <summary>
    /// The HMONITOR for a device, from a light enumeration.
    /// <para>
    /// The full <see cref="EnumerateMonitors"/> reads the EDID, and the EDID
    /// path walks the registry. Doing that on every slider tick meant a drag
    /// across the brightness control re-read the display list and a chunk of
    /// HKLM dozens of times, all of it to arrive at the same handle. This asks
    /// only for names, and caches the answer for the length of a probe.
    /// </para>
    /// </summary>
    private static IntPtr? FindMonitorHandle(string deviceName)
    {
        foreach ((IntPtr handle, string device) in EnumerateHandles())
        {
            if (string.Equals(device, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return handle;
            }
        }

        return null;
    }

    private static IEnumerable<(IntPtr Handle, string Device)> EnumerateHandles()
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
            yield return (handle, device);
        }
    }


    /// <summary>
    /// When each device was last sent a brightness value, keyed by device name.
    /// <para>
    /// Unlike <see cref="LastCallUtc"/>, this table is touched before the worker
    /// task takes <see cref="Gate"/> rather than inside it, so it cannot rely on
    /// that lock. Two monitors dragged at the same time put two threads here at
    /// once, and a plain Dictionary can be left structurally broken by that, which
    /// shows up as a spin inside the runtime rather than as an exception anybody
    /// can catch.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> LastWriteUtc = new();


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
    /// One VCP 0x10 read, on a worker thread with a hard deadline, reporting how
    /// it ended rather than only that it did. A monitor whose I2C engine has
    /// wedged leaves the worker blocked and is reported as a timeout, which is
    /// what it is.
    /// </summary>
    private static BusResult ReadBrightness(IntPtr hMonitor)
    {
        if (!TryEnterBus())
        {
            // Deliberately not a refusal and not a no-reply verdict. Nothing was
            // asked of the monitor, so nothing can be concluded from this, and
            // letting it read as a decline is what greyed the row out on hardware
            // that had not actually been spoken to.
            return new BusResult
            {
                Outcome = BusOutcome.Busy,
                Why = "skipped: another call was still on the bus"
            };
        }

        try
        {
            return ReadBrightnessCore(hMonitor);
        }
        finally
        {
            LeaveBus();
        }
    }

    /// <summary>The body of a read, with the bus already claimed.</summary>
    private static BusResult ReadBrightnessCore(IntPtr hMonitor)
    {
        var started = Stopwatch.StartNew();

        Task<BusResult> read = Task.Run(() =>
        {
            lock (Gate)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count))
                {
                    return new BusResult
                    {
                        Outcome = BusOutcome.Failed,
                        Why = "the driver would not list physical monitors",
                        Win32Error = Marshal.GetLastWin32Error(),
                        ElapsedMs = (int)started.ElapsedMilliseconds
                    };
                }

                if (count == 0)
                {
                    return new BusResult
                    {
                        Outcome = BusOutcome.Failed,
                        Why = "the driver reports no physical monitor on this display",
                        ElapsedMs = (int)started.ElapsedMilliseconds
                    };
                }

                // The struct carries a by-value string, so it is not blittable and
                // an [Out] array of them silently fails to copy the handles back:
                // every hPhysicalMonitor then reads as zero and the monitor looks
                // like it declined. A raw buffer with the struct read out of it by
                // hand is the only version of this that actually works.
                int stride = Marshal.SizeOf<PHYSICAL_MONITOR>();
                IntPtr buffer = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                    {
                        return new BusResult
                        {
                            Outcome = BusOutcome.Failed,
                            Why = "no physical monitor handles",
                            Win32Error = Marshal.GetLastWin32Error(),
                            ElapsedMs = (int)started.ElapsedMilliseconds
                        };
                    }

                    List<string> declined = new();
                    int lastError = 0;
                    int usedAttempts = 0;
                    string? firstZeroHandle = null;

                    for (int i = 0; i < count; i++)
                    {
                        PHYSICAL_MONITOR monitor = Marshal.PtrToStructure<PHYSICAL_MONITOR>(IntPtr.Add(buffer, i * stride));

                        if (monitor.hPhysicalMonitor == IntPtr.Zero)
                        {
                            // The driver listed a physical monitor but handed back
                            // no handle, which is how it says there is no DDC/CI
                            // pathway to this display. Nothing can be asked of it,
                            // and retrying would be pointless. This is its own
                            // outcome, distinct from a decline: the monitor was
                            // never spoken to, so a refusal count against it is
                            // counting something that did not happen.
                            firstZeroHandle ??= monitor.szPhysicalMonitorDescription;
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
                        bool ok = GetVCPFeatureAndVCPFeatureReply(
                            monitor.hPhysicalMonitor, VcpBrightness, out VcpCodeType type, out uint current, out uint max);
                        int error = ok ? 0 : Marshal.GetLastWin32Error();
                        usedAttempts = 1;

                        // AOC and other scalers are documented to drop the first
                        // AUX packet and to be slow to answer, so a single refusal
                        // is not treated as final. Two short retries with a pause
                        // settle the difference between a dropped packet and a
                        // monitor that genuinely does not implement the code.
                        for (int attempt = 1; attempt <= RetryAttempts && !ok; attempt++)
                        {
                            Thread.Sleep(RetryDelayMs);
                            RespectMinimumGap(monitor.hPhysicalMonitor);
                            ok = GetVCPFeatureAndVCPFeatureReply(
                                monitor.hPhysicalMonitor, VcpBrightness, out type, out current, out max);
                            usedAttempts = attempt + 1;
                            if (!ok)
                            {
                                error = Marshal.GetLastWin32Error();
                            }
                        }

                        if (ok && max > 0)
                        {
                            uint min = ReadMinimum(monitor.hPhysicalMonitor);
                            return new BusResult
                            {
                                Outcome = BusOutcome.Ok,
                                Reading = new BrightnessReading
                                {
                                    Minimum = min,
                                    Current = current,
                                    Maximum = max,
                                    CodeType = type
                                },
                                Attempts = usedAttempts,
                                PhysicalMonitor = monitor.szPhysicalMonitorDescription,
                                ElapsedMs = (int)started.ElapsedMilliseconds
                            };
                        }

                        lastError = error;
                        declined.Add("\"" + monitor.szPhysicalMonitorDescription + "\" declined 0x10 after "
                            + usedAttempts + " attempts, " + DdcErrors.Describe(error));
                    }

                    bool zeroHandleOnly = firstZeroHandle is not null && declined.Count == 1;

                    return new BusResult
                    {
                        Outcome = zeroHandleOnly ? BusOutcome.NoDdcPathway : BusOutcome.Refused,
                        Why = declined.Count == 0
                            ? "no usable physical monitor handle"
                            : string.Join("; ", declined),
                        Win32Error = lastError,
                        Attempts = usedAttempts,
                        PhysicalMonitor = firstZeroHandle,
                        ElapsedMs = (int)started.ElapsedMilliseconds
                    };
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
            return new BusResult
            {
                Outcome = BusOutcome.TimedOut,
                Why = "no answer within " + ProbeTimeoutMs + "ms, so the bus is treated as wedged",
                ElapsedMs = ProbeTimeoutMs
            };
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
    private static IEnumerable<(IntPtr, string, string, bool, EdidReading, string, string)> EnumerateMonitors()
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
            string hardwareId = string.Empty;
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

                    // With EDD_GET_EDID set, DeviceID is REPLACED by the EDID in
                    // hex. A driver that declines the request leaves the hardware
                    // id in place instead, which is why this field is sometimes an
                    // EDID and sometimes not. DeviceKey is not rewritten, so it is
                    // the dependable source of the panel's registry key.
                    if (LooksLikeHexEdid(md.DeviceID))
                    {
                        driverEdid = md.DeviceID;
                        hardwareId = PanelKeyOf(md.DeviceKey.Length > 0 ? md.DeviceKey : md.DeviceID);
                    }
                    else
                    {
                        hardwareId = PanelKeyOf(md.DeviceID.Length > 0 ? md.DeviceID : md.DeviceKey);
                    }

                    external = (md.StateFlags & DisplayDeviceAttachedToDesktop) == 0;
                }
            }

            // Asked for outside the loop on purpose. This driver refuses the
            // EDD_GET_EDID request outright, so the enumeration above can come
            // back empty, and a fallback that only runs when the driver answered
            // is a fallback that never runs.
            EdidReading reading = ReadEdid(driverEdid, friendly, hardwareId, out string source);

            yield return (handle, device, friendly, external, reading, source, hardwareId);
        }
    }


    /// <summary>An EDD_GET_EDID reply is a long hex string; a hardware id is not.</summary>
    private static bool LooksLikeHexEdid(string value) =>
        value.Length >= 256 && value.Length % 2 == 0;


    /// <summary>
    /// The panel's key under HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY, pulled
    /// out of a device id. Drivers spell these several ways, so the two that
    /// actually turn up are both handled:
    /// <code>\\?\DISPLAY#AOCA610#7&amp;272e3773&amp;0&amp;UID264#{...}</code> and
    /// <code>MONITOR\AOCA610\{...}\0001</code>.
    /// </summary>
    public static string PanelKeyOf(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        int hash = deviceId.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        if (hash >= 0)
        {
            int start = hash + "DISPLAY#".Length;
            int end = deviceId.IndexOf('#', start);
            if (end > start)
            {
                return deviceId[start..end];
            }
        }

        int backslash = deviceId.IndexOf('\\');
        if (backslash >= 0)
        {
            int start = backslash + 1;
            int end = deviceId.IndexOf('\\', start);
            if (end > start)
            {
                return deviceId[start..end];
            }
        }

        return string.Empty;
    }


    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);


    /// <summary>
    /// How each active display is wired, and whether the compositor is driving it
    /// in HDR. Read only, and never allowed to influence whether a write is
    /// attempted: this exists so the log can answer "is it the cable, the port or
    /// HDR" instead of leaving a user to guess.
    /// <para>
    /// HDR matters here because it is the one setting that changes the answer
    /// without changing any hardware. A panel that answers VCP 0x10 perfectly well
    /// will stop answering it the moment the display is switched to HDR, and the
    /// only way to tell those two situations apart from the outside is to ask the
    /// compositor.
    /// </para>
    /// </summary>
    private static IReadOnlyList<DisplayLink> ReadDisplayLinks()
    {
        List<DisplayLink> links = new();

        try
        {
            if (GetDisplayConfigBufferSizes(QueryDisplayConfigOnlyActive, out uint pathCount, out uint modeCount) != 0
                || pathCount == 0)
            {
                return links;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            if (QueryDisplayConfig(
                    QueryDisplayConfigOnlyActive,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero) != 0)
            {
                return links;
            }

            foreach (DISPLAYCONFIG_PATH_INFO path in paths)
            {
                if (!path.targetInfo.targetAvailable)
                {
                    continue;
                }

                var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DisplayConfigGetTargetName,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                string friendly = DisplayConfigGetDeviceInfo(ref name) == 0
                    ? name.monitorFriendlyDeviceName
                    : string.Empty;

                var colour = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DisplayConfigGetAdvancedColorInfoType,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                bool hdrActive = false;
                bool advancedKnown = DisplayConfigGetDeviceInfo(ref colour) == 0;

                if (advancedKnown)
                {
                    // colorEncoding is DISPLAYCONFIG_COLOR_ENCODING_INTENSITY, the
                    // SDR value. Anything else means the compositor is driving the
                    // panel through an HDR encoding.
                    hdrActive = colour.colorEncoding != 0;
                }

                links.Add(new DisplayLink
                {
                    FriendlyName = friendly,
                    Connection = ConnectionNameOf(path.targetInfo.videoOutputTechnology),
                    HdrActive = hdrActive,
                    HdrKnown = advancedKnown,

                    // The OS only answers this for a target it considers advanced
                    // colour capable, so a successful answer is the strongest
                    // statement available here without a bus transaction.
                    HdrSupported = advancedKnown
                });
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never be the thing that breaks a probe.
            TraceLog.Write("HWB display link read failed: " + ex.GetType().Name);
        }

        return links;
    }


    private static string ConnectionNameOf(uint technology) => technology switch
    {
        1 => "VGA",
        5 => "DVI",
        6 => "HDMI",
        10 => "DisplayPort",
        11 => "DisplayPort (embedded)",
        14 => "Miracast",
        15 => "Indirect (wired)",
        16 => "Indirect (virtual)",
        17 => "DisplayPort (USB tunnel)",
        _ => "unknown"
    };


    private const uint QueryDisplayConfigOnlyActive = 0x00000002;
    private const uint DisplayConfigGetTargetName = 2;
    private const uint DisplayConfigGetAdvancedColorInfoType = 9;


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


    private static EdidReading ReadEdid(string hex, string modelName, string hardwareId, out string source)
    {
        if (TryHex(hex, out byte[]? fromDriver) && fromDriver is not null)
        {
            source = "driver";
            return ParseEdid(fromDriver, modelName);
        }

        // Plenty of drivers decline the EDD_GET_EDID request and still publish
        // the block in the registry, which is where Windows itself caches it. One
        // real machine here reports "Generic PnP Monitor" through the driver and
        // holds a perfectly good 128 byte EDID under Enum\DISPLAY, so without this
        // the pre-flight check would refuse to look at a perfectly ordinary panel.
        byte[]? fromRegistry = ReadRegistryEdid(modelName, hardwareId);
        if (fromRegistry is null)
        {
            source = "none";
            return new EdidReading { ModelName = modelName, Verdict = EdidVerdict.Unreadable };
        }

        source = "registry";
        return ParseEdid(fromRegistry, modelName);
    }


    /// <summary>
    /// The EDID Windows cached for the display panels, straight out of
    /// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY. A registry read, so it costs
    /// nothing on the bus and cannot be the thing that upsets a monitor.
    /// <para>
    /// Matching is by the panel's hardware id where we have one, because the name
    /// is worthless often enough to matter: several unrelated monitors all report
    /// "Generic PnP Monitor", and Windows keeps an entry per connection, so a
    /// display that is no longer plugged in still has a block sitting in the
    /// registry under the same name as the one that is. Matching on the name
    /// picked up a stale entry and refused the live monitor.
    /// </para>
    /// </summary>
    private static byte[]? ReadRegistryEdid(string modelName, string hardwareId)
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

            List<(byte[] Edid, string Name, string Panel)> found = new();

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

                    found.Add((
                        bytes,
                        FriendlyNameOf(instanceKey?.GetValue("DeviceDesc") as string),
                        panel));
                }
            }

            if (found.Count == 0)
            {
                return null;
            }

            // The hardware id is exact, so it wins outright and no name matching
            // happens at all.
            if (hardwareId.Length > 0)
            {
                foreach ((byte[] edid, string _, string panel) in found)
                {
                    if (string.Equals(panel, hardwareId, StringComparison.OrdinalIgnoreCase))
                    {
                        RegistryMatchBy = "panel key " + panel;
                        return edid;
                    }
                }
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
                RegistryMatchBy = "single panel name";
                return found[0].Edid;
            }

            foreach ((byte[] edid, string name, string _) in found)
            {
                if (name.Length > 0
                    && (modelName.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(modelName, StringComparison.OrdinalIgnoreCase)))
                {
                    RegistryMatchBy = "loose name match";
                    return edid;
                }
            }

            RegistryMatchBy = "ambiguous, no match";
            return null;
        }
        catch (Exception ex)
        {
            LastRegistryError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>How the registry fallback chose its block, for the log.</summary>
    public static string RegistryMatchBy { get; private set; } = string.Empty;


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


    /// <summary>
    /// Decodes a 128 byte EDID block. Public and side effect free so it can be
    /// driven from synthetic blocks in the test suite, which is the only way to
    /// cover the 0xFF model-year flag and the little endian serial without a
    /// monitor on the desk.
    /// </summary>
    public static EdidReading ParseEdid(byte[] edid, string modelName)
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

        // EDID 1.3 and 1.4 put the manufacture week in byte 16 and the year, as an
        // offset from 1990, in byte 17. This used to be built from bytes 19, 12
        // and 11, which are the EDID revision, the first serial byte and the low
        // product code byte, so it produced a large nonsense number for every
        // monitor ever made. On a real AOC AG276QZD2 those bytes give 41746,
        // while byte 17 holds 35, which is 2025.
        int week = edid[16];
        int yearByte = edid[17];
        int? year = yearByte == 0 ? null : 1990 + yearByte;

        // A week of 0xFF is not a week. It is the flag EDID 1.4 defines for a
        // model year rather than a manufacture date, and it is common on panels
        // that only ever claim to be from their design year. Reporting 6095
        // because of it would be its own kind of nonsense, so it is dropped and
        // the week is only reported when it looks like one.
        if (week == 0xFF)
        {
            week = 0;
        }
        else if (week is < 1 or > 54)
        {
            week = 0;
        }

        // The year is deliberately NOT one of the blocking reasons. Plenty of
        // panels put something unhelpful in it, and treating a bad year as proof
        // of a dangerous monitor would throw away working hardware. It is
        // reported because it is useful, not because it is a verdict.

        int widthCm = edid[21];
        int heightCm = edid[22];
        double? diagonal = widthCm > 0 && heightCm > 0
            ? Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54
            : null;
        if (diagonal is not null && (diagonal < 5 || diagonal > 150))
        {
            reasons.Add("panel size " + diagonal.Value.ToString("0") + "\" is not believable");
        }

        // Bytes 12 to 15 are a 32 bit little endian serial number. This read only
        // the top two of them, so the AG276QZD2 whose serial is 1175 was reported
        // as 4. The number is not logged, because a serial is an identifier and
        // this file is copied into bug reports.
        uint serialRaw = (uint)(edid[12]
            | (edid[13] << 8)
            | (edid[14] << 16)
            | (edid[15] << 24));

        if (yearByte == 0 && widthCm == 0 && serialRaw == 0)
        {
            return new EdidReading
            {
                Manufacturer = maker,
                ModelName = modelName,
                Verdict = EdidVerdict.Unreadable,
                Reasons = new[] { "EDID is all zeroes" }
            };
        }

        return new EdidReading
        {
            Manufacturer = maker,
            ModelName = modelName,
            ProductCode = (ushort)(edid[10] | (edid[11] << 8)),
            Year = year,
            Week = week == 0 ? null : week,
            DiagonalInches = diagonal,
            Serial = serialRaw == 0 ? null : (int)serialRaw,
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


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;

        public uint Denominator;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_LUID
    {
        public uint LowPart;

        public int HighPart;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public DISPLAYCONFIG_LUID adapterId;

        public uint id;

        public uint modeInfoIdx;

        public uint statusFlags;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public DISPLAYCONFIG_LUID adapterId;

        public uint id;

        public uint modeInfoIdx;

        public uint videoOutputTechnology;

        public uint rotation;

        public uint scaling;

        public DISPLAYCONFIG_RATIONAL refreshRate;

        public uint scanLineOrdering;

        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;

        public uint statusFlags;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;

        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;

        public uint flags;
    }


    /// <summary>
    /// Only ever allocated to be counted: the caller never reads a mode out of
    /// it. Its real size has to be right or the compositor refuses the whole
    /// query, so it is the 64 byte union from the header rather than a guess.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;

        public uint id;

        public DISPLAYCONFIG_LUID adapterId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] modeInfo;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;

        public uint size;

        public DISPLAYCONFIG_LUID adapterId;

        public uint id;
    }


    /// <summary>
    /// The layout has to match the SDK exactly, and it does: 20 bytes for the
    /// header, 404 for this and 28 for the advanced colour block, all measured on
    /// a real machine rather than assumed. Two wrong answers were worth ruling
    /// out along the way. The header is 20 and not 24, because LUID is two 32 bit
    /// fields and so aligns to 4; adding Pack = 8 does not change that, because
    /// Pack caps alignment rather than raising it. And PATH_INFO is 72 and not
    /// 100: 20 plus 48 plus 4 is 72, so QueryDisplayConfig is not writing past
    /// the array.
    /// <para>
    /// The query still fails on some driver and build combinations, returning
    /// ERROR_INVALID_PARAMETER, which is why HDR is reported as a tri-state and
    /// never guessed at. dxdiag remains the fallback for that question, and it is
    /// far too slow to call per probe.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        public uint colorEncoding;

        public uint bitsPerColorChannel;
    }


    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);


    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);


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


    /// <summary>
    /// The 5 parameter form of the call. It was declared with 4, which put every
    /// value in the wrong place: the third argument is the VCP code TYPE, so it
    /// was being read back as the current value, the current value was being read
    /// back as the maximum, and the real maximum was written into a stack slot
    /// nobody had declared. The result was a reading whose Current was 0 or 1 and
    /// whose Maximum was whatever the panel happened to be showing.
    /// </summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor,
        byte code,
        out VcpCodeType type,
        out uint current,
        out uint max);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint minimum, out uint current, out uint maximum);


    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
}
