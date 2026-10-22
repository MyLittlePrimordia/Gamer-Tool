using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Build the tab and load the crosshair it opens on.
    /// </summary>
    /// <param name="headless">
    /// True for the test harness, which stands the tab up with no tray, no hotkeys
    /// and a profile in a temporary folder. Three things are skipped, and each of
    /// them is a real side effect on the machine: unpacking the starter pictures into
    /// the pack folder, pruning pictures the profile does not name out of the
    /// user's own crosshair folder, and subscribing to the system's display-changed
    /// event. None of them is needed for the tab to work, and all three would be
    /// wrong to do from a test.
    /// </param>
    private void InitCrosshairTab(bool headless = false)
    {
        try
        {
            _settings.CustomCrosshairs ??= new List<CrosshairPreset>();

            BuildCrosshairTiles();
            BuildEffectChoices();

            if (!headless)
            {
                // Unpack the starter pictures now, off the UI thread, so the folder is
                // already there the first time anyone opens the browser or its folder.
                _ = Task.Run(CrosshairPack.EnsureFolder);
            }

            _xhDayImage = LoadCrosshairScene("crosshair_day.png");
            _xhNightImage = LoadCrosshairScene("crosshair_night.png");

            if (!headless)
            {
                CrosshairImageStore.PruneUnused(_settings.CustomCrosshairs.Select(p => p.CustomImage));
            }

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

            if (!headless)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnXhDisplaySettingsChanged;
            }

            _xhReady = true;
            RefreshCrosshairBox();
            UpdateXhControls();            RenderCrosshairPreview();
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
    private static CrosshairPreset EffectiveCrosshair(CrosshairPreset preset, out CrosshairPicture? picture)
    {
        picture = null;
        if (preset.ShapeKind != CrosshairShape.Custom)
        {
            return preset;
        }

        picture = CrosshairImageStore.LoadPicture(preset.CustomImage);
        if (picture is not null)
        {
            return preset;
        }

        // Only what is drawn falls back to a cross. The preset keeps its picture
        // name on purpose: a restored backup whose pictures have not arrived yet, or
        // a file on a drive that is unplugged, shows the picture again as soon as it
        // is back, and nothing here is ever written to the settings.
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
            (CrosshairShape.Custom, "Your own picture or animation")
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
                    Text = "PIC",
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

    /// <summary>
    /// One row of the effect dropdown: the value, the words on screen, and the little
    /// moving crosshair drawn beside them.
    /// <para>
    /// ToString is what the closed box reads when it has no template of its own, and it
    /// is what any code that is not looking at the fields will print, so it stays the
    /// label rather than the type name and every field of it.
    /// </para>
    /// </summary>
    private sealed record EffectChoice(CrosshairEffect Value, string Label, CrosshairPreset Preview)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Fill the effect dropdown once, at start. The list is the enum's menu order, so a
    /// new effect is added in one place and appears here, in the saved text and in the
    /// animation without anything else being edited.
    /// </summary>
    private void BuildEffectChoices()
    {
        XhEffectBox.ItemsSource = CrosshairAnimation.Menu
            .Select(effect => new EffectChoice(
                effect, CrosshairAnimation.Label(effect), CrosshairAnimation.Thumbnail(effect)))
            .ToList();
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
        CrosshairShape.T,
        CrosshairShape.Custom
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

            // The swatch itself is the rainbow wheel, drawn by its style, so all this
            // sets is the dot in the middle: the colour the swatch is holding.
            if (CrosshairPreset.TryParseColor(p.Color, out byte r, out byte g, out byte b))
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                XhSwatchCustom.Background = brush;
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
            XhGapSlider.Value = p.ShapeKind == CrosshairShape.Custom ? p.PictureGap : p.Gap;
            XhOpacitySlider.Value = p.Opacity;
            XhRotationSlider.Value = p.Rotation;
            XhDotSizeSlider.Value = p.EffectiveCenterDotSize();
            XhEffectSpeedSlider.Value = p.EffectSpeed;
            XhEffectAmountSlider.Value = p.EffectAmount;
            UpdateXhValueLabels();

            XhOutlineBox.IsChecked = p.Outline;
            XhGlowBox.IsChecked = p.Glow;
            XhCenterDotBox.IsChecked = p.CenterDot;
            XhOwnColoursBox.IsChecked = p.KeepOwnColours;
            XhOledBox.IsChecked = _settings.OledGuard;

            foreach (KeyValuePair<CrosshairShape, RadioButton> tile in _xhTiles)
            {
                tile.Value.IsChecked = tile.Key == p.ShapeKind;
            }

            UpdateXhColorUi();

            // The effect dropdown, and the two sliders under it. The sliders are only
            // there while an effect is chosen, because a speed slider for "nothing" is
            // a control that does nothing; amount is dimmed for the one effect that has
            // no amplitude, rather than hidden, so the row never changes height.
            XhEffectBox.SelectedItem = XhEffectBox.Items.OfType<EffectChoice>()
                .FirstOrDefault(c => c.Value == p.EffectKind);
            UpdateXhEffectUi();

            // Only the controls that mean something for this shape are live. A dot
            // has no thickness and no turn, and a ring has no gap and looks the same
            // however it is turned. A picture has all of them: its gap opens a hole
            // at its middle, the way a drawn cross opens. Everything else, colour,
            // outline, glow, opacity and size, works on all of them.
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
            UpdateXhDotUi();

            // Only a picture has colours of its own to keep. Shapes are drawn in the
            // chosen colour and nothing else, so the switch is not there to be
            // wondered about rather than dimmed.
            XhTintRow.Visibility = kind == CrosshairShape.Custom ? Visibility.Visible : Visibility.Collapsed;

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

        // "Auto" is the dot following the line's thickness. The number after it is
        // what that comes to, so the slider is never showing a value the dot is not.
        string dotSize = ((int)XhDotSizeSlider.Value).ToString(CultureInfo.InvariantCulture) + " px";
        XhDotSizeValue.Text = _workCrosshair.HasOwnCenterDotSize ? dotSize : "Auto \u00B7 " + dotSize;

        XhEffectSpeedValue.Text = ((int)XhEffectSpeedSlider.Value).ToString(CultureInfo.InvariantCulture);
        XhEffectAmountValue.Text = ((int)XhEffectAmountSlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the effect controls look like for the effect that is chosen: whether there
    /// is anything to tune, and which of the two sliders means anything.
    /// </summary>
    private void UpdateXhEffectUi()
    {
        CrosshairEffect effect = _workCrosshair.EffectKind;
        bool wanted = effect != CrosshairEffect.None;
        bool amountOn = CrosshairAnimation.UsesAmount(effect);

        XhEffectTuningPanel.Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;

        // What the effect does, and when it cannot show on what it is on, a sentence
        // saying so: an effect that quietly does nothing reads as a broken one.
        string caveat = CrosshairAnimation.Caveat(effect, _workCrosshair.ShapeKind, _workCrosshair.KeepOwnColours);
        string hint = CrosshairAnimation.Hint(effect);
        XhEffectHint.Text = caveat.Length == 0 ? hint : hint + " " + caveat;

        XhEffectAmountSlider.IsEnabled = amountOn;
        XhEffectAmountPanel.Opacity = amountOn ? 1.0 : 0.4;
        XhEffectAmountPanel.IsHitTestVisible = amountOn;
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

    // ------------------------------------------------------ center dot colour

    /// <summary>
    /// The chip beside the CENTER DOT switch: what the dot is painted, and whether
    /// it can be opened at all.
    /// <para>
    /// With no colour of its own the dot follows the crosshair, and the chip shows
    /// that colour - so an untouched preset reads as one colour rather than two,
    /// and picking one is what splits them.
    /// </para>
    /// </summary>
    /// <summary>
    /// The dot's size, live only while there is a dot to size. Dimmed rather than
    /// hidden, like the other controls that a shape puts out of use, so the row does
    /// not jump when the switch is flipped and the value that comes back is readable.
    /// </summary>
    private void UpdateXhDotUi()
    {
        if (!_xhReady)
        {
            return;
        }

        // A dot shape is its own dot, so there is no second one to size.
        bool live = _workCrosshair.CenterDot && _workCrosshair.ShapeKind != CrosshairShape.Dot;
        XhDotSizeSlider.IsEnabled = live;
        XhDotSizePanel.Opacity = live ? 1.0 : 0.4;
        XhDotSizePanel.IsHitTestVisible = live;

        UpdateXhCenterDotColorUi();
    }

    private void UpdateXhCenterDotColorUi()
    {
        if (!_xhReady)
        {
            return;
        }

        string hex = _workCrosshair.EffectiveCenterDotColor();
        if (CrosshairPreset.TryParseColor(hex, out byte r, out byte g, out byte b))
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            XhDotColorChip.Fill = brush;
        }

        // A dot shape is its own dot, so there is nothing for this to paint. A dot
        // that is switched off has nothing to paint either.
        bool live = _workCrosshair.CenterDot && _workCrosshair.ShapeKind != CrosshairShape.Dot;
        XhDotColorButton.IsEnabled = live;
        XhDotColorButton.ToolTip = !live
            ? "Turn the center dot on to give it its own color"
            : _workCrosshair.HasOwnCenterDotColor
                ? "Center dot color"
                : "Center dot color, following the crosshair until you pick one";
    }

    private void OnXhDotColorClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady)
        {
            return;
        }

        if (CrosshairPreset.TryParseColor(_workCrosshair.EffectiveCenterDotColor(), out byte r, out byte g, out byte b))
        {
            XhDotHsv.SetColor(r, g, b);
        }

        SyncXhDotPickerReadout();
        XhDotColorPopup.IsOpen = true;
    }

    /// <summary>The dot's picker moved: that is the new colour, live, nothing to confirm.</summary>
    private void OnXhDotHsvChanged(object? sender, EventArgs e)
    {
        Color picked = XhDotHsv.Color;
        SetXhCenterDotColor(CrosshairPreset.FormatColor(picked.R, picked.G, picked.B));
    }

    private void SetXhCenterDotColor(string hex)
    {
        if (!_xhReady)
        {
            return;
        }

        _workCrosshair.CenterDotColor = hex;
        OnXhEdited();
        UpdateXhCenterDotColorUi();
        SyncXhDotPickerReadout();
    }

    private void SyncXhDotPickerReadout()
    {
        string hex = _workCrosshair.EffectiveCenterDotColor();
        if (!XhDotPickHex.IsKeyboardFocused)
        {
            XhDotPickHex.Text = hex;
        }

        if (CrosshairPreset.TryParseColor(hex, out byte r, out byte g, out byte b))
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            XhDotPickPreview.Background = brush;
        }
    }

    private void OnXhDotPickHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitXhDotPickHex();
            e.Handled = true;
        }
    }

    private void OnXhDotPickHexCommit(object sender, RoutedEventArgs e)
    {
        CommitXhDotPickHex();
    }

    private void CommitXhDotPickHex()
    {
        if (!CrosshairPreset.TryParseColor(XhDotPickHex.Text, out byte r, out byte g, out byte b))
        {
            XhDotPickHex.Text = _workCrosshair.EffectiveCenterDotColor();
            return;
        }

        string hex = CrosshairPreset.FormatColor(r, g, b);
        XhDotHsv.SetColor(r, g, b);
        if (!string.Equals(hex, _workCrosshair.EffectiveCenterDotColor(), StringComparison.OrdinalIgnoreCase))
        {
            SetXhCenterDotColor(hex);
        }

        XhDotPickHex.Text = hex;
    }

    private void OnXhDotColorDoneClick(object sender, RoutedEventArgs e)
    {
        XhDotColorPopup.IsOpen = false;
    }

    private void OnXhDotColorPopupClosed(object? sender, EventArgs e)
    {
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

        // The one slider drives whichever gap the shape uses, and leaves the other
        // alone so switching shape back and forth does not lose either setting.
        if (_workCrosshair.ShapeKind == CrosshairShape.Custom)
        {
            _workCrosshair.PictureGap = (int)XhGapSlider.Value;
        }
        else
        {
            _workCrosshair.Gap = (int)XhGapSlider.Value;
        }

        _workCrosshair.Opacity = (int)XhOpacitySlider.Value;
        _workCrosshair.Rotation = (int)XhRotationSlider.Value;

        // Moving this is what turns "as thick as the line" into a size of its own,
        // the same way picking a colour stops the dot following the crosshair's.
        _workCrosshair.CenterDotSize = (int)XhDotSizeSlider.Value;
        UpdateXhValueLabels();
        OnXhEdited();
    }

    private void OnXhToggleChanged(object sender, RoutedEventArgs e)
    {        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        _workCrosshair.Outline = XhOutlineBox.IsChecked == true;
        _workCrosshair.Glow = XhGlowBox.IsChecked == true;
        _workCrosshair.CenterDot = XhCenterDotBox.IsChecked == true;
        _workCrosshair.KeepOwnColours = XhOwnColoursBox.IsChecked == true;
        OnXhEdited();

        // The dot's colour and size are only worth offering while there is a dot.
        UpdateXhDotUi();
        UpdateXhEffectUi();
    }

    private void OnXhEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        if (XhEffectBox.SelectedItem is not EffectChoice choice)
        {
            return;
        }

        _workCrosshair.EffectKind = choice.Value;

        if (choice.Value != CrosshairEffect.None)
        {
            // Each effect starts at the rate and reach it is meant to be seen at,
            // rather than both sliders sitting at fifty and the user having to guess
            // which way to drag them. A value this puts here is the user's from the
            // moment they touch a slider, and a saved crosshair opens on its own
            // numbers because this handler does not run while the controls are being
            // filled in.
            (int speed, int amount) = CrosshairAnimation.Defaults(choice.Value);
            _workCrosshair.EffectSpeed = speed;
            _workCrosshair.EffectAmount = amount;

            _xhUpdating = true;
            try
            {
                XhEffectSpeedSlider.Value = speed;
                XhEffectAmountSlider.Value = amount;
            }
            finally
            {
                _xhUpdating = false;
            }

            UpdateXhValueLabels();
        }

        UpdateXhEffectUi();
        OnXhEdited();
    }

    private void OnXhEffectSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_xhUpdating || !_xhReady)
        {
            return;
        }

        _workCrosshair.EffectSpeed = (int)XhEffectSpeedSlider.Value;
        _workCrosshair.EffectAmount = (int)XhEffectAmountSlider.Value;
        UpdateXhValueLabels();
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

        CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out CrosshairPicture? picture);
        XhPreview.Picture = picture;
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

        // The crosshair is drawn at the middle of its element, which on these
        // pictures is the soldier's belt. Nobody aims there, so it is moved up to
        // his chest, where players rest a crosshair. A render transform and not a
        // layout one: nothing about the box changes, only where the crosshair sits
        // in it, and the real overlay on the screen is untouched.
        (double dx, double dy) = CrosshairScene.Offset(frame.ActualWidth, frame.ActualHeight);
        XhPreview.RenderTransform = new TranslateTransform(dx, dy);
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
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out CrosshairPicture? picture);
            (_xhCentreX, _xhCentreY) = CrosshairPlacement.ForegroundMonitorCentre();

            _xhOverlay ??= new CrosshairOverlay();
            _xhOverlay.ShowAt(effective, picture, _xhCentreX, _xhCentreY);

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

        if (_xhVisible)
        {
            // Whatever put it up - the button, the key, a slot, or the guard giving it
            // back - this is the moment it appeared, and the reading on the tab counts
            // from here.
            _xhGuarded = false;
            _xhShownAt = DateTime.UtcNow;
        }

        SyncOledWatch();
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
        _xhShownAt = null;

        // Stopped for a crosshair the user took down, kept for one the guard took: the
        // guard has to stay awake to notice them come back.
        SyncOledWatch();
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
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out CrosshairPicture? picture);
            _xhOverlay.ShowAt(effective, picture, _xhCentreX, _xhCentreY);
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

        // How long it has been up. The point of the number is to make an afternoon on
        // the screen visible as a fact rather than as something to remember, because
        // that is the part of OLED wear no setting can undo.
        if (_xhVisible)
        {
            XhStatusText.Text = "On screen \u00B7 " + OledGuard.OnScreenFor(_xhShownAt, DateTime.UtcNow);
        }
        else if (_xhGuarded)
        {
            XhStatusText.Text = "Hidden while idle";
        }
        else
        {
            XhStatusText.Text = "Hidden";
        }

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
            StopOledWatch();
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

    /// <summary>
    /// The crosshair on the tab as a new preset, in one press.
    /// <para>
    /// Save copies it and asks what to call it first; this is the other thing people
    /// do with a crosshair they like, which is keep it and carry on working. What is
    /// copied is the tab's own state rather than what is stored, so an edited
    /// built-in or a preset with an unsaved change comes over as it looks now. It is
    /// also the only way to keep an edited built-in: those cannot be overwritten, so
    /// a copy is where that edit has to live.
    /// </para>
    /// <para>
    /// The picture file is not copied, deliberately. Both presets name the same one, which
    /// is what Delete already expects - it removes the file only once no preset is
    /// left naming it - so a duplicate cannot take the picture from under the
    /// original, and a pack with a dozen copies in it stays one file on disk.
    /// </para>
    /// </summary>
    private void OnXhDuplicateClick(object sender, RoutedEventArgs e)
    {
        CrosshairPreset copy = _workCrosshair.Copy();
        copy.Id = AppProfileTools.NewId("xh");
        copy.Name = FreeCrosshairName(copy.Name);

        _settings.CustomCrosshairs.Add(copy);
        _workCrosshair = copy.Copy();
        _xhPresetId = copy.Id;
        _settings.ActiveCrosshairId = copy.Id;
        Commit();
        RefreshCrosshairBox();

        // The copy is the user's own preset, so Rename and Delete become usable now
        // where they were greyed out on the built-in it came from.
        UpdateXhStatus();
        BuildSlots();
        Flash(copy.Name + " created");
    }

    /// <summary>
    /// A name no other crosshair already has. Duplicate is one press, so it cannot
    /// stop to ask what to call the copy, and two entries reading the same words in
    /// the dropdown are indistinguishable from each other - the second one comes out
    /// as "GREEN copy 2".
    /// <para>
    /// A suffix already on the name is dropped first, so copying a copy gives
    /// "GREEN copy 3" rather than "GREEN copy copy 2": the suffix says which number
    /// this one is, it is not part of the name being copied. "Tuned" is the tab's
    /// name for an unsaved state rather than a name anyone chose, so a copy of that
    /// starts from "Crosshair" instead of reading "Tuned copy".
    /// </para>
    /// </summary>
    private string FreeCrosshairName(string name)
    {
        string root = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

        int suffix = root.LastIndexOf(" copy", StringComparison.OrdinalIgnoreCase);
        if (suffix > 0)
        {
            string tail = root[(suffix + 5)..].Trim();
            if (tail.Length == 0 || int.TryParse(tail, out _))
            {
                root = root[..suffix].Trim();
            }
        }

        if (root.Length == 0 || string.Equals(root, "Tuned", StringComparison.OrdinalIgnoreCase))
        {
            root = "Crosshair";
        }

        List<CrosshairPreset> taken = AllCrosshairs().ToList();
        bool InUse(string candidate) => taken.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));

        string copyOf = root + " copy";
        for (int n = 2; InUse(copyOf); n++)
        {
            copyOf = root + " copy " + n;
        }

        return copyOf;
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
    {            var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a crosshair picture or animation",
            Filter = CrosshairFileKinds.OpenFilter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _ = UseCrosshairPictureAsync(dialog.FileName);
    }

    /// <summary>
    /// Make a picture file the crosshair, whether it came from the Import button or
    /// from the pack. Either way it is copied into the app's own folder in a
    /// cleaned-up form first, so the preset never depends on the original staying
    /// where it was, and the picture then answers to every control on the tab.
    /// <para>
    /// The reading happens on a worker thread. A still is quick, but an animated GIF
    /// is dozens of frames to decode, build and shrink, and doing that on the UI
    /// thread would freeze the window for as long as it took.
    /// </para>
    /// </summary>
    private Task UseCrosshairPictureAsync(string path)
    {
        return UiGuard.RunAsync(
            "CROSSHAIR PICTURE",
            async () =>
            {
                (CrosshairImageStore.StoredPicture? stored, string problem) = await Task.Run(() =>
                {
                    CrosshairImageStore.StoredPicture? picture = CrosshairImageStore.Import(path, out string reason);
                    return (picture, reason);
                });

                if (stored is null)
                {
                    Flash(problem, true);
                    return;
                }

                string name = stored.Name;
                string previous = _workCrosshair.CustomImage;
                _workCrosshair.CustomImage = name;
                _workCrosshair.ShapeKind = CrosshairShape.Custom;

                // A picture that brought colours of its own is shown as it came, so a
                // multi-coloured animation arrives multi-coloured instead of in
                // whatever colour the tab happened to be showing. A photograph used to
                // get the same result by being forced to white; the difference now is
                // that the colour is left where the user put it, for the glow. A white
                // or grey picture is a mask and goes on answering to the swatches.
                _workCrosshair.KeepOwnColours = stored.OwnColours;

                OnXhEdited();
                UpdateXhControls();

                // The picture it replaced is only kept while a saved crosshair uses it.
                if (!string.IsNullOrEmpty(previous)
                    && !_settings.CustomCrosshairs.Any(p => string.Equals(p.CustomImage, previous, StringComparison.OrdinalIgnoreCase)))
                {
                    CrosshairImageStore.Delete(previous);
                }

                Flash(CrosshairImageStore.LoadPicture(name) is { IsAnimated: true } ? "Animation ready" : "Picture ready");
            },
            ex => Flash("Could not use that picture", true));
    }

    // ------------------------------------------------------------- the pack

    /// <summary>Counts up each time the browser fills, so a fill that was interrupted knows to stop.</summary>
    private int _xhPackLoad;

    /// <summary>
    /// Side of a tile's animation frame, in pixels. Larger than the tile itself so a
    /// 150 percent scaled window still shows it sharp.
    /// </summary>
    private const int PackPreviewSide = 64;

    /// <summary>
    /// Most tiles allowed to play at once. A folder full of animations would otherwise
    /// hold every decoded frame of every picture in memory for a grid that is mostly
    /// scrolled out of sight; past this the rest stay on their first frame.
    /// </summary>
    private const int MaxAnimatedPackTiles = 48;

    /// <summary>How often the browser's clock steps its tiles, in milliseconds.</summary>
    private const int PackPreviewTickMs = 40;

    /// <summary>Longest side of a tile's first frame, in pixels. The tile draws it at 46.</summary>
    private const int PackThumbSide = 96;

    /// <summary>Tiles that are playing, with the frame each is on.</summary>
    private readonly List<PackPreviewPlayer> _xhPackPlayers = new();

    /// <summary>Two decodes at a time: enough to fill in quickly, few enough to leave the UI responsive.</summary>
    private readonly SemaphoreSlim _xhPackPreviewSlots = new(2);

    private DispatcherTimer? _xhPackClock;

    /// <summary>Tiles whose preview has been started but not attached yet, so the cap counts them.</summary>
    private int _xhPackPreviewsPending;

    /// <summary>
    /// The folder listing taken when the browser opened. Typing in the filter box
    /// narrows this rather than reading the disk again on every keystroke. Null
    /// while the listing is still being read, and after the browser has closed.
    /// </summary>
    private List<string>? _xhPackFiles;

    private async void OnXhPackClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady)
        {
            return;
        }

        // Open first and read after. The first launch unpacks the starter pictures
        // in the background, and EnsureFolder waits for that copy, so doing it on
        // this thread would freeze the window for as long as the copy takes.
        int generation = ++_xhPackLoad;
        _xhPackFiles = null;
        XhPackPanel.Children.Clear();
        XhPackCount.Text = string.Empty;
        XhPackEmpty.Text = "Loading pack...";
        XhPackEmpty.Visibility = Visibility.Visible;
        XhPackPopup.IsOpen = true;

        List<string> files = await Task.Run(CrosshairPack.List);

        if (generation != _xhPackLoad)
        {
            // Closed (or reopened) while the folder was being read.
            return;
        }

        _xhPackFiles = files;
        FillXhPack();
    }

    private void OnXhPackClosed(object? sender, EventArgs e)
    {
        // Let go of every thumbnail. They are rebuilt from the folder next time.
        _xhPackLoad++;
        _xhPackFiles = null;
        ClearPackPreviews();
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
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
        // Nothing to filter until the listing has arrived; FillXhPack runs then
        // and reads the box as it stands.
        if (XhPackPopup.IsOpen && _xhPackFiles is not null)
        {
            FillXhPack();
        }
    }

    private void FillXhPack()
    {
        int generation = ++_xhPackLoad;
        ClearPackPreviews();
        XhPackPanel.Children.Clear();

        List<string> files = _xhPackFiles is null ? new List<string>() : new List<string>(_xhPackFiles);
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
            XhPackEmpty.Text = "No pictures yet. Add PNG, GIF, WebP or JPG files with Open folder, then reopen.";
        }

        // Masks are shown in the colour the crosshair is set to, so the grid previews
        // what choosing one will look like. Pictures with colours of their own are
        // shown as they are when BuildPackTile builds their tile, for the same reason.
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
                Button? tile = BuildPackTile(files[next], tint, colour, generation);
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

    private Button? BuildPackTile(string path, Brush tint, Color colour, int generation)
    {
        try
        {
            BitmapSource? thumb = PackThumbnail(path);
            if (thumb is null)
            {
                // A picture no decoder here can open: left out of the grid rather
                // than shown as an empty tile that does nothing when clicked.
                return null;
            }

            // The picture's own shape filled with the chosen colour, which is what
            // the multiply tint does to a white mask - and what the pack largely is.
            // A picture that will arrive in its own colours is shown as it came, so
            // the grid cannot promise a colour the import is not going to apply.
            bool ownColours = KeepsOwnColours(path, thumb);
            FrameworkElement face;
            if (ownColours)
            {
                face = new System.Windows.Controls.Image { Width = 46, Height = 46, Source = thumb, Stretch = Stretch.Uniform };
            }
            else
            {
                face = new System.Windows.Shapes.Rectangle
                {
                    Width = 46,
                    Height = 46,
                    Fill = tint,
                    OpacityMask = new ImageBrush(thumb) { Stretch = Stretch.Uniform }
                };
            }

            var tile = new Button
            {
                Style = (Style)FindResource("XhPackTile"),
                Content = face,
                Tag = path,
                ToolTip = Path.GetFileNameWithoutExtension(path) + " (" + FileSizeText(path) + ")"
            };

            AutomationProperties.SetName(tile, "Use crosshair " + Path.GetFileNameWithoutExtension(path));
            tile.Click += OnXhPackTileClick;

            // An animation is asked for its frames in the background. Until they
            // arrive the tile shows its first frame, which is exactly what it used
            // to show all the time, so a slow one costs nothing but a moment of
            // stillness.
            if (CrosshairFileKinds.FromPath(path) is PictureKind.Gif or PictureKind.WebP)
            {
                StartPackPreview(tile, path, colour, ownColours, generation);
            }

            return tile;
        }
        catch (Exception ex)
        {
            // One unreadable file is skipped, not allowed to empty the browser.
            AppLog.Error("CROSSHAIR PACK TILE", ex);
            return null;
        }
    }

    /// <summary>
    /// Begin decoding a tile's animation, unless the grid is already playing as many
    /// as it will. Decoding happens a couple at a time on worker threads, because a
    /// GIF is dozens of frames to compose and the grid is filled while the window has
    /// to stay responsive.
    /// </summary>
    private void StartPackPreview(Button tile, string path, Color colour, bool ownColours, int generation)
    {
        if (_xhPackPlayers.Count + _xhPackPreviewsPending >= MaxAnimatedPackTiles)
        {
            return;
        }

        _xhPackPreviewsPending++;
        _ = AttachPackPreviewAsync(tile, path, colour, ownColours, generation);
    }

    private async Task AttachPackPreviewAsync(
        Button tile,
        string path,
        Color colour,
        bool ownColours,
        int generation)
    {
        CrosshairPackPreview? preview = null;

        await _xhPackPreviewSlots.WaitAsync();
        try
        {
            // Rechecked inside the slot: the browser may have been closed or refilled
            // while this one was queued, and decoding then is pure waste.
            if (generation == _xhPackLoad)
            {
                preview = await Task.Run(() => CrosshairPackPreview.Build(
                    path, PackPreviewSide, ownColours ? null : colour));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CROSSHAIR PACK PREVIEW", ex);
        }
        finally
        {
            _xhPackPreviewSlots.Release();
            _xhPackPreviewsPending--;
        }

        // The generation is the browser's own answer to "is this still wanted":
        // it moves on both when the grid refills and when the popup closes, and the
        // close handler clears the players. That is why there is no second check on
        // the popup being open here - two guards for one fact is how the two come to
        // disagree, and it is what made this attach untestable without a real popup.
        if (preview is null || generation != _xhPackLoad)
        {
            return;
        }

        // The frames carry the tint already, so the tile becomes a plain image. A
        // rectangle with an opacity mask cannot follow a picture that changes frame.
        var image = new System.Windows.Controls.Image
        {
            Width = 46,
            Height = 46,
            Source = preview.Frames[0],
            Stretch = Stretch.Uniform
        };

        tile.Content = image;
        _xhPackPlayers.Add(new PackPreviewPlayer(image, preview.Frames, preview.DelaysMs));
        StartPackClock();
    }

    /// <summary>
    /// The one clock the whole grid shares. A timer per tile would wake the window
    /// dozens of times a frame for the same animation; this wakes it once and steps
    /// them all.
    /// </summary>
    private void StartPackClock()
    {
        if (_xhPackClock is null)
        {
            _xhPackClock = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PackPreviewTickMs)
            };

            _xhPackClock.Tick += OnPackClockTick;
        }

        _xhPackClock.Start();
    }

    private void OnPackClockTick(object? sender, EventArgs e)
    {
        foreach (PackPreviewPlayer player in _xhPackPlayers)
        {
            player.Advance(PackPreviewTickMs);
        }
    }

    /// <summary>
    /// Stop the clock and let go of every decoded frame. Called when the browser
    /// closes or refills: the frames are rebuilt from the folder next time, and a
    /// closed popup holding a few hundred images is memory nobody asked for.
    /// </summary>
    private void ClearPackPreviews()
    {
        _xhPackClock?.Stop();
        _xhPackPlayers.Clear();
    }

    /// <summary>
    /// One tile playing its animation: which frame it is on, and how long it has been
    /// holding it. Advanced from the shared clock with the time that has passed since
    /// the last tick, so the loop plays at the speed the GIF asked for rather than at
    /// the speed of the timer.
    /// </summary>
    private sealed class PackPreviewPlayer
    {
        private readonly System.Windows.Controls.Image _target;
        private readonly IReadOnlyList<BitmapSource> _frames;
        private readonly IReadOnlyList<int> _delaysMs;
        private int _index;
        private int _elapsedMs;

        public PackPreviewPlayer(
            System.Windows.Controls.Image target,
            IReadOnlyList<BitmapSource> frames,
            IReadOnlyList<int> delaysMs)
        {
            _target = target;
            _frames = frames;
            _delaysMs = delaysMs;
        }

        public void Advance(int milliseconds)
        {
            _elapsedMs += milliseconds;
            int start = _index;

            // A delay is never below 20 ms, so this always ends.
            while (_elapsedMs >= _delaysMs[_index])
            {
                _elapsedMs -= _delaysMs[_index];
                _index = (_index + 1) % _frames.Count;
            }

            if (_index != start)
            {
                _target.Source = _frames[_index];
            }
        }
    }

    /// <summary>
    /// The first frame of a pack picture, small enough for its tile.
    /// <para>
    /// Everything WIC knows how to open is decoded by it, which is every kind but
    /// WebP. A WebP goes through the app's own decoder instead: Windows only opens
    /// one when a Store codec happens to be installed, and a folder the browser
    /// could not show would look like a broken pack rather than a missing codec.
    /// </para>
    /// <para>
    /// Null for a picture that will not open, which is a tile left out of the grid.
    /// One unreadable file must not empty the browser.
    /// </para>
    /// </summary>
    private static BitmapSource? PackThumbnail(string path)
    {
        if (CrosshairFileKinds.FromPath(path) == PictureKind.WebP)
        {
            if (WebpReader.ReadFirstFrame(path) is not { } frame)
            {
                return null;
            }

            byte[] square = PixelResize.FitSquare(frame.Pixels, frame.Width, frame.Height, PackThumbSide, out int side);
            BitmapSource bitmap = BitmapSource.Create(
                side, side, 96, 96, PixelFormats.Pbgra32, null, square, side * 4);
            bitmap.Freeze();
            return bitmap;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var thumb = new BitmapImage();
        thumb.BeginInit();
        thumb.CacheOption = BitmapCacheOption.OnLoad;
        thumb.DecodePixelWidth = PackThumbSide;
        thumb.StreamSource = stream;
        thumb.EndInit();
        thumb.Freeze();
        return thumb;
    }

    /// <summary>
    /// Whether a pack picture will arrive in its own colours, by the same rule the
    /// import uses: a photograph or bitmap always does, a PNG, GIF or WebP only if
    /// it carries colour. Read off the thumbnail the tile is already showing rather
    /// than off the file again, so the preview and the import cannot disagree about
    /// anything but the resize.
    /// </summary>
    private static bool KeepsOwnColours(string path, BitmapSource thumb)
    {
        PictureKind kind = CrosshairFileKinds.FromPath(path);
        if (kind is PictureKind.Jpeg or PictureKind.Bmp)
        {
            return true;
        }

        try
        {
            var converted = new FormatConvertedBitmap(thumb, PixelFormats.Pbgra32, null, 0);
            byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return CrosshairImageStore.HasOwnColours(pixels);
        }
        catch (Exception ex)
        {
            // An odd palette or format that will not convert: show it as a mask, the
            // way a picture that cannot be read would be shown.
            AppLog.Error("CROSSHAIR PACK TINT", ex);
            return false;
        }
    }

    /// <summary>A file's size the way a person reads it. Blank rather than an error if it cannot be read.</summary>
    private static string FileSizeText(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            return bytes >= 1024 * 1024
                ? (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
                : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "size unknown";
        }
    }

    private void OnXhPackTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        XhPackPopup.IsOpen = false;
        _ = UseCrosshairPictureAsync(path);
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
            CrosshairPreset effective = EffectiveCrosshair(_workCrosshair, out CrosshairPicture? picture);

            // A PNG holds one frame, and a frame caught at an arbitrary point in a
            // moving crosshair is a worse picture of it than the crosshair itself. An
            // exported file is always the still one, whatever the tab is doing.
            if (effective.Animated)
            {
                effective = effective.Copy();
                effective.EffectKind = CrosshairEffect.None;
            }

            int side = CrosshairGeometry.WindowSide(effective);

            // A PNG holds one picture, so an animation is exported as its first
            // frame, which is what a view that is not on the screen draws.
            var view = new CrosshairView
            {
                Width = side,
                Height = side,
                Preset = effective,
                Picture = picture,
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

            Flash(picture is { IsAnimated: true } ? "First frame exported" : "Crosshair exported");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The place the user chose would not take the file: read-only folder,
            // full disk, a file held open elsewhere.
            AppLog.Error("CROSSHAIR EXPORT WRITE", ex);
            Flash("Could not save that file", true);
        }
        catch (Exception ex)
        {
            // Rendering or encoding failed, which a different folder would not fix.
            AppLog.Error("CROSSHAIR EXPORT", ex);
            Flash("Could not export the crosshair", true);
        }
    }

    /// <summary>
    /// Put the crosshair back to how a new one starts: default colour, size,
    /// thickness, gap, opacity, rotation, outline, glow and center dot. The shape,
    /// the picture and the name stay.
    /// <para>
    /// A picture that has colours of its own goes back to them, which is the way out
    /// of a colour picked over a multi-coloured animation. That is read off the stored
    /// picture rather than remembered from the import, because the file may have been
    /// restored from a backup or imported before this setting existed.
    /// </para>
    /// </summary>
    private void OnXhResetClick(object sender, RoutedEventArgs e)
    {
        if (!_xhReady)
        {
            return;
        }

        ShowConfirmModal(
            "Reset crosshair?",
            "The color, size, thickness, gap, opacity, rotation, outline, glow and center dot go back to their defaults. The shape and the picture stay.",
            "Reset",
            () =>
            {
                _workCrosshair.ResetTuning();
                _workCrosshair.KeepOwnColours = CrosshairImageStore.PictureHasOwnColours(_workCrosshair.CustomImage);
                OnXhEdited();
                UpdateXhControls();
                Flash("Crosshair reset");
            });
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
