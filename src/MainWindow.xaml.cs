using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Core;
using GamerTool.ViewModels;

namespace GamerTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    private const int PreviewWidth = 512;
    private const int PreviewHeight = 288;

    // The unmodified base scene, generated procedurally once at startup.
    // Every ramp update remaps this exact buffer through the new RAMP lookup
    // tables and writes the result into _previewBitmap - it never remaps an
    // already-remapped frame, so repeated slider moves never accumulate error.
    private byte[]? _baseScenePixels; // BGRA32, PreviewWidth*PreviewHeight*4
    private WriteableBitmap? _previewBitmap;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        _viewModel.PreviewRampChanged += OnPreviewRampChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BuildBaseScene();

        // Paint the preview immediately with whatever ramp the currently
        // selected Display preset already computed at ViewModel construction
        // time, so the box isn't blank until the very next slider move.
        var currentRamp = DisplayManager.Instance.GetCurrentRamp();
        if (currentRamp.HasValue)
            RenderRampToPreview(currentRamp.Value);
    }

    /// <summary>
    /// Procedurally paints a dim corridor with a silhouetted "enemy" shape
    /// into _baseScenePixels. Stands in for the artist-provided
    /// preview_base_dark_room.png / preview_enemy_silhouette.png assets from
    /// the Phase 2 asset map - those are binary art files outside the scope
    /// of a code deliverable, so this generates an equivalent dark test scene
    /// in code so the live preview is fully functional without external
    /// assets. RenderRampToPreview below is completely agnostic of where
    /// _baseScenePixels came from, so swapping this for a BitmapDecoder load
    /// of the real PNG assets later is a drop-in change.
    /// </summary>
    private void BuildBaseScene()
    {
        _baseScenePixels = new byte[PreviewWidth * PreviewHeight * 4];

        for (int y = 0; y < PreviewHeight; y++)
        {
            for (int x = 0; x < PreviewWidth; x++)
            {
                int i = (y * PreviewWidth + x) * 4;

                // Dim corridor: a vertical gradient from near-black floor to
                // a slightly lighter "ambient light" band near the top third.
                double verticalT = y / (double)PreviewHeight;
                byte ambient = (byte)(18 + 22 * Math.Exp(-Math.Pow((verticalT - 0.25) * 4, 2)));

                byte r = ambient;
                byte g = ambient;
                byte b = (byte)Math.Min(255, ambient + 6); // faint cool tint typical of a dark game corridor

                // Enemy silhouette: a simple humanoid blob (ellipse body +
                // circle head), several shades darker than the surrounding
                // floor so it's genuinely hard to see until a preset lifts
                // shadows / boosts contrast.
                const double cx = PreviewWidth * 0.62;
                const double cyBody = PreviewHeight * 0.68;
                const double cyHead = PreviewHeight * 0.46;
                const double bodyRx = 26, bodyRy = 46, headR = 16;

                bool inBody = Math.Pow((x - cx) / bodyRx, 2) + Math.Pow((y - cyBody) / bodyRy, 2) <= 1.0;
                bool inHead = Math.Pow(x - cx, 2) + Math.Pow(y - cyHead, 2) <= headR * headR;

                if (inBody || inHead)
                {
                    r = (byte)Math.Max(0, ambient - 12);
                    g = (byte)Math.Max(0, ambient - 12);
                    b = (byte)Math.Max(0, ambient - 8);
                }

                _baseScenePixels[i + 0] = b; // BGRA byte order
                _baseScenePixels[i + 1] = g;
                _baseScenePixels[i + 2] = r;
                _baseScenePixels[i + 3] = 255;
            }
        }
    }

    private void OnPreviewRampChanged(RAMP ramp) => RenderRampToPreview(ramp);

    /// <summary>
    /// Remaps every pixel of the base scene through the given RAMP's
    /// 256-entry per-channel lookup tables - the same tables just sent to the
    /// physical monitor via SetDeviceGammaRamp - and writes the result into
    /// the preview bitmap. A 256-entry ramp is a true LUT, not a linear
    /// transform, so a per-pixel remap here is more accurate for previewing
    /// it than a WPF ColorMatrixEffect, which can only express linear/affine
    /// color transforms.
    /// </summary>
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

    #region Title bar / window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // the tray icon (added at App startup) remains; double-click or "Open" in its menu restores
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // GamerTool never truly closes from the window chrome - Alt+F4, the
        // close glyph, and the system menu all just hide to tray, matching
        // App's ShutdownMode="OnExplicitShutdown". Only the tray "Exit" menu
        // item (App.ExitApplication) performs a real shutdown.
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

    private void CancelHotkeyRecording_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelHotkeyRecording();
    }

    #endregion
}
