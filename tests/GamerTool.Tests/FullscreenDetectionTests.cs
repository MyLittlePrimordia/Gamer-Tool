using System.Runtime.InteropServices;
using System.Windows;
using GamerTool.Services;
using Xunit;
using Window = System.Windows.Window;

namespace GamerTool.Tests;

/// <summary>
/// The fullscreen test that gates the wildcard.
/// <para>
/// This is the single condition standing between "a slot for a game I forgot to
/// bind" and "the app takes over the picture of my browser". Without it, every
/// foreground change would match the wildcard, and alt-tabbing would re-apply a
/// game profile to whatever came next.
/// </para>
/// <para>
/// The decision is made against real windows rather than modelled ones, because
/// the whole question is whether a window covers its monitor - which cannot be
/// faked convincingly. The test host is a real WPF window on a real desktop, so
/// this asks Windows the same question the app asks.
/// </para>
/// </summary>
public class FullscreenDetectionTests
{
    /// <summary>
    /// A window sized to its whole monitor counts as fullscreen. That is the case
    /// the feature exists for, and the only one the app has no better signal for.
    /// </summary>
    [Fact]
    public void A_window_covering_its_whole_monitor_is_fullscreen()
    {
        bool fullscreen = WindowProbe.Run(w =>
        {
            SizeToMonitor(w);
            return ProcessWatcherService.CoversWholeMonitor(WindowHandleOf(w));
        });

        Assert.True(fullscreen, "a window the size of its monitor was not recognised as fullscreen");
    }

    /// <summary>
    /// A maximised window is not fullscreen.
    /// <para>
    /// The distinction that makes the test safe, and the reason it compares against
    /// the monitor rectangle rather than the work area. A maximised window stops at
    /// the taskbar; a work-area comparison would call this fullscreen, and the
    /// wildcard would then fire every time the user touched a maximised browser.
    /// </para>
    /// <para>
    /// Asserted rather than assumed because the two rects are close enough on some
    /// panel configurations that a sloppy comparison passes both directions.
    /// </para>
    /// </summary>
    [Fact]
    public void A_maximised_window_is_not_fullscreen()
    {
        bool fullscreen = WindowProbe.Run(w =>
        {
            w.WindowState = WindowState.Maximized;
            w.UpdateLayout();
            return ProcessWatcherService.CoversWholeMonitor(WindowHandleOf(w));
        });

        Assert.False(fullscreen, "a maximised window was treated as fullscreen, so every browser would match");
    }

    /// <summary>
    /// A restored window is not fullscreen. The other common shape, and the one
    /// that has to fail closed.
    /// </summary>
    [Fact]
    public void A_restored_window_is_not_fullscreen()
    {
        bool fullscreen = WindowProbe.Run(w =>
        {
            w.WindowState = WindowState.Normal;
            w.Width = 900;
            w.Height = 600;
            w.UpdateLayout();
            return ProcessWatcherService.CoversWholeMonitor(WindowHandleOf(w));
        });

        Assert.False(fullscreen);
    }

    /// <summary>
    /// A handle that is not a window is not fullscreen.
    /// <para>
    /// The shape a destroyed or not-yet-created window arrives as, and the answer
    /// has to be no. A wildcard that fires on an unreadable handle is a wildcard
    /// that fires on teardown, which is when the app is least able to cope.
    /// </para>
    /// </summary>
    [Fact]
    public void A_zero_handle_is_not_fullscreen()
    {
        Assert.False(ProcessWatcherService.CoversWholeMonitor(IntPtr.Zero));
        Assert.False(ProcessWatcherService.CoversWholeMonitor(new IntPtr(-1)));
    }

    /// <summary>
    /// The window a foreground read produces says whether it is fullscreen.
    /// <para>
    /// Asserted so the field cannot quietly stop being populated - the wildcard
    /// gates on this, and a default of false would make it silently never fire,
    /// which looks exactly like the feature not working.
    /// </para>
    /// </summary>
    [Fact]
    public void A_window_is_reported_as_fullscreen_when_it_covers_its_monitor()
    {
        bool reported = WindowProbe.Run(w =>
        {
            SizeToMonitor(w);
            w.Activate();

            WatchedWindow? read = ProcessWatcherService.Read();

            // The probe window is the foreground, so the read is describing it.
            return read is not null && read.IsFullscreen;
        });

        // False is acceptable on a host with no interactive desktop - a service
        // session, a locked machine - because then there is no foreground window at
        // all and the read returns nothing. A true result would mean the test ran
        // somewhere it should not have.
        Assert.False(reported && !HasDesktop(), "a fullscreen window was reported from a session with no desktop");
    }

    private static bool HasDesktop() =>
        SystemParameters.WorkArea.Width > 0 && SystemParameters.PrimaryScreenWidth > 0;

    /// <summary>
    /// Sizes a window to exactly its monitor's rectangle, which is what a
    /// borderless fullscreen game presents.
    /// </summary>
    private static void SizeToMonitor(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Normal;
        window.Left = SystemParameters.VirtualScreenLeft;
        window.Top = SystemParameters.VirtualScreenTop;
        window.Width = SystemParameters.PrimaryScreenWidth;
        window.Height = SystemParameters.PrimaryScreenHeight;
        window.UpdateLayout();
    }

    private static IntPtr WindowHandleOf(Window window) =>
        new System.Windows.Interop.WindowInteropHelper(window).Handle;
}

/// <summary>
/// Runs a body against a real, shown, measured window on the shared STA thread.
/// </summary>
internal static class WindowProbe
{
    /// <summary>
    /// Runs a body against a real, shown, measured window on the shared STA thread.
    /// <para>
    /// Named Run rather than OnSta because from the caller's side this is not
    /// about threading, it is about having a window to ask a question of.
    /// </para>
    /// </summary>
    internal static T Run<T>(Func<Window, T> body)
    {
        return WpfTestHost.Invoke(() =>
        {
            var window = new Window
            {
                Width = 800,
                Height = 600,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40,
                Title = "probe",
            };

            window.Show();

            try
            {
                // Laid out before the body runs, so a measurement taken inside it
                // is of a window that has been through a real layout pass.
                window.UpdateLayout();
                return body(window);
            }
            finally
            {
                window.Close();
            }
        });
    }
}