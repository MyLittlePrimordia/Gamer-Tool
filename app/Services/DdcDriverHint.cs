using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>Which company made the GPU driving the displays.</summary>
public enum GpuVendor
{
    Unknown,
    Amd,
    Nvidia,
    Intel
}


/// <summary>
/// The "your driver may be in the way" hint shown beside a monitor that could
/// not be reached.
/// <para>
/// This exists because the most common reason a perfectly capable monitor reports
/// no DDC/CI handle is a setting in the GPU vendor's own control panel, and no
/// amount of searching finds it. An AMD card with HDCP Support switched on in
/// Adrenalin returns no physical monitor handle at all: the probe stops before a
/// single byte goes over the bus, the row greys out, and nothing on screen says
/// why. Turning that one setting off makes the identical hardware work.
/// </para>
/// <para>
/// Two rules this file exists to enforce. The hint is only ever offered when the
/// probe specifically failed to get a DDC/CI handle, never merely because a
/// display is unsupported, because a TV or a laptop panel is unsupported for
/// entirely different reasons and telling those users to go and change a GPU
/// setting is both wrong and the fastest way to teach them to ignore the hint.
/// And the copy is per vendor, because the one path that has actually been
/// confirmed is AMD's; a step-by-step for a vendor nobody has checked would be a
/// guess presented as an instruction.
/// </para>
/// </summary>
public static class DdcDriverHint
{
    /// <summary>
    /// Whether this display is worth offering the hint for. True only for the one
    /// failure a driver setting can plausibly fix.
    /// </summary>
    public static bool ShouldOffer(MonitorProbe monitor)
    {
        if (monitor is null)
        {
            return false;
        }

        // A display already on the exclusion list, or one the EDID pre-flight
        // refused, has its own reason and its own advice somewhere else.
        if (monitor.BlockedReason is not null)
        {
            return false;
        }

        return monitor.Outcome == BusOutcome.NoDdcPathway;
    }

    /// <summary>
    /// Short headline. Says what is wrong without claiming a cause.
    /// <para>
    /// Sentence case, not the shouting caps the rest of this app's dialogs use.
    /// Those are one line of labels. This is a paragraph of instructions that
    /// someone is going to read carefully and type back into another program, and
    /// set that in capitals it stops looking like something to follow.
    /// </para>
    /// </summary>
    public static string Title(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => "AMD driver is blocking hardware brightness",
        _ => "Your GPU driver may be blocking hardware brightness"
    };

    /// <summary>
    /// The steps, in the order they are done. Only AMD's are spelled out: that is
    /// the path that has been confirmed on real hardware, down to the EULA that
    /// hides the Overrides page until it is accepted. Anyone else is pointed at
    /// the idea rather than at clicks nobody has verified.
    /// <para>
    /// The restart is the driver's own requirement, not ours. Adrenalin says so
    /// under the toggle: "HDCP changes will take effect after the next system
    /// restart", next to a Restart Now button. Telling someone to reopen Gamer
    /// Tool instead would have them do the work, see nothing change, and conclude
    /// the fix does not work.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Steps(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => new[]
        {
            "Open AMD Software, then Display",
            "Under Overrides, turn off \"HDCP Support\"",
            "Restart your PC"
        },
        _ => new[]
        {
            "Open your GPU vendor's control panel",
            "Look for an HDCP or content protection setting",
            "Turn it off, then restart your PC"
        }
    };

    /// <summary>
    /// The cost of following the advice, stated plainly. Leaving this out would
    /// make the hint read as free, and it is not: HDCP is what protected video
    /// needs in order to play.
    /// </summary>
    public const string Warning =
        "Turning off HDCP can affect DRM-protected video playback, like Netflix and some 4K video.";

    private static GpuVendor? _cached;

    /// <summary>
    /// The vendor of the graphics hardware, worked out once. Detection failure is
    /// not an error: an unknown vendor gets the generic copy, which is why nothing
    /// here can throw and take a probe down with it.
    /// </summary>
    public static GpuVendor Vendor
    {
        get
        {
            if (_cached is { } known)
            {
                return known;
            }

            GpuVendor found = GpuVendor.Unknown;
            try
            {
                // Only the first adapter is looked at. Every display on a machine
                // tends to be driven by the same card, and walking all of them
                // costs a call per output for an answer that would not change.
                DISPLAY_DEVICE adapter = NewDisplayDevice();
                if (EnumDisplayDevices(IntPtr.Zero, 0, ref adapter, 0))
                {
                    found = VendorFromName(adapter.DeviceString);
                }
            }
            catch (Exception ex)
            {
                // Diagnostics must never be the thing that breaks a probe.
                TraceLog.Write("HWB gpu vendor read failed: " + ex.GetType().Name);
            }

            _cached = found;
            return found;
        }
    }

    /// <summary>Forgets the cached vendor. Called when the display layout changes.</summary>
    public static void ResetVendorCache() => _cached = null;

    /// <summary>
    /// The vendor from a graphics adapter's name. Pure, so the naming is pinned
    /// by tests rather than by whatever machine happens to be running them.
    /// </summary>
    public static GpuVendor VendorFromName(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return GpuVendor.Unknown;
        }

        string name = adapterName;

        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ATI ", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX", StringComparison.Ordinal))
        {
            return GpuVendor.Nvidia;
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc ", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }

    /// <summary>Everything the modal needs, gathered so the view stays empty of logic.</summary>
    public static HintContent For(GpuVendor vendor) => new(
        Title(vendor),
        new ReadOnlyCollection<string>(Steps(vendor).ToList()),
        Warning);

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

    // IntPtr rather than a string for the device: a null device is what asks for
    // the adapter list, and marshalling a null C# string does not reliably arrive
    // as a null pointer, which fails the call and looks like there is no GPU.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(IntPtr device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);
}


/// <summary>The modal's contents, assembled away from the view.</summary>
public sealed class HintContent
{
    public HintContent(string title, IReadOnlyList<string> steps, string warning)
    {
        Title = title;
        Steps = steps;
        Warning = warning;
    }

    public string Title { get; }

    public IReadOnlyList<string> Steps { get; }

    public string Warning { get; }
}
