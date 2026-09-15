using System;
using System.IO;
using System.Linq;
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
        // Icons are embedded resources extracted once to %TEMP% - the tray
        // LoadImage P/Invoke needs real on-disk .ico paths.
        string? trayIdlePath = AssetPathResolver.ExtractIconToTemp("tray_idle.ico", "tray_idle.ico");
        string? trayActivePath = AssetPathResolver.ExtractIconToTemp("tray_active.ico", "tray_active.ico");
        TrayManager.Instance.Initialize(
            iconIdlePath: trayIdlePath ?? string.Empty,
            iconActivePath: trayActivePath ?? string.Empty,
            initialTooltip: "Gamer Tool");

        TrayManager.Instance.RestoreRequested += ShowMainWindow;
        TrayManager.Instance.TrayIconRightClicked += ShowTrayContextMenu;

        // Live tray: tooltip + preset icon refresh whenever the active
        // display/audio selection or combo changes.
        _viewModel.ActiveStateChanged += RefreshTrayState;

        // --- Main window ---
        _mainWindow = new MainWindow(_viewModel);

        bool startMinimized = Array.Exists(e.Args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized)
            _mainWindow.Show();

        RefreshTrayState();
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

        var vm = _viewModel;

        var displayMenu = new TrayMenuItemDefinition
        {
            Header = "Display",
            Children = vm.DisplayPresets
                .OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name)
                .Select(p => new TrayMenuItemDefinition
            {
                Header = $"{(p.IsFavorite ? "★ " : "")}{p.Name}{(p.HotkeyDisplayLabel != null ? $" ({p.HotkeyDisplayLabel})" : "")}",
                IsChecked = vm.SelectedDisplayPreset?.Id == p.Id,
                OnClick = () => { vm.ApplyDisplayPresetFromTray(p); vm.ShowToast($"🖥 {p.Name}"); }
            }).ToList()
        };

        var audioMenu = new TrayMenuItemDefinition
        {
            Header = "Audio",
            Children = _viewModel.AudioPresets
                .OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name)
                .Select(p => new TrayMenuItemDefinition
            {
                Header = $"{(p.IsFavorite ? "★ " : "")}{p.Name}{(p.HotkeyDisplayLabel != null ? $" ({p.HotkeyDisplayLabel})" : "")}",
                IsChecked = _viewModel.SelectedAudioPreset?.Id == p.Id,
                OnClick = () => { _viewModel.ApplyAudioPresetFromTray(p); _viewModel.ShowToast($"🎧 {p.Name}"); }
            }).ToList()
        };

        var combosMenu = new TrayMenuItemDefinition
        {
            Header = "Combos",
            Children = _viewModel.ComboPresets.Count == 0
                ? new List<TrayMenuItemDefinition> { new() { Header = "(none yet)" } }
                : _viewModel.ComboPresets
                    .OrderByDescending(c => c.IsFavorite).ThenBy(c => c.Name)
                    .Select(c => new TrayMenuItemDefinition
                {
                    Header = $"{(c.IsFavorite ? "★ " : "")}{c.Name}{(c.HotkeyDisplayLabel != null ? $" ({c.HotkeyDisplayLabel})" : "")}",
                    IsChecked = _viewModel.IsActiveCombo(c),
                    OnClick = () => _viewModel.ActivateCombo(c)
                }).ToList()
        };

        TrayManager.Instance.ShowContextMenu(new[]
        {
            new TrayMenuItemDefinition { Header = "Open Gamer Tool", OnClick = ShowMainWindow },
            displayMenu,
            audioMenu,
            combosMenu,
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

    /// <summary>
    /// Called by the ViewModel whenever the active display/audio preset or
    /// combo changes: refreshes the tray tooltip and swaps the tray icon to
    /// the active preset's icon (combo icon wins - it represents both).
    /// </summary>
    internal void RefreshTrayState()
    {
        if (_viewModel is null)
            return;

        string display = _viewModel.SelectedDisplayPreset?.Name ?? "None";
        string audio = _viewModel.SelectedAudioPreset?.Name ?? "None";
        string tooltip = $"Gamer Tool - {display} / {audio}";

        // Combo icon if the current pairing matches a combo, else display icon.
        var activeCombo = _viewModel.ComboPresets.FirstOrDefault(c => _viewModel.IsActiveCombo(c));
        var iconKey = activeCombo?.Icon ?? _viewModel.SelectedDisplayPreset?.Icon;

        var bitmap = IconCatalog.GetTrayBitmap(iconKey);
        if (bitmap is not null)
            TrayManager.Instance.SetIconFromPng(bitmap);
        else
            TrayManager.Instance.SetActiveState(_viewModel.SelectedDisplayPreset is { IsBuiltIn: false } || _viewModel.SelectedAudioPreset is { IsBuiltIn: false });

        TrayManager.Instance.SetTooltip(TruncateTooltip(tooltip));
    }

    private static string TruncateTooltip(string value)
        => value.Length <= 127 ? value : value[..127];

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
