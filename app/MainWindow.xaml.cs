using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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


    private readonly AudioDeviceService _audioDevices = new();


    private readonly AppLibraryService _library = new();


    private readonly ProcessWatcherService _watcher = new();


    private readonly HotkeyService _hotkeys = new();


    private readonly StartupService _startup = new();


    private readonly RetroOsd _osd = new();


    private readonly List<Slider> _bandSliders = new();


    private readonly List<TextBlock> _bandValueBlocks = new();


    private readonly List<TextBlock> _bandLabelBlocks = new();


    private readonly Dictionary<string, Border> _displayCards = new(StringComparer.OrdinalIgnoreCase);


    private readonly Dictionary<string, Border> _audioCards = new(StringComparer.OrdinalIgnoreCase);


    private readonly Dictionary<string, Border> _slotRows = new(StringComparer.OrdinalIgnoreCase);


    private AppSettings _settings;


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


    private TrayService? _tray;


    private bool _quitting;


    private string _activeDisplayId = string.Empty;


    private string _activeAudioId = string.Empty;


    private string _liveDisplayName = "NOTHING";


    private string _liveAudioName = "NOTHING";


    private bool _previewNight;


    private string _autoSlotId = string.Empty;


    private string _autoProcess = string.Empty;


    private DateTime _autoStamp = DateTime.MinValue;


    private bool _previewQueued;


    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };


    public MainWindow()
    {
        InitializeComponent();

        // Keep the native caption strip black to match the OLED window body.
        SourceInitialized += (_, _) => DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Icon = IconFactory.LoadWindowIcon();

        _settings = _profiles.Load();
        _audio.ExePath = _settings.FxSoundPath;
        SessionState.Current.Display = _display;
        SessionState.Current.Audio = _audio;
        _workDisplay = FindDisplay(_settings.ActiveDisplayPresetId) ?? DisplayPreset.Defaults[0].Copy();
        _workAudio = FindAudio(_settings.ActiveAudioPresetId) ?? AudioPreset.Defaults[0].Copy();
        _activeDisplayId = _workDisplay.Id;
        _activeAudioId = _workAudio.Id;

        BuildBandStrip(AudioPreset.PresetBandCount);
        BandCountBox.ItemsSource = AudioPreset.BandCounts.ToList();
        BandCountBox.SelectedItem = AudioPreset.PresetBandCount;
        BlueLightBox.ItemsSource = DisplayPreset.BlueLightNames.ToList();
        BlueLightBox.SelectedItem = DisplayPreset.BlueLightNames[
            Math.Clamp(_settings.BlueLightFilter, 0, DisplayPreset.BlueLightNames.Length - 1)];
        BuildModal();

        _display.StatusChanged += text => RailStatus.Text = text;
        _audio.StatusChanged += text => RailStatus.Text = text;
        _setup.StatusChanged += text => RailStatus.Text = text;
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Failed += OnHotkeyFailed;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _watcher.TargetLaunched += OnTargetLaunched;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Loaded += OnWindowLoaded;

        GammaLockBox.IsChecked = _settings.GammaLock;
        OsdBox.IsChecked = _settings.ShowOsd;
        AutoSwitchBox.IsChecked = _settings.AutoSwitch;
        StartHiddenBox.IsChecked = _settings.StartHidden;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        StartWithWindowsBox.IsChecked = _startup.IsEnabled;
        AntiClipBox.IsChecked = _settings.AntiClip;
        _audio.AntiClipEnabled = _settings.AntiClip;
        UpdateAntiClipReadout();

        _tray = new TrayService();
        _tray.ShowRequested += RestoreFromTray;
        _tray.ResetScreenRequested += () => Dispatcher.Invoke(() => _display.Reset());
        _tray.ResetSoundRequested += () => Dispatcher.Invoke(() => _ = _audio.ResetSoundAsync());
        _tray.QuitRequested += QuitApp;

        LoadDevices();
        LoadTune(_workDisplay, _workAudio);
        BuildDisplayCards();
        BuildAudioCards();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        AudioPreviewButton.Content = "▶";
        UpdatePreviewPills();
        UpdateAudioPreviewState();
        UpdateLiveLabels();
        RenderDisplayPreview();
        _audioPreview.StateChanged += UpdateAudioPreviewState;
        _audioPreview.Failed += text => Flash("[ " + text + " ]", "CHECK THE ASSETS FOLDER");
        _audioPreview.Prepare();

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
        });
    }


    private void QuitApp()
    {
        _quitting = true;
        Show();
        _audioPreview.Pause();
        EmergencyReset.Run();
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
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
        PageTitleText.Text = index switch
        {
            0 => "Display",
            1 => "Audio",
            2 => "Hotkeys",
            _ => "Settings"
        };

        HeroImage.Source = LoadTabIcon(index);

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


    private static readonly string[] TabIconFiles = { "display.png", "audio.png", "hotkeys.png", "settings.png" };


    private static readonly Dictionary<int, System.Windows.Media.ImageSource> TabIconCache = new();


    private static System.Windows.Media.ImageSource? LoadTabIcon(int index)
    {
        if (index < 0 || index >= TabIconFiles.Length)
        {
            return null;
        }

        if (TabIconCache.TryGetValue(index, out System.Windows.Media.ImageSource? cached))
        {
            return cached;
        }

        try
        {
            byte[] data = DisplayPreview.ReadAsset(TabIconFiles[index]);
            if (data.Length == 0)
            {
                return null;
            }

            System.Windows.Media.Imaging.BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            TabIconCache[index] = image;
            return image;
        }
        catch (Exception ex)
        {
            TraceLog.Write("TAB ICON", ex);
            return null;
        }
    }


    private async System.Threading.Tasks.Task WarmAppList()
    {
        await EnsureAppList();
        if (Pages.SelectedIndex == 2)
        {
            BuildSlots();
        }
    }


    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _display.ScanMonitors();
        _display.StartLock();
        _display.SetLock(_settings.GammaLock);

        UpdateFxBanner();
        LoadDevices();
        EnsureUsableAudioOutput();
        RefreshFxState(true);

        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _stateTimer.Tick += (s, e) => RefreshFxState(false);
        _stateTimer.Start();

        if (_settings.StartHidden)
        {
            WindowState = WindowState.Minimized;
        }

        if (_settings.ShowOsd)
        {
            _osd.ShowToast("[ GAMER TOOL READY ]", "HIT A SLOT KEY TO LOAD");
        }

        HotkeySlot? startSlot = _settings.Slots.FirstOrDefault(s => s.Enabled && s.ApplyOnStart && s.HasWork);
        if (startSlot is not null)
        {
            PlaySlot(startSlot, announce: true);
        }
        else
        {
            ScreenActiveSpec.Text = "NOT ON YOUR SCREEN YET - PRESS APPLY OR A SLOT KEY";
        }

        UpdateLiveLabels();
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

        if (index == Pages.SelectedIndex)
        {
            PageTitleText.Text = index switch
            {
                0 => "Display",
                1 => "Audio",
                2 => "Hotkeys",
                _ => "Settings"
            };
            return;
        }

        SelectPage(index);
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


    private UIElement PresetCard<T>(T preset, bool isMine, Dictionary<string, Border> cards, RoutedEventHandler click, RoutedEventHandler delete, RoutedEventHandler rename) where T : class
    {
        string id = GetId(preset);
        string name = GetName(preset);
        string tag = GetTag(preset);
        string spec = GetSpec(preset);

        Border card = new()
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(6),
            Padding = new Thickness(14, 12, 14, 12)
        };

        Grid layout = new();
        StackPanel content = new();
        content.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = tag, Style = (Style)FindResource("CardSub"), Margin = new Thickness(0, 3, 0, 0) });
        content.Children.Add(new TextBlock { Text = spec, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 10, Foreground = (Brush)FindResource("TextLow"), Margin = new Thickness(0, 6, 0, 0) });

        Button hit = new()
        {
            Style = (Style)FindResource("PresetCardButton"),
            Tag = id,
            Content = content,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        hit.Click += click;

        layout.Children.Add(hit);
        card.Child = layout;

        if (isMine)
        {
            StackPanel tools = new()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0)
            };
            tools.Children.Add(ChipButton("Rename", id, rename));
            tools.Children.Add(ChipButton("Delete", id, delete));
            layout.Children.Add(tools);

            Border badge = new()
            {
                Style = (Style)FindResource("Badge"),
                Background = (Brush)FindResource("TealDim"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 0, 8)
            };
            badge.Child = new TextBlock { Text = "MINE", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Teal") };
            layout.Children.Add(badge);
        }

        cards[id] = card;
        return card;
    }


    private static Button ChipButton(string text, string tag, RoutedEventHandler handler)
    {
        Button button = new()
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("GhostButton"),
            FontSize = 10,
            MinWidth = 0,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Tag = tag
        };
        button.Click += handler;
        return button;
    }


    private static string GetId<T>(T preset)
    {
        return preset switch
        {
            DisplayPreset d => d.Id,
            AudioPreset a => a.Id,
            _ => string.Empty
        };
    }


    private static string GetName<T>(T preset)
    {
        return preset switch
        {
            DisplayPreset d => d.Name,
            AudioPreset a => a.Name,
            _ => string.Empty
        };
    }


    private static string GetTag<T>(T preset)
    {
        return preset switch
        {
            DisplayPreset d => d.Tag,
            AudioPreset a => a.Tag,
            _ => string.Empty
        };
    }


    private static string GetSpec<T>(T preset)
    {
        return preset switch
        {
            DisplayPreset d => d.CompactSpec,
            AudioPreset a => a.CompactSpec,
            _ => string.Empty
        };
    }


    private void HighlightCards()
    {
        foreach (KeyValuePair<string, Border> pair in _displayCards)
        {
            bool active = string.Equals(pair.Key, _activeDisplayId, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderBrush = active ? (Brush)FindResource("Teal") : (Brush)FindResource("Line");
            pair.Value.Background = active ? (Brush)FindResource("TealDim") : (Brush)FindResource("CardBg");
        }

        foreach (KeyValuePair<string, Border> pair in _audioCards)
        {
            bool active = string.Equals(pair.Key, _activeAudioId, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderBrush = active ? (Brush)FindResource("Pink") : (Brush)FindResource("Line");
            pair.Value.Background = active ? (Brush)FindResource("PinkDim") : (Brush)FindResource("CardBg");
        }
    }


    private void LoadTune(DisplayPreset display, AudioPreset audio)
    {
        _workDisplay = display;
        _workAudio = audio;

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
        }
        finally
        {
            _updating = false;
        }

        UpdateScreenLabels(display);
        UpdateSoundLabels(audio);
    }


    private static string Signed(double value, string format)
    {
        double rounded = Math.Round(value, 2);
        if (Math.Abs(rounded) < 0.0001)
        {
            return "0";
        }

        string text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return rounded > 0 ? "+" + text : text;
    }


    private void UpdateLiveLabels()
    {
        ScreenLiveText.Text = "ON SCREEN NOW: " + _liveDisplayName;
        AudioLiveText.Text = _liveAudioName;
    }


    private void Commit()
    {
        _profiles.Save(_settings);
    }


    private void Flash(string headline, string subline)
    {
        if (_settings.ShowOsd)
        {
            _osd.ShowToast(headline, subline);
        }
    }


    public sealed class DeviceChoice
    {
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
