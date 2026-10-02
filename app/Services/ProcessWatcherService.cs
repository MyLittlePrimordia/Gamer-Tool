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

    /// <summary>
    /// Whether this window covers its entire monitor.
    /// <para>
    /// The shape that means a game is running. A maximised browser does not: it
    /// stops at the work area and leaves the taskbar showing, so the test has to
    /// compare against the monitor rectangle rather than the work area, or half the
    /// desktop would qualify.
    /// </para>
    /// <para>
    /// Carried on the window rather than asked for separately because the handle
    /// is the only reliable way to find out, and by the time a caller has the
    /// strings above, the foreground may have moved on. False when it cannot be
    /// determined, which is the answer that keeps a wildcard slot standing down
    /// rather than guessing.
    /// </para>
    /// </summary>
    public bool IsFullscreen { get; set; }
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

    /// <summary>
    /// Processes to watch for exit even though no slot is bound to them.
    /// <para>
    /// Added to by the wildcard when it claims a program. Everything here is the
    /// same shape as the watch list for the rest of the scan: alive is built from
    /// it, and a name that stops being alive raises an exit. Without this a
    /// wildcard-matched game was never in either set, so its exit was never
    /// noticed and the profile stayed boosted for whatever ran next.
    /// </para>
    /// <para>
    /// Deliberately separate from <see cref="_knownProcesses"/> rather than added
    /// to it. That list is rebuilt from the slots every time the watch state is
    /// applied, and a wildcard contributes nothing to it by design, so anything put
    /// there would be swept away on the next edit to any slot.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _extraWatched = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Watches a process the slot list never named, so its exit is still noticed.
    /// <para>
    /// Called when a wildcard claims a program. Returns the name it is watching, or
    /// empty when a different wildcard process was already being watched - there is
    /// only ever one, so a second would leave the first's exit unmonitored and the
    /// revert keyed to the wrong process.
    /// </para>
    /// </summary>
    public string WatchUnbound(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        foreach (string watched in _extraWatched)
        {
            if (!string.Equals(watched, processName, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        _extraWatched.Add(processName);

        // Marked as already announced, so the next scan does not raise a launch for
        // it. It was not launched - the app matched it on the foreground window
        // while it was already running - and announcing it would start a second
        // apply for something already applied.
        _firedProcesses.Add(processName);

        TraceLog.Write("WATCH unbound " + processName);
        return processName;
    }

    /// <summary>Stops watching an unbound process. Called when the wildcard reverts.</summary>
    public void ForgetUnbound(string processName)
    {
        if (!string.IsNullOrWhiteSpace(processName)
            && _extraWatched.Remove(processName))
        {
            _firedProcesses.Remove(processName);
            TraceLog.Write("WATCH unbound released " + processName);
        }
    }

    public void PrimeProcesses(IEnumerable<string> names)
    {
        _knownProcesses.Clear();

        // The unbound watch goes with it. This is reached from ApplyWatchState, which
        // fires on any edit to any slot, so a wildcard's process - which is whatever
        // game happened to be fullscreen when it applied - would otherwise be watched
        // for the rest of the session. Its eventual exit would then raise a revert
        // for a preset the user had since moved on from.
        _extraWatched.Clear();

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
        // Both lists, not just the slot-derived one. A wildcard armed with no other
        // game bound contributes nothing to _knownProcesses, and returning early on
        // that alone meant the scan never ran at all - so the exit of the very
        // process it had claimed was never noticed.
        if (_knownProcesses.Count == 0 && _extraWatched.Count == 0)
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
                // Per process, not per pass. Reading ProcessName throws for a
                // process that exited between GetProcesses() and this line, and
                // the only handler was the outer catch - which abandoned the rest
                // of the loop *and* the exit-detection loop below it. So one dying
                // process anywhere in the list suppressed every launch announcement
                // and every auto-revert for that pass, and a game quitting at that
                // moment stayed applied until the next scan three seconds later.
                //
                // The pass is long enough for that to happen routinely on a busy
                // machine, which is why it was never diagnosed as a bug: from the
                // outside it looks like the watcher being slow.
                try
                {
                    string name = process.ProcessName;
                    if (!_knownProcesses.Contains(name) && !_extraWatched.Contains(name))
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
                catch (Exception ex)
                {
                    // One process we could not read. The pass continues.
                    TraceLog.Write("SCAN one process", ex);
                }
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

                // Released before the event is raised, not after. The handler for a
                // wildcard exit reverts the preset and stands the auto-apply guard
                // down, which will call back in here - and a watch entry still
                // present would be released by that, making the removal below a
                // no-op on an entry that had already gone.
                _extraWatched.Remove(gone);

                // Per item, not one try around the lot. A revert touches WPF, does
                // a synchronous profile save, and marshals a hardware write onto a
                // worker, so it is the kind of code that throws - and a throw here
                // used to abandon the rest of the loop. Three games closing together
                // would then revert one of them and silently leave the other two
                // sitting in their presets on the desktop, with a single log line
                // naming a scan rather than the games that were missed.
                //
                // So one failing revert costs that revert and nothing else.
                try
                {
                    TargetExited?.Invoke(gone);
                }
                catch (Exception ex)
                {
                    TraceLog.Write("SCAN exit " + gone, ex);
                }
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

    /// <summary>
    /// The processes watched for exit that no slot is bound to.
    /// <para>
    /// For tests. The distinction that matters - a wildcard's process being in the
    /// exit path at all - cannot be observed from outside without watching a
    /// process start and stop, which is slow, racy, and does not work in a
    /// sandbox. These two expose the two sets so a test can assert the membership
    /// that the exit logic actually depends on.
    /// </para>
    /// </summary>
    internal IReadOnlyCollection<string> WatchedNamesForTest => _extraWatched.ToArray();

    /// <summary>Every process the scan currently considers, from either list.</summary>
    internal IReadOnlyCollection<string> TrackedNamesForTest
    {
        get
        {
            HashSet<string> all = new(_knownProcesses, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(_extraWatched);
            return all.ToArray();
        }
    }

    public void Reset()
    {
        _lastKey = string.Empty;
        _knownProcesses.Clear();
        _firedProcesses.Clear();

        // Included because Reset is what ApplyWatchState calls on both arms of a
        // turn. An unbound watch that survived it would keep raising exits for a
        // program the app has stopped tracking, and the exit handler would revert a
        // preset the user had already moved on from.
        _extraWatched.Clear();
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
                ProcessName = name,
                IsFullscreen = CoversWholeMonitor(hwnd)
            };
        }
        catch (Exception ex)
        {
            TraceLog.Write("WATCH", ex);
            return null;
        }
    }

    /// <summary>
    /// How far a window's edge may sit inside the monitor's and still count as
    /// covering it.
    /// <para>
    /// Sixteen, and generous on purpose. The obvious sources of a few pixels are
    /// all real: DWM's extended frame bounds put a window's shadow *inside*
    /// <c>GetWindowRect</c> by a handful of pixels on each side, a borderless
    /// window that snaps to the work area of a display whose taskbar is set to auto
    /// hide lands exactly on the monitor rectangle, and DPI rounding at 125% or
    /// 150% quantises an edge to a whole physical pixel.
    /// </para>
    /// <para>
    /// A one pixel tolerance was the value here before, which is the number that
    /// sounds precise and is not: it fails the borderless case the feature exists
    /// for, and there is no version of a fullscreen test that a maximised window
    /// cannot be argued into, so the tolerance is not what separates the two
    /// outcomes. Coverage of the whole monitor is.
    /// </para>
    /// </summary>
    internal const int MonitorSlack = 16;

    /// <summary>
    /// Whether a window rectangle covers a monitor rectangle.
    /// <para>
    /// Split out from the Win32 call so the geometry can be tested as geometry.
    /// Every case that matters - oversized, exactly aligned, inset by a shadow, a
    /// maximised window, a degenerate rectangle - is a set of four integers, and
    /// asserting them directly is the only way to cover them without a borderless
    /// fullscreen window, which a test cannot reliably produce.
    /// </para>
    /// <para>
    /// Outward extension is unbounded and deliberately so. A window that reaches
    /// <em>past</em> the monitor still covers it, and exclusive fullscreen on some
    /// drivers reports a rectangle larger than the display by the border width. An
    /// outward cap would reject that; there is no reason to reject it, because
    /// covering the whole monitor is still the test.
    /// </para>
    /// </summary>
    internal static bool RectCoversMonitor(Rect window, Rect screen)
    {
        if (window.Right - window.Left <= 0 || window.Bottom - window.Top <= 0)
        {
            return false;
        }

        return window.Left <= screen.Left + MonitorSlack
            && window.Top <= screen.Top + MonitorSlack
            && window.Right >= screen.Right - MonitorSlack
            && window.Bottom >= screen.Bottom - MonitorSlack;
    }

    /// <summary>
    /// Whether a window covers its whole monitor, borders and all.
    /// <para>
    /// Compared against the monitor rectangle rather than the work area, and that
    /// distinction is the entire test. A maximised application stops at the work
    /// area and leaves the taskbar visible, so a work-area comparison would call
    /// every maximised window fullscreen - which for a wildcard slot would mean
    /// applying a game profile to the user's browser.
    /// </para>
    /// <para>
    /// The arithmetic itself is <see cref="RectCoversMonitor"/>, which is where the
    /// tolerance is documented and where it is tested. This is only the part that
    /// needs Win32: finding the window's rectangle and the monitor it is mostly on.
    /// </para>
    /// <para>
    /// False on any failure, and specifically when the window is minimised or
    /// off-screen: <c>GetWindowRect</c> returns a degenerate rectangle for those,
    /// which the comparison rejects rather than treating as covering nothing.
    /// </para>
    /// </summary>
    public static bool CoversWholeMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out Rect window))
        {
            return false;
        }

        // The monitor the window is mostly on, rather than the one the cursor is
        // on. They differ while a window is being dragged between displays, and
        // the window is what is being asked about.
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        MonitorInfo info = default;
        info.Size = Marshal.SizeOf<MonitorInfo>();
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // False on a degenerate rectangle, which is what GetWindowRect returns for a
        // minimised or off-screen window. A zero sized window would otherwise
        // compare equal to nothing at all.
        return RectCoversMonitor(window, info.Monitor);
    }


    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }


    private const uint MonitorDefaultToNearest = 0x00000002;


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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder text, ref int size);
}
