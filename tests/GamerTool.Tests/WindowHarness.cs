using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GamerTool;
using GamerTool.Models;
using GamerTool.Services;

namespace GamerTool.Tests;

/// <summary>
/// Everything a test may set up before the window exists.
/// <para>
/// The window reads its profile and its picture folder as it is constructed, so
/// anything a test wants it to open on has to be in place first. This is that gap:
/// the temporary profile, the picture and pack folders, and the settings object
/// that is about to be saved into it.
/// </para>
/// </summary>
internal sealed class WindowSetup
{
    internal WindowSetup(string root)
    {
        ProfileFolder = Path.Combine(root, "profile");
        CrosshairsFolder = Path.Combine(root, "crosshairs");
        PackFolder = Path.Combine(root, "pack");
        Directory.CreateDirectory(CrosshairsFolder);
        Directory.CreateDirectory(PackFolder);

        // Current, so the profile is not treated as an old one and migrated on load.
        Settings = new AppSettings { Schema = AppSettings.CurrentSchema };

        // No toast. A toast is a real window shown on the user's screen, and a test
        // that is not about the toast has no business putting one there. A test that
        // is about it can switch it back on.
        Settings.ShowOsd = false;

        // An engine path that is plausible and does not exist: absolute, named
        // FxSound.exe, under this run's temp folder. IsInstalled is a file check, so
        // this is what turns every engine call a tab makes - a live tune, a reset -
        // into a no-op instead of a real --power=0 against the user's FxSound.
        Settings.FxSoundPath = Path.Combine(root, "no-engine", "FxSound.exe");
    }

    public AppSettings Settings { get; }

    public string ProfileFolder { get; }

    public string CrosshairsFolder { get; }

    public string PackFolder { get; }
}

/// <summary>
/// A real <see cref="MainWindow"/> with its markup loaded, one or more tabs brought
/// up, and a profile and picture folder in a temporary directory.
/// <para>
/// This exists because the tabs were the one part of the app checked only by opening
/// it and looking: their switches, sliders and buttons all run through private
/// handlers and real routed events that no unit test touched. A test of the services
/// underneath cannot see a switch wired backwards, a button pointing at the wrong
/// handler, or a dialog whose confirm button does nothing.
/// </para>
/// <para>
/// What it deliberately does not do: show a window, load the user's profile, put an
/// icon in the tray, register a hotkey, read a display, start the audio engine, or
/// touch the user's own crosshair or pack folders. The window is the internal
/// constructor built for this, the profile is a temp folder, and
/// <see cref="CrosshairImageStore.RedirectTo"/> points the picture store at a temp
/// folder too.
/// </para>
/// <para>
/// Which tabs come up is the caller's choice (<see cref="MainWindowTabs"/>); the
/// rest stay present and inert, so a failure in a test is about the tab it asked
/// for. Tab-specific operations live in a driver beside this file - see
/// <see cref="CrosshairTab"/> - and reach the window through the helpers here.
/// </para>
/// </summary>
internal sealed class WindowHarness : IDisposable
{
    private readonly string _root;
    private readonly Dispatcher _dispatcher;
    private readonly WindowSetup _setup;
    private bool _disposed;

    private WindowHarness(MainWindowTabs tabs, Action<WindowSetup>? setup)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _root = Path.Combine(Path.GetTempPath(), "gamertool-window-" + Guid.NewGuid().ToString("N")[..8]);
        _setup = new WindowSetup(_root);
        setup?.Invoke(_setup);

        var profiles = new ProfileManager(_setup.ProfileFolder);
        profiles.Save(_setup.Settings);

        Tabs = tabs;
        CrosshairImageStore.RedirectTo(_setup.CrosshairsFolder);

        try
        {
            EnsureTheme();
            Window = new MainWindow(profiles, tabs);
        }
        catch (Exception)
        {
            // A construction that fails before the harness exists must not leave the
            // picture store pointed at a folder that is about to be deleted, or every
            // later test in the run would be drawing from nowhere.
            CrosshairImageStore.RedirectTo(null);

            // Nor leave the folder behind: nothing will dispose this harness, so this
            // is the only chance its temp profile has to go.
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not what the failure is about.
            }

