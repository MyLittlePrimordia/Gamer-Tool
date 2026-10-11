using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GamerTool.Tests;

/// <summary>
/// The one STA thread with a WPF <see cref="Application"/> on it, shared by every
/// test class that has to stand up real WPF objects.
/// <para>
/// It has to be shared rather than per class, and that is not a style preference.
/// WPF allows exactly one <see cref="Application"/> per AppDomain, and its
/// constructor throws <see cref="InvalidOperationException"/> if a second one
/// exists. Two test classes each spinning up their own "one STA thread, one
/// Application" therefore pass alone and abort the whole run the moment the suite
/// grows a second one - the host process dies, and the failure names neither
/// class. So the Application and its dispatcher live here, once.
/// </para>
/// <para>
/// Classes using it must sit in the "wpf" collection, which is what stops two of
/// them reaching for this at the same time.
/// </para>
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();

    private static Dispatcher? _dispatcher;

    /// <summary>
    /// Runs <paramref name="body"/> on the shared STA thread and returns what it
    /// produced. Dispatcher affinity is the whole reason this exists: a
    /// <see cref="System.Windows.Controls.Grid"/> constructed on a test thread
    /// and then touched from another throws, so the work has to happen on one
    /// thread throughout rather than merely being started there.
    /// </summary>
    public static T Invoke<T>(Func<T> body)
    {
        EnsureThread();
        return _dispatcher!.Invoke(body);
    }

    public static void Invoke(Action body)
    {
        EnsureThread();
        _dispatcher!.Invoke(body);
    }

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return;
            }

            using var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "wpf test host"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }
    }
}
