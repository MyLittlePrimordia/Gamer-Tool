using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class MonitorChoice
{
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// The panel's identity, which outlives the <c>\\.\DISPLAYn</c> in
    /// <see cref="Device"/>. Empty when the adapter reports no monitor id, in
    /// which case the device name is all there is to go on.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public override string ToString()
    {
        return Name;
    }
}

public sealed class DisplayService
{
    private const int RampSize = 256;

    private readonly Dictionary<string, Ramp> _originalRamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the originals are kept between runs, or null in tests and on any
    /// path that does not own the screen for the session.
    /// </summary>
    private readonly GammaBaselineStore? _store;

    public DisplayService()
        : this(null)
    {
    }

    internal DisplayService(GammaBaselineStore? store)
    {
        _store = store;
    }

    private readonly List<string> _monitors = new();

    private readonly List<string> _monitorNames = new();

    /// <summary>
    /// The panel identity behind each entry in <see cref="_monitors"/>, in the
    /// same order. Scanned with the names because both come from the same
    /// enumeration, and a key computed later could disagree with a list that has
    /// since been rescanned.
    /// </summary>
    private readonly List<string> _monitorKeys = new();

    private readonly object _gate = new();

    private DispatcherTimer? _timer;

    private IntPtr _lastForeground;

    private int _tick;

    private DateTime _lastDriftLogged = DateTime.MinValue;

    /// <summary>
    /// What the driver itself reported straight after each of this app's own writes,
    /// per screen. The gamma lock compares against this rather than against the ramp
    /// that was asked for, because a driver may quantise or rescale what it is given,
    /// and a comparison against the request would then read as drift on every tick.
    /// </summary>
    private readonly Dictionary<string, Ramp> _baselines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many lock ticks apart the unconditional re-push is. Thirteen is about
    /// twenty seconds. The lock used to write on every second tick whether or not
    /// anything had changed; now it writes when it sees a change and keeps this as a
    /// net under the cases a read-back cannot see, such as a driver that resets the
    /// hardware table without updating the copy Windows hands back.
    /// </summary>
    private const int SafetyNetTicks = 13;

    /// <summary>
    /// How far a table entry may move, out of 65535, before it counts as changed.
    /// Reading a ramp back straight after writing it returns the same table, so this
    /// only has to absorb noise, and it is small enough that another program setting
    /// a different table is nowhere near it.
    /// </summary>
    private const int DriftTolerance = 128;

    private bool _lockEnabled = true;

    private bool _dirty;

    private string _lastLogged = string.Empty;