            throw;
        }

        Settings = Read<AppSettings>("_settings");
    }

    public MainWindow Window { get; }

    public MainWindowTabs Tabs { get; }

    /// <summary>The window's own settings object, which is the one its handlers write to.</summary>
    public AppSettings Settings { get; }

    public string Root => _root;

    public string ProfileFolder => _setup.ProfileFolder;

    public string CrosshairsFolder => _setup.CrosshairsFolder;

    public string PackFolder => _setup.PackFolder;

    /// <summary>
    /// Stand a harness up, run <paramref name="body"/> on it, and take it down. Runs
    /// on the one WPF thread, because every control in the window belongs to it.
    /// </summary>
    public static void Run(
        MainWindowTabs tabs,
        Action<WindowHarness> body,
        Action<WindowSetup>? setup = null)
    {
        WpfTestHost.Invoke(() =>
        {
            using var harness = new WindowHarness(tabs, setup);
            body(harness);
        });
    }

    /// <summary>
    /// Choose a preset in one of the two preset boxes, the way the dropdown does, so
    /// the selection change reaches the handler that loads it. Selecting the item a
    /// test asks for by id rather than by index keeps the built-in list from deciding
    /// what a test is pointing at.
    /// </summary>
    public static void PickPreset(System.Windows.Controls.ComboBox box, string id)
    {
        MainWindow.PresetChoice choice = box.ItemsSource
            .Cast<MainWindow.PresetChoice>()
            .First(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        box.SelectedItem = choice;
    }

    /// <summary>The preset the box is showing, which is what the user reads.</summary>
    public static string SelectedPresetId(System.Windows.Controls.ComboBox box) =>
        (box.SelectedItem as MainWindow.PresetChoice)?.Id ?? string.Empty;

    /// <summary>Press a button the way a mouse does, so its real handler runs.</summary>
    public static void Click(System.Windows.Controls.Primitives.ButtonBase button)
    {
        button.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
    }

    /// <summary>
    /// Run the dispatcher until <paramref name="condition"/> holds, or give up. Work
    /// that is handed to a worker thread and comes back through the dispatcher - a
    /// pack tile decoding its frames, for one - needs this between the action and the
    /// assertion, or the test would be asserting on the state before the work.
    /// </summary>
    public void PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("The window did not get there in " + timeoutMs + " ms.");
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(20),
                DispatcherPriority.Background,
                (_, _) => frame.Continue = false,
                _dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    // -------------------------------------------------------------- reflection

    /// <summary>
    /// Read a private field of the window as the type it really is. The few that are
    /// read are named in the drivers, and each one fails with a sentence rather than a
    /// <see cref="NullReferenceException"/> if it is ever renamed.
    /// </summary>
    public T Read<T>(string name)
    {
        return (T)Field(name).GetValue(Window)!;
    }

    /// <summary>Read a private field that may legitimately be null yet.</summary>
    public object? Peek(string name) => Field(name).GetValue(Window);

    /// <summary>Call a private method of the window, for the handlers that have no control.</summary>
    public object? Invoke(string name, params object?[]? arguments)
    {
        // Looked up rather than `?.Invoke(...) ?? throw`, because a void method
        // returns null and would have been reported as missing.
        MethodInfo method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow." + name + "() is gone; the harness needs updating.");

        return method.Invoke(Window, arguments);
    }

    private static FieldInfo Field(string name)
    {
        return typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow." + name + " is gone; the harness needs updating.");
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// The window's markup uses the theme's styles and brushes, and the app supplies
    /// them from App.xaml at launch. The test host has a bare <see cref="Application"/>,
    /// so the theme is merged here, the way every other markup test does it.
    /// </summary>
    private static void EnsureTheme()
    {
        object loaded = Application.LoadComponent(new Uri("/GamerTool;component/UI/Theme.xaml", UriKind.Relative));
        if (loaded is not ResourceDictionary theme)
        {
            throw new InvalidOperationException("UI/Theme.xaml did not load as a ResourceDictionary.");
        }

        if (!Application.Current.Resources.MergedDictionaries.Contains(theme))
        {
            Application.Current.Resources.MergedDictionaries.Add(theme);
        }
    }

    // ---------------------------------------------------------------- teardown

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // The crosshair save timer runs half a second after an edit, the pack
            // clock runs while the browser is open, and the dialog clock runs during a
            // run. Any of them can outlive the test that started it, and the save one
            // would then write to a folder that has been deleted.
            foreach (string name in new[]
                     {
                         "_xhSaveTimer", "_xhPackClock", "_modalElapsed", "_previewTimer", "_liveTuneTimer"
                     })
            {
                if (typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Window)
                    is DispatcherTimer timer)
                {
                    timer.Stop();
                }
            }

            Window.XhPackPanel.Children.Clear();
            Invoke("ClearPackPreviews");
        }
        catch (Exception)
        {
            // Teardown is not where a test should fail: a leftover temp folder and a
            // stopped timer are not what the assertion was about.
        }

        CrosshairImageStore.RedirectTo(null);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not a test failure.
        }
    }
}
