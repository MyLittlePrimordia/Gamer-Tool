using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The Crosshair tab driven the way a person drives it: pressing its buttons and
/// switches, and reading back what the preset, the saved profile and the browser
/// actually did.
/// <para>
/// These are not tests of a copy of the tab's logic. The driver loads the real
/// MainWindow.xaml, runs the real <c>InitCrosshairTab</c>, and every assertion below
/// is about a routed event reaching a handler that was written by hand in markup.
/// A switch bound to the wrong flag, a Reset button pointing at nothing, or a pack
/// tile that never starts playing all fail here, where the service tests below them
/// cannot see them.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairTabTests
{
    [Fact]
    public void The_tab_stands_up_on_the_saved_picture_crosshair()
    {
        CrosshairTab.Run(h =>
        {
            // Loaded from the temp profile and drawn, not just parsed: the picture
            // exists, so the tab is on its picture controls rather than falling back
            // to a cross.
            Assert.Equal(CrosshairShape.Custom, h.Work.ShapeKind);
            Assert.Equal("Test", h.Work.Name);
            Assert.Equal("#FF2BB0", h.Work.Color);
            Assert.Equal(57, h.Work.Size);

            // The own-colours switch is there for a picture, and off for this white mask.
            Assert.Equal(Visibility.Visible, h.Window.XhTintRow.Visibility);
            Assert.False(h.Window.XhOwnColoursBox.IsChecked == true);
        });
    }

    [Fact]
    public void The_switch_turns_own_colours_on_in_the_preset_and_in_the_saved_copy()
    {
        CrosshairTab.Run(h =>
        {
            h.Window.XhOwnColoursBox.IsChecked = true;

            Assert.True(h.Work.KeepOwnColours);

            // OnXhEdited writes a user-made crosshair straight back into the profile,
            // so the stored copy has to agree, or the tab would forget on reload.
            Assert.True(h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test").KeepOwnColours);

            h.Window.XhOwnColoursBox.IsChecked = false;
            Assert.False(h.Work.KeepOwnColours);
        });
    }

    [Fact]
    public void The_switch_goes_away_when_a_drawn_shape_is_chosen()
    {
        CrosshairTab.Run(h =>
        {
            // Pressing the tile is what a user does, and the tile handler is wired in
            // code rather than markup, so this is the half a markup test cannot see.
            h.SelectShape(CrosshairShape.Cross);

            Assert.Equal(CrosshairShape.Cross, h.Work.ShapeKind);
            Assert.Equal(Visibility.Collapsed, h.Window.XhTintRow.Visibility);

            // And back again, with the picture still named and the switch showing what
            // it had stored, so a shape detour loses nothing.
            h.Work.KeepOwnColours = true;
            h.Window.XhTilePanel.Children
                .OfType<RadioButton>()
                .First(rb => rb.Tag is CrosshairShape tag && tag == CrosshairShape.Custom)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(CrosshairShape.Custom, h.Work.ShapeKind);
            Assert.Equal(Visibility.Visible, h.Window.XhTintRow.Visibility);
            Assert.True(h.Window.XhOwnColoursBox.IsChecked == true);
        });
    }

    [Fact]
    public void Reset_asks_before_it_changes_anything()
    {
        CrosshairTab.Run(h =>
        {
            h.Window.XhSizeSlider.Value = 44;
            Assert.Equal(44, h.Work.Size);

            CrosshairTab.Click(h.Window.XhResetButton);

            Assert.True(h.ModalOpen);
            Assert.Equal("Reset to Test?", h.ModalTitle);

            // It names the preset and says what comes back, so a Reset cannot be a
            // surprise to anyone who reads it.
            Assert.Contains("color", h.ModalWarning);
            Assert.Contains("size", h.ModalWarning);
            Assert.Contains("shape and picture", h.ModalWarning);
            Assert.Contains("Any changes since then are dropped.", h.ModalWarning);

            // Nothing has moved yet: the dialog is a question, not a countdown.
            Assert.Equal(44, h.Work.Size);
            Assert.Equal("#FF2BB0", h.Work.Color);
        });
    }

    [Fact]
    public void Cancelling_the_reset_leaves_the_crosshair_alone()
    {
        CrosshairTab.Run(h =>
        {
            h.Window.XhSizeSlider.Value = 44;
            h.Window.XhThicknessSlider.Value = 3;

            CrosshairTab.Click(h.Window.XhResetButton);
            h.CancelModal();

            Assert.False(h.ModalOpen);
            Assert.Equal("#FF2BB0", h.Work.Color);
            Assert.Equal(44, h.Work.Size);
            Assert.Equal(3, h.Work.Thickness);
            Assert.Equal(11, h.Work.PictureGap);
            Assert.True(h.Work.Glow);
        });
    }

    [Fact]
    public void Confirming_the_reset_puts_the_loaded_preset_back_including_its_picture()
    {
        CrosshairTab.Run(h =>
        {
            // Change every kind of thing the preset holds: colour, the numbers, a
            // switch, the shape, and the own-colours setting that only a picture has.
            h.Window.XhSwatch0.IsChecked = true;
            h.Window.XhSizeSlider.Value = 30;
            h.Window.XhThicknessSlider.Value = 2;
            h.Window.XhGapSlider.Value = 5;
            h.Window.XhOpacitySlider.Value = 60;
            h.Window.XhRotationSlider.Value = 10;
            h.Window.XhOwnColoursBox.IsChecked = true;
            h.Window.XhCenterDotBox.IsChecked = false;
            h.Window.XhGlowBox.IsChecked = false;
            h.Window.XhOutlineBox.IsChecked = true;
            h.SelectShape(CrosshairShape.Cross);

            Assert.Equal("#FF3B3B", h.Work.Color);
            Assert.Equal(30, h.Work.Size);
            Assert.Equal(CrosshairShape.Cross, h.Work.ShapeKind);

            CrosshairTab.Click(h.Window.XhResetButton);
            h.ConfirmModal();

            Assert.False(h.ModalOpen);

            // The preset as it was opened: its own colour and tuning...
            Assert.Equal("#FF2BB0", h.Work.Color);
            Assert.Equal(57, h.Work.Size);
            Assert.Equal(7, h.Work.Thickness);
            Assert.Equal(11, h.Work.PictureGap);
            Assert.Equal(35, h.Work.Opacity);
            Assert.Equal(200, h.Work.Rotation);
            Assert.True(h.Work.CenterDot);
            Assert.True(h.Work.Glow);
            Assert.False(h.Work.Outline);

            // ...and its picture and shape, which came back with it.
            Assert.Equal(CrosshairShape.Custom, h.Work.ShapeKind);
            Assert.Equal(CrosshairTab.WhitePicture, h.Work.CustomImage);
            Assert.False(h.Work.KeepOwnColours);

            // The saved copy was put back too, so closing the tab cannot resurrect
            // the changes the reset just dropped.
            CrosshairPreset stored = h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test");
            Assert.Equal(57, stored.Size);
            Assert.Equal(7, stored.Thickness);
            Assert.Equal(CrosshairShape.Custom, stored.ShapeKind);

            // And the tab is showing the result, not just holding it.
            Assert.Equal(Visibility.Visible, h.Window.XhTintRow.Visibility);
            Assert.False(h.Window.XhOwnColoursBox.IsChecked == true);
            Assert.Equal("57", h.Window.XhSizeValue.Text.Replace(" px", string.Empty));
        });
    }

    [Fact]
    public void Reset_an_edited_built_in_restores_that_preset_not_app_defaults()
    {
        CrosshairTab.Run(h =>
        {
            SelectPreset(h, "xh-ring");
            Assert.Equal(26, h.Work.Size);

            // The shape and the tuning both move, which is exactly the state the old
            // Reset answered with a new crosshair's defaults.
            h.SelectShape(CrosshairShape.Cross);
            h.Window.XhSizeSlider.Value = 60;
            Assert.True(h.Read<bool>("_crosshairTuned"));

            CrosshairTab.Click(h.Window.XhResetButton);
            Assert.True(h.ModalOpen);
            Assert.Equal("Reset to Range Ring?", h.ModalTitle);

            h.ConfirmModal();

            Assert.False(h.ModalOpen);
            Assert.False(h.Read<bool>("_crosshairTuned"));
            Assert.Equal(CrosshairShape.Circle, h.Work.ShapeKind);
            Assert.Equal(26, h.Work.Size);
            Assert.Equal(1, h.Work.Thickness);
            Assert.Equal(0, h.Work.Gap);
            Assert.Equal("xh-ring", h.PresetId);
            Assert.Equal("xh-ring", h.ShownPresetId);
            Assert.DoesNotContain("edited", h.Window.XhPresetBox.Text);
        });
    }

    [Fact]
    public void Reset_a_crosshair_that_still_matches_its_preset_changes_nothing()
    {
        CrosshairTab.Run(h =>
        {
            CrosshairTab.Click(h.Window.XhResetButton);

            // Nothing has been changed, so there is nothing to ask about and nothing
            // to put back. A dialog here would have only one real answer.
            Assert.False(h.ModalOpen);
            Assert.Equal(57, h.Work.Size);
            Assert.Equal(7, h.Work.Thickness);
            Assert.Equal(CrosshairShape.Custom, h.Work.ShapeKind);
        });
    }

    [Fact]
    public void Reset_sits_beside_the_button_that_puts_the_crosshair_on_the_screen()
    {
        CrosshairTab.Run(h =>
        {
            // One row in the footer, where undoing a crosshair that has been tuned
            // into a mess belongs. It used to be in the SHAPE row, where nothing
            // about it was about shape.
            var row = Assert.IsType<StackPanel>(h.Window.XhResetButton.Parent);
            Assert.Same(row, h.Window.XhShowButton.Parent);
            Assert.Contains(h.Window.XhShowButton, row.Children.OfType<Button>());
            Assert.Contains(h.Window.XhResetButton, row.Children.OfType<Button>());

            // A button with a word on it, next to the one it belongs with.
            Assert.Equal("Reset", h.Window.XhResetButton.Content);

            // And actually laid out there. The window is never shown in the harness,
            // so the tab is measured and arranged by hand; without that every button
            // in the window is zero-sized and the row above would prove nothing about
            // where anything ends up.
            h.LayOut();

            Button reset = h.Window.XhResetButton;
            Button show = h.Window.XhShowButton;
            Assert.True(reset.ActualWidth > 0 && reset.ActualHeight > 0, "Reset has no size on the page");
            Assert.Equal(Visibility.Visible, reset.Visibility);

            var owner = (UIElement)h.Window.XhShowButton.Parent;
            Point resetAt = reset.TranslatePoint(new Point(0, 0), owner)!;
            Point showAt = show.TranslatePoint(new Point(0, 0), owner)!;

            Assert.True(resetAt.X + reset.ActualWidth <= showAt.X, "Reset is not to the left of Show on screen");
            Assert.InRange(Math.Abs((resetAt.Y + (reset.ActualHeight / 2)) - (showAt.Y + (show.ActualHeight / 2))), 0, 1);
        });
    }

    [Fact]
    public void The_center_dot_takes_a_colour_of_its_own_from_its_picker()
    {
        CrosshairTab.Run(h =>
        {
            // Following the crosshair until it is told otherwise.
            Assert.Equal(h.Work.Color, h.Work.EffectiveCenterDotColor());
            Assert.False(h.Work.HasOwnCenterDotColor);

            Assert.True(h.Window.XhDotColorButton.IsEnabled, "the dot is on, so its colour can be set");
            CrosshairTab.Click(h.Window.XhDotColorButton);

            // The picker came up holding the colour the dot is already wearing, so it
            // opens on the current value rather than on red. Its open state is not
            // asserted: a popup that is not StaysOpen closes itself the moment it
            // loses focus, and with no window on screen it loses it at once.
            Color at = h.Window.XhDotHsv.Color;
            Assert.InRange(at.R, 0xFD, 0xFF);
            Assert.InRange(at.G, 0x29, 0x2D);
            Assert.InRange(at.B, 0xAE, 0xB2);

            // Typing the hex is the picker's exact-color route, and it is the one a
            // test can drive without a mouse.
            h.Window.XhDotPickHex.Text = "#1FE0FF";
            h.Window.XhDotPickHex.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.Equal("#1FE0FF", h.Work.CenterDotColor);

            // The saved copy too, or the colour would be gone on reload.
            Assert.Equal("#1FE0FF", h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test").CenterDotColor);

            // And the chip is showing what the dot will be painted.
            var fill = Assert.IsType<SolidColorBrush>(h.Window.XhDotColorChip.Fill);
            Assert.Equal(0x1F, fill.Color.R);
            Assert.Equal(0xE0, fill.Color.G);
            Assert.Equal(0xFF, fill.Color.B);
        });
    }

    [Fact]
    public void The_center_dot_is_drawn_in_its_own_colour_and_Reset_puts_it_back()
    {
        CrosshairTab.Run(h =>
        {
            h.Window.XhDotPickHex.Text = "#1FE0FF";
            h.Window.XhDotPickHex.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.True(h.Work.HasOwnCenterDotColor);

            // The drawing, not just the setting: a red cross with a cyan dot in the
            // middle of it, pixel for pixel.
            h.Work.Color = "#FF0000";
            h.Work.Outline = false;
            h.Work.Glow = false;
            h.Work.CenterDot = true;
            (byte R, byte G, byte B) middle = DotPixel(h.Work);

            // Within a rounding step: an alpha blend through a premultiplied bitmap
            // can land one count off the colour that went in.
            Assert.InRange(middle.R, 0x1D, 0x21);
            Assert.InRange(middle.G, 0xDE, 0xE2);
            Assert.InRange(middle.B, 0xFD, 0xFF);

            // Reset is the way back to one colour, which is how this preset was loaded.
            CrosshairTab.Click(h.Window.XhResetButton);
            h.ConfirmModal();

            Assert.False(h.Work.HasOwnCenterDotColor);
            Assert.Equal(h.Work.Color, h.Work.EffectiveCenterDotColor());
        });
    }

    [Fact]
    public void The_dot_size_slider_sits_beside_rotation_on_the_same_row()
    {
        CrosshairTab.Run(h =>
        {
            h.LayOut();
            FrameworkElement page = h.Page();

            StackPanel dot = h.Window.XhDotSizePanel;
            StackPanel rotation = h.Window.XhRotationPanel;
            Assert.True(dot.ActualWidth > 0 && rotation.ActualWidth > 0, "a control on the tuning row has no size");

            Point dotAt = dot.TranslatePoint(new Point(0, 0), page)!;
            Point rotationAt = rotation.TranslatePoint(new Point(0, 0), page)!;

            // The cell the grid had going spare: right of Rotation, on its row, and
            // still inside the page rather than hanging off the end of it.
            Assert.True(dotAt.X >= rotationAt.X + rotation.ActualWidth, "dot size is not beside rotation");
            Assert.InRange(Math.Abs(dotAt.Y - rotationAt.Y), 0, 1);
            Assert.True(dotAt.X + dot.ActualWidth <= page.ActualWidth + 1, "dot size runs off the page");
        });
    }

    [Fact]
    public void The_center_dot_size_follows_the_line_until_the_slider_is_moved()
    {
        CrosshairTab.Run(h =>
        {
            // The harness crosshair is seven pixels thick, so the dot it inherits is
            // eight wide, and the slider shows that rather than a number of its own.
            Assert.Equal(7, h.Work.Thickness);
            Assert.False(h.Work.HasOwnCenterDotSize);
            Assert.Equal(8, h.Window.XhDotSizeSlider.Value);
            Assert.Equal(8, h.Work.EffectiveCenterDotSize());
            Assert.Contains("Auto", h.Window.XhDotSizeValue.Text);
            Assert.Contains("8 px", h.Window.XhDotSizeValue.Text);

            // Moving it is what makes the size its own.
            h.Window.XhDotSizeSlider.Value = 3;

            Assert.True(h.Work.HasOwnCenterDotSize);
            Assert.Equal(3, h.Work.CenterDotSize);
            Assert.Equal(3, h.Work.EffectiveCenterDotSize());
            Assert.Equal("3 px", h.Window.XhDotSizeValue.Text);

            // And the saved copy, or the size would be gone on the next reload.
            Assert.Equal(3, h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test").CenterDotSize);

            // Reset puts the preset back as it was loaded: no size of its own, on its
            // seven pixel line, so the dot it inherits is eight wide again.
            CrosshairTab.Click(h.Window.XhResetButton);
            h.ConfirmModal();

            Assert.False(h.Work.HasOwnCenterDotSize);
            Assert.Equal(0, h.Work.CenterDotSize);
            Assert.Equal(7, h.Work.Thickness);
            Assert.Equal(8, h.Window.XhDotSizeSlider.Value);
            Assert.Contains("Auto", h.Window.XhDotSizeValue.Text);
            Assert.Contains("8 px", h.Window.XhDotSizeValue.Text);

            // The saved copy follows, or the size would be back on the next load.
            Assert.Equal(0, h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test").CenterDotSize);
        });
    }

    [Fact]
    public void The_center_dot_size_is_dead_without_a_dot_to_size()
    {
        CrosshairTab.Run(h =>
        {
            // The harness crosshair has its dot on, so the slider is live.
            Assert.True(h.Window.XhDotSizeSlider.IsEnabled);
            Assert.Equal(1.0, h.Window.XhDotSizePanel.Opacity);

            h.Window.XhCenterDotBox.IsChecked = false;

            // Dimmed rather than hidden, so the row does not jump and the size it
            // will come back with is still readable.
            Assert.False(h.Window.XhDotSizeSlider.IsEnabled);
            Assert.Equal(0.4, h.Window.XhDotSizePanel.Opacity);
            Assert.Equal(Visibility.Visible, h.Window.XhDotSizePanel.Visibility);

            h.Window.XhCenterDotBox.IsChecked = true;
            Assert.True(h.Window.XhDotSizeSlider.IsEnabled);

            // A dot shape is already a dot, so there is no second one to size.
            h.SelectShape(CrosshairShape.Dot);

            Assert.False(h.Window.XhDotSizeSlider.IsEnabled);
            Assert.Equal(0.4, h.Window.XhDotSizePanel.Opacity);
        });
    }

    [Fact]
    public void The_custom_colour_swatch_is_a_wheel_rather_than_a_plus()
    {
        WpfTestHost.Invoke(() =>
        {
            var wheel = Assert.IsAssignableFrom<BitmapSource>(ColorWheel.Wheel);
            Assert.True(wheel.IsFrozen, "it is shared by every swatch and read on the render thread");
            Assert.True(wheel.PixelWidth > 16, "a wheel scaled up from a tiny bitmap has a jagged rim");

            // White in the middle, and a different hue either side: which no flat
            // disc and no plus sign can be.
            (byte R, byte G, byte B)? middle = ColourAt(wheel, 0.5, 0.5);
            (byte R, byte G, byte B)? right = ColourAt(wheel, 0.94, 0.5);
            (byte R, byte G, byte B)? left = ColourAt(wheel, 0.06, 0.5);

            Assert.NotNull(middle);
            Assert.True(middle!.Value.R > 240 && middle.Value.G > 240 && middle.Value.B > 240, "the middle of a hue wheel is white");
            Assert.NotNull(right);
            Assert.NotNull(left);
            Assert.True(right!.Value.R > right.Value.G, "the right side of the wheel is red");
            Assert.True(left!.Value.G > left.Value.R, "the left side of the wheel is cyan");
        });
    }

    [Fact]
    public void The_centre_dot_chip_is_the_same_wheel_as_the_custom_colour_swatch()
    {
        CrosshairTab.Run(h =>
        {
            h.Window.XhDotColorButton.ApplyTemplate();

            Assert.NotNull(FindWheel(h.Window.XhDotColorButton));

            // The wheel is behind the dot in the middle, not in place of it: the chip
            // is what the centre dot is wearing.
            Assert.True(
                h.Window.XhDotColorChip.Width < h.Window.XhDotColorButton.Width,
                "the chip is the colour in the middle of the wheel, not the wheel");
        });
    }

    /// <summary>The rainbow wheel in a control's template, if it has one.</summary>
    private static ImageBrush? FindWheel(DependencyObject root)
    {
        if (root is not Visual visual)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(visual, i);

            if (child is Ellipse { Fill: ImageBrush brush } && ReferenceEquals(brush.ImageSource, ColorWheel.Wheel))
            {
                return brush;
            }

            if (FindWheel(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    [Fact]
    public void Duplicate_comes_back_as_a_second_preset_with_a_name_of_its_own()
    {
        CrosshairTab.Run(h =>
        {
            CrosshairTab.Click(h.Window.XhDuplicateButton);

            Assert.Equal(2, h.Saved.Count);
            CrosshairPreset copy = h.Saved[1];

            Assert.NotEqual("xh-test", copy.Id);
            Assert.Equal("Test copy", copy.Name);

            // Everything the tab is showing came over, picture included.
            Assert.Equal(CrosshairShape.Custom, copy.ShapeKind);
            Assert.Equal(CrosshairTab.WhitePicture, copy.CustomImage);
            Assert.Equal("#FF2BB0", copy.Color);
            Assert.Equal(57, copy.Size);
            Assert.Equal(7, copy.Thickness);
            Assert.Equal(11, copy.PictureGap);
            Assert.Equal(35, copy.Opacity);

            // And the tab is on the copy, which is what the dropdown reads.
            Assert.Equal(copy.Id, h.PresetId);
            Assert.Equal(copy.Id, h.Settings.ActiveCrosshairId);
            Assert.Equal(copy.Id, h.ShownPresetId);

            // The original is where it was, so this is a fork rather than a move.
            Assert.Equal("Test", h.Saved[0].Name);
            Assert.Equal(57, h.Saved[0].Size);
        });
    }

    [Fact]
    public void Duplicating_twice_numbers_the_copies_instead_of_stacking_the_word_copy()
    {
        CrosshairTab.Run(h =>
        {
            CrosshairTab.Click(h.Window.XhDuplicateButton);
            CrosshairTab.Click(h.Window.XhDuplicateButton);

            Assert.Equal(
                new[] { "Test", "Test copy", "Test copy 2" },
                h.Saved.Select(p => p.Name).ToArray());
        });
    }

    [Fact]
    public void An_edited_built_in_can_be_kept_as_a_copy_under_a_name_that_says_what_it_is()
    {
        CrosshairTab.Run(
            h =>
            {
                // A slider is the gesture that tunes it, and a built-in cannot be
                // written to, so the tab is holding an edit against that preset.
                h.Window.XhSizeSlider.Value = 44;

                // The id stays, and the box says which preset the edit belongs to.
                Assert.Equal("xh-classic", h.PresetId);
                Assert.Equal("xh-classic", h.ShownPresetId);
                Assert.Equal("Standard Cross", h.Work.Name);
                Assert.Contains("edited", h.Window.XhPresetBox.Text);
                Assert.Equal(44, h.Work.Size);

                // The way to keep that edit: a copy, because the built-in it came
                // from will not take it.
                CrosshairTab.Click(h.Window.XhDuplicateButton);

                CrosshairPreset kept = Assert.Single(h.Saved);
                Assert.Equal("Standard Cross copy", kept.Name);
                Assert.Equal(44, kept.Size);
                Assert.Equal(CrosshairShape.Cross, kept.ShapeKind);
                Assert.Equal(kept.Id, h.Settings.ActiveCrosshairId);

                // Rename and Delete were dead on the built-in and are live now.
                Assert.True(h.Window.XhRenameButton.IsEnabled);
                Assert.True(h.Window.XhDeleteButton.IsEnabled);
            },
            settings =>
            {
                settings.CustomCrosshairs.Clear();
                settings.ActiveCrosshairId = "xh-classic";
            });
    }

    [Fact]
    public void The_OLED_tooltip_fits_on_one_line_instead_of_being_cut_off()
    {
        // It used to be a sentence, and the tooltip was cut off mid-word: the style caps
        // a tooltip at 260 pixels and nothing in it wraps. Short enough to measure is a
        // rule now, so this fails if the sentence comes back.
        CrosshairTab.Run(h =>
        {
            string tip = h.Window.XhOledBox.ToolTip as string ?? string.Empty;

            Assert.False(string.IsNullOrWhiteSpace(tip));
            Assert.Contains("15 min", tip);

            var font = (FontFamily)h.Window.FindResource("Sans");
            var text = new FormattedText(
                tip,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11,
                Brushes.White,
                1.0);

            // 260 is the tooltip's own cap, less its nine pixels of padding either side.
            Assert.True(
                text.Width <= 240,
                $"the tooltip wants {text.Width:0}px of the 240 it has, so it would be clipped");
        });
    }

    /// <summary>The pixel at the middle of a drawn crosshair, which is where the centre dot is.</summary>
    private static (byte R, byte G, byte B) DotPixel(CrosshairPreset preset)
    {
        const int side = 80;
        var view = new CrosshairView { Preset = preset, Magnification = 1.0 };
        view.Measure(new Size(side, side));
        view.Arrange(new Rect(0, 0, side, side));

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);

        return ColourAt(bitmap, 0.5, 0.5)!.Value;
    }

    /// <summary>A pixel of an image, read by fraction across and down. Null outside it.</summary>
    private static (byte R, byte G, byte B)? ColourAt(BitmapSource image, double across, double down)
    {
        // Bgra32 and not Pbgra32: premultiplied pixels would report a translucent
        // crosshair's colour darkened by its own opacity, and the point here is the
        // colour it is painted, not how much of it comes through.
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        int x = Math.Clamp((int)Math.Round(across * (width - 1)), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(down * (height - 1)), 0, height - 1);
        int at = ((y * width) + x) * 4;

        // Nothing drawn there at all.
        return pixels[at + 3] == 0 ? null : (pixels[at + 2], pixels[at + 1], pixels[at]);
    }

    [Fact]
    public void The_pack_browser_builds_a_tile_per_picture_and_plays_the_animation()
    {
        CrosshairTab.Run(h =>
        {
            string still = h.WritePackPng("plain.png");
            string loop = h.WritePackGif("loop.gif", (0xFF, 0x3B, 0x3B), (0x20, 0xD0, 0x40));

            h.FillPack(still, loop);

            Assert.Equal(2, h.PackTiles);
            Assert.Equal(0, h.AnimatedTiles);

            // The GIF's frames are decoded off the UI thread, so wait for the swap.
            h.PumpUntil(() => h.AnimatedTiles == 1);

            Assert.True(h.PackClockRunning, "the browser needs its clock once a tile is playing");
            Assert.IsType<Image>(h.PackTileFor(loop).Content);
            Assert.NotNull(h.PackTileFor(still).Content);

            // Closing the browser stops the clock and lets the frames go.
            h.ClosePack();

            Assert.Equal(0, h.AnimatedTiles);
            Assert.False(h.PackClockRunning);
            Assert.Equal(0, h.PackTiles);
        });
    }

    [Fact]
    public void Switching_away_from_an_edited_built_in_asks_before_dropping_it()
    {
        CrosshairTab.Run(h =>
        {
            // A built-in cannot be written back to, so editing one leaves the box
            // saying that preset is edited, with the edit held on the tab.
            h.LayOut();
            SelectPreset(h, CrosshairPreset.BuiltIns[0].Id);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.ShownPresetId);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.PresetId);

            h.Window.XhSizeSlider.Value = 60;
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.PresetId);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.ShownPresetId);
            Assert.Contains("edited", h.Window.XhPresetBox.Text);
            Assert.Equal(60, h.Work.Size);

            // Picking another preset is where the tuning would have gone, so it is
            // asked about first, and the dropdown goes back to what is on the tab.
            SelectPreset(h, "xh-test");

            Assert.True(h.ModalOpen);
            Assert.Equal("KEEP YOUR TUNING?", h.ModalTitle);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.PresetId);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.ShownPresetId);
            Assert.Equal(60, h.Work.Size);

            // Saving puts it in a crosshair of its own, with the tuning in it, and
            // then does the switch that was asked for.
            h.ConfirmModal();

            Assert.False(h.ModalOpen);
            Assert.Equal("xh-test", h.PresetId);
            Assert.Equal(60, h.Saved.Last().Size);
            Assert.Equal("Standard Cross copy", h.Saved.Last().Name);
            Assert.Equal(2, h.Saved.Count);
            Assert.Equal("xh-test", h.Settings.ActiveCrosshairId);
        });
    }

    [Fact]
    public void Switching_away_from_an_edited_built_in_can_drop_the_edit_instead()
    {
        CrosshairTab.Run(h =>
        {
            SelectPreset(h, CrosshairPreset.BuiltIns[0].Id);
            h.Window.XhSizeSlider.Value = 60;
            Assert.Equal(60, h.Work.Size);

            SelectPreset(h, "xh-test");
            Assert.True(h.ModalOpen);

            // The third answer is really on the dialog, not just in the code behind
            // it: two ways out and one way forward were what made switching a dead end.
            Button discard = h.Read<Button>("_modalExtraButton");
            Assert.Equal(Visibility.Visible, discard.Visibility);
            Assert.Equal("DON'T SAVE", discard.Content?.ToString());

            // Switch, and let the edit go. No preset is created.
            h.DiscardModal();

            Assert.False(h.ModalOpen);
            Assert.Single(h.Saved);
            Assert.Equal("xh-test", h.PresetId);
            Assert.Equal("xh-test", h.ShownPresetId);
            Assert.DoesNotContain("edited", h.Window.XhPresetBox.Text);
            Assert.Equal(57, h.Work.Size);
            Assert.Equal("Test", h.Work.Name);
            Assert.Equal("xh-test", h.Settings.ActiveCrosshairId);

            // And the built-in itself was never touched.
            Assert.Equal(16, CrosshairPreset.BuiltIns[0].Size);
        });
    }

    [Fact]
    public void Cancelling_the_question_keeps_the_tuning_on_the_tab()
    {
        CrosshairTab.Run(h =>
        {
            SelectPreset(h, CrosshairPreset.BuiltIns[0].Id);
            h.Window.XhSizeSlider.Value = 60;

            SelectPreset(h, "xh-test");
            h.CancelModal();

            Assert.False(h.ModalOpen);

            // Cancel is the safe half: the switch did not happen, and the edited
            // crosshair is still the one on the tab, one Save away from being kept.
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.PresetId);
            Assert.Equal(CrosshairPreset.BuiltIns[0].Id, h.ShownPresetId);
            Assert.Contains("edited", h.Window.XhPresetBox.Text);
            Assert.Equal(60, h.Work.Size);
            Assert.Single(h.Saved);
        });
    }

    /// <summary>Pick a preset from the dropdown, which is what brings its handler in.</summary>
    private static void SelectPreset(CrosshairTab h, string id) => WindowHarness.PickPreset(h.Window.XhPresetBox, id);
}
