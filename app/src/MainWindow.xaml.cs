using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Core;
using GamerTool.ViewModels;

namespace GamerTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    private const int PreviewWidth = 360;
    private const int PreviewHeight = 203;

    // The unmodified base scene, generated procedurally once at startup.
    // Every preview update remaps this exact buffer through the current RAMP
    // lookup tables - never remapping an already-remapped frame, so repeated
    // slider moves never accumulate error.
    private byte[]? _baseScenePixels; // BGRA32
    private WriteableBitmap? _previewBitmap;
    private WriteableBitmap? _eqCurveBitmap;

    // ---------------- Accent-per-tab ----------------
    private static readonly Dictionary<AppTab, (Color Accent, Color Dim)> TabAccents = new()
    {
        [AppTab.Display] = ((Color)ColorConverter.ConvertFromString("#FF00E676"), (Color)ColorConverter.ConvertFromString("#FF0E3B24")),
        [AppTab.Audio] = ((Color)ColorConverter.ConvertFromString("#FF00B0FF"), (Color)ColorConverter.ConvertFromString("#FF0C2C3F")),
        [AppTab.Combos] = ((Color)ColorConverter.ConvertFromString("#FFFFC400"), (Color)ColorConverter.ConvertFromString("#FF3F3000")),
        [AppTab.Settings] = ((Color)ColorConverter.ConvertFromString("#FFB388FF"), (Color)ColorConverter.ConvertFromString("#FF2E2145")),
    };

    // ---------------- Audio test player ----------------
    private MediaPlayer? _testPlayer;
    private bool _testLoopActive;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        _viewModel.PreviewRampChanged += OnPreviewRampChanged;
        _viewModel.EqGainsChanged += OnEqGainsChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.PresetNameRequested += ShowPresetNameDialog;

        // initial accent + curve
        ApplyTabAccent(_viewModel.CurrentTab);
        RenderEqCurve(_viewModel.BandGains.Select(b => b.GainDb).ToArray());
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentTab))
        {
            ApplyTabAccent(_viewModel.CurrentTab);

            // The first EQ curve render can beat band loading; re-render on
            // entering the Audio tab so the canvas always matches state.
            if (_viewModel.CurrentTab == AppTab.Audio)
                RenderEqCurve(_viewModel.BandGains.Select(b => b.GainDb).ToArray());
        }
        else if (e.PropertyName == nameof(MainViewModel.BandGains))
        {
            RenderEqCurve(_viewModel.BandGains.Select(b => b.GainDb).ToArray());
        }
    }

    /// <summary>
    /// Rewrites the unfrozen AccentBrush/AccentDimBrush/AccentColor resources;
    /// every DynamicResource in the theme re-tints instantly.
    /// </summary>
    private void ApplyTabAccent(AppTab tab)
    {
        if (!TabAccents.TryGetValue(tab, out var accent))
            return;

        if (Resources["AccentBrush"] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = accent.Accent;
        if (Resources["AccentDimBrush"] is SolidColorBrush dim && !dim.IsFrozen)
            dim.Color = accent.Dim;
        if (Resources["AccentColor"] is Color c)
            Resources["AccentColor"] = accent.Accent;
        if (Resources["AccentDimColor"] is Color c2)
            Resources["AccentDimColor"] = accent.Dim;
    }

    #region Display preview

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BuildBaseScene();

        // Paint the preview with the currently-selected preset's ramp.
        var currentRamp = DisplayManager.Instance.GetCurrentRamp();
        if (currentRamp.HasValue)
            RenderRampToPreview(currentRamp.Value);
    }

    /// <summary>
    /// Loads the AI-generated FPS gameplay scene (Assets\PreviewScenes\
    /// preview_scene.png) into the raw BGRA pixel buffer the ramp remapper
    /// consumes. Falls back to the old procedural dark-corridor scene when
    /// the asset is missing or unreadable, so the preview never breaks.
    /// </summary>
    private void BuildBaseScene()
    {
        if (TryLoadSceneFromPng(out var loaded))
        {
            _baseScenePixels = loaded;
            return;
        }

        BuildProceduralFallbackScene();
    }

    private bool TryLoadSceneFromPng(out byte[] pixels)
    {
        pixels = Array.Empty<byte>();

        try
        {
            // Embedded resource - works in the single-file bundle.
            var source = AssetPathResolver.LoadPreviewScene();
            if (source is null)
                return false;

            // Scale to the preview buffer's exact dimensions, then force
            // into Bgra32 - the format the ramp remapper indexes.
            var scaled = new TransformedBitmap(source, new ScaleTransform(
                PreviewWidth / (double)source.PixelWidth,
                PreviewHeight / (double)source.PixelHeight));
            var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int stride = PreviewWidth * 4;
            pixels = new byte[PreviewHeight * stride];
            formatted.CopyPixels(pixels, stride, 0);
            return true;
        }
        catch
        {
            return false; // malformed/missing image -> procedural fallback
        }
    }

    /// <summary>
    /// Procedurally paints a dim corridor with a silhouetted "enemy" shape -
    /// the fallback dark test scene when preview_scene.png is unavailable.
    /// </summary>
    private void BuildProceduralFallbackScene()
    {
        _baseScenePixels = new byte[PreviewWidth * PreviewHeight * 4];

        for (int y = 0; y < PreviewHeight; y++)
        {
            for (int x = 0; x < PreviewWidth; x++)
            {
                int i = (y * PreviewWidth + x) * 4;

                double verticalT = y / (double)PreviewHeight;
                byte ambient = (byte)(18 + 22 * Math.Exp(-Math.Pow((verticalT - 0.25) * 4, 2)));

                byte r = ambient;
                byte g = ambient;
                byte b = (byte)Math.Min(255, ambient + 6);

                const double cx = PreviewWidth * 0.62;
                const double cyBody = PreviewHeight * 0.68;
                const double cyHead = PreviewHeight * 0.46;
                const double bodyRx = 20, bodyRy = 36, headR = 13;

                bool inBody = Math.Pow((x - cx) / bodyRx, 2) + Math.Pow((y - cyBody) / bodyRy, 2) <= 1.0;
                bool inHead = Math.Pow(x - cx, 2) + Math.Pow(y - cyHead, 2) <= headR * headR;

                if (inBody || inHead)
                {
                    r = (byte)Math.Max(0, ambient - 12);
                    g = (byte)Math.Max(0, ambient - 12);
                    b = (byte)Math.Max(0, ambient - 8);
                }

                _baseScenePixels[i + 0] = b;
                _baseScenePixels[i + 1] = g;
                _baseScenePixels[i + 2] = r;
                _baseScenePixels[i + 3] = 255;
            }
        }
    }

    private void OnPreviewRampChanged(RAMP ramp) => RenderRampToPreview(ramp);

    /// <summary>Remaps the base scene through the ramp LUTs into the preview bitmap.</summary>
    private void RenderRampToPreview(RAMP ramp)
    {
        if (_baseScenePixels is null)
            return;

        if (_previewBitmap is null)
        {
            _previewBitmap = new WriteableBitmap(PreviewWidth, PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _previewBitmap;
        }

        var output = new byte[_baseScenePixels.Length];

        for (int i = 0; i < _baseScenePixels.Length; i += 4)
        {
            byte b = _baseScenePixels[i + 0];
            byte g = _baseScenePixels[i + 1];
            byte r = _baseScenePixels[i + 2];
            byte a = _baseScenePixels[i + 3];

            output[i + 0] = (byte)(ramp.Blue[b] >> 8);
            output[i + 1] = (byte)(ramp.Green[g] >> 8);
            output[i + 2] = (byte)(ramp.Red[r] >> 8);
            output[i + 3] = a;
        }

        _previewBitmap.WritePixels(
            new Int32Rect(0, 0, PreviewWidth, PreviewHeight),
            output,
            PreviewWidth * 4,
            0);
    }

    #endregion

    #region EQ response curve

    // Frequencies mirrored from AudioManager.BandFrequenciesHz.
    private static readonly double[] CurveBandsHz = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private const double CurveQ = 1.414;

    /// <summary>
    /// Renders the combined magnitude response of the 10 cascaded peaking
    /// biquads (same RBJ coefficients the native APO uses) as a smooth
    /// log-frequency curve, filled under the line with the tab accent.
    /// </summary>
    private void OnEqGainsChanged(double[] gains) => RenderEqCurve(gains);

    private void RenderEqCurve(double[]? gains)
    {
        if (gains is null || gains.Length != CurveBandsHz.Length)
            gains = new double[CurveBandsHz.Length];

        const int W = 360, H = 203;
        if (_eqCurveBitmap is null)
        {
            _eqCurveBitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
            EqCurveImage.Source = _eqCurveBitmap;
        }

        var accent = (Color)(Resources["AccentColor"] is Color c ? c : Colors.DodgerBlue);
        var accentDim = Color.FromArgb(70, accent.R, accent.G, accent.B);

        // dB range: -15..+15 vertically (pad beyond slider range for filter ripple)
        const double DbMin = -15, DbMax = 15;
        const double FMin = 20, FMax = 20000;

        double DbToY(double db) => H - 1 - (db - DbMin) / (DbMax - DbMin) * (H - 1);

        double ResponseAt(double[] g, double f)
        {
            // Cascaded peaking EQ magnitude in dB, evaluated at f.
            double total = 0;
            const int SampleRate = 48000;
            for (int i = 0; i < CurveBandsHz.Length; i++)
            {
                double gainDb = g[i];
                if (gainDb > -0.05 && gainDb < 0.05)
                    continue;

                double A = Math.Pow(10, gainDb / 40);
                double w0 = 2 * Math.PI * CurveBandsHz[i] / SampleRate;
                double cw = Math.Cos(w0), sw = Math.Sin(w0);
                double alpha = sw / (2 * CurveQ);
                double a0 = 1 + alpha / A;

                double b0 = (1 + alpha * A) / a0, b1 = (-2 * cw) / a0, b2 = (1 - alpha * A) / a0;
                double a1 = (-2 * cw) / a0, a2 = (1 - alpha / A) / a0;

                // |H(ejw)| at w=2*pi*f/SR: numerator/denominator magnitudes
                double nr = b0 + b1 * Math.Cos(2 * Math.PI * f / SampleRate) + b2 * Math.Cos(4 * Math.PI * f / SampleRate);
                double ni = b1 * Math.Sin(2 * Math.PI * f / SampleRate) + b2 * Math.Sin(4 * Math.PI * f / SampleRate);
                double dr = 1 + a1 * Math.Cos(2 * Math.PI * f / SampleRate) + a2 * Math.Cos(4 * Math.PI * f / SampleRate);
                double di = a1 * Math.Sin(2 * Math.PI * f / SampleRate) + a2 * Math.Sin(4 * Math.PI * f / SampleRate);

                double num = Math.Sqrt(nr * nr + ni * ni);
                double den = Math.Sqrt(dr * dr + di * di);
                total += 20 * Math.Log10(num / den + 1e-12);
            }
            return total;
        }

        var pixels = new byte[W * H * 4];

        // Background: card color
        var bg = (Color)ColorConverter.ConvertFromString("#FF181B21");

        // 0 dB midline + grid lines at +-6/12 dB, faint
        void HorizontalGrid(double db, byte alphaByte)
        {
            int y = (int)Math.Round(DbToY(db));
            if (y < 0 || y >= H) return;
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                pixels[i] = (byte)(bg.B + (90 - bg.B) * alphaByte / 255);
                pixels[i + 1] = (byte)(bg.G + (90 - bg.G) * alphaByte / 255);
                pixels[i + 2] = (byte)(bg.R + (90 - bg.R) * alphaByte / 255);
                pixels[i + 3] = 255;
            }
        }
        HorizontalGrid(0, 60);
        HorizontalGrid(6, 25);
        HorizontalGrid(-6, 25);
        HorizontalGrid(12, 15);
        HorizontalGrid(-12, 15);

        // Precompute the curve path
        var curveY = new double[W];
        for (int x = 0; x < W; x++)
        {
            double f = FMin * Math.Pow(FMax / FMin, x / (double)(W - 1));
            curveY[x] = DbToY(Math.Clamp(ResponseAt(gains, f), DbMin, DbMax));
        }

        // Fill under the curve with dim accent, curve line with accent
        for (int x = 0; x < W; x++)
        {
            double y0 = DbToY(0);
            int yTop = (int)Math.Min(curveY[x], y0);
            int yBot = (int)Math.Max(curveY[x], y0);

            for (int y = yTop; y <= yBot && y < H; y++)
            {
                if (y < 0) continue;
                int i = (y * W + x) * 4;
                pixels[i] = accentDim.B;
                pixels[i + 1] = accentDim.G;
                pixels[i + 2] = accentDim.R;
                pixels[i + 3] = 255;
            }

            // curve line (2px thick)
            int yc = (int)Math.Round(curveY[x]);
            for (int dy = 0; dy <= 1; dy++)
            {
                int y = yc + dy;
                if (y < 0 || y >= H) continue;
                int i = (y * W + x) * 4;
                pixels[i] = accent.B;
                pixels[i + 1] = accent.G;
                pixels[i + 2] = accent.R;
                pixels[i + 3] = 255;
            }
        }

        _eqCurveBitmap.WritePixels(new Int32Rect(0, 0, W, H), pixels, W * 4, 0);
    }

    #endregion

    #region Audio test loop

    /// <summary>Plays/stops the sample.mp3 loop shipped with the app (Assets\sample.mp3).</summary>
    private void TestAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testLoopActive)
        {
            StopTestLoop();
            return;
        }

        try
        {
            var samplePath = AssetPathResolver.GetOrExtractSampleMp3();
            if (samplePath is null)
            {
                _viewModel.StatusMessage = "Test sample unavailable.";
                return;
            }

            _testPlayer?.Close();
            _testPlayer = new MediaPlayer();
            _testPlayer.Open(new Uri(samplePath));
            _testPlayer.MediaEnded += (_, _) =>
            {
                // Loop: seek back and replay (dispatcher-safe).
                Dispatcher.BeginInvoke(() =>
                {
                    if (_testLoopActive && _testPlayer is not null)
                    {
                        _testPlayer.Position = TimeSpan.Zero;
                        _testPlayer.Play();
                    }
                });
            };
            _testLoopActive = true;
            _testPlayer.Play();
            TestAudioButton.Content = "\u23F8  Stop";
        }
        catch
        {
            _viewModel.StatusMessage = "Could not play the test sample.";
        }
    }

    private void StopTestLoop()
    {
        _testLoopActive = false;
        _testPlayer?.Stop();
        TestAudioButton.Content = "\u25B6  Play Test";
    }

    #endregion

    #region Preset name dialog with icon picker

    /// <summary>Preset names cap at 24 chars - enough to be descriptive,
    /// short enough for the cycler, tray menu, and tray tooltip.</summary>
    private const int MaxPresetNameLength = 24;

    /// <summary>
    /// Modal dialog for naming/renaming presets: blank name field (validated
    /// live, Save disabled until non-empty, 24-char cap), plus an icon picker
    /// - a compact button that opens a flyout grid of the color preset icons,
    /// one highlighted as the selected icon. Returns (name, icon) or null
    /// when cancelled.
    /// </summary>
    private (string Name, string Icon)? ShowPresetNameDialog(string title, string? suggestedName, string? suggestedIcon)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Background = FindResource("BackgroundBrush") as Brush ?? Brushes.Black
        };

        string selectedIcon = suggestedIcon ?? "videogame";

        var accentBrush = FindResource("AccentBrush") as SolidColorBrush ?? Brushes.DodgerBlue;
        var cardBrush = FindResource("CardBrush") as SolidColorBrush ?? Brushes.DimGray;

        // ---- Name row ----
        var nameLabel = new TextBlock { Text = "Preset name:", Style = FindResource("BodyTextStyle") as Style, Margin = new Thickness(0, 0, 0, 6) };

        var textBox = new TextBox
        {
            Style = FindResource("DarkTextBoxStyle") as Style,
            MaxLength = MaxPresetNameLength
        };
        if (!string.IsNullOrWhiteSpace(suggestedName))
            textBox.Text = suggestedName;
        else
            textBox.Focus();

        // ---- Icon picker flyout ----
        var iconButton = new Button
        {
            Style = FindResource("SecondaryButtonStyle") as Style,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = Cursors.Hand
        };
        void RefreshIconButton()
        {
            var source = IconCatalog.GetIcon(selectedIcon);
            var contentPanel = new StackPanel { Orientation = Orientation.Horizontal };
            if (source is not null)
            {
                contentPanel.Children.Add(new System.Windows.Controls.Image
                {
                    Source = source,
                    Width = 18, Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                });
            }
            contentPanel.Children.Add(new TextBlock
            {
                Text = "Icon",
                VerticalAlignment = VerticalAlignment.Center
            });
            iconButton.Content = contentPanel;
        }
        RefreshIconButton();

        // Flyout panel: 4x4 grid of all catalog icons with selection highlight.
        var flyout = new Popup
        {
            PlacementTarget = iconButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };
        var flyoutBorder = new Border
        {
            Background = cardBrush,
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0)
        };
        var iconGrid = new UniformGrid { Rows = 4, Columns = 4 };
        var iconChoices = new List<(string Key, Button Btn)>();

        foreach (var key in IconCatalog.Keys)
        {
            var source = IconCatalog.GetIcon(key);
            if (source is null)
                continue;

            var choice = new Button
            {
                Content = new System.Windows.Controls.Image { Source = source, Width = 26, Height = 26 },
                Padding = new Thickness(5),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Template = (ControlTemplate)FindResource("FlatEmojiButtonTemplate")
            };
            var capturedKey = key;
            choice.Click += (_, _) =>
            {
                selectedIcon = capturedKey;
                foreach (var (_, other) in iconChoices)
                {
                    other.Background = Brushes.Transparent;
                    other.BorderBrush = Brushes.Transparent;
                }
                choice.Background = new SolidColorBrush(Color.FromArgb(60, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));
                choice.BorderBrush = accentBrush;
                RefreshIconButton();
                flyout.IsOpen = false;
            };
            iconChoices.Add((capturedKey, choice));
            iconGrid.Children.Add(choice);
        }

        // Highlight the suggested icon initially.
        var initial = iconChoices.FirstOrDefault(c => c.Key == selectedIcon);
        if (initial != default)
        {
            initial.Btn.Background = new SolidColorBrush(Color.FromArgb(60, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));
            initial.Btn.BorderBrush = accentBrush;
        }

        flyoutBorder.Child = iconGrid;
        flyout.Child = flyoutBorder;
        iconButton.Click += (_, _) => flyout.IsOpen = true;

        // ---- Buttons ----
        var okButton = new Button { Content = "Save", Style = FindResource("NeonButtonStyle") as Style, Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new Button { Content = "Cancel", Style = FindResource("SecondaryButtonStyle") as Style, Width = 84 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        // ---- Assemble ----
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(nameLabel);
        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock { Text = "Icon:", Style = FindResource("BodyTextStyle") as Style, Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(iconButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        // ---- Live validation: Save disabled until the name is valid ----
        void Validate()
            => okButton.IsEnabled = !string.IsNullOrWhiteSpace(textBox.Text);

        textBox.TextChanged += (_, _) => Validate();
        Validate(); // initial state (blank -> disabled for save-as-new)

        (string Name, string Icon)? result = null;
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text))
                return; // disabled anyway; belt and suspenders

            result = (textBox.Text.Trim(), selectedIcon);
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && okButton.IsEnabled) okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (e.Key == Key.Escape) dialog.Close();
        };

        dialog.ShowDialog();
        return result;
    }

    #endregion

    #region Title bar / window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Only the tray "Exit" menu item performs a real shutdown.
        e.Cancel = true;
        Hide();
    }

    #endregion

    #region Hotkey recording capture

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingHotkey)
            return;

        if (e.Key == Key.Escape)
        {
            _viewModel.CancelHotkeyRecording();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _viewModel.CaptureHotkeyInput(Keyboard.Modifiers, key);
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingHotkey)
            return;
        _viewModel.CaptureModifierRelease(Keyboard.Modifiers);
        e.Handled = true;
    }

    private void CompareButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BypassDisplayOnCommand.Execute(null);
    }

    private void CompareButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BypassDisplayOffCommand.Execute(null);
    }

    private void CancelHotkeyRecording_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelHotkeyRecording();
    }

    #endregion

    #region Overflow ("...") action menus

    /// <summary>
    /// Opens the "..." button's own ContextMenu on click, so the library
    /// actions (Rename/Duplicate/Favorite/Clear Hotkey/Delete) that used to
    /// be five permanently-visible buttons per preset collapse into one
    /// compact menu - fewer buttons on screen, same number of clicks to
    /// reach any single action.
    /// </summary>
    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    #endregion
}
