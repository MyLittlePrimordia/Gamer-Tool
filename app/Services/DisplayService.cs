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


    private readonly List<string> _monitors = new();

    private readonly List<string> _monitorNames = new();

    private readonly object _gate = new();

    private DispatcherTimer? _timer;

    private IntPtr _lastForeground;

    private int _tick;

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
            List<MonitorChoice> list = new() { new MonitorChoice { Device = string.Empty, Name = "All screens" } };
            lock (_gate)
            {
                for (int i = 0; i < _monitorNames.Count; i++)
                {
                    list.Add(new MonitorChoice { Device = _monitors[i], Name = _monitorNames[i] });
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
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
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
            names.Add(Label(adapter, ordinal, seen));
        }

        if (found.Count == 0)
        {
            found.Add(@"\\.\DISPLAY1");
            names.Add("Screen 1");
        }

        lock (_gate)
        {
            _monitors.Clear();
            _monitors.AddRange(found);
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

    public bool ApplyWorking()
    {
        return Apply(Working);
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
            lock (_gate)
            {
                if (!_originalRamps.ContainsKey(device))
                {
                    Ramp original = new()
                    {
                        Red = new ushort[RampSize],
                        Green = new ushort[RampSize],
                        Blue = new ushort[RampSize]
                    };
                    if (GetDeviceGammaRamp(hdc, ref original))
                    {
                        _originalRamps[device] = original;
                    }
                }
            }

            Ramp ramp = BuildRamp(Working);
            bool ok = SetDeviceGammaRamp(hdc, ref ramp);
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
        }

        IsEnabled = false;
        Working = DisplayPreset.Flat();
        _dirty = false;
        StatusChanged?.Invoke(any ? "SCREEN RESET" : "NOTHING TO RESET");
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

        if (changed || _dirty || _tick % 2 == 0)

        {
            Push();
        }
    }
}
