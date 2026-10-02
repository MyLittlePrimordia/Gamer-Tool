using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace GamerTool;

public partial class MainWindow : Window
{
    private readonly ProfileManager _profiles = new();


    private readonly BackupService _backups = new();


    private readonly DisplayPreview _displayPreview = new();


    private readonly AudioPreviewPlayer _audioPreview = new();


    private readonly DisplayService _display = new();


    private readonly AudioService _audio = new();


    private readonly SetupService _setup = new();


    private readonly AudioDeviceManager _devices = new();


    private readonly AppLibraryService _library = new();


    private readonly ProcessWatcherService _watcher = new();


    private readonly HotkeyService _hotkeys = new();


    private readonly StartupService _startup = new();


    private readonly RetroOsd _osd = new();


    private SpectrumView? _spectrum;


    private readonly List<Slider> _bandSliders = new();


    private readonly List<TextBlock> _bandValueBlocks = new();


    private readonly List<TextBlock> _bandLabelBlocks = new();


    /// <summary>Only populated for the band counts whose centre frequencies can move.</summary>
    private readonly List<FrequencyDial> _frequencyDials = new();


    private AppSettings _settings;

    /// <summary>
    /// A one-line account of a profile that did not come from the file, shown once
    /// the window is up. Null the rest of the time, so the common launch is silent.
    /// </summary>
    private readonly string? _settingsRescue;


    private DisplayPreset _workDisplay = DisplayPreset.Flat();


    private AudioPreset _workAudio = AudioPreset.Flat();


    private List<AppCandidate> _appList = new();


    private DispatcherTimer? _stateTimer;


    private CancellationTokenSourceHolder? _install;


    private bool _updating;


    private bool _ready;


    private bool _suppressDeviceEvents;




    private string? _captureSlotId;


    private TextBox? _captureBox;

    /// <summary>
    /// True while the panic keycap on the Settings tab is the one listening.
    /// The panic key is not a slot, so capture has to be able to say which of the
    /// two it is talking to.
    /// </summary>
    private bool _capturingPanic;


    private TrayService? _tray;


    private bool _quitting;


    private string _activeDisplayId = string.Empty;


    private string _activeAudioId = string.Empty;


    private string _liveDisplayName = "NOTHING";


    private string _liveAudioName = "NOTHING";


    /// <summary>Set by the launch-time winget query in SetupService.</summary>
    private bool _fxUpdateAvailable;

    /// <summary>Keeps the launch-time query to once, whatever else asks for it.</summary>
    private bool _fxUpdateChecked;


    private string _fxUpdateVersion = string.Empty;


    private PreviewScene _previewScene = PreviewScene.Day;


    /// <summary>Which frame of the looping scene is on screen.</summary>
    private int _previewFrame;


    /// <summary>
    /// Drives the loop, and only while the looping scene is the one being shown
    /// with its tab open. Nothing else in the app has a reason to keep a picture
    /// moving in the background.
    /// </summary>
    private DispatcherTimer? _previewFrameTimer;


    /// <summary>
    /// Puts the clipboard mark back after it has swapped itself for a confirmation,
    /// so the diagnostics row never gets left showing a result from a press that
    /// has scrolled off screen.
    /// </summary>
    private DispatcherTimer? _diagFeedbackTimer;


    private string _autoSlotId = string.Empty;


    private string _autoProcess = string.Empty;


    /// <summary>
    /// The process a wildcard slot applied itself for.
    /// <para>
    /// Separate from <see cref="_autoProcess"/> because the revert cannot use that
    /// one. <see cref="OnTargetExited"/> looks the slot up by process name through
    /// <see cref="SlotService.MatchProcessName"/>, which finds nothing here: a
    /// wildcard matched a program that is not bound to any slot, so there is no
    /// entry to match. Without this the profile would stay boosted for whatever the
    /// user launched next.
    /// </para>
    /// <para>
/// Cleared by the same places that clear the rest of the auto state, so a
    /// revert cannot fire for a wildcard application the user has since overridden.
    /// </para>
    /// </summary>
    private string _autoWildcardProcess = string.Empty;


    /// <summary>
    /// The device whose panel the loaded preset put where it is, or empty when no
    /// preset owns a panel.
    /// <para>
    /// A device name rather than a bool, and that is the point of the fix. The
    /// stand-down decision needs to know whether *this* preset moved *that* panel,
    /// and the rescan needs to be able to drop the claim when that panel goes away.
    /// A bare flag can answer neither question: nothing can tell whether the panel
    /// it referred to is still attached, so ownership outlived an undock and the
    /// next stand-down pushed a detached monitor's captured baseline onto whatever
    /// display had been handed the same \\.\DISPLAYn name in the meantime.
    /// </para>
    /// <para>
    /// Recorded at apply time rather than read back off the preset on the way out,
    /// for two reasons. The preset id is overwritten by the stand-down itself
    /// before the question gets asked, so there would be nothing left to read. And
    /// the honest answer is about what actually happened to the panel, not about
    /// what a preset file says.
    /// </para>
    /// </summary>
    private string _presetOwnedPanelDevice = string.Empty;

    /// <summary>
    /// Whether the loaded preset currently owns a panel, which decides whether a
    /// stand-down restores one.
    /// </summary>
    private bool _presetOwnsPanel => _presetOwnedPanelDevice.Length > 0;

    private DateTime _autoStamp = DateTime.MinValue;


