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
        private DispatcherTimer? _timer;


    private string _lastKey = string.Empty;

    /// <summary>
    /// The process names this app is watching for. Filled from the armed slots by
    /// <see cref="PrimeProcesses"/> and read by nothing else.
    /// </summary>
    private readonly HashSet<string> _knownProcesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names already announced through <see cref="TargetLaunched"/>.
    /// <para>
    /// This has to be a different set from the watch list. When one set did both
    /// jobs, "have I already announced this?" was really "is this name in the list
    /// of names I am watching for?", which is true for every target by
    /// construction. The answer was therefore always yes and the launch event
    /// below could never fire, for any game, in any session.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _firedProcesses = new(StringComparer.OrdinalIgnoreCase);

    private int _processTick;

    public event Action<WatchedWindow>? ForegroundChanged;

    public event Action<WatchedWindow>? TargetLaunched;

    /// <summary>
    /// A watched target that had been announced has since stopped running. Carries
    /// the process name, which is the same identity the launch event used, so a
    /// caller can match an exit against the slot it applied without holding on to
    /// a process id that may already be meaningless.
    /// </summary>
    public event Action<string>? TargetExited;

    public void PrimeProcesses(IEnumerable<string> names)
    {
        _knownProcesses.Clear();

        // A new watch list is also a new set of things not yet announced.
        // Otherwise a game that was already running when the list changed could
        // never be announced under the slot that now owns it.
        _firedProcesses.Clear();

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

        // Inside a try, which the scan below has always been and this was not.
        // A handler that throws from a DispatcherTimer tick escapes as an
        // unhandled dispatcher exception, which puts a dialog on screen in the
        // middle of a game launch and keeps the timer running, so the next tick
        // does it again. The scan's handler raises were guarded; these were the
        // ones that were not.
        try
        {
            if (window is not null)
            {
                string key = window.ExePath + "|" + window.ProcessName;
                if (!string.Equals(key, _lastKey, StringComparison.OrdinalIgnoreCase))
                {
                    _lastKey = key;
                    ForegroundChanged?.Invoke(window);
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("FOREGROUND", ex);
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

        // GetProcesses hands back a live OS handle per entry, and only the array
        // itself is collectable, so the elements are disposed here rather than
        // left to the finaliser queue. This runs every few seconds for as long as
        // the app is open.
        Process[] running = Process.GetProcesses();
        try
        {
            // Every watched name seen in this pass, whether or not it has already
            // been announced. This has to be recorded before the fired check
            // below, or a target that launched on an earlier pass would never
            // appear here and would read as having exited.
            HashSet<string> alive = new(StringComparer.OrdinalIgnoreCase);

            foreach (Process process in running)
            {
                string name = process.ProcessName;
                if (!_knownProcesses.Contains(name))
                {
                    continue;
                }

                alive.Add(name);

                if (_firedProcesses.Contains(name))
                {
                    continue;
                }

                uint pid = (uint)process.Id;
                string path = ReadImagePath(pid);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                // Recorded only once the launch is genuinely being announced, so a
                // process whose image path could not be read gets another chance
                // on the next scan instead of being silently written off.
                _firedProcesses.Add(name);
                TargetLaunched?.Invoke(new WatchedWindow
                {
                    Title = process.ProcessName,
                    ExePath = path,
                    ProcessName = name
                });
            }

            // Anything announced and not seen this time has gone. Matched on
            // process name rather than id on purpose, so a second instance of the
            // same game, or a restart, is not mistaken for an exit while it is
            // still running. The list is snapshotted because the event handler is
            // free to call back in here and re-enter, and this loop is mutating
            // the very set being walked.
            foreach (string gone in _firedProcesses.Where(n => !alive.Contains(n)).ToList())
            {
                _firedProcesses.Remove(gone);
                TargetExited?.Invoke(gone);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("SCAN", ex);
        }
        finally
        {
            foreach (Process process in running)
            {
                process.Dispose();
            }
        }
    }

    public void Reset()
    {
        _lastKey = string.Empty;
        _knownProcesses.Clear();
        _firedProcesses.Clear();
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
