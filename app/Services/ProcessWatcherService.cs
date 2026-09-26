using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GamerTool.Services;

public sealed class WatchedWindow
{
    public string Title { get; set; } = string.Empty;

    public string ExePath { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;
}

public sealed class ProcessWatcherService
{
    private const int SwSkipTaskbar = 0x400;

    private const int SwExclude = 0x400;

    private DispatcherTimer? _timer;

    private string _lastKey = string.Empty;

    private readonly HashSet<string> _knownProcesses = new(StringComparer.OrdinalIgnoreCase);

    private int _processTick;

    public event Action<WatchedWindow>? ForegroundChanged;

    public event Action<WatchedWindow>? TargetLaunched;

    public void PrimeProcesses(IEnumerable<string> names)
    {
        _knownProcesses.Clear();
        foreach (string name in names)
        {
            _knownProcesses.Add(name);
        }
    }

    public void Start(int intervalMs = 1500)
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
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
        WatchedWindow? window = Read();
        if (window is not null)
        {
            string key = window.ExePath + "|" + window.ProcessName;
            if (!string.Equals(key, _lastKey, StringComparison.OrdinalIgnoreCase))
            {
                _lastKey = key;
                ForegroundChanged?.Invoke(window);
            }
        }

        _processTick++;
        if (_processTick % 2 == 0)
        {
            ScanForLaunches();
        }
    }

    public void ScanForLaunches()
    {
        if (_knownProcesses.Count == 0)
        {
            return;
        }

        try
        {
            Process[] running = Process.GetProcesses();
            foreach (Process process in running)
            {
                string name = process.ProcessName;
                if (!_knownProcesses.Contains(name))
                {
                    continue;
                }

                if (!_knownProcesses.Add(name))
                {
                    continue;
                }

                uint pid = (uint)process.Id;
                string path = ReadImagePath(pid);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                TargetLaunched?.Invoke(new WatchedWindow
                {
                    Title = process.ProcessName,
                    ExePath = path,
                    ProcessName = name
                });
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("SCAN", ex);
        }
    }

    public void Reset()
    {
        _lastKey = string.Empty;
        _knownProcesses.Clear();
    }

    public static WatchedWindow? Read()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            if (name.Equals("GamerTool", StringComparison.OrdinalIgnoreCase)
                || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = ReadImagePath(processId);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            StringBuilder title = new(512);
            _ = GetWindowTextW(hwnd, title, 512);

            return new WatchedWindow
            {
                Title = title.ToString(),
                ExePath = path,
                ProcessName = name
            };
        }
        catch (Exception ex)
        {
            TraceLog.Write("WATCH", ex);
            return null;
        }
    }

    public static string ReadImagePath(uint processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            int size = 1024;
            StringBuilder buffer = new(size);
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return buffer.ToString(0, size);
            }

            return string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder text, ref int size);
}
