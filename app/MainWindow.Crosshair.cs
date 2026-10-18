using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GamerTool;

/// <summary>
/// The Crosshair tab: choosing a crosshair, tuning it, and putting it on the
/// screen.
/// <para>
/// There is one working crosshair, <c>_workCrosshair</c>, the same way the Display
/// tab has one working tune. The preview draws it, the overlay on the screen
/// draws it, and a slot that carries a crosshair loads it here first and then
/// shows it, so the tab always shows what is actually up.
/// </para>
/// <para>
/// Editing a crosshair you saved updates that one in place. Editing one that
/// ships leaves it alone and the box reads "Tuned" until it is saved under a new
/// name with the plus button, so a built in can never be damaged by a stray
/// slider.
/// </para>
/// </summary>
public partial class MainWindow
{
    private CrosshairOverlay? _xhOverlay;

    private CrosshairPreset _workCrosshair = CrosshairPreset.Default();

    /// <summary>Id selected in the box. Empty while the crosshair is tuned and unsaved.</summary>
    private string _xhPresetId = string.Empty;

    private bool _xhReady;

    private bool _xhUpdating;

    private bool _xhVisible;

    /// <summary>True when a slot put the crosshair up, so standing the slot down takes it away.</summary>
    private bool _xhBySlot;

    private bool _xhNight;

    private int _xhCentreX;

    private int _xhCentreY;

    private BitmapSource? _xhDayImage;

    private BitmapSource? _xhNightImage;

    private readonly Dictionary<CrosshairShape, RadioButton> _xhTiles = new();

    private CrosshairView? _xhCustomTileView;

    private TextBlock? _xhCustomTileHint;

    /// <summary>Saving to disk waits for the slider to stop moving.</summary>
    private readonly DispatcherTimer _xhSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void InitCrosshairTab()
    {
        try
        {
            _settings.CustomCrosshairs ??= new List<CrosshairPreset>();

            BuildCrosshairTiles();

            // Unpack the starter pictures now, off the UI thread, so the folder is
            // already there the first time anyone opens the browser or its folder.
            _ = Task.Run(CrosshairPack.EnsureFolder);

            _xhDayImage = LoadCrosshairScene("crosshair_day.png");
            _xhNightImage = LoadCrosshairScene("crosshair_night.png");

            CrosshairImageStore.PruneUnused(_settings.CustomCrosshairs.Select(p => p.CustomImage));

            CrosshairPreset start = FindCrosshair(_settings.ActiveCrosshairId) ?? CrosshairPreset.Default();
            _workCrosshair = start.Copy().Sanitised();
            _xhPresetId = start.Id;

            _xhSaveTimer.Tick += (s, e) =>
            {
                _xhSaveTimer.Stop();
                Commit();
            };

            WireKeycap(CrosshairKeyBox, KeycapCapture.Crosshair);
            WireKeycap(CrosshairCycleKeyBox, KeycapCapture.CrosshairCycle);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnXhDisplaySettingsChanged;

            _xhReady = true;
            RefreshCrosshairBox();
            UpdateXhControls();
            RenderCrosshairPreview();
            UpdateXhStatus();
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR INIT", ex);
        }
    }

    // ---------------------------------------------------------------- lookup

    private IEnumerable<CrosshairPreset> AllCrosshairs()
    {
        return CrosshairPreset.BuiltIns.Concat(_settings.CustomCrosshairs);
    }

