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

    private const int RampWords = RampSize * 3;

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

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public event Action<string>? StatusChanged;

    public event Action<DisplayPreset>? Applied;

    public bool IsGammaReady { get; private set; }

    public bool IsEnabled { get; private set; }

    public DisplayPreset Working { get; private set; } = DisplayPreset.Flat();

    public string ActiveName { get; private set; } = "STANDARD";

    public string ActiveDevice { get; private set; } = string.Empty;

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

        return found;
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

    private static double Curve(DisplayPreset preset, int index)
    {
        double norm = index / 255.0;
        double val = Math.Pow(norm, 1.0 / Math.Max(preset.Gamma, 0.1));
        if (preset.ShadowBoost > 0 && norm < 0.5)
        {
            val += (1.0 - (norm * 2.0)) * (preset.ShadowBoost / 100.0) * 0.35;
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
        ActiveName = preset.Name;
        ActiveDevice = device ?? string.Empty;
        _dirty = true;
        bool ok = Push();
        TraceLog.Write("DISPLAY APPLY " + preset.Name + " scope=" + (ActiveDevice.Length == 0 ? "ALL" : ActiveDevice)
            + " gamma=" + preset.Gamma.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + " result=" + ok);
        if (ok)
        {
            IsEnabled = true;
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
        IReadOnlyList<string> all = Monitors.Count > 0 ? Monitors : ScanMonitors();
        List<string> targets = new();
        if (!string.IsNullOrWhiteSpace(ActiveDevice))
        {
            if (all.Contains(ActiveDevice, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(ActiveDevice);
            }
            else
            {
                foreach (string device in all)
                {
                    if (device.EndsWith(ActiveDevice, StringComparison.OrdinalIgnoreCase))
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
            IsGammaReady = true;
        }

        return any;
    }

    public bool PushDevice(string device)
    {
        IntPtr hdc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        }

        if (hdc == IntPtr.Zero)
        {
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

        IsEnabled = false;
        Working = DisplayPreset.Flat();
        ActiveName = "STANDARD";
        _dirty = false;
        StatusChanged?.Invoke(any ? "SCREEN RESET" : "SCREEN RESET");
        return any;
    }

    public void SetLock(bool enabled)
    {
        _lockEnabled = enabled;
    }

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

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_lockEnabled)
        {
            return;
        }

        _tick++;
        IntPtr foreground = GetForegroundWindow();
        bool changed = foreground != _lastForeground;
        if (changed)
        {
            GetWindowThreadProcessId(foreground, out _);
        }

        _lastForeground = foreground;
        if (changed || _dirty || _tick % 2 == 0)
        {
            Push();
        }
    }
}
