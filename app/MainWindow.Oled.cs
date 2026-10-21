using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GamerTool.Services;

namespace GamerTool;

/// <summary>
/// The OLED guard: taking the crosshair off the screen while nobody is at the machine,
/// and putting it back when they are.
/// <para>
/// A separate file from the rest of the Crosshair tab because it is a separate job.
/// The tab decides what a crosshair looks like; this decides whether one bright shape
/// should be left lit on a panel for the length of an afternoon. Keeping them apart
/// also keeps the tab's code about drawing rather than about idleness.
/// </para>
/// <para>
/// It reads input with <c>GetLastInputInfo</c>, which is a question to Windows about
/// the last event it saw - no hook, no injection and nothing installed into another
/// process, so it changes nothing about what anti-cheat software sees. It polls every
/// fifteen seconds while a crosshair is up and once a second while the guard has it
/// down, so waking is instant and an idle machine costs one call every fifteen
/// seconds.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>When the crosshair on the screen was put there, for the "on screen" reading.</summary>
    private DateTime? _xhShownAt;

    /// <summary>True when the guard, rather than the user, is the reason nothing is showing.</summary>
    private bool _xhGuarded;

    /// <summary>Whether the guarded crosshair was put up by a slot, so it goes back the same way.</summary>
    private bool _xhGuardedBySlot;

    private DispatcherTimer? _oledWatch;

    /// <summary>The switch itself: remembered in the profile and applied to the next crosshair too.</summary>
    private void OnXhOledChanged(object sender, RoutedEventArgs e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        _settings.OledGuard = XhOledBox.IsChecked == true;
        Commit();

        // Turning it off while it is holding a crosshair back has to give it back, or the
        // switch would leave the screen empty with nothing left running to fill it.
        if (!_settings.OledGuard && _xhGuarded)
        {
            bool bySlot = _xhGuardedBySlot;
            _xhGuarded = false;
            _xhGuardedBySlot = false;
            ShowCrosshair(bySlot);
            return;
        }

        // Turning it on while a crosshair is already up starts the watch at once, so the
        // switch is not a promise about next time.
        SyncOledWatch();
    }

    /// <summary>
    /// Run the watch while there is something for it to do: a crosshair on the screen, or
    /// one it has taken down and owes back.
    /// </summary>
    private void SyncOledWatch()
    {
        bool wanted = _xhVisible || _xhGuarded;
        if (!wanted)
        {
            StopOledWatch();
            return;
        }

        if (_oledWatch is null)
        {
            _oledWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            _oledWatch.Tick += OnOledTick;
        }

        _oledWatch.Interval = _xhGuarded ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15);
        if (!_oledWatch.IsEnabled)
        {
            _oledWatch.Start();
        }
    }

    private void StopOledWatch()
    {
        _oledWatch?.Stop();
    }

    private void OnOledTick(object? sender, EventArgs e)
    {
        try
        {
            // With the switch off, the tick still runs while a crosshair is up, because
            // the tab's "on screen" reading is worth a quarter hour's tick. Nothing is
            // polled and nothing can happen: the guard is not consulted at all.
            if (!_settings.OledGuard)
            {
                UpdateXhStatus();
                return;
            }

            TimeSpan idle = IdleTime();

            if (_xhGuarded)
            {
                if (!OledGuard.ShouldWake(rested: true, idle))
                {
                    return;
                }

                // Back the way it went: a slot's crosshair is still a slot's crosshair,
                // and one the user turned on by hand is still theirs.
                bool bySlot = _xhGuardedBySlot;
                _xhGuarded = false;
                _xhGuardedBySlot = false;
                ShowCrosshair(bySlot);
                return;
            }

            if (!OledGuard.ShouldRest(
                    _settings.OledGuard, _xhVisible, GameIsFullscreen(), idle))
            {
                return;
            }

            _xhGuardedBySlot = _xhBySlot;

            // Set before the hide, because the hide is what decides whether the watch
            // keeps running: it stops it for a crosshair the user took down, and this
            // one has to come back.
            _xhGuarded = true;
            HideCrosshair();
            SyncOledWatch();
            UpdateXhStatus();
        }
        catch (Exception ex)
        {
            // Never let a guard tick escape into the dispatcher. The worst outcome
            // allowed here is that the crosshair stays where it is.
            AppLog.Error("OLED GUARD", ex);
        }
    }

    /// <summary>
    /// Whether a game covers the screen right now. A pad player reads as idle to
    /// <c>GetLastInputInfo</c>, so a fullscreen window is taken as "somebody is using
    /// this" and the guard stands down for the whole session.
    /// </summary>
    private static bool GameIsFullscreen()
    {
        try
        {
            return ProcessWatcherService.Read()?.IsFullscreen == true;
        }
        catch (Exception ex)
        {
            AppLog.Error("OLED GUARD READ", ex);
            return false;
        }
    }

    /// <summary>How long since the last keyboard or mouse event anywhere on the machine.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both sides are unsigned and both wrap after 49 days, so the subtraction is
        // done in unsigned arithmetic and only then turned into a duration.
        uint elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
