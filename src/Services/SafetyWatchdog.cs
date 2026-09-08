using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using GamerTool.Core;

namespace GamerTool.Services;

/// <summary>
/// Wires every process-level and OS-level termination signal to
/// DisplayManager.RestoreFactoryGamma(), so a custom gamma ramp is never left
/// applied after GamerTool stops running - whether that's a clean exit, an
/// unhandled exception on any thread, a system shutdown/logoff, or a console
/// control event. DisplayManager.RestoreFactoryGamma() is itself idempotent, so
/// it is safe for more than one of these hooks to fire for the same
/// termination without any coordination between them.
/// </summary>
public sealed class SafetyWatchdog
{
    private static readonly Lazy<SafetyWatchdog> _instance = new(() => new SafetyWatchdog());
    public static SafetyWatchdog Instance => _instance.Value;

    private Kernel32Native.ConsoleCtrlHandlerDelegate? _consoleHandler;
    private bool _hooksInstalled;

    private SafetyWatchdog() { }

    /// <summary>
    /// Installs every exit hook. Must be called once, early in App startup,
    /// strictly after DisplayManager.Initialize() has captured the factory
    /// ramp (there would be nothing correct to restore otherwise) and after
    /// HotkeyManager.Initialize() has created the message-only window (so
    /// SessionEnding can be subscribed to).
    /// </summary>
    public void InstallHooks(Application application)
    {
        if (_hooksInstalled)
            return;

        // Clean process exit, including normal WPF shutdown via tray "Exit".
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            RestoreAndLog("AppDomain.ProcessExit");

        // Any unhandled exception on any non-UI thread. isTerminating is
        // logged for diagnostics, but restoration happens regardless - the CLR
        // is going down either way once this fires with IsTerminating=true.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RestoreAndLog($"AppDomain.UnhandledException (terminating={args.IsTerminating})");

        // Unhandled exceptions on the WPF UI dispatcher thread. We restore
        // gamma first and deliberately do NOT set e.Handled = true - an
        // exception severe enough to reach here should still surface/terminate
        // normally rather than leave the app running in an unknown state.
        application.DispatcherUnhandledException += (_, args) =>
            RestoreAndLog($"DispatcherUnhandledException: {args.Exception.Message}");

        // Exceptions from unobserved fire-and-forget Tasks anywhere in the app,
        // including inside the audio/display engines themselves.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RestoreAndLog($"UnobservedTaskException: {args.Exception.Message}");
            args.SetObserved();
        };

        // System shutdown / logoff / restart, delivered via
        // WM_QUERYENDSESSION / WM_ENDSESSION through HotkeyManager's
        // message-only window.
        HotkeyManager.Instance.SessionEnding += () =>
            RestoreAndLog("WM_QUERYENDSESSION/WM_ENDSESSION");

        // Defense-in-depth: console control events. GamerTool is a WinExe with
        // no console, so this handler is unlikely to ever fire in practice,
        // but it costs nothing to register and covers edge-case termination
        // paths (e.g. certain process-management/service wrappers) that
        // deliver CTRL_CLOSE/LOGOFF/SHUTDOWN_EVENT to GUI-subsystem processes.
        _consoleHandler = ctrlType =>
        {
            if (ctrlType == Kernel32Native.CTRL_CLOSE_EVENT ||
                ctrlType == Kernel32Native.CTRL_LOGOFF_EVENT ||
                ctrlType == Kernel32Native.CTRL_SHUTDOWN_EVENT)
            {
                RestoreAndLog($"ConsoleCtrlHandler ({ctrlType})");
            }
            return false; // allow default OS handling to continue afterward
        };
        Kernel32Native.SetConsoleCtrlHandler(_consoleHandler, true);

        _hooksInstalled = true;
    }

    private static void RestoreAndLog(string source)
    {
        try
        {
            DisplayManager.Instance.RestoreFactoryGamma();
        }
        catch
        {
            // RestoreFactoryGamma() already swallows its own native-call
            // failures internally; this catch exists solely so that a hook
            // running during process teardown can never itself throw and mask
            // the original termination reason.
        }
        finally
        {
            DiagnosticsLog.Write($"[SafetyWatchdog] Gamma restore triggered by: {source}");
        }
    }
}

/// <summary>
/// Minimal, dependency-free append-only log for post-mortem diagnosis of crash
/// recovery. Never throws - a logging failure must never prevent or delay a
/// safety restore, so every call site here is wrapped defensively.
/// </summary>
internal static class DiagnosticsLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerTool", "diagnostics.log");

    public static void Write(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics are best-effort only.
        }
    }
}
