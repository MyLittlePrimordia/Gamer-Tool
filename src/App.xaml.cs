using System;
using System.IO;
using System.Threading;
using System.Windows;
using GamerTool.Core;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.ViewModels;

namespace GamerTool;

public partial class App : Application
{
    private const string MutexName = @"Global\GamerTool_SingleInstance";
    private Mutex? _singleInstanceMutex;

    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- Single-instance enforcement ---
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // A second launch while GamerTool is already running. A full
            // "signal the existing instance to restore its window" bridge
            // (named pipe / broadcast window message) is a Phase 5+ polish
            // item; for this phase, simply exit rather than risk two
            // processes fighting over the same gamma ramp and hotkeys.
            Shutdown();
            return;
        }

        // --- Crash-safety subsystem bootstrap, in the exact order Phase 1 requires ---

        // 1) Display gamma: capture the factory ramp before anything else touches it.
        DisplayManager.Instance.Initialize();

        // 2) Hotkeys: create the hidden message-only window so WM_HOTKEY and
        //    WM_QUERYENDSESSION have somewhere to arrive.
        HotkeyManager.Instance.Initialize();

        // 3) Wire every process/OS exit signal to DisplayManager.RestoreFactoryGamma().
        SafetyWatchdog.Instance.InstallHooks(this);

        // 4) Register the panic hotkey first, before any preset hotkey -
        //    Ctrl+Alt+R must work even if a later preset hotkey registration
        //    fails or a preset's saved binding conflicts with something else.
        HotkeyManager.Instance.RegisterPanicHotkey(() => DisplayManager.Instance.RestoreFactoryGamma());

        // --- Settings + ViewModel ---
        var settings = AppSettings.LoadOrCreateDefault();
        _viewModel = new MainViewModel(settings);
        _viewModel.RestoreSavedHotkeys();

        // --- Tray icon ---
        string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");
        TrayManager.Instance.Initialize(
            iconIdlePath: Path.Combine(assetsRoot, "tray_idle.ico"),
            iconActivePath: Path.Combine(assetsRoot, "tray_active.ico"),
            initialTooltip: "Gamer Tool");

        TrayManager.Instance.RestoreRequested += ShowMainWindow;
        TrayManager.Instance.TrayIconRightClicked += ShowTrayContextMenu;

        // --- Main window ---
        _mainWindow = new MainWindow(_viewModel);

        bool startMinimized = Array.Exists(e.Args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized)
            _mainWindow.Show();

        TrayManager.Instance.SetTooltip($"Gamer Tool - {_viewModel.SelectedDisplayPreset?.Name} / {_viewModel.SelectedAudioPreset?.Name}");
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowTrayContextMenu()
    {
        if (_viewModel is null)
            return;

        TrayManager.Instance.ShowContextMenu(new[]
        {
            new TrayMenuItemDefinition { Header = "Open Gamer Tool", OnClick = ShowMainWindow },
            new TrayMenuItemDefinition { IsSeparator = true },
            new TrayMenuItemDefinition
            {
                Header = "Run on Windows Startup",
                IsCheckable = true,
                IsChecked = _viewModel.RunOnStartup,
                OnClick = () => _viewModel.RunOnStartup = !_viewModel.RunOnStartup
            },
            new TrayMenuItemDefinition { Header = "Emergency Reset (Ctrl+Alt+R)", OnClick = () => _viewModel.PanicResetCommand.Execute(null) },
            new TrayMenuItemDefinition { IsSeparator = true },
            new TrayMenuItemDefinition { Header = "Exit", OnClick = ExitApplication }
        });
    }

    private void ExitApplication()
    {
        DisplayManager.Instance.RestoreFactoryGamma();
        HotkeyManager.Instance.Dispose();
        TrayManager.Instance.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned - e.g. exiting from the OnStartup early-return path */ }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Final safety net: even if ExitApplication() wasn't the path taken
        // (e.g. Shutdown() called from elsewhere), never let a display ramp
        // survive process exit.
        DisplayManager.Instance.RestoreFactoryGamma();
        base.OnExit(e);
    }
}