    private bool _previewQueued;


    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };


    public MainWindow()
    {
        InitializeComponent();

        Icon = IconFactory.LoadWindowIcon();

        _settings = _profiles.Load();

        // Said once, on the window, rather than nowhere. A profile that came back
        // from somewhere other than the file itself is worth knowing about, and so
        // is a profile that started from defaults because the real one was
        // damaged: without a line saying so, both look identical to the user who
        // finds their presets and slots have gone.
        _settingsRescue = _profiles.Quarantined
            ? _profiles.RecoveredFromBackup
                ? "Settings were damaged, and were restored from the backup"
                : "Settings were damaged. A copy was kept in the settings folder"
            : _profiles.RecoveredFromBackup
                ? "Settings were restored from the backup"
                : null;

        // Settings saved by an older build carry the old defaults, so they are
        // brought up to date before anything reads the active presets.
        if (_settings.Schema < AppSettings.CurrentSchema)
        {
            _settings.Migrate();
            _profiles.Save(_settings);
        }

        _audio.ExePath = _settings.FxSoundPath;
        SessionState.Current.Display = _display;
        SessionState.Current.Audio = _audio;
        _workDisplay = (FindDisplay(_settings.ActiveDisplayPresetId) ?? DisplayPreset.Flat()).Copy();
        _workAudio = (FindAudio(_settings.ActiveAudioPresetId) ?? AudioPreset.Flat()).Copy();
        _activeDisplayId = _workDisplay.Id;
        _activeAudioId = _workAudio.Id;

        BuildBandStrip(AudioPreset.PresetBandCount);
        BandCountBox.ItemsSource = AudioPreset.BandCounts.ToList();
        BandCountBox.SelectedItem = AudioPreset.PresetBandCount;
        BlueLightBox.ItemsSource = DisplayPreset.BlueLightNames.ToList();
        BlueLightBox.SelectedItem = DisplayPreset.BlueLightNames[
            Math.Clamp(_settings.BlueLightFilter, 0, DisplayPreset.BlueLightNames.Length - 1)];

        // The two hour pickers, filled here rather than in the sync below.
        // SyncNightScheduleControls only ever sets SelectedItem, and a selection
        // with nothing behind it is silently dropped - so with the list left
        // empty the two boxes rendered as empty bordered rectangles and the
        // schedule had no times to work from. Both halves of a combo are needed
        // to make one.
        NightStartBox.ItemsSource = NightSchedule.TimeLabels;
        NightEndBox.ItemsSource = NightSchedule.TimeLabels;
        BuildModal();

        _display.StatusChanged += SetRailStatus;
        _audio.StatusChanged += SetRailStatus;
        _setup.StatusChanged += SetRailStatus;
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Failed += OnHotkeyFailed;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _watcher.TargetLaunched += OnTargetLaunched;
        _watcher.TargetExited += OnTargetExited;

        PreviewKeyDown += OnPreviewKeyDown;

        // Keeps the caption buttons' focus ring for the keyboard only.
        //
        // The styles react to IsKeyboardFocusWithin, which is also true after a
        // mouse click, so clicking minimize or close drew a plate and a border
        // around a button that had already changed colour to say it was hovered.
        // The obvious XAML fix is a MultiDataTrigger also requiring
        // KeyboardNavigation.ShowKeyboardCues, which is WPF's own "did the last
        // input come from a key" flag. Do not do that. It compiles, it passes the
        // whole test suite, and the app then refuses to start with
        // "Provide value on 'System.Windows.StaticResourceExtension' threw an
        // exception" every single time. A binding on an attached property inside
        // a ControlTemplate trigger breaks template parsing at runtime in a way
        // nothing in the build catches.
        //
        // So the two are separated here instead. A button that is not focusable
        // cannot be focused, and cannot report keyboard focus, so the ring stays
        // away when the mouse pressed it. A key press makes them focusable again
        // before the tab is processed, so tabbing still reaches them, which is the
        // half that matters: close is the one control here that has to be usable
        // without a mouse.
        PreviewKeyDown += (_, _) => SetCaptionFocusable(true);
        PreviewMouseDown += (_, _) => SetCaptionFocusable(false);
        SetCaptionFocusable(true);

        WirePanicKeycap();
        Closing += OnClosing;
        Loaded += OnWindowLoaded;

        // The display-change hook. On the window's own HwndSource rather than a
        // hidden one, because this window is always alive - it hides to the tray
        // rather than closing - and a second source would be a second thing to
        // leak if the teardown order were ever wrong. Hooked here rather than in
        // OnWindowLoaded because the handle exists as soon as the source is
        // initialised and Loaded can fire more than once.
        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => _displayDebounce.Ignore = true;

        // The displays go back to the brightness they were found at on the way out, and
        // this is what puts that in front of the exit path. EmergencyReset calls it
        // before it checks anything else, because whether the gamma ramp or the
        // audio were ever touched is beside the point: a display this app dimmed
        // has to come back whatever else happened.
        //
        // Worth being exact about what that covers, because it used to be claimed
        // for a kill as well. It covers closing the window, quitting from the tray,
        // and a crash - an unhandled exception still runs the ProcessExit handler,
        // which is why these two registrations exist at all.
        //
        // It does not cover being terminated from outside: Task Manager's End Task,
        // Stop-Process -Force, another program calling TerminateProcess, or a hard
        // power cut. Those do not run managed code, so nothing here can answer
        // them, and a monitor the app had dimmed stays dimmed. There is no honest
        // way to close that gap - the process is already gone - and the
        // remembered-brightness map is not a recovery for it either, since nothing
        // reads it back at startup. Worth knowing the limit, not worth pretending
        // otherwise.
        SessionState.Current.RestoreBacklight = () => Backlight.RestoreAll();

        SyncControlsFromSettings();

        _tray = new TrayService();
        _tray.ShowRequested += RestoreFromTray;

        // Routed through the same two methods the reset button and the panic key
        // use, rather than calling the services directly. Those are documented as
        // the one definition of "back to normal", and the tray was the third way
        // in that skipped them: the screen went to neutral on the display while
        // the panel, the remembered preset id and the profile all still said
        // something else, so the app came back up on the preset the user had just
        // turned off. And the sound reset ran its engine calls on the dispatcher,
        // which is up to ten seconds of a frozen window with no sign anything was
        // happening - from a tray menu, where the window is not even on screen.
        _tray.ResetScreenRequested += () => Dispatcher.Invoke(GoScreenNeutral);
        _tray.ResetSoundRequested += () => _ = Dispatcher.InvokeAsync(async () => await GoSoundNeutralAsync());

        // The same dispatcher hop the two above use, and then ToggleSlot rather
        // than PlaySlot, so choosing a slot from the tray loads it the first
        // time and takes it back off the second, exactly as its key does.
        _tray.SlotRequested += id => _ = Dispatcher.InvokeAsync(async () =>
        {
            HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == id);
            if (slot is not null)
            {
                await ToggleSlot(slot);
            }
        });

        _tray.QuitRequested += QuitApp;

        // The output list is not filled here. Working it out means reading the
        // audio engine, which is slow, and doing that before Show() is what made
        // the window take seconds to appear. FinishStartupAsync does it once the
        // window is up. Nothing below this line depends on it.
        LoadTune(_workDisplay, _workAudio);
        RefreshPresetBoxes();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        UpdatePreviewPills();
        UpdateAudioPreviewState();
        UpdateLiveLabels();
        RenderDisplayPreview();
        _audioPreview.StateChanged += UpdateAudioPreviewState;
        // No "check the assets folder" any more. There is no assets folder: the
        // loops are read out of the executable, so the only thing that can make
        // one fail is a damaged build.
        _audioPreview.Failed += text => Flash(text + " failed, the app may be damaged", true);
        _audioPreview.Prepare();
        UpdatePreviewTrackButton();

    _spectrum = new SpectrumView(SpectrumHost, new LoopbackSampleFeed());


        _ready = true;

        SelectPage(StartPage());

        if (StartHidden())
        {
            HideToTray();
        }
    }


    private static bool StartHidden()
    {
        string[] args = Environment.GetCommandLineArgs();
        foreach (string arg in args)
        {
            if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _tray?.Show();

            // Both halves of the Audio tab's background work stop here rather than
            // only the spectrum. This used to stop the analyser on the way to the
            // tray and leave the four second poll running, so an app nobody could
            // see went on starting FxSound.exe --status about fifteen times a
            // minute for as long as it stayed resident.
            UpdateAudioTabActivity();
        }


    /// <summary>
    /// Whether anyone can actually see the Audio tab.
    /// <para>
    /// Selected, on screen, and not minimised. Deliberately does not include
    /// whether the window has focus: the case this exists for is a fullscreen
    /// game with the tool behind it, but a second monitor with the tool parked on
    /// it is the same picture - the spectrum is right there in plain sight, and
    /// freezing it because an unrelated window elsewhere took focus would be
    /// wrong.
    /// </para>
    /// </summary>
    private bool AudioTabVisible =>
        IsVisible
        && WindowState != WindowState.Minimized
        && Pages.SelectedIndex == 1;

    /// <summary>
    /// The one decision about whether the Audio tab is doing background work.
    /// <para>
    /// Two things used to be tied together by hand and drifted apart. The
    /// analyser stopped on a tab change and on a hide to the tray, but not on a
    /// minimise or on the window being covered, and the four second engine poll
    /// stopped on nothing at all. Each poll starts a FxSound.exe --status process
    /// and waits on it, and each running analyser holds a WASAPI capture open that
    /// shows up in the volume mixer as the app using audio for no visible reason.
    /// So both are now asked the same question here.
    /// </para>
    /// <para>
    /// Coming back to the tab takes one immediate read rather than waiting out the
    /// first interval, so the band frequencies under the faders are right straight
    /// away. Every event-driven read elsewhere - after an apply, a restore, an
    /// install, a device change - is untouched and still fires whether the tab is
    /// up or not, because those are answers to something the user just did rather
    /// than a background watch.
    /// </para>
    /// </summary>
    private void UpdateAudioTabActivity()
    {
        if (AudioTabVisible)
        {
            _spectrum?.Start();

            if (_stateTimer is not null && !_stateTimer.IsEnabled && _audio.IsInstalled)
            {
                _stateTimer.Start();
                _ = RefreshFxStateAsync(true);
            }

            return;
        }

        _spectrum?.Stop();
        _stateTimer?.Stop();
    }


    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();

            // Asked as a question rather than answered by looking at the tab
            // index, because arriving back on the Display tab should start
            // nothing and arriving back on the Audio tab should start both.
            UpdateAudioTabActivity();
        });
    }


    private void QuitApp()
    {
        _quitting = true;

        // Before anything else, so a fault raised by the rest of this is not shown
        // to the user on the way out.
        SessionState.Current.ShuttingDown = true;

        Show();
        _audioPreview.Pause();
        StopNightScheduleTimer();
        EmergencyReset.Run();

        // Released here rather than waiting for the close handler, so the WASAPI
        // client and the decoded MP3 go while the runtime is still healthy. Left to
        // the closing pass they are torn down later in the shutdown sequence, and
        // anything a finalizer touches during AppDomain unload is running against
        // a process that is already coming apart. Dispose is guarded, so the close
        // handler doing it again is harmless.
        _audioPreview.Dispose();

        // For the same reason the window's close path does this after the restore
        // rather than before: restoring empties the map of the brightness each
        // display was found at, and quitting through the tray without saving
        // afterwards left the last Commit's copy of it on disk for the next
        // launch to trust.
        Commit();

        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }


    /// <summary>
    /// Makes the caption buttons focusable, or not, depending on how the user is
    /// driving the window.
    /// <para>
    /// Focusable is the lever, because it is the one thing that keeps a mouse
    /// click from producing keyboard focus without needing a trigger that XAML
    /// will refuse to parse. See the constructor for why this is not a
    /// MultiDataTrigger on ShowKeyboardCues.
    /// </para>
    /// </summary>
    private void SetCaptionFocusable(bool value)
    {
        if (CaptionMinButton.Focusable == value && CaptionCloseButton.Focusable == value)
        {
            return;
        }

        CaptionMinButton.Focusable = value;
        CaptionCloseButton.Focusable = value;
    }


    /// <summary>
    /// The one place the settings switches are told what the profile says.
    /// <para>
    /// This used to be written out inline in the constructor, and a restore
    /// repeated a copy of it with five rows missing. A control left showing the
    /// old value while the profile held the new one is worse than either on its
    /// own, because the next save from any interaction writes the stale control
    /// back and silently undoes the restore. Anything that replaces the profile
    /// calls this rather than remembering which switches it has to remember.
    /// </para>
    /// <para>
    /// The startup registry entry is the exception and is asked of the system
    /// rather than the profile, because that is where the truth lives.
    /// </para>
    /// <para>
    /// Its comment used to sit above SetCaptionFocusable instead, because the two
    /// were written with one closing tag between them and the compiler read that as
    /// one doc comment on the wrong method. Which left the method a restore depends
    /// on with no documentation at all, and a second one on a method that did not
    /// want it.
    /// </para>
    /// </summary>
    private void SyncControlsFromSettings()
    {
        GammaLockBox.IsChecked = _settings.GammaLock;
        OsdBox.IsChecked = _settings.ShowOsd;
        AutoSwitchBox.IsChecked = _settings.AutoSwitch;
        AutoRevertBox.IsChecked = _settings.AutoRevertOnExit;
        FxPromptBox.IsChecked = _settings.FxPromptDisabled;
        StartHiddenBox.IsChecked = _settings.StartHidden;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        AntiClipBox.IsChecked = _settings.AntiClip;
        BypassBox.IsChecked = BypassToggle.IsEngaged(_settings.EffectsEnabled);
        LoudGuardBox.IsChecked = _settings.LoudGuard;
        HardwareBrightnessBox.IsChecked = _settings.HardwareBrightnessEnabled;

        // A machine the app retired the feature on comes back with the switch off
        // and nothing to say about it, because the reason lives in the counter
        // rather than in settings. The note is empty in that case and stays
        // collapsed; the user turns the switch on and gets the two rounds again.
        RefreshBacklightNote();

        // Asked of the registry, and the registry is the only place it lives. The
        // switch used to be mirrored into the settings file as well, which made
        // the JSON claim to hold a value nothing ever read.
        StartWithWindowsBox.IsChecked = _startup.IsEnabled;

        BlueLightBox.SelectedItem = DisplayPreset.BlueLightNames[
            Math.Clamp(_settings.BlueLightFilter, 0, DisplayPreset.BlueLightNames.Length - 1)];

        _audio.AntiClipEnabled = _settings.AntiClip;
        _audio.EffectsEnabled = _settings.EffectsEnabled;
        _audio.LoudGuardEnabled = _settings.LoudGuard;
        _display.SetLock(_settings.GammaLock);
        UpdateAntiClipReadout();
    }

    private static int StartPage()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--page", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                && page >= 0 && page <= 3)
            {
                return page;
            }
        }

        return 0;
    }


    private void SelectPage(int index)
    {
        RadioButton[] nav = { NavDisplay, NavAudio, NavHotkeys, NavSettings };
        foreach (RadioButton item in nav)
        {
            item.IsChecked = ReferenceEquals(item, nav[index]);
        }

        Pages.SelectedIndex = index;

        // The backlight is probed when its own tab is opened, not at launch and
        // not until the option is flipped. That way the I2C bus is never touched
        // before the user has actually gone looking at the thing that needs it,
        // and an option that was left on still works on the next run.
        if (index == 0 && Backlight.IsEnabled && !Backlight.HasProbed)
        {
            _ = Task.Run(() =>
            {
                Backlight.Probe();
                Dispatcher.InvokeAsync(AfterBacklightProbe);
            });
        }
        else if (index == 0)
        {
            RefreshBacklightRows();
            RefreshBacklightNote();
        }

        // The analyser only burns CPU while it is actually on screen, and the engine
        // poll only runs while there is somebody to read it. Asked as one question
        // so the two cannot come to disagree.
        UpdateAudioTabActivity();

        // Same reasoning for the looping preview: no reason to keep a picture
        // moving on a tab nobody is looking at.
        UpdatePreviewPlayback();

        if (index == 2)
        {
            BuildSlots();
            _ = WarmAppList();
        }

        if (index != 1 && _audioPreview.IsPlaying)
        {
            _audioPreview.Pause();
        }
    }


    /// <summary>
    /// Puts one line in the corner status strip.
    /// <para>
    /// Every one of these services raises its status from wherever it happens to
    /// be, and several of them are mid-way through a ConfigureAwait(false), which
    /// puts the rest of their work on a thread pool thread with no dispatcher.
    /// Writing straight to the label from there throws, and because the raise
    /// happens on the line that reports success, that exception was being read as
    /// the install failing when it had just succeeded. Anything that touches a
    /// control comes through here instead.
    /// </para>
    /// </summary>
    private void SetRailStatus(string text)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RailStatus.Text = text;
            return;
        }

        // Not called inline: a raise that happens during teardown would otherwise
        // fault, and an unhandled fault on the dispatcher is a dialog nobody asked
        // for. Losing the last status line is not worth that.
        _ = Dispatcher.BeginInvoke(new Action(() => RailStatus.Text = text));
    }


    private async System.Threading.Tasks.Task WarmAppList()
    {
        await EnsureAppList();
        if (Pages.SelectedIndex == 2)
        {
            BuildSlots();
        }
    }


    /// <summary>
    /// The source the message hook was attached to, and the delegate itself, so
    /// both can be undone on the way out.
    /// <para>
    /// RemoveHook takes the delegate rather than a token, and a delegate that is
    /// built twice is two different objects, so removing one does not remove the
    /// other. Naming the method - which is what this field holds - is what makes
    /// the removal match.
    /// </para>
    /// </summary>
    private HwndSource? _displayHookSource;

    private readonly DisplayChangeDebounce _displayDebounce = new();

    private DispatcherTimer? _displayDebounceTimer;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        _displayHookSource = source;
        source.AddHook(OnWindowMessage);

        // Started here rather than in the constructor so the debounce timer does
        // not exist until there is a window to receive the messages. One second,
        // not the 200ms the burst is measured in: short enough that the screen is
        // not visibly unboosted for long, long enough that the messages a dock
        // produces have all arrived.
        _displayDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _displayDebounceTimer.Tick += (_, _) => RunDisplayRescan();
        _displayDebounceTimer.Start();
    }

    /// <summary>
    /// The window's message hook. Only display topology is interesting; everything
    /// else is handed straight back to WPF.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WindowMessage.Matters(msg, wParam.ToInt64()))
        {
            _displayDebounce.Notify();
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Re-enumerates the displays and puts the ramp back.
    /// <para>
    /// Two things, in this order, and the second is the reason this exists rather
    /// than just a nicer display list. Windows resets the gamma ramp on a display
    /// event, so the screen the user had just set up is sitting on the OS default
    /// by the time the message arrives. The gamma lock's own timer re-pushes within
    /// 1.5 seconds, but only if it is switched on, and it re-pushes without ever
    /// re-enumerating - so a screen that had just been attached was never in the
    /// list to be pushed to. That is the whole defect: the ramp comes back for
    /// screens the app already knew about, and never for the new one.
    /// </para>
    /// <para>
    /// So the rescan runs first and the push immediately after, rather than leaving
    /// the screen wrong until the next lock tick.
    /// </para>
    /// </summary>
    private void RunDisplayRescan()
    {
        if (!_displayDebounce.IsDue)
        {
            return;
        }

        _displayDebounce.Consume();

        try
        {
            IReadOnlyList<string> before = _display.Monitors;

            _display.Rescan();

            IReadOnlyList<string> after = _display.Monitors;
            bool added = after.Count > before.Count;
            bool removed = after.Count < before.Count;

            TraceLog.Write("DISPLAY rescan: "
                + before.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + after.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (added ? " (added)" : removed ? " (removed)" : string.Empty));

            // Ownership of the panel dropped, if the panel this preset moved is not
            // in the list any more.
            //
            // Deliberately a physical fact rather than a bookkeeping one. There is
            // nothing to restore to: an undocked monitor is off the DDC/CI bus, its
            // scaler holds whatever it was last set to until it is powered off, and
            // trying anyway risks writing to a *different* panel that Windows has
            // since handed the same \\.\DISPLAYn name. Every DDC utility behaves this
            // way - a detached panel keeps its last hardware level - and that is the
            // correct trade against flashing a laptop's internal panel to full.
            //
            // So the claim is surrendered here and the stand-down leaves the panel
            // alone, rather than carrying a baseline for hardware that is no longer
            // reachable to somewhere that is.
            if (_presetOwnedPanelDevice.Length > 0
                && !after.Any(d => DisplayService.DeviceMatches(d, _presetOwnedPanelDevice)))
            {
                TraceLog.Write("backlight ownership dropped: " + _presetOwnedPanelDevice + " is no longer attached");
                _presetOwnedPanelDevice = string.Empty;
            }

            // The service's own tables, for the same reason and with more teeth.
            //
            // Dropping the claim here stops the *stand-down* from restoring. It does
            // not stop the *exit* path, which restores straight from the baseline
            // record without consulting the window at all - so the record itself has
            // to be pruned, while a rescan is already running. Otherwise a monitor
            // that is no longer attached stays in the record, "\\.\DISPLAYn" gets
            // reassigned to different hardware, and quitting writes one panel's
            // captured brightness onto another. A DDC transaction takes seconds and
            // the shutdown budget is about one, so re-probing on the way out is not
            // available; pruning here is what makes the restore's own skip mean
            // something.
            Backlight.ForgetDetached(after);

            // Read before the rebuild, because the rebuild is what destroys it.
            // A docking station is plugged in by somebody who may well have been
            // halfway through editing that exact slot row with the keyboard, and
            // rebuilding under them drops focus to nothing.
            string? focused = FocusedSlotTag();

            // Both lists have to be rebuilt from the new set: a slot row's monitor
            // dropdown is a snapshot of MonitorChoices taken when the row was built,
            // so without this the dropdown keeps offering a screen that was just
            // unplugged. BuildSlots is already the whole answer - it is what every
            // other change to the slot list calls.
            RefreshPresetBoxes();
            BuildSlots();
            RestoreSlotFocus(focused);
            RefreshTraySlots();

            // And the ramp, because Windows has just thrown it away. Only when
            // there is one of ours to put back: pushing when IsEnabled is false
            // would write a flat ramp over a screen nobody had tuned, which is the
            // mistake the gamma lock already had to be corrected for.
            if (_display.ShouldDefend)
            {
                _display.Push();
                TraceLog.Write("DISPLAY ramp re-pushed after a display change");
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("DISPLAY rescan", ex);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _display.Rescan();
        _display.StartLock();
        _display.SetLock(_settings.GammaLock);

        // After the scan, because the first thing the schedule does when it is
        // already inside its hours is push a ramp, and it needs a monitor to
        // push it to. Started here rather than in the constructor for the same
        // reason the audio state timer is: nothing below this line depends on
        // it, and a constructor that touches the display service before the
        // window is up is the thing that made the window take seconds to
        // appear.
        StartNightScheduleTimer();

        UpdateFxBanner();
        _ = FinishStartupAsync();

        // Created here, started by UpdateAudioTabActivity and not before. It used
        // to start immediately and then run for the whole life of the process,
        // whatever the user was looking at or whether the window existed at all.
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _stateTimer.Tick += (s, e) => _ = RefreshFxStateAsync(false);

        // The two moments that are neither a tab change nor a hide to the tray,
        // and so were the two the spectrum was still capturing through.
        StateChanged += (_, _) => UpdateAudioTabActivity();
        IsVisibleChanged += (_, _) => UpdateAudioTabActivity();

        UpdateAudioTabActivity();

        if (_settings.StartHidden)
        {
            WindowState = WindowState.Minimized;
        }

        if (_settings.ShowOsd)
        {
            Flash("Ready, hit a slot key to load");
        }

        // Said after the ready line rather than instead of it, because it explains
        // the state the window is now in and the ready line is just orientation.
        // Only for a profile that did not come from where it was left, so a normal
        // launch says nothing extra.
        //
        // The status strip carries it rather than the OSD because Flash is
        // suppressed when the OSD is switched off, and this is the one line that
        // must not be suppressed: without it a user whose presets and slots have
        // gone has no way to connect what they are looking at with what happened
        // to the file.
        if (!string.IsNullOrEmpty(_settingsRescue))
        {
            RailStatus.Text = _settingsRescue;
            RailStatus.Foreground = (Brush)FindResource(_profiles.Quarantined ? "Amber" : "TextMid");

            if (_settings.ShowOsd)
            {
                Flash(_settingsRescue, _profiles.Quarantined);
            }
        }

        HotkeySlot? startSlot = _settings.Slots.FirstOrDefault(s => s.Enabled && s.ApplyOnStart && s.HasWork);
        if (startSlot is not null)
        {
            PlaySlot(startSlot, announce: true);
        }

        // The spec line under the preview always shows the real values. It used to
        // be overwritten with "press apply" on a cold start, which is already said
        // by the live state on the right and by the edited dot on the preset tag.
        UpdateLiveLabels();

        // No update check here. It used to run on every launch, and it spawns
        // winget, so an app that never phones home still reached the network
        // before the user had clicked anything - on a machine where the only
        // thing the user had done was start it. The check is reachable from the
        // banner's own update button instead, so it happens when somebody asks
        // for it rather than every time.
        bool missing = !_setup.IsInstalled(_audio);
        if (missing)
        {
            PromptForFxSound();
        }
    }


    /// <summary>
    /// Asked on every launch while FxSound is missing, rather than once and never
    /// again.
    /// <para>
    /// The old one-shot flag was set before the dialog was even shown, so a single
    /// "Later" silenced the prompt permanently: uninstall FxSound a month later
    /// and the app never mentioned it again, leaving the audio tab dead with no
    /// explanation. Now "Not now" means not now, and the way to stop being asked is
    /// an explicit setting.
    /// </para>
    /// <para>
    /// Skipped when the app is starting into the tray, because a modal that yanks
    /// a hidden window onto the screen is worse than the problem it is offering to
    /// solve.
    /// </para>
    /// </summary>
    private void PromptForFxSound()
    {
        if (_settings.FxPromptDisabled)
        {
            return;
        }

        if (_settings.StartHidden || StartHidden())
        {
            return;
        }

        ShowConfirmModal(
            "FXSOUND IS NOT INSTALLED",
            "Needed for the audio engine. Without it, sound stays flat.",
            "Install FxSound",
            () => _ = InstallAsync(true),
            "Not now",
            keepOpen: true);
    }


    /// <summary>
    /// Asks winget whether FxSound has a newer build, off the UI thread.
    /// <para>
    /// This reaches the network, and it is only ever called because the user
    /// pressed the banner's update button. It used to be called on every launch as
    /// well, which put a winget call in front of a user who had done nothing but
    /// open the app.
    /// </para>
    /// <para>
    /// A launch re-checks at most once, so a second press on the button is a no-op
    /// rather than a second network call - the guard is here so that promise does
    /// not quietly depend on there being only one caller.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task CheckFxSoundUpdateAsync()
    {
        if (_fxUpdateChecked)
        {
            return;
        }

        _fxUpdateChecked = true;
        FxUpgradeButton.IsEnabled = false;

        try
        {
            FxUpdateResult? result = await System.Threading.Tasks.Task.Run(() => _setup.CheckForUpdate());
            if (result is not null)
            {
                _fxUpdateAvailable = result.UpdateAvailable;
                _fxUpdateVersion = result.AvailableVersion;
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("UPDATE CHECK", ex);
        }
        finally
        {
            FxUpgradeButton.IsEnabled = true;
            UpdateFxBanner();
        }
    }


    /// <summary>
    /// The startup work that has to talk to the audio engine.
    /// <para>
    /// Enumerating the output devices and repairing a bad default both read the
    /// engine's state file, and reading it means starting the engine, waiting for
    /// it, and then polling for the file to change. That is seconds when the
    /// engine is slow, and it was being done on the dispatcher during load, so
    /// the window could sit there unresponsive after it had already appeared.
    /// The window is on screen by now, so the work moves to a worker and the
    /// controls are filled in when it lands.
    /// </para>
    /// </summary>
    private async Task FinishStartupAsync()
    {
        try
        {
            // The blocking read, on a worker.
            List<DeviceChoice> choices = await Task.Run(BuildDeviceChoices);
            OutputRepair repair = await Task.Run(DetectOutputRepair);

            await Dispatcher.InvokeAsync(() =>
            {
                LoadDevicesFrom(choices);

                // The list is handed over so applying a startup repair cannot
                // enumerate the output devices again on the dispatcher. It used to,
                // twice, which put the freeze back after the window had appeared.
                EnsureUsableAudioOutput(repair, choices);
                _ = RefreshFxStateAsync(true);
                UpdatePreviewPlayback();
            });
        }
        catch (Exception ex)
        {
            // Includes the window shutting down underneath the read.
            TraceLog.Write("STARTUP AUDIO", ex);
        }
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton box || box.Tag is not string tag || !_ready)
        {
            return;
        }

        if (!int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return;
        }

        SelectPage(index);
    }


    // ============ custom title bar ============

    private void OnTitleBarDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // A second click while already moving; nothing to do.
        }
    }


    private void OnCaptionMinClick(object sender, RoutedEventArgs e)
    {
        // Minimising to the tray rather than the taskbar keeps the hotkeys live
        // and matches what the tray menu does.
        HideToTray();
    }


    private void OnCaptionCloseClick(object sender, RoutedEventArgs e)
    {
        // Close normally and let OnClosing decide what closing means, which is
        // hide-to-tray or shut down depending on the setting. Calling OnClosing
        // straight from here ran the teardown and then let Shutdown raise Closing
        // and run it a second time, on the way out through the non tray path.
        Close();
    }


    private IReadOnlyList<DisplayPreset> AllDisplayPresets()
    {
        List<DisplayPreset> list = new(DisplayPreset.Defaults);
        list.AddRange(_settings.CustomDisplayPresets);
        return list;
    }


    private IReadOnlyList<AudioPreset> AllAudioPresets()
    {
        List<AudioPreset> list = new(AudioPreset.Defaults);
        list.AddRange(_settings.CustomAudioPresets);
        return list;
    }


    private DisplayPreset? FindDisplay(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AllDisplayPresets().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }


    private AudioPreset? FindAudio(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AllAudioPresets().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// Rebuilds both preset dropdowns from the built-ins plus anything the user
    /// has saved, and re-selects whichever preset is loaded.
    /// </summary>
    private void RefreshPresetBoxes()
    {
        _updating = true;
        try
        {
            List<PresetChoice> display = new();
            HashSet<string> builtInDisplay = DisplayPreset.Defaults.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayPreset preset in AllDisplayPresets())
            {
                display.Add(new PresetChoice
                {
                    Kind = "display",
                    Id = preset.Id,
                    Name = preset.Name,
                    BuiltIn = builtInDisplay.Contains(preset.Id)
                });
            }

            List<PresetChoice> audio = new();
            HashSet<string> builtInAudio = AudioPreset.Defaults.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (AudioPreset preset in AllAudioPresets())
            {
                audio.Add(new PresetChoice
                {
                    Kind = "audio",
                    Id = preset.Id,
                    Name = preset.Name,
                    BuiltIn = builtInAudio.Contains(preset.Id)
                });
            }

            DisplayPresetBox.ItemsSource = display;
            AudioPresetBox.ItemsSource = audio;
            SelectChoice(DisplayPresetBox, _activeDisplayId);
            SelectChoice(AudioPresetBox, _activeAudioId);
        }
        finally
        {
            _updating = false;
        }

        UpdatePresetChrome();
    }


    private static void SelectChoice(ComboBox box, string id)
    {
        if (box.ItemsSource is not IEnumerable<PresetChoice> choices)
        {
            return;
        }

        foreach (PresetChoice choice in choices)
        {
            if (string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = choice;
                return;
            }
        }
    }


    /// <summary>
    /// Rename and delete only ever act on the user's own presets, so the two
    /// buttons are greyed out unless the loaded one is theirs.
    /// </summary>
    private void UpdatePresetChrome()
    {
        DisplayPreset? display = FindDisplay(_activeDisplayId);
        bool displayMine = _settings.CustomDisplayPresets.Any(p => p.Id == _activeDisplayId);
        DisplayRenameButton.IsEnabled = displayMine;
        DisplayDeleteButton.IsEnabled = displayMine;

        AudioPreset? audio = FindAudio(_activeAudioId);
        bool audioMine = _settings.CustomAudioPresets.Any(p => p.Id == _activeAudioId);
        AudioRenameButton.IsEnabled = audioMine;
        AudioDeleteButton.IsEnabled = audioMine;

        SaveToFxSoundButton.IsEnabled = _audio.IsInstalled;
    }


    private void LoadTune(DisplayPreset display, AudioPreset audio)
    {
        // Copied on the way in. The working tune gets renamed to "Tuned" whenever
        // a slider moves, and FindDisplay and FindAudio hand back the built-in
        // entries themselves, so without a copy that rename would land on the
        // shared list and the shipped presets would start calling themselves
        // "Tuned" for the rest of the session.
        _workDisplay = display.Copy();
        _workAudio = audio.Copy();

        int count = audio.NumBands <= 0 ? AudioPreset.PresetBandCount : audio.NumBands;
        if (_bandSliders.Count != count)
        {
            BuildBandStrip(count);
            BandCountBox.SelectedItem = count;
        }

        _updating = true;
        try
        {
            GammaSlider.Value = display.Gamma;
            ShadowSlider.Value = display.ShadowBoost;
            BrightSlider.Value = display.Brightness;
            ContrastSlider.Value = display.Contrast;
            RedSlider.Value = display.RedGain;
            GreenSlider.Value = display.GreenGain;
            BlueSlider.Value = display.BlueGain;
            ClaritySlider.Value = audio.Clarity;
            AmbienceSlider.Value = audio.Ambience;
            SurroundSlider.Value = audio.Surround;
            DynamicBoostSlider.Value = audio.DynamicBoost;
            BassBoostSlider.Value = audio.BassBoost;
            MasterGainSlider.Value = audio.MasterGain;
            LevelingSlider.Value = audio.VolumeLeveling;
            FilterQSlider.Value = audio.FilterQ;
            BalanceSlider.Value = audio.Balance;
            for (int i = 0; i < _bandSliders.Count; i++)
            {
                _bandSliders[i].Value = audio.Band(i);
            }

            // Put each frequency dial where the loaded tune says it belongs, so a
            // saved tuning comes back with its bands where they were left.
            if (_frequencyDials.Count == count && AudioPreset.HasFrequencyDial(count))
            {
                EnsureFrequencies(audio, count);
                for (int i = 0; i < _frequencyDials.Count; i++)
                {
                    AudioPreset.BandWindow window = AudioPreset.Window(count, i);
                    double step = AudioPreset.StepFromFrequency(window, audio.Frequencies[i]);
                    _frequencyDials[i].SetStep(step);
                    _frequencyDials[i].DefaultStep = AudioPreset.StepFromFrequency(window, AudioPreset.BandFrequency(count, i));
                    if (i < _bandLabelBlocks.Count)
                    {
                        _bandLabelBlocks[i].Text = AudioPreset.FormatFrequency(audio.Frequencies[i]);
                    }
                }
            }
        }
        finally
        {
            _updating = false;
        }

        // The copies made above, not the parameters. Both of these re-alias the working
        // field to whatever they are handed, so passing the originals undid the
        // copy on the very next line - and the copy is the whole reason the working
        // tune can be renamed to "Tuned" without the rename landing on a shipped
        // preset that every other part of the app also holds a reference to. The
        // protection existed and was undone one line later.
        UpdateScreenLabels(_workDisplay);
        UpdateSoundLabels(_workAudio);
        UpdatePresetChrome();

        // The curve reads the faders' own geometry, so it can only be drawn once
        // they have been laid out. Waiting for the dispatcher means the numbers are
        // final and ActualHeight is real, instead of guessing a height.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(UpdateEqCurve));
    }


    private static string Signed(double value, string format)
    {
        double rounded = Math.Round(value, 2);
        if (Math.Abs(rounded) < 0.0001)
        {
            // Formatted like any other value rather than collapsed to a bare "0".
            // The band faders, the master gain and the balance all came through
            // here while the effect readouts did not, so one panel showed "0" on
            // some rows and "0.0" on others for the same state.
            return (0.0).ToString(format, CultureInfo.InvariantCulture);
        }

        string text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return rounded > 0 ? "+" + text : text;
    }


    private void UpdateLiveLabels()
    {
        ScreenLiveText.Text = _liveDisplayName == "NOTHING" ? "Nothing applied" : _liveDisplayName;
        ScreenLiveDot.Fill = _liveDisplayName == "NOTHING"
            ? (Brush)FindResource("TextLow")
            : (Brush)FindResource("AccentDisplay");

        UpdateTrayStatus();
    }


    /// <summary>
    /// Puts what the app is currently doing into the tray tooltip.
    /// <para>
    /// The tooltip said "Gamer Tool" and nothing else, for the whole life of the
    /// app, even though the tray is the one surface the user goes to precisely
    /// when they have lost the window: it is already open, it is already in the
    /// corner, and the question at that moment is almost always "is it actually
    /// doing the thing, or did it quietly not work".
    /// </para>
    /// <para>
    /// Composed from the live preset ids rather than from remembered state, so it
    /// agrees with the on-screen label by construction. The two halves are named
    /// the way the slot board names them, and a missing half says so rather than
    /// leaving a gap that reads like a bug.
    /// </para>
    /// <para>
    /// Deliberately not driven from <see cref="Flash"/>. Every message the app
    /// raises passes through there, including one-line confirmations, so hooking
    /// it would leave the tooltip describing the last thing that happened rather
    /// than the state it is in. Errors are the exception and go through
    /// <see cref="SetTrayProblem"/>, which does write over it.
    /// </para>
    /// </summary>
    private void UpdateTrayStatus()
    {
        try
        {
            _tray?.SetStatus(
                (ActiveSlotName() is { } slot ? slot + " - " : string.Empty)
                + (PresetName(_activeDisplayId) ?? "no screen")
                + " + "
                + (PresetName(_activeAudioId) ?? "no sound"));

            RefreshTraySlots();
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }


    /// <summary>
    /// Rebuilds the slot lines in the tray menu.
    /// <para>
    /// Driven from <see cref="UpdateTrayStatus"/>, which is the one place that
    /// already runs whenever the loaded slot or either loaded preset changes.
    /// That is the whole requirement: the lines have to be right when the menu
    /// opens, and the menu can only be opened by the user, so tying this to a
    /// "something changed" event rather than to opening the menu means the work
    /// is not done on the UI thread of a hover and cannot make the menu feel slow
    /// to open.
    /// </para>
    /// <para>
    /// A slot with nothing set is still listed. Hiding it would make the menu
    /// change shape as slots are edited, and an empty slot is a slot the user
    /// made and has not finished; it says so in its own hover text rather than
    /// vanishing and leaving them wondering where it went.
    /// </para>
    /// </summary>
    private void RefreshTraySlots()
    {
        if (_tray is null)
        {
            return;
        }

        string? activeId = _settings.Slots
            .FirstOrDefault(s => s.Enabled && SlotService.IsLoaded(s, _activeDisplayId, _activeAudioId))?.Id;

        List<TraySlotEntry> entries = new(_settings.Slots.Count(s => s.Enabled));

        foreach (HotkeySlot slot in _settings.Slots)
        {
            if (!slot.Enabled)
            {
                continue;
            }

            string monitor = string.IsNullOrWhiteSpace(slot.MonitorDevice)
                ? string.Empty
                : _display.MonitorChoices
                    .FirstOrDefault(c => string.Equals(c.Device, slot.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? string.Empty;

            entries.Add(new TraySlotEntry
            {
                Id = slot.Id,
                Label = TrayService.ComposeSlotLabel(slot.Name, monitor),
                Detail = TrayService.ComposeSlotDetail(slot.Hotkey, slot.HasWork ? slot.WorkText : null, monitor),
                Active = string.Equals(slot.Id, activeId, StringComparison.OrdinalIgnoreCase)
            });
        }

        _tray.SetSlots(entries);
    }


    /// <summary>
    /// The name of the slot that matches what is loaded, or null when none does.
    /// <para>
    /// Found by asking <see cref="SlotService.IsLoaded"/> rather than by tracking
    /// a "current slot" field. A slot can be loaded by a hotkey, by clicking its
    /// row, by auto-switch when a game starts, or by restoring a profile, and
    /// remembering it in one place means the tooltip is right for three of those
    /// four and quietly wrong for the fourth. This asks the same question the
    /// toggle logic asks, so the tray cannot disagree with what pressing the key
    /// again would do.
    /// </para>
    /// <para>
    /// Enabled slots are searched first. <c>IsLoaded</c> treats a slot with only
    /// one half set as matching, so on a profile with a sound-only and a
    /// screen-only slot the first one in the list would otherwise be named
    /// regardless of which was actually pressed.
    /// </para>
    /// </summary>
    private string? ActiveSlotName()
    {
        HotkeySlot? match = _settings.Slots.FirstOrDefault(s =>
            s.Enabled && SlotService.IsLoaded(s, _activeDisplayId, _activeAudioId));

        return match?.Name;
    }


    /// <summary>
    /// Overwrites the tooltip with something that went wrong, which is more worth
    /// knowing than the current preset until it is fixed.
    /// </summary>
    private void SetTrayProblem(string message)
    {
        try
        {
            _tray?.SetStatus(message);
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }


    /// <summary>
    /// The display name for a preset id, or null when it is not one we know.
    /// <para>
    /// Searches the user's own presets as well as the built-ins, because a user
    /// who has made their own "Cyberpunk" tune is exactly the person hovering the
    /// tray to check which one is loaded, and a tooltip reading "no screen" for
    /// it would be worse than no tooltip.
    /// </para>
    /// </summary>
    private string? PresetName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _settings.CustomDisplayPresets
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? DisplayPreset.Defaults
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? _settings.CustomAudioPresets
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? AudioPreset.Defaults
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
    }


    private void Commit()
    {
        _profiles.Save(_settings);
    }


    /// <summary>
    /// One short line of confirmation. The old pair of bracketed headline and
    /// shouted subline was two rows of text for something that only ever needed to
    /// say what just happened, so it is now a single trimmed sentence.
    /// </summary>
    private void Flash(string message)
    {
        if (_settings.ShowOsd)
        {
            _osd.ShowToast(message);
        }
    }


    /// <summary>As <see cref="Flash"/>, but flagged amber for anything that failed.</summary>
    private void Flash(string message, bool warn)
    {
        if (_settings.ShowOsd)
        {
            _osd.ShowToast(message, warn);
        }

        // A warning is the one case where the tooltip should stop describing the
        // current presets and say what went wrong instead. The window may be
        // closed, the OSD may be switched off, and either way the user is not
        // being told that a value did not reach the engine. UpdateLiveLabels
        // puts the presets back the next time one actually changes.
        if (warn)
        {
            SetTrayProblem(message);
        }
    }


    /// <summary>
    /// The quiet half of an apply. The status line says which of the three
    /// outcomes it was, in three words, and the tooltip carries the list of
    /// values the engine did not take. No toast: an apply is a deliberate act,
    /// so it does not need to interrupt, but a mismatch still has to be findable
    /// without turning the sound up and down to hunt for it.
    /// </summary>
    private void ReportApply(AudioService.ApplyReport report)
    {
        string text;
        string? detail;
        bool warn;

        switch (report.Outcome)
        {
            case AudioService.ApplyOutcome.Applied:
                text = "SOUND APPLIED";
                detail = "The engine is doing everything that was asked of it.";
                warn = false;
                break;

            case AudioService.ApplyOutcome.Drifted:
                text = "SOUND DRIFTED";
                detail = "The engine did not take all of it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, report.Mismatches);
                warn = true;
                break;

            default:
                text = "SOUND FAILED";
                detail = report.Mismatches.Count == 0
                    ? "The engine did not report its state."
                    : string.Join(Environment.NewLine, report.Mismatches);
                warn = true;
                break;
        }

        RailStatus.Text = text;
        RailStatus.Foreground = warn
            ? (Brush)FindResource("Amber")
            : (Brush)FindResource("TextMid");
        RailStatus.ToolTip = detail;
    }


public sealed class DeviceChoice
{
    /// <summary>
    /// The name of the entry that means "whatever the app is set to".
    /// <para>
    /// Shared rather than written in both places. It appears in the Settings
    /// dropdown and again in every slot's output picker, and the whole point of
    /// the slot picker saying the same words is that the user recognises the
    /// row's default as the same thing as the setting it follows. Two literals
    /// that happen to match today are two literals that can stop matching.
    /// </para>
    /// </summary>
    public const string SystemDefaultName = "System default";

    public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public override string ToString()
        {
            return Name;
        }
    }


    public sealed class PresetChoice
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>True for a preset the app ships, false for one the user saved.</summary>
        public bool BuiltIn { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }


    private sealed class CancellationTokenSourceHolder
    {
        public System.Threading.CancellationTokenSource Source { get; } = new();

        public System.Threading.CancellationToken Token => Source.Token;

        public void Cancel()
        {
            Source.Cancel();
        }
    }

}