    private bool _lastPushOk;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Ramp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = RampSize)]
        public ushort[] Red;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = RampSize)]
        public ushort[] Green;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = RampSize)]
        public ushort[] Blue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceInfo
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref Ramp ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref Ramp ramp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string? device, string? window, IntPtr flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDeviceInfo info, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public event Action<string>? StatusChanged;

    public event Action<DisplayPreset>? Applied;



    public bool IsEnabled { get; private set; }

    /// <summary>
    /// True when <see cref="RestoreAfterCrash"/> put a ramp back, so the window
    /// can say why the screen changed on its own.
    /// </summary>
    public bool RestoredAfterCrash { get; private set; }

    /// <summary>
    /// Marks the service as holding a ramp on screen, for the tests.
    /// <para>
    /// A real <see cref="Apply"/> is the only other thing that sets
    /// <see cref="IsEnabled"/>, and it sets it by asking Win32 to actually change
    /// a display's gamma. A unit test cannot do that and should not, so this
    /// stands in for the part of Apply that lands, and nothing else.
    /// </para>
    /// </summary>
    internal void MarkAppliedForTest() => IsEnabled = true;

    public DisplayPreset Working { get; private set; } = DisplayPreset.Flat();



    /// <summary>
    /// The display a scope names, or empty for every attached screen.
    /// <para>
    /// Guarded by the same lock as the monitor list, and that is not tidiness. The
    /// gamma lock's timer calls <see cref="Push"/> on its own interval while a
    /// rescan is rewriting the monitor list and reconciling this field, and <see
    /// cref="Push"/> read it *outside* the lock. Reconciling a detached display
    /// clears it, and a push that read the cleared value applied a scoped preset to
    /// every attached screen - so the scope the user chose was the one thing the
    /// race could take away.
    /// </para>
    /// <para>
    /// The setter is public so callers can point the service at a screen, but the
    /// write is still a lock, so a reader never observes a half-written value or an
    /// intermediate one that no code chose.
    /// </para>
    /// </summary>
    public string ActiveDevice
    {
        get
        {
            lock (_gate)
            {
                return _activeDevice;
            }
        }

        set
        {
            lock (_gate)
            {
                _activeDevice = value ?? string.Empty;
            }
        }
    }

    private string _activeDevice = string.Empty;

    public IReadOnlyList<string> Monitors
    {
        get
        {
            lock (_gate)
            {
                return _monitors.ToArray();
            }
        }
    }

    public IReadOnlyList<MonitorChoice> MonitorChoices
    {
        get
        {
            List<MonitorChoice> list = new() { new MonitorChoice { Device = string.Empty, Key = string.Empty, Name = "All screens" } };
            lock (_gate)
            {
                for (int i = 0; i < _monitorNames.Count; i++)
                {
                    list.Add(new MonitorChoice
                    {
                        Device = _monitors[i],
                        Key = i < _monitorKeys.Count ? _monitorKeys[i] : string.Empty,
                        Name = _monitorNames[i]
                    });
                }
            }

            return list;
        }
    }

    /// <summary>
    /// Enumerates the attached displays and replaces the cached set with whatever
    /// is there now.
    /// <para>
    /// <see cref="ScanMonitors"/> is the same work and has always cleared the cache
    /// before filling it, so this is a name rather than new behaviour. It exists
    /// because the callers that need a forced rescan - the display-change hook, the
    /// refresh button - were reaching for ScanMonitors directly, which reads as
    /// "enumerate" and gives no hint that it also throws away what was known. That
    /// matters here because every other call site guards on
    /// <c>Monitors.Count &gt; 0</c> and will therefore never call it again: a
    /// docked laptop kept yesterday's display list for the rest of the session.
    /// </para>
    /// <para>
    /// The remembered ramps are kept, deliberately. They are what the exit path
    /// puts back, and they are keyed by device name - \\.\DISPLAY1 does not change
    /// identity when a second screen appears, it is the same adapter - so
    /// discarding them would lose the ability to restore a screen that has just
    /// been unplugged and re-plugged.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Rescan() => ScanMonitors();

    /// <summary>
    /// The device this service is currently writing to, for tests that need to
    /// prove a scope survived or was widened by a re-scan.
    /// <para>
    /// Push refuses when this names a screen it cannot find, so the only way to
    /// check the reconciliation without a monitor attached is to read it. Exposed
    /// internally rather than publicly for the same reason the tests can see the
    /// internals at all: this is a property of the bus's target, not an API the
    /// app has any use for.
    /// </para>
    /// </summary>
    internal string ActiveDeviceForTest => ActiveDevice;

    /// <summary>
    /// Points the service at a device without going through an apply.
    /// <para>
    /// A test-only seam. Apply writes a real gamma ramp to real hardware, which is
    /// the one thing a unit test must not do, and there is no other route to the
    /// scope - it is set as a side effect of Apply. This is that route.
    /// </para>
    /// </summary>
    internal void ScopeToForTest(string device) => ActiveDevice = device ?? string.Empty;

    /// <summary>How many displays this service holds an original ramp for.</summary>
    internal int RememberedRampCountForTest
    {
        get
        {
            lock (_gate)
            {
                return _originalRamps.Count;
            }
        }
    }

    public IReadOnlyList<string> ScanMonitors()
    {
        List<string> found = new();
        List<string> names = new();
        List<string> keys = new();
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> seenKeys = new(StringComparer.OrdinalIgnoreCase);
        int ordinal = 0;

        for (uint i = 0; i < 16; i++)
        {
            DisplayDeviceInfo adapter = new();
            adapter.Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>();
            if (!EnumDisplayDevices(null, i, ref adapter, 0))
            {
                break;
            }

            bool attached = (adapter.StateFlags & 0x1) == 0x1;
            bool primary = (adapter.StateFlags & 0x4) == 0x4;
            if ((!attached && !primary) || string.IsNullOrWhiteSpace(adapter.DeviceName) || found.Contains(adapter.DeviceName))
            {
                continue;
            }

            ordinal++;
            found.Add(adapter.DeviceName);
            keys.Add(Key(adapter.DeviceName, seenKeys));
            names.Add(Label(adapter, ordinal, seen));
        }

        if (found.Count == 0)
        {
            found.Add(@"\\.\DISPLAY1");
            keys.Add(@"\\.\DISPLAY1");
            names.Add("Screen 1");
        }

        lock (_gate)
        {
            _monitors.Clear();
            _monitors.AddRange(found);
            _monitorKeys.Clear();
            _monitorKeys.AddRange(keys);
            _monitorNames.Clear();
            _monitorNames.AddRange(names);
        }

        ReconcileActiveDevice(found);
        return found;
    }

    /// <summary>
    /// Drops a target that is no longer attached, so the next push lands somewhere
    /// rather than nowhere.
    /// <para>
    /// Push refuses outright when <see cref="ActiveDevice"/> names a screen it
    /// cannot find, and reports "NO SUCH SCREEN". That is right when a user picks
    /// a monitor that is not there - they made a mistake and should be told. It
    /// is wrong for a dock: undocking the laptop renumbers \\.\DISPLAY2 away and
    /// the preset that was scoped to it now matches nothing, so every subsequent
    /// re-push refuses and the screen sits on whatever Windows put there.
    /// </para>
    /// <para>
    /// So the scope is widened to all screens rather than refused. The curve being
    /// applied is the one the user chose and it is still the right curve; losing it
    /// because a cable moved is the worse of the two failures. The case is
    /// deliberately narrow - an empty target, or one that is genuinely still
    /// present, or one this device matches by suffix, are all left alone - so the
    /// "NO SUCH SCREEN" path still does its job for a mistyped choice.
    /// </para>
    /// </summary>
    private void ReconcileActiveDevice(IReadOnlyList<string> present)
    {
        // Checked and cleared as one locked operation rather than read-then-write.
        // The read-decide-write pair was the race: a push could arrive between the
        // test and the clear, or observe the cleared field and spread a scoped
        // preset across every attached screen.
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_activeDevice))
            {
                return;
            }

            foreach (string device in present)
            {
                if (DeviceMatches(device, _activeDevice))
                {
                    return;
                }
            }

            TraceLog.Write("DISPLAY target " + _activeDevice + " is gone, applying to all screens instead");
            _activeDevice = string.Empty;
        }
    }

    /// <summary>
    /// Reconciles the scope against a list of attached devices, for the tests.
    /// <para>
    /// The reconcile is the interesting part of 2.2 and there is no other way to
    /// reach it: it runs inside <see cref="Rescan"/>, which needs real hardware. It
    /// is exposed rather than the scan so a test can drive the check-and-clear
    /// directly, including concurrently with readers, which is the whole question.
    /// </para>
    /// </summary>
    internal void ReconcileForTest(IReadOnlyList<string> present) => ReconcileActiveDevice(present);

    /// <summary>
    /// Whether an attached device is the one a scope names.
    /// <para>
    /// Exact match, or a suffix match that has to land on a delimiter boundary.
    /// The plain <c>EndsWith</c> this replaced had no boundary, so a stored
    /// "DISPLAY2" claimed "DISPLAY20", "DISPLAY21" and every other display on a
    /// machine with more than ten of them - and the failure was silent in the worst
    /// direction, because the app reported a successful scoped push while writing
    /// to a screen the user had never pointed it at.
    /// </para>
    /// <para>
    /// Requiring the character immediately before the matched run to be a path
    /// separator is what makes the leniency safe: it is true for the suffix forms
    /// this was written for - "\\.\DISPLAY2" against a stored "DISPLAY2", or a full
    /// path stored without its device prefix - and false for a partial hit inside a
    /// longer name, which is the only case the leniency was ever at risk for.
    /// </para>
    /// </summary>
    internal static bool DeviceMatches(string device, string wanted)
    {
        if (string.Equals(device, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (wanted.Length == 0
            || device.Length <= wanted.Length
            || !device.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        char before = device[device.Length - wanted.Length - 1];
        return before == '.' || before == '\\';
    }

    /// <summary>
    /// The panel behind a display device, as a name that survives a renumbering.
    /// <para>
    /// <c>\\.\DISPLAY2</c> is a slot in the adapter list, not a monitor. Undock a
    /// laptop and the same panel comes back as DISPLAY1, so anything filed under
    /// the old name - a saved baseline, a slot's screen - belongs to whichever
    /// panel happens to take that number next. The PnP id includes the model and
    /// the instance, so it follows the hardware.
    /// </para>
    /// <para>
    /// Falls back to the device name when an adapter reports no monitor id, which
    /// some virtual and remote displays do. The fallback is no worse than the old
    /// behaviour and keeps a machine with an unhelpful driver working.
    /// </para>
    /// </summary>
    internal static string MonitorKeyFor(string device)
    {
        DisplayDeviceInfo monitor = new();
        monitor.Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>();

        if (EnumDisplayDevices(device, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceId))
        {
            return monitor.DeviceId;
        }

        return device;
    }

    /// <summary>
    /// The attached screen a saved (key, name) pair refers to, or null when this
    /// PC has no such screen.
    /// <para>
    /// Key first, because it is the one that follows the hardware; the name
    /// second, so a profile written before keys existed still resolves.
    /// </para>
    /// <para>
    /// A name that matches but carries a different key is a mismatch, not a hit.
    /// That is the case the key exists for: Windows has handed
    /// <c>\\.\DISPLAY2</c> to a different panel since the slot was written, and
    /// treating it as found would apply the preset to hardware the user never
    /// pointed at. It is answered the same way as a screen that is not attached,
    /// so the caller clears or reports it rather than using it.
    /// </para>
    /// </summary>
    internal static MonitorChoice? MatchMonitor(IReadOnlyList<MonitorChoice> monitors, string? key, string? device)
    {
        string wantedKey = key?.Trim() ?? string.Empty;
        string wantedName = device?.Trim() ?? string.Empty;

        if (wantedKey.Length > 0)
        {
            foreach (MonitorChoice choice in monitors)
            {
                if (choice.Key.Length > 0 && string.Equals(choice.Key, wantedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return choice;
                }
            }
        }

        if (wantedName.Length == 0)
        {
            return null;
        }

        foreach (MonitorChoice choice in monitors)
        {
            if (!string.Equals(choice.Device, wantedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool keyAgrees = wantedKey.Length == 0
                || choice.Key.Length == 0
                || string.Equals(choice.Key, wantedKey, StringComparison.OrdinalIgnoreCase);

            return keyAgrees ? choice : null;
        }

        return null;
    }

    /// <summary>
    /// The device a saved screen should be applied to now.
    /// <para>
    /// Falls back to what was saved rather than to an empty string, because empty
    /// means every screen to <see cref="Apply"/> - and widening a preset the user
    /// scoped to one monitor across all of them is worse than reporting that the
    /// monitor is gone. Handing back the saved key when there is one is safe for
    /// the same reason: a key never equals a device name, so the missing-screen
    /// path fires rather than a wrong-screen one.
    /// </para>
    /// </summary>
    public string ResolveDevice(string key, string device)
    {
        MonitorChoice? match = MatchMonitor(MonitorChoices, key, device);
        if (match is not null)
        {
            return match.Device;
        }

        return !string.IsNullOrWhiteSpace(key) ? key : device ?? string.Empty;
    }


    /// <summary>
    /// Whether two scans describe a different set of screens, rather than the
    /// same screens in a different order.
    /// <para>
    /// The display-change rescan used to rebuild every preset box and slot row on
    /// every event. That is right when a monitor was plugged in or removed, and
    /// wrong for every other display event: a resolution change, a refresh change
    /// and an HDR flip all raise the same notification. Rebuilding under a user
    /// who is editing a slot drops their focus, and rebuilding during an apply
    /// can pull the row out from under the code that is still using it.
    /// </para>
    /// </summary>
    internal static bool MonitorListChanged(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        HashSet<string> seen = new(before, StringComparer.OrdinalIgnoreCase);

        foreach (string device in after)
        {
            if (!seen.Remove(device))
            {
                return true;
            }
        }

        return seen.Count != 0;
    }

    private static string Label(DisplayDeviceInfo adapter, int ordinal, Dictionary<string, int> seen)
    {
        (string? id, string? name) = FirstMonitor(adapter.DeviceName);
        string label = MonitorNameResolver.Resolve(id, name, ordinal);

        if (!seen.TryGetValue(label, out int count))
        {
            seen[label] = 1;
            return label;
        }

        count++;
        seen[label] = count;
        return label + " (" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// The panel identity for a display device, disambiguated the way the label
    /// is. Two identical panels are the one case where a PnP id can repeat, and
    /// without the ordinal both would answer to the same key and a slot pointed
    /// at either would follow whichever was scanned first.
    /// </summary>
    private static string Key(string device, Dictionary<string, int> seen)
    {
        string key = MonitorKeyFor(device);

        if (!seen.TryGetValue(key, out int count))
        {
            seen[key] = 1;
            return key;
        }

        count++;
        seen[key] = count;
        return key + "#" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string? Id, string? Name) FirstMonitor(string adapterName)
    {
        for (uint j = 0; j < 8; j++)
        {
            DisplayDeviceInfo monitor = new();
            monitor.Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>();
            if (EnumDisplayDevices(adapterName, j, ref monitor, 0))
            {
                return (monitor.DeviceId, monitor.DeviceString);
            }
        }

        return (null, null);
    }

    private static Ramp BuildRamp(DisplayPreset preset)
    {
        Ramp ramp = new()
        {
            Red = new ushort[RampSize],
            Green = new ushort[RampSize],
            Blue = new ushort[RampSize]
        };

        // Normalise against a single shared peak. Per-channel peaks were cancelling
        // out RedGain/GreenGain/BlueGain, so any colour tint (blue light filter,
        // warm presets) had no effect on the applied ramp. The tone curve itself is
        // channel independent, so one peak covers all three.
        double peak = 0.0;
        for (int i = 0; i < RampSize; i++)
        {
            peak = Math.Max(peak, Curve(preset, i));
        }

        if (peak <= 0.0)
        {
            peak = 1.0;
        }

        Fill(preset, peak, preset.RedGain, ramp.Red);
        Fill(preset, peak, preset.GreenGain, ramp.Green);
        Fill(preset, peak, preset.BlueGain, ramp.Blue);
        return ramp;
    }

    private static void Fill(DisplayPreset preset, double peak, double gain, ushort[] target)
    {
        for (int i = 0; i < RampSize; i++)
        {
            double scaled = Math.Clamp(Curve(preset, i) / peak, 0.0, 1.0) * Math.Max(gain, 0.0);
            target[i] = (ushort)Math.Round(Math.Clamp(scaled, 0.0, 1.0) * 65535.0);
        }
    }

    /// <summary>
    /// The share of the input range each channel holds at full scale.
    /// <para>
    /// For the tests, and the only way to find out what a preset does to the top of
    /// the range without putting it on a monitor and going outside to look at a
    /// sky. Measured over the same <see cref="ushort"/> ramp that goes to the
    /// hardware, so it counts exactly what the display will do rather than an
    /// approximation of it.
    /// </para>
    /// <para>
    /// Inputs zero to <c>RampSize - 2</c>, deliberately excluding the top entry:
    /// that one is white in by construction and is not evidence of anything. What
    /// is left is the number of inputs that came out white when they should not
    /// have, and a per-channel answer rather than one average - a ramp whose three
    /// channels flatten at different levels does not crush to white, it desaturates
    /// and hue-shifts as the highlights come up, which is worse and much easier to
    /// miss.
    /// </para>
    /// </summary>
    internal static double[] HighlightFlattening(DisplayPreset preset)
    {
        Ramp ramp = BuildRamp(preset);
        return new[] { Flattened(ramp.Red), Flattened(ramp.Green), Flattened(ramp.Blue) };

        static double Flattened(ushort[] channel)
        {
            int count = 0;

            for (int i = 0; i < RampSize - 1; i++)
            {
                if (channel[i] >= ushort.MaxValue)
                {
                    count++;
                }
            }

            return count / (double)(RampSize - 1);
        }
    }

    /// <summary>
    /// How far up the tone scale the shadow lift reaches.
    /// <para>
    /// This was a half, which is a mistake, though a common one. The lift tapers
    /// to nothing at the window edge, so a window that reaches mid grey spends a
    /// third of the signal range lifting tones that were never meant to be lifted.
    /// What that looks like is not extra shadow detail, it is a grey haze over the
    /// lower midtones: the black point and the midtones both come up and the image
    /// goes flat, which is the opposite of what a visibility control is for.
    /// </para>
    /// <para>
    /// A third stops the lift where the shadows stop being shadows. The hardware
    /// equivalent on a monitor OSD works over roughly the bottom fifth to third
    /// of the range for the same reason.
    /// </para>
    /// <para>
    /// Narrowing it does not weaken a preset. The lift at black is unchanged
    /// because that is set by the coefficient, not the window, and the midtones and
    /// above are outside the window either way, so the only thing that moves is
    /// the haze between them. Measured against the old half window, Competitive
    /// keeps black at 59 and mid grey at 150 while the quarter-tone code drops
    /// from 109 to 89, and Night Mode keeps 85 and 166 while 135 falls to 108.
    /// Every preset therefore means the same amount of shadow boost as before and
    /// needs no retuning.
    /// </para>
    /// </summary>
    private const double ShadowWindow = 0.30;

    private static double Curve(DisplayPreset preset, int index)
    {
        double norm = index / 255.0;
        double val = Math.Pow(norm, 1.0 / Math.Max(preset.Gamma, 0.1));
        if (preset.ShadowBoost > 0 && norm < ShadowWindow)
        {
            val += (1.0 - (norm / ShadowWindow)) * (preset.ShadowBoost / 100.0) * 0.35;
        }

        val = ((val - 0.5) * (1.0 + (preset.Contrast / 100.0))) + 0.5 + (preset.Brightness / 100.0);
        return Math.Clamp(val, 0.0, 1.0);
    }

    public static byte[] BuildPreviewLut(DisplayPreset preset)
    {
        Ramp ramp = BuildRamp(preset);
        byte[] lut = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            lut[(i * 3) + 0] = (byte)(ramp.Red[i] >> 8);
            lut[(i * 3) + 1] = (byte)(ramp.Green[i] >> 8);
            lut[(i * 3) + 2] = (byte)(ramp.Blue[i] >> 8);
        }

        return lut;
    }

    public void SetWorking(DisplayPreset preset)
    {
        Working = preset;
    }

    public bool Apply(DisplayPreset preset)
    {
        return Apply(preset, string.Empty);
    }

    public bool Apply(DisplayPreset preset, string device)
    {
        Working = preset;
        ActiveDevice = device ?? string.Empty;
        _dirty = true;
        bool ok = Push();
        TraceLog.Write("DISPLAY APPLY " + preset.Name + " scope=" + (string.IsNullOrEmpty(ActiveDevice) ? "ALL" : ActiveDevice)
            + " gamma=" + preset.Gamma.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + " result=" + ok);
        if (ok)
        {
            IsEnabled = true;

            // The emergency reset on the way out can only put a ramp back if
            // something remembers that a ramp was taken. Nothing else records
            // this, and a failed push leaves the screen alone, so the flag is set
            // here and only here, where the push actually landed.
            SessionState.Current.DisplayTouched = true;
            Applied?.Invoke(preset);
        }

        return ok;
    }

    public bool Push()
    {
        // Scanned first when the list is empty, because the scan reconciles the
        // scope and the scope has to be read after that or this pushes to a screen
        // the scan has just learned is gone.
        IReadOnlyList<string> all = Monitors.Count > 0 ? Monitors : ScanMonitors();

        // Scope and monitor list read as one pair. Read separately, they can be a
        // monitor list from before a rescan and a scope that has since been cleared
        // by the same rescan - which is a scoped preset spread across every screen,
        // silently, on the gamma lock's own timer.
        string scope;
        lock (_gate)
        {
            scope = _activeDevice;
        }

        List<string> targets = new();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            if (all.Contains(scope, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(scope);
            }
            else
            {
                foreach (string device in all)
                {
                    if (DeviceMatches(device, scope))
                    {
                        targets.Add(device);
                    }
                }

                if (targets.Count == 0)
                {
                    StatusChanged?.Invoke("NO SUCH SCREEN");
                    return false;
                }
            }
        }
        else
        {
            targets.AddRange(all);
        }

        bool any = false;
        foreach (string device in targets)
        {
            if (PushDevice(device))
            {
                any = true;
            }
        }

        if (any)
        {
            _dirty = false;
        }

        return any;
    }

    public bool PushDevice(string device)
    {
        IntPtr hdc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            // No fallback to the default display. Opening the unnamed DC gives
            // the primary monitor, so a write meant for the second screen would
            // land on the first, and the original ramp read back from it would be
            // filed under the second device's name, leaving the reset path with
            // the wrong screen's curve. Failing here says which screen is the
            // problem instead of quietly tinting the wrong one.
            StatusChanged?.Invoke("NO SCREEN");
            return false;
        }

        try
        {
            Ramp? known = null;
            lock (_gate)
            {
                if (!_originalRamps.ContainsKey(device))
                {
                    Ramp original = NewRamp();
                    if (GetDeviceGammaRamp(hdc, ref original))
                    {
                        _originalRamps[device] = original;
                    }
                }

                if (_originalRamps.TryGetValue(device, out Ramp captured))
                {
                    known = captured;
                }
            }

            // Filed before the write, because the write is the thing a crash can
            // leave on the screen. There is nothing to file when the driver would
            // not read back; a later crash then falls back to identity rather than
            // to a tint it cannot recognise.
            if (known is Ramp baseline)
            {
                _store?.BeginDirty(MonitorKeyFor(device), Flatten(baseline));
            }

            Ramp ramp = BuildRamp(Working);
            bool ok = SetDeviceGammaRamp(hdc, ref ramp);
            RecordBaseline(device, hdc, ok);
            if (Working.Name != _lastLogged || ok != _lastPushOk)
            {
                _lastLogged = Working.Name;
                _lastPushOk = ok;
                TraceLog.Write("DISPLAY PUSH " + device + " preset=" + Working.Name + " first=" + ramp.Red[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " step32=" + ramp.Red[32].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " last=" + ramp.Red[255].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ok=" + ok);
            }

            if (!ok)
            {
                StatusChanged?.Invoke("SCREEN BLOCKED");
            }

            return ok;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    public bool Reset()
    {
        IReadOnlyList<string> devices = Monitors.Count > 0 ? Monitors : ScanMonitors();
        bool any = false;
        foreach (string device in devices)
        {
            Ramp original;
            lock (_gate)
            {
                if (!_originalRamps.TryGetValue(device, out original))
                {
                    continue;
                }
            }

            IntPtr hdc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                any |= SetDeviceGammaRamp(hdc, ref original);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        // Only stand the flag down when the restore actually landed. If it did
        // not, the ramp may still be sitting out there tinted, and leaving the
        // flag set is what gives the exit path another turn at it.
        if (any)
        {
            SessionState.Current.DisplayTouched = false;
            _store?.MarkClean();
        }

        lock (_gate)
        {
            _baselines.Clear();
        }

        IsEnabled = false;
        Working = DisplayPreset.Flat();
        _dirty = false;
        StatusChanged?.Invoke(any ? "SCREEN RESET" : "NOTHING TO RESET");
        return any;
    }

    /// <summary>
    /// Puts back a ramp left on screen by a run that did not exit cleanly.
    /// <para>
    /// Runs once, at startup, and only when the stored marker was written during
    /// this boot. A marker older than the boot is cleared without touching the
    /// screen: Windows has already reset the gamma table, and writing an old
    /// baseline over whatever the user has set since would be the app undoing
    /// somebody else's work.
    /// </para>
    /// <para>
    /// A screen with no stored baseline but a dirty file gets an identity ramp,
    /// not a read-back. That read-back is the bug this whole file guards: after a
    /// crash it returns the tint, and filing it as "original" makes the tint
    /// permanent.
    /// </para>
    /// </summary>
    public bool RestoreAfterCrash()
    {
        if (_store is null || !_store.Dirty)
        {
            return false;
        }

        if (!_store.DirtyAppliesToThisBoot(DateTime.UtcNow, TimeSpan.FromMilliseconds(Environment.TickCount64)))
        {
            TraceLog.Write("DISPLAY baseline is dirty but predates this boot; clearing it");
            _store.MarkClean();
            return false;
        }

        IReadOnlyList<string> devices = Monitors.Count > 0 ? Monitors : ScanMonitors();
        bool any = false;

        foreach (string device in devices)
        {
            ushort[]? saved = _store.Find(MonitorKeyFor(device), device);

            // A screen this app never captured is left alone. Identity is only the
            // answer when there is no baseline at all, which means the file knows a
            // ramp was written but not what was under it.
            if (saved is null && !_store.IsEmpty)
            {
                continue;
            }

            Ramp ramp = Unflatten(saved ?? GammaBaselineStore.Identity());

            IntPtr hdc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                any |= SetDeviceGammaRamp(hdc, ref ramp);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        if (any)
        {
            // Clean only once the screen actually came back. If the write failed
            // the marker has to stay, so the next launch - or the user's Reset -
            // gets another turn at it.
            _store.MarkClean();
            RestoredAfterCrash = true;
            TraceLog.Write("DISPLAY baseline restored after an unclean exit");
        }

        return any;
    }

    public void SetLock(bool enabled)
    {
        _lockEnabled = enabled;
    }

    /// <summary>Whether the user has the gamma lock switched on.</summary>
    internal bool IsLockOn => _lockEnabled;

    public void StartLock()
    {
        if (_timer is not null)
        {
            return;
        }

        ScanMonitors();
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Interval = TimeSpan.FromMilliseconds(1500);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void StopLock()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>
    /// Whether there is anything of ours on screen worth defending.
    /// <para>
    /// The lock re-pushes the ramp on a timer because a fullscreen game, a driver
    /// reset or a competing gamma tool can put something else there. But there is
    /// only a ramp to defend once one of ours has actually landed: without this,
    /// the timer wrote a flat ramp over whatever the user, the driver or another
    /// tool had set, from the moment the app opened.
    /// </para>
    /// <para>
    /// That is not only wrong, it is the same wrong twice. It fights f.lux and a
    /// driver's own gamma slider for no reason, and it re-flattens the ramp Reset
    /// had just restored, so a reset was undone about three seconds later for
    /// anyone whose original ramp was not already flat. <see cref="IsEnabled"/> is
    /// set only by a successful <see cref="Apply"/> and cleared by
    /// <see cref="Reset"/>, so it answers exactly the question being asked.
    /// </para>
    /// </summary>
    internal bool ShouldDefend => _lockEnabled && IsEnabled;

    private void OnTick(object? sender, EventArgs e)
    {
        if (!ShouldDefend)
        {
            return;
        }

        _tick++;

        IntPtr foreground = GetForegroundWindow();
        bool changed = foreground != _lastForeground;
        _lastForeground = foreground;

        // Cheapest test first. A foreground change is still the moment games tend to
        // reset the ramp, so it still pushes at once. Otherwise the ramp is read back
        // and only written if something has changed it, with the periodic push as the
        // backstop. It used to write every second tick regardless, which is a driver
        // write every three seconds for as long as a preset is up, on the dispatcher.
        if (changed || _dirty || _tick % SafetyNetTicks == 0 || RampDrifted())
        {
            Push();
        }
    }

    private static Ramp NewRamp() => new()
    {
        Red = new ushort[RampSize],
        Green = new ushort[RampSize],
        Blue = new ushort[RampSize]
    };

    /// <summary>Three channels end to end, which is the shape the store writes.</summary>
    private static ushort[] Flatten(Ramp ramp)
    {
        ushort[] all = new ushort[GammaBaselineStore.Values];
        Array.Copy(ramp.Red, 0, all, 0, RampSize);
        Array.Copy(ramp.Green, 0, all, RampSize, RampSize);
        Array.Copy(ramp.Blue, 0, all, RampSize * 2, RampSize);
        return all;
    }

    private static Ramp Unflatten(ushort[] all)
    {
        Ramp ramp = NewRamp();
        Array.Copy(all, 0, ramp.Red, 0, RampSize);
        Array.Copy(all, RampSize, ramp.Green, 0, RampSize);
        Array.Copy(all, RampSize * 2, ramp.Blue, 0, RampSize);
        return ramp;
    }

    /// <summary>
    /// Files what the driver reports straight after a write as the table the lock
    /// should expect to find. A failed write, or a read that fails, files nothing,
    /// which leaves that screen to the periodic push rather than to a comparison
    /// with nothing behind it.
    /// </summary>
    private void RecordBaseline(string device, IntPtr hdc, bool wrote)
    {
        Ramp after = NewRamp();
        bool read = wrote && GetDeviceGammaRamp(hdc, ref after);

        lock (_gate)
        {
            if (read)
            {
                _baselines[device] = after;
            }
            else
            {
                _baselines.Remove(device);
            }
        }
    }

    /// <summary>
    /// True when a screen's live table no longer matches what the driver reported
    /// after this app last wrote it, which is to say something else has replaced it.
    /// A screen that cannot be opened or read is no evidence either way and does not
    /// count; the periodic push covers it.
    /// </summary>
    private bool RampDrifted()
    {
        KeyValuePair<string, Ramp>[] expected;
        lock (_gate)
        {
            expected = _baselines.ToArray();
        }

        foreach (KeyValuePair<string, Ramp> pair in expected)
        {
            IntPtr hdc = CreateDC("DISPLAY", pair.Key, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                Ramp live = NewRamp();
                if (!GetDeviceGammaRamp(hdc, ref live))
                {
                    continue;
                }

                if (!RampsMatch(pair.Value.Red, live.Red, DriftTolerance)
                    || !RampsMatch(pair.Value.Green, live.Green, DriftTolerance)
                    || !RampsMatch(pair.Value.Blue, live.Blue, DriftTolerance))
                {
                    // Said at most once in a while: a program that keeps resetting the
                    // ramp would otherwise put a line in the log every tick.
                    if (DateTime.UtcNow - _lastDriftLogged > TimeSpan.FromSeconds(30))
                    {
                        _lastDriftLogged = DateTime.UtcNow;
                        TraceLog.Write("DISPLAY lock: ramp changed under us on " + pair.Key + ", restoring");
                    }

                    return true;
                }
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        return false;
    }

    /// <summary>Whether two channels agree everywhere to within a tolerance.</summary>
    internal static bool RampsMatch(ushort[] expected, ushort[] live, int tolerance)
    {
        if (expected is null || live is null || expected.Length != live.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - live[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }
}