    private CrosshairPreset? FindCrosshair(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AllCrosshairs().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private int CustomCrosshairIndex(string id)
    {
        return _settings.CustomCrosshairs.FindIndex(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What to draw for a preset. One whose picture has gone missing, from a
    /// restored backup on another machine say, draws as a cross rather than as
    /// nothing, so the user sees something is there.
    /// </summary>
    private static CrosshairPreset EffectiveCrosshair(CrosshairPreset preset, out BitmapSource? image)
    {
        image = null;
        if (preset.ShapeKind != CrosshairShape.Custom)
        {
            return preset;
        }

        image = CrosshairImageStore.Load(preset.CustomImage);
        if (image is not null)
        {
            return preset;
        }

        CrosshairPreset fallback = preset.Copy();
        fallback.ShapeKind = CrosshairShape.Cross;
        return fallback;
    }

    private static BitmapSource? LoadCrosshairScene(string asset)
    {
        try
        {
            byte[] raw = DisplayPreview.ReadAsset(asset);
            if (raw.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(raw);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 820;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR SCENE", ex);
            return null;
        }
    }

    // ------------------------------------------------------------ the picker

    private void BuildCrosshairTiles()
    {
        (CrosshairShape Shape, string Tip)[] shapes =
        {
            (CrosshairShape.Cross, "Cross"),
            (CrosshairShape.Dot, "Dot"),
            (CrosshairShape.X, "X"),
            (CrosshairShape.Circle, "Circle"),
            (CrosshairShape.Square, "Square"),
            (CrosshairShape.Chevrons, "Chevrons"),
            (CrosshairShape.Triangle, "Triangle"),
            (CrosshairShape.T, "T"),
            (CrosshairShape.Custom, "Your own picture")
        };

        foreach ((CrosshairShape shape, string tip) in shapes)
        {
            var view = new CrosshairView
            {
                Width = 44,
                Height = 44,
                Preset = new CrosshairPreset
                {
                    ShapeKind = shape,
                    Color = "#FFFFFF",
                    Size = shape == CrosshairShape.Custom ? 34 : 26,
                    Thickness = 2,
                    Gap = 5,
                    Outline = false,
                    Glow = false
                }
            };

            object content = view;
            if (shape == CrosshairShape.Custom)
            {
                // Empty until a picture is imported: the tile says what it is for.
                _xhCustomTileView = view;
                _xhCustomTileHint = new TextBlock
                {
                    Text = "PNG",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextMid"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };

                var grid = new Grid { Width = 44, Height = 44 };
                grid.Children.Add(view);
                grid.Children.Add(_xhCustomTileHint);
                content = grid;
            }

            var tile = new RadioButton
            {
                Style = (Style)FindResource("XhTile"),
                GroupName = "XhShape",
                Tag = shape,
                Margin = new Thickness(0, 0, 6, 0),
                Content = content
            };

            AutomationProperties.SetName(tile, tip + " crosshair");
            tile.Click += OnXhTileClick;
            XhTilePanel.Children.Add(tile);
            _xhTiles[shape] = tile;
        }
    }

    private void RefreshCrosshairBox()
    {
        if (!_xhReady)
        {
            return;
        }

        _xhUpdating = true;
        try
        {
            List<PresetChoice> items = new();

            if (_xhPresetId.Length == 0)
            {
                // BuiltIn only because that is what hides the "YOURS" chip. This
                // row is not a preset, it is the unsaved state.
                items.Add(new PresetChoice { Kind = "crosshair", Id = string.Empty, Name = "Tuned", BuiltIn = true });
            }

            HashSet<string> builtIn = CrosshairPreset.BuiltIns.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (CrosshairPreset preset in AllCrosshairs())
            {
                items.Add(new PresetChoice
                {
                    Kind = "crosshair",
                    Id = preset.Id,
                    Name = preset.Name,
                    BuiltIn = builtIn.Contains(preset.Id)
                });
            }

            XhPresetBox.ItemsSource = items;
            XhPresetBox.SelectedItem = items.FirstOrDefault(
                i => string.Equals(i.Id, _xhPresetId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _xhUpdating = false;
        }
    }

    private void OnXhPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        if (XhPresetBox.SelectedItem is not PresetChoice choice || choice.Id.Length == 0)
        {
            return;
        }

        CrosshairPreset? preset = FindCrosshair(choice.Id);
        if (preset is null)
        {
            return;
        }

        LoadCrosshair(preset);
        _settings.ActiveCrosshairId = preset.Id;
        Commit();
    }

    /// <summary>Make a preset the working crosshair and bring the whole tab, and the screen, in line.</summary>
    private void LoadCrosshair(CrosshairPreset preset)
    {
        _workCrosshair = preset.Copy().Sanitised();
        _xhPresetId = preset.Id;
        RefreshCrosshairBox();
        UpdateXhControls();
        UpdateXhStatus();
        RenderCrosshairPreview();
        PushCrosshairToOverlay();
    }

    // -------------------------------------------------------------- controls

    private static readonly CrosshairShape[] GapShapes =
    {
        CrosshairShape.Cross,
        CrosshairShape.X,
        CrosshairShape.Chevrons,
        CrosshairShape.T
    };

    /// <summary>
    /// The colour row: which swatch is ringed, what the plus swatch shows, and
    /// the hex box. Split out so the picker can call it on every mouse move
    /// without touching the sliders under the user's hand.
    /// </summary>
    private void UpdateXhColorUi()
    {
        bool was = _xhUpdating;
        _xhUpdating = true;
        try
        {
            CrosshairPreset p = _workCrosshair;

            RadioButton[] swatches = { XhSwatch0, XhSwatch1, XhSwatch2, XhSwatch3, XhSwatch4, XhSwatch5 };
            int match = Array.FindIndex(CrosshairPreset.Palette, c => string.Equals(c, p.Color, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < swatches.Length; i++)
            {
                swatches[i].IsChecked = i == match;
            }

            if (match < 0 && CrosshairPreset.TryParseColor(p.Color, out byte r, out byte g, out byte b))
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                XhSwatchCustom.Background = brush;
                XhSwatchPlus.Visibility = Visibility.Collapsed;
            }
            else
            {
                XhSwatchCustom.Background = (Brush)FindResource("WellBg");
                XhSwatchPlus.Visibility = Visibility.Visible;
            }

            XhSwatchCustom.IsChecked = match < 0;
        }
        finally
        {
            _xhUpdating = was;
        }
    }

    /// <summary>Set every control on the tab from the working crosshair.</summary>
    private void UpdateXhControls()
    {
        if (!_xhReady)
        {
            return;
        }

        _xhUpdating = true;
        try
        {
            CrosshairPreset p = _workCrosshair;

            XhSizeSlider.Value = p.Size;
            XhThicknessSlider.Value = p.Thickness;
            XhGapSlider.Value = p.Gap;
            XhOpacitySlider.Value = p.Opacity;
            XhRotationSlider.Value = p.Rotation;
            UpdateXhValueLabels();

            XhOutlineBox.IsChecked = p.Outline;
            XhGlowBox.IsChecked = p.Glow;
            XhCenterDotBox.IsChecked = p.CenterDot;

            foreach (KeyValuePair<CrosshairShape, RadioButton> tile in _xhTiles)
            {
                tile.Value.IsChecked = tile.Key == p.ShapeKind;
            }

            UpdateXhColorUi();

            // Only the controls that mean something for this shape are live. A dot
            // has no thickness and no turn, a ring has no gap and looks the same
            // however it is turned, and a picture has no gap either. Everything
            // else, colour, outline, glow, opacity and size, works on all of them.
            // Disabled groups are dimmed (not only IsEnabled) so the whole row
            // reads as inactive, not just the thumb.
            CrosshairShape kind = p.ShapeKind;
            bool gapOn = GapShapes.Contains(kind);
            bool thicknessOn = kind != CrosshairShape.Dot;
            bool rotationOn = kind != CrosshairShape.Dot && kind != CrosshairShape.Circle;

            XhGapSlider.IsEnabled = gapOn;
            XhGapPanel.Opacity = gapOn ? 1.0 : 0.4;
            XhGapPanel.IsHitTestVisible = gapOn;

            XhThicknessSlider.IsEnabled = thicknessOn;
            XhThicknessPanel.Opacity = thicknessOn ? 1.0 : 0.4;
            XhThicknessPanel.IsHitTestVisible = thicknessOn;

            XhRotationSlider.IsEnabled = rotationOn;
            XhRotationPanel.Opacity = rotationOn ? 1.0 : 0.4;
            XhRotationPanel.IsHitTestVisible = rotationOn;

            XhCenterDotBox.IsEnabled = kind != CrosshairShape.Dot;

            RefreshCustomTile();
        }
        finally
        {
            _xhUpdating = false;
        }
    }

    private void UpdateXhValueLabels()
    {
        XhSizeValue.Text = ((int)XhSizeSlider.Value).ToString(CultureInfo.InvariantCulture) + " px";
        XhThicknessValue.Text = ((int)XhThicknessSlider.Value).ToString(CultureInfo.InvariantCulture) + " px";
        XhGapValue.Text = ((int)XhGapSlider.Value).ToString(CultureInfo.InvariantCulture) + " px";
        XhOpacityValue.Text = ((int)XhOpacitySlider.Value).ToString(CultureInfo.InvariantCulture) + "%";
        XhRotationValue.Text = ((int)XhRotationSlider.Value).ToString(CultureInfo.InvariantCulture) + "\u00B0";
    }

    private void RefreshCustomTile()
    {
        if (_xhCustomTileView is null || _xhCustomTileHint is null)
        {
            return;
        }

        BitmapSource? image = string.IsNullOrEmpty(_workCrosshair.CustomImage)
            ? null
            : CrosshairImageStore.Load(_workCrosshair.CustomImage);

        _xhCustomTileView.Image = image;
        _xhCustomTileView.InvalidateVisual();
        _xhCustomTileHint.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnXhTileClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady || sender is not RadioButton { Tag: CrosshairShape shape })
        {
            return;
        }

        if (shape == CrosshairShape.Custom
            && (string.IsNullOrEmpty(_workCrosshair.CustomImage)
                || CrosshairImageStore.Load(_workCrosshair.CustomImage) is null))
        {
            // Nothing to select yet, so the tile is the way in: the pack, which is
            // the quickest route to a picture. Import is the button beside it.
            UpdateXhControls();
            OnXhPackClick(sender, e);
            return;
        }

        _workCrosshair.ShapeKind = shape;
        OnXhEdited();
        UpdateXhControls();
    }

    private void OnXhSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (_xhUpdating || !_xhReady || sender is not RadioButton box || box.Tag is not string tag)
        {
            return;
        }

        if (!int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            || index < 0 || index >= CrosshairPreset.Palette.Length)
        {
            return;
        }

        _workCrosshair.Color = CrosshairPreset.Palette[index];
        OnXhEdited();
        UpdateXhControls();
    }

    // ---------------------------------------------------------- colour picker

    private void OnXhCustomSwatchClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady)
        {
            return;
        }

        if (CrosshairPreset.TryParseColor(_workCrosshair.Color, out byte r, out byte g, out byte b))
        {
            XhHsv.SetColor(r, g, b);
        }

        SyncXhPickerReadout();
        XhColorPopup.IsOpen = true;
    }

    /// <summary>The picker moved: that is the new colour, live, with nothing to confirm.</summary>
    private void OnXhHsvChanged(object? sender, EventArgs e)
    {
        Color picked = XhHsv.Color;
        SetXhColorFromPicker(CrosshairPreset.FormatColor(picked.R, picked.G, picked.B));
    }

    private void SetXhColorFromPicker(string hex)
    {
        if (!_xhReady)
        {
            return;
        }

        _workCrosshair.Color = hex;
        OnXhEdited();
        UpdateXhColorUi();
        SyncXhPickerReadout();
    }

    private void SyncXhPickerReadout()
    {
        if (!XhPickHex.IsKeyboardFocused)
        {
            XhPickHex.Text = _workCrosshair.Color;
        }

        if (CrosshairPreset.TryParseColor(_workCrosshair.Color, out byte r, out byte g, out byte b))
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            XhPickPreview.Background = brush;
        }
    }

    private void OnXhPickHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitXhPickHex();
            e.Handled = true;
        }
    }

    private void OnXhPickHexCommit(object sender, RoutedEventArgs e)
    {
        CommitXhPickHex();
    }

    private void CommitXhPickHex()
    {
        if (!CrosshairPreset.TryParseColor(XhPickHex.Text, out byte r, out byte g, out byte b))
        {
            XhPickHex.Text = _workCrosshair.Color;
            return;
        }

        string hex = CrosshairPreset.FormatColor(r, g, b);
        XhHsv.SetColor(r, g, b);
        if (!string.Equals(hex, _workCrosshair.Color, StringComparison.OrdinalIgnoreCase))
        {
            SetXhColorFromPicker(hex);
        }

        XhPickHex.Text = hex;
    }

    private void OnXhColorDoneClick(object sender, RoutedEventArgs e)
    {
        XhColorPopup.IsOpen = false;
    }

    private void OnXhColorPopupClosed(object? sender, EventArgs e)
    {
        // Back in line with the rest of the tab, and the saved copy catches up.
        UpdateXhControls();
    }

    private void OnXhSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        _workCrosshair.Size = (int)XhSizeSlider.Value;
        _workCrosshair.Thickness = (int)XhThicknessSlider.Value;
        _workCrosshair.Gap = (int)XhGapSlider.Value;
        _workCrosshair.Opacity = (int)XhOpacitySlider.Value;
        _workCrosshair.Rotation = (int)XhRotationSlider.Value;
        UpdateXhValueLabels();
        OnXhEdited();
    }

    private void OnXhToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        _workCrosshair.Outline = XhOutlineBox.IsChecked == true;
        _workCrosshair.Glow = XhGlowBox.IsChecked == true;
        _workCrosshair.CenterDot = XhCenterDotBox.IsChecked == true;
        OnXhEdited();
    }

    /// <summary>
    /// Something on the tab changed the working crosshair. Keep the saved copy
    /// in step if it is one the user made, otherwise call it Tuned, then redraw
    /// the preview and anything on the screen.
    /// </summary>
    private void OnXhEdited()
    {
        if (!_xhReady)
        {
            return;
        }

        int index = _xhPresetId.Length == 0 ? -1 : CustomCrosshairIndex(_xhPresetId);
        if (index >= 0)
        {
            CrosshairPreset stored = _workCrosshair.Copy();
            stored.Id = _settings.CustomCrosshairs[index].Id;
            stored.Name = _settings.CustomCrosshairs[index].Name;
            _settings.CustomCrosshairs[index] = stored;
            _xhSaveTimer.Stop();
            _xhSaveTimer.Start();
        }
        else if (_xhPresetId.Length > 0)
        {
            _xhPresetId = string.Empty;
            _workCrosshair.Id = string.Empty;
            _workCrosshair.Name = "Tuned";
            RefreshCrosshairBox();
            UpdateXhStatus();
        }

        RenderCrosshairPreview();
        PushCrosshairToOverlay();
    }

    // --------------------------------------------------------------- preview

    private void RenderCrosshairPreview()
    {
        if (!_xhReady)
        {
            return;
        }

        CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out BitmapSource? image);
        XhPreview.Image = image;
        XhPreview.Preset = effective;
        XhPreview.InvalidateVisual();
        XhPreviewImage.Source = _xhNight ? _xhNightImage : _xhDayImage;
    }

    private void OnXhSceneClick(object sender, RoutedEventArgs e)
    {
        _xhNight = !_xhNight;
        XhSceneGlyph.Glyph = _xhNight ? "moon" : "sun";
        XhSceneButton.ToolTip = _xhNight ? "Night scene" : "Day scene";
        RenderCrosshairPreview();
    }

    private void OnXhPreviewFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border frame)
        {
            return;
        }

        double radius = Math.Max(0, frame.CornerRadius.TopLeft);
        XhPreviewClip.Clip = new RectangleGeometry(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight), radius, radius);
    }

    // ------------------------------------------------------- the screen part

    private void OnXhShowClick(object sender, RoutedEventArgs e)
    {
        if (_xhVisible)
        {
            HideCrosshair();
        }
        else
        {
            ShowCrosshair(bySlot: false);
        }
    }

    /// <summary>The global key. Works with the window hidden, which is the point of it.</summary>
    private void ToggleCrosshairFromKey()
    {
        if (_xhVisible)
        {
            HideCrosshair();
            Flash("Crosshair off");
        }
        else
        {
            ShowCrosshair(bySlot: false);
            Flash("Crosshair on");
        }
    }

    private void ShowCrosshair(bool bySlot)
    {
        try
        {
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out BitmapSource? image);
            (_xhCentreX, _xhCentreY) = CrosshairPlacement.ForegroundMonitorCentre();

            _xhOverlay ??= new CrosshairOverlay();
            _xhOverlay.ShowAt(effective, image, _xhCentreX, _xhCentreY);

            _xhVisible = true;
            _xhBySlot = bySlot;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR SHOW", ex);
            // Exclusive fullscreen games cannot be overlaid by any ordinary window;
            // borderless / windowed works. Keep the button state truthful.
            _xhVisible = false;
            _xhBySlot = false;
            Flash("Could not show crosshair — use borderless or windowed mode", true);
        }

        UpdateXhStatus();
    }

    private void HideCrosshair()
    {
        try
        {
            _xhOverlay?.HideOverlay();
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR HIDE", ex);
        }

        _xhVisible = false;
        _xhBySlot = false;
        UpdateXhStatus();
    }

    /// <summary>
    /// Take the crosshair off if a slot put it there. One the user turned on by
    /// hand stays, because nothing about a game closing says they are done with it.
    /// </summary>
    private void HideSlotCrosshair()
    {
        if (_xhVisible && _xhBySlot)
        {
            HideCrosshair();
        }
    }

    /// <summary>Redraw what is on the screen after an edit, in the place it already is.</summary>
    private void PushCrosshairToOverlay()
    {
        if (!_xhVisible || _xhOverlay is null)
        {
            return;
        }

        try
        {
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out BitmapSource? image);
            _xhOverlay.ShowAt(effective, image, _xhCentreX, _xhCentreY);
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PUSH", ex);
        }
    }

    /// <summary>
    /// Load a slot's crosshair and put it up. Returns false when the slot has
    /// none, or its crosshair has since been deleted.
    /// </summary>
    private bool ApplySlotCrosshair(HotkeySlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.CrosshairPresetId))
        {
            return false;
        }

        CrosshairPreset? preset = FindCrosshair(slot.CrosshairPresetId);
        if (preset is null)
        {
            Flash("The crosshair for " + slot.Name + " was deleted", true);
            return false;
        }

        LoadCrosshair(preset);
        ShowCrosshair(bySlot: true);
        return _xhVisible;
    }

    private void OnXhDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A monitor was plugged in, unplugged or changed resolution. The middle
        // of the screen may have moved, so find it again. Cheap, and only does
        // anything while a crosshair is up.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_xhVisible || _xhOverlay is null)
            {
                return;
            }

            (_xhCentreX, _xhCentreY) = CrosshairPlacement.ForegroundMonitorCentre();
            _xhOverlay.Recentre(_xhCentreX, _xhCentreY);
        }));
    }

    private void UpdateXhStatus()
    {
        if (!_xhReady)
        {
            return;
        }

        XhShowButton.Content = _xhVisible ? "Hide crosshair" : "Show on screen";
        XhStatusText.Text = _xhVisible ? "On screen" : "Hidden";
        XhLiveDot.Fill = (Brush)FindResource(_xhVisible ? "Green" : "TextLow");

        // Built-ins that have been edited become "Tuned" (empty id). Rename and
        // Delete only make sense for a saved custom preset.
        bool canEditMeta = _xhPresetId.Length > 0
            && !CrosshairPreset.BuiltIns.Any(p => string.Equals(p.Id, _xhPresetId, StringComparison.OrdinalIgnoreCase));
        XhRenameButton.IsEnabled = canEditMeta;
        XhDeleteButton.IsEnabled = canEditMeta;
    }

    /// <summary>The window is closing for good. Take the overlay with it.</summary>
    private void CloseCrosshair()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnXhDisplaySettingsChanged;
            _xhSaveTimer.Stop();
            _xhOverlay?.Close();
            _xhOverlay = null;
            _xhVisible = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR CLOSE", ex);
        }
    }

    /// <summary>After a backup restore replaced the settings underneath the tab.</summary>
    private void ReloadCrosshairFromSettings()
    {
        if (!_xhReady)
        {
            return;
        }

        HideCrosshair();
        CrosshairPreset preset = FindCrosshair(_settings.ActiveCrosshairId) ?? CrosshairPreset.Default();
        LoadCrosshair(preset);
        ShowSlotKey(CrosshairKeyBox, _settings.CrosshairHotkey);
        ShowSlotKey(CrosshairCycleKeyBox, _settings.CrosshairCycleHotkey);
    }

    // ------------------------------------------------- save, rename, delete

    private void OnXhSaveAsClick(object sender, RoutedEventArgs e)
    {
        ShowModal("NAME THIS CROSSHAIR", "My crosshair", name =>
        {
            CrosshairPreset preset = _workCrosshair.Copy();
            preset.Id = AppProfileTools.NewId("xh");
            preset.Name = name;
            _settings.CustomCrosshairs.Add(preset);

            _workCrosshair = preset.Copy();
            _xhPresetId = preset.Id;
            _settings.ActiveCrosshairId = preset.Id;
            Commit();
            RefreshCrosshairBox();
            BuildSlots();
            Flash(name + " saved");
        });
    }

    private void OnXhRenameClick(object sender, RoutedEventArgs e)
    {
        int index = _xhPresetId.Length == 0 ? -1 : CustomCrosshairIndex(_xhPresetId);
        if (index < 0)
        {
            Flash("Built in crosshairs cannot be renamed", true);
            return;
        }

        CrosshairPreset mine = _settings.CustomCrosshairs[index];
        ShowModal("RENAME CROSSHAIR", mine.Name, name =>
        {
            mine.Name = name;
            _workCrosshair.Name = name;
            Commit();
            RefreshCrosshairBox();
            BuildSlots();
        });
    }

    private void OnXhDeleteClick(object sender, RoutedEventArgs e)
    {
        int index = _xhPresetId.Length == 0 ? -1 : CustomCrosshairIndex(_xhPresetId);
        if (index < 0)
        {
            Flash("Built in crosshairs stay put", true);
            return;
        }

        CrosshairPreset mine = _settings.CustomCrosshairs[index];
        string removed = mine.Name;
        List<HotkeySlot> inUse = _settings.Slots
            .Where(s => string.Equals(s.CrosshairPresetId, mine.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string usage = SlotService.DescribeUsage(inUse);

        ShowConfirmModal(
            "DELETE \u201C" + removed.ToUpperInvariant() + "\u201D?",
            "This takes the crosshair out of Gamer Tool and out of any slot that points at it."
                + (usage.Length > 0 ? "  " + usage : string.Empty),
            "Delete",
            () =>
            {
                _settings.CustomCrosshairs.RemoveAll(p => string.Equals(p.Id, mine.Id, StringComparison.OrdinalIgnoreCase));
                foreach (HotkeySlot slot in inUse)
                {
                    slot.CrosshairPresetId = null;
                }

                if (!string.IsNullOrEmpty(mine.CustomImage)
                    && !_settings.CustomCrosshairs.Any(p => string.Equals(p.CustomImage, mine.CustomImage, StringComparison.OrdinalIgnoreCase)))
                {
                    CrosshairImageStore.Delete(mine.CustomImage);
                }

                CrosshairPreset fallback = CrosshairPreset.Default();
                _settings.ActiveCrosshairId = fallback.Id;
                Commit();
                LoadCrosshair(fallback);
                BuildSlots();
                Flash(removed + " deleted");
            });
    }

    // ---------------------------------------------------------------- import

    private void OnXhImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a crosshair picture",
            Filter = "PNG picture (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        UseCrosshairPicture(dialog.FileName);
    }

    /// <summary>
    /// Make a PNG file the crosshair, whether it came from the Import button or
    /// from the pack. Either way it is copied into the app's own folder in a
    /// cleaned-up form first, so the preset never depends on the original staying
    /// where it was, and the picture then answers to every control on the tab.
    /// </summary>
    private void UseCrosshairPicture(string path)
    {
        string? name = CrosshairImageStore.Import(path, out string problem);
        if (name is null)
        {
            Flash(problem, true);
            return;
        }

        string previous = _workCrosshair.CustomImage;
        _workCrosshair.CustomImage = name;
        _workCrosshair.ShapeKind = CrosshairShape.Custom;
        OnXhEdited();
        UpdateXhControls();

        // The picture it replaced is only kept while a saved crosshair uses it.
        if (!string.IsNullOrEmpty(previous)
            && !_settings.CustomCrosshairs.Any(p => string.Equals(p.CustomImage, previous, StringComparison.OrdinalIgnoreCase)))
        {
            CrosshairImageStore.Delete(previous);
        }

        Flash("Picture ready");
    }

    // ------------------------------------------------------------- the pack

    /// <summary>Counts up each time the browser fills, so a fill that was interrupted knows to stop.</summary>
    private int _xhPackLoad;

    private void OnXhPackClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady)
        {
            return;
        }

        CrosshairPack.EnsureFolder();
        XhPackPopup.IsOpen = true;
        FillXhPack();
    }

    private void OnXhPackClosed(object? sender, EventArgs e)
    {
        // Let go of every thumbnail. They are rebuilt from the folder next time.
        _xhPackLoad++;
        XhPackPanel.Children.Clear();
        if (XhPackFilter is not null)
        {
            XhPackFilter.Text = string.Empty;
        }
    }

    private void OnXhPackFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CrosshairPack.EnsureFolder();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + CrosshairPack.Folder + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK OPEN", ex);
            Flash("Could not open the folder", true);
        }
    }

    /// <summary>
    /// Read the folder and put a tile in the browser for every PNG in it.
    /// Done every time the browser opens, which is the whole mechanism by which
    /// a picture dropped into the folder shows up with no change to the program.
    /// Tiles go in a batch at a time at background priority, so a big folder
    /// opens at once and fills in while the window stays responsive.
    /// </summary>
    private void OnXhPackFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (XhPackPopup.IsOpen)
        {
            FillXhPack();
        }
    }

    private void FillXhPack()
    {
        int generation = ++_xhPackLoad;
        XhPackPanel.Children.Clear();

        List<string> files = CrosshairPack.List();
        string filter = (XhPackFilter?.Text ?? string.Empty).Trim();
        if (filter.Length > 0)
        {
            files = files
                .Where(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        XhPackCount.Text = files.Count == 0
            ? string.Empty
            : files.Count.ToString(CultureInfo.InvariantCulture) + " pictures";
        XhPackEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (files.Count == 0 && filter.Length > 0)
        {
            XhPackEmpty.Text = "No pictures match \"" + filter + "\".";
        }
        else if (files.Count == 0)
        {
            XhPackEmpty.Text = "No pictures yet. Add PNGs with Open folder, then reopen.";
        }

        // Shown in the colour the crosshair is set to, so the grid previews what
        // choosing one will look like.
        Color colour = CrosshairPreset.TryParseColor(_workCrosshair.Color, out byte r, out byte g, out byte b)
            ? Color.FromRgb(r, g, b)
            : Colors.White;
        var tint = new SolidColorBrush(colour);
        tint.Freeze();

        int next = 0;

        void Batch()
        {
            if (generation != _xhPackLoad)
            {
                return;
            }

            int end = Math.Min(files.Count, next + 24);
            for (; next < end; next++)
            {
                Button? tile = BuildPackTile(files[next], tint);
                if (tile is not null)
                {
                    XhPackPanel.Children.Add(tile);
                }
            }

            if (next < files.Count)
            {
                Dispatcher.InvokeAsync(Batch, DispatcherPriority.Background);
            }
        }

        Batch();
    }

    private Button? BuildPackTile(string path, Brush tint)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.DecodePixelWidth = 96;
            thumb.StreamSource = stream;
            thumb.EndInit();
            thumb.Freeze();

            // The picture's own shape filled with the chosen colour. That is what
            // the multiply tint does to a white picture, which is what the pack is.
            var face = new System.Windows.Shapes.Rectangle
            {
                Width = 46,
                Height = 46,
                Fill = tint,
                OpacityMask = new ImageBrush(thumb) { Stretch = Stretch.Uniform }
            };

            var tile = new Button
            {
                Style = (Style)FindResource("XhPackTile"),
                Content = face,
                Tag = path
            };

            AutomationProperties.SetName(tile, "Use crosshair " + Path.GetFileNameWithoutExtension(path));
            tile.Click += OnXhPackTileClick;
            return tile;
        }
        catch (Exception ex)
        {
            // One unreadable file is skipped, not allowed to empty the browser.
            AppLog.Error("CROSSHAIR PACK TILE", ex);
            return null;
        }
    }

    private void OnXhPackTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        XhPackPopup.IsOpen = false;
        UseCrosshairPicture(path);
    }

    // ----------------------------------------------------------------- export

    private void OnXhExportClick(object sender, RoutedEventArgs e)
    {
        string suggestion = string.Concat(_workCrosshair.Name.Select(
            c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)).Trim();
        if (suggestion.Length == 0)
        {
            suggestion = "crosshair";
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export crosshair",
            Filter = "PNG picture (*.png)|*.png",
            FileName = suggestion + ".png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // The same element the overlay uses, at one crosshair pixel to one
            // real pixel and assuming a 96 dpi screen, so the file is the true
            // size and identical whatever monitor it was exported from.
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out BitmapSource? image);
            int side = CrosshairGeometry.WindowSide(effective);

            var view = new CrosshairView
            {
                Width = side,
                Height = side,
                Preset = effective,
                Image = image,
                Magnification = 1.0,
                AssumeDpi = 1.0
            };

            var size = new System.Windows.Size(side, side);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();

            var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
            target.Render(view);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using FileStream output = File.Create(dialog.FileName);
            encoder.Save(output);

            Flash("Crosshair exported");
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR EXPORT", ex);
            Flash("Could not save that file", true);
        }
    }

    // ------------------------------------------------------------------ cycle

    /// <summary>
    /// Step to the next crosshair in the list, built in ones first and then the
    /// user's own, and wrap round. Puts the crosshair up if it was hidden, since
    /// pressing a key to change it means you want to see it.
    /// </summary>
    private void CycleCrosshairFromKey()
    {
        List<CrosshairPreset> all = AllCrosshairs().ToList();
        if (all.Count == 0)
        {
            return;
        }

        int at = _xhPresetId.Length == 0
            ? -1
            : all.FindIndex(p => string.Equals(p.Id, _xhPresetId, StringComparison.OrdinalIgnoreCase));
        CrosshairPreset next = all[(at + 1) % all.Count];

        LoadCrosshair(next);
        _settings.ActiveCrosshairId = next.Id;
        _xhSaveTimer.Stop();
        _xhSaveTimer.Start();

        if (!_xhVisible)
        {
            ShowCrosshair(bySlot: false);
        }

        Flash(next.Name);
    }

    // ------------------------------------------------------------ tray menu

    /// <summary>The tray's preset list: every crosshair, the loaded one ticked.</summary>
    private List<TraySlotEntry> BuildTrayCrosshairEntries()
    {
        List<TraySlotEntry> entries = new();

        foreach (CrosshairPreset preset in AllCrosshairs())
        {
            entries.Add(new TraySlotEntry
            {
                Id = preset.Id,
                Label = string.IsNullOrWhiteSpace(preset.Name) ? "Unnamed" : preset.Name.Trim(),
                Detail = "Use this crosshair",
                Active = string.Equals(preset.Id, _xhPresetId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return entries;
    }

    /// <summary>
    /// Picks a crosshair from the tray: loads it, remembers it, and puts it on the
    /// screen if it was hidden, as the cycle key does.
    /// </summary>
    private void PickCrosshairFromTray(string id)
    {
        CrosshairPreset? preset = FindCrosshair(id);
        if (preset is null)
        {
            return;
        }

        LoadCrosshair(preset);
        _settings.ActiveCrosshairId = preset.Id;
        _xhSaveTimer.Stop();
        _xhSaveTimer.Start();

        if (!_xhVisible)
        {
            ShowCrosshair(bySlot: false);
        }

        Flash(preset.Name);
    }
}
