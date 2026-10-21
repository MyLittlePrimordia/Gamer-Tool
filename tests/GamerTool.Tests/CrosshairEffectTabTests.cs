using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The effect controls on the Crosshair tab, driven the way a mouse drives them: the
/// dropdown, the two sliders, Reset, and the OLED switch.
/// <para>
/// The drawing itself is checked without a window; what is checked here is the wiring -
/// that choosing an effect reaches the working crosshair and the file on disk, that the
/// controls that mean nothing are dimmed rather than hidden, and that a crosshair saved
/// before any of this existed still opens as still.
/// </para>
/// </summary>
[Collection("wpf")]
public class CrosshairEffectTabTests
{
    private static int IndexOf(CrosshairEffect effect)
    {
        return CrosshairAnimation.Menu.ToList().IndexOf(effect);
    }

    private static void Choose(CrosshairTab h, CrosshairEffect effect)
    {
        h.Window.XhEffectBox.SelectedIndex = IndexOf(effect);
    }

    [Fact]
    public void The_tab_opens_on_a_still_crosshair_with_nothing_to_tune()
    {
        CrosshairTab.Run(h =>
        {
            Assert.Equal(CrosshairEffect.None, h.Work.EffectKind);
            Assert.Equal(Visibility.Collapsed, h.Window.XhEffectTuningPanel.Visibility);
            Assert.Equal("Still. Nothing moves.", h.Window.XhEffectHint.Text);
        });
    }

    [Fact]
    public void Choosing_an_effect_reaches_the_crosshair_and_the_saved_copy()
    {
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Spin);

            Assert.Equal(CrosshairEffect.Spin, h.Work.EffectKind);
            Assert.Equal(Visibility.Visible, h.Window.XhEffectTuningPanel.Visibility);

            // The file on disk, or the effect is gone the next time the tab opens.
            CrosshairPreset stored = h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test");
            Assert.Equal("Spin", stored.Effect);
            Assert.Equal(CrosshairEffect.Spin, stored.EffectKind);
        });
    }

    [Fact]
    public void The_effect_sliders_start_on_the_numbers_the_crosshair_holds()
    {
        // The labels used to be written before the sliders were moved, so a fresh tab
        // read "1" and "0" next to tracks that were sitting at fifty.
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Pulse);

            // Pulse starts at 50 and 60 now, its own numbers rather than the pair that
            // used to be every effect's.
            Assert.Equal("50", h.Window.XhEffectSpeedValue.Text);
            Assert.Equal("60", h.Window.XhEffectAmountValue.Text);
            Assert.Equal(50, (int)h.Window.XhEffectSpeedSlider.Value);
            Assert.Equal(60, (int)h.Window.XhEffectAmountSlider.Value);
        });
    }

    [Fact]
    public void The_amount_slider_is_dimmed_for_the_one_effect_that_has_no_amount()
    {
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Spin);
            Assert.False(h.Window.XhEffectAmountSlider.IsEnabled, "spin has no amplitude");
            Assert.True(h.Window.XhEffectSpeedSlider.IsEnabled);
            Assert.InRange(h.Window.XhEffectAmountPanel.Opacity, 0.39, 0.41);

            Choose(h, CrosshairEffect.Sparks);
            Assert.True(h.Window.XhEffectAmountSlider.IsEnabled);
            Assert.InRange(h.Window.XhEffectAmountPanel.Opacity, 0.99, 1.0);
        });
    }

    [Fact]
    public void The_sliders_reach_the_crosshair_and_the_saved_copy()
    {
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Sparks);

            h.Window.XhEffectSpeedSlider.Value = 88;
            h.Window.XhEffectAmountSlider.Value = 12;

            Assert.Equal(88, h.Work.EffectSpeed);
            Assert.Equal(12, h.Work.EffectAmount);

            CrosshairPreset stored = h.Settings.CustomCrosshairs.Single(p => p.Id == "xh-test");
            Assert.Equal(88, stored.EffectSpeed);
            Assert.Equal(12, stored.EffectAmount);

            Assert.Equal("88", h.Window.XhEffectSpeedValue.Text);
            Assert.Equal("12", h.Window.XhEffectAmountValue.Text);
        });
    }

    [Fact]
    public void Reset_puts_the_effect_back_to_still()
    {
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Ripple);
            Assert.True(h.Work.Animated);

            CrosshairTab.Click(h.Window.XhResetButton);
            h.ConfirmModal();

            Assert.Equal(CrosshairEffect.None, h.Work.EffectKind);
            Assert.Equal(string.Empty, h.Work.Effect);
            Assert.Equal(Visibility.Collapsed, h.Window.XhEffectTuningPanel.Visibility);
        });
    }

    [Fact]
    public void A_preset_that_already_carries_an_effect_opens_on_it()
    {
        CrosshairTab.Run(
            h =>
            {
                Assert.Equal(CrosshairEffect.Sheen, h.Work.EffectKind);
                Assert.Equal(IndexOf(CrosshairEffect.Sheen), h.Window.XhEffectBox.SelectedIndex);
                Assert.Equal(Visibility.Visible, h.Window.XhEffectTuningPanel.Visibility);
                Assert.Equal(70, (int)h.Window.XhEffectSpeedSlider.Value);
                Assert.Equal(30, (int)h.Window.XhEffectAmountSlider.Value);
            },
            settings =>
            {
                var sheen = new CrosshairPreset
                {
                    Id = "xh-sheeny",
                    Name = "Sheeny",
                    ShapeKind = CrosshairShape.Cross,
                    EffectKind = CrosshairEffect.Sheen,
                    EffectSpeed = 70,
                    EffectAmount = 30
                };

                settings.CustomCrosshairs.Add(sheen);
                settings.ActiveCrosshairId = sheen.Id;
            });
    }

    [Fact]
    public void The_dropdown_lists_every_effect_in_menu_order()
    {
        CrosshairTab.Run(h =>
        {
            Assert.Equal(CrosshairAnimation.Menu.Count, h.Window.XhEffectBox.Items.Count);

            h.Window.XhEffectBox.SelectedIndex = 0;
            Assert.Equal(CrosshairEffect.None, h.Work.EffectKind);
        });
    }

    [Fact]
    public void The_dropdown_offers_the_quiet_effects_and_not_the_busy_ones()
    {
        // The glitch, the orbit, the chase and the twinkle all read as something moving
        // beside the aim point rather than as the crosshair itself, so they were replaced
        // by effects that leave the sight picture where it is.
        CrosshairTab.Run(h =>
        {
            var labels = h.Window.XhEffectBox.Items.OfType<object>().Select(i => i.ToString()).ToList();

            Assert.Contains("Breathe", labels);
            Assert.Contains("Sheen", labels);
            Assert.DoesNotContain("Twinkle", labels);
            Assert.DoesNotContain("Orbit", labels);
            Assert.DoesNotContain("Chase", labels);
            Assert.DoesNotContain("Glitch", labels);

            Choose(h, CrosshairEffect.Sheen);
            Assert.Equal("A soft highlight passes across it now and then.", h.Window.XhEffectHint.Text);
        });
    }

    [Fact]
    public void The_whole_tab_fits_with_every_effect_selected()
    {
        // The effect controls cost the page its last forty pixels, and the switches, the
        // guard and half the sliders were drawn below the footer where nobody could
        // reach them. This is the guard against that happening again: with any effect
        // chosen, the column has to want no more room than the panel gives it.
        CrosshairTab.Run(h =>
        {
            FrameworkElement page = h.LayOut(1200, 790);
            StackPanel column = (StackPanel)h.Window.XhTilePanel.Parent;
            double room = ((FrameworkElement)column.Parent).ActualHeight;

            Assert.True(room > 500, "the harness did not lay the page out");

            foreach (CrosshairEffect effect in CrosshairAnimation.Menu)
            {
                Choose(h, effect);
                page.UpdateLayout();

                column.Measure(new Size(column.ActualWidth, double.PositiveInfinity));
                Assert.True(
                    column.DesiredSize.Height <= room,
                    $"{effect} wants {column.DesiredSize.Height:0}px of a {room:0}px panel");
            }
        });
    }

    [Fact]
    public void Choosing_an_effect_leaves_the_glow_switch_to_the_user()
    {
        // No effect needs the glow any more: each draws marks of its own, so the switch
        // stays the user's and nothing is turned on behind their back.
        CrosshairTab.Run(h =>
        {
            h.Window.XhGlowBox.IsChecked = false;
            Assert.False(h.Work.Glow);

            Choose(h, CrosshairEffect.Sparks);

            Assert.True(h.Window.XhGlowBox.IsEnabled, "an effect must not take the switch over");
            Assert.False(h.Work.Glow, "and must not turn it on");
        });
    }

    [Fact]
    public void Choosing_an_effect_starts_it_at_its_own_rate_and_reach()
    {
        // Fifty on both sliders was where every effect began, which for a breath was too
        // slight to see and for sparks too slow to read. Each effect now arrives at the
        // numbers it is meant to be seen at - and the user's own numbers take over from
        // the moment a slider moves.
        CrosshairTab.Run(h =>
        {
            Choose(h, CrosshairEffect.Ripple);

            (int speed, int amount) = CrosshairAnimation.Defaults(CrosshairEffect.Ripple);
            Assert.Equal(speed, h.Work.EffectSpeed);
            Assert.Equal(amount, h.Work.EffectAmount);
            Assert.Equal(speed, (int)h.Window.XhEffectSpeedSlider.Value);
            Assert.Equal(amount, (int)h.Window.XhEffectAmountSlider.Value);
            Assert.Equal(speed.ToString(), h.Window.XhEffectSpeedValue.Text);

            h.Window.XhEffectSpeedSlider.Value = 91;
            Assert.Equal(91, h.Work.EffectSpeed);

            // A different effect is a different animation, so it arrives at its own
            // numbers rather than inheriting the last one's.
            Choose(h, CrosshairEffect.Sparks);
            Assert.Equal(CrosshairAnimation.Defaults(CrosshairEffect.Sparks).Speed, h.Work.EffectSpeed);
            Assert.NotEqual(91, h.Work.EffectSpeed);
        });
    }

    [Fact]
    public void Every_row_of_the_dropdown_carries_a_moving_thumbnail_of_its_effect()
    {
        // The row template is instantiated the way the popup would, with each row's own
        // item as its data, so this is the thumbnail a user actually sees rather than a
        // description of one.
        CrosshairTab.Run(h =>
        {
            DataTemplate template = h.Window.XhEffectBox.ItemTemplate;

            Assert.Equal(CrosshairAnimation.Menu.Count, h.Window.XhEffectBox.Items.Count);

            foreach (object item in h.Window.XhEffectBox.Items)
            {
                string label = item.ToString() ?? string.Empty;
                CrosshairEffect effect = CrosshairAnimation.Menu
                    .First(e => string.Equals(CrosshairAnimation.Label(e), label, StringComparison.Ordinal));

                var row = (FrameworkElement)template.LoadContent();
                row.DataContext = item;
                row.Measure(new Size(240, 30));
                row.Arrange(new Rect(0, 0, 240, 30));
                row.UpdateLayout();

                CrosshairView? thumbnail = Descendants(row).OfType<CrosshairView>().FirstOrDefault();
                Assert.True(thumbnail is not null, $"the {label} row has no thumbnail");
                Assert.Equal(effect, thumbnail.Preset?.EffectKind);

                TextBlock? caption = Descendants(row).OfType<TextBlock>().FirstOrDefault();
                Assert.Equal(label, caption?.Text);

                // Small enough to sit in the swatch it is drawn in.
                Assert.InRange(thumbnail.Preset!.Size, CrosshairPreset.SizeMin, 14);
            }
        });
    }

    [Fact]
    public void The_effect_list_shows_every_row_without_a_scrollbar_or_a_half_row()
    {
        // The rows carry a twenty-four pixel thumbnail, so eight of them need more room
        // than the text-only dropdown the shared style was written for: the list arrived
        // with a scrollbar and a sliver of a row at the bottom that read as an empty
        // slot. This is the sum that says they all fit, read off the control and the
        // styles it is actually using rather than from a number in the test.
        CrosshairTab.Run(h =>
        {
            ComboBox box = h.Window.XhEffectBox;
            var itemStyle = (Style)box.FindResource("ModernComboItem");

            var padding = (Thickness)itemStyle.Setters
                .OfType<Setter>()
                .First(setter => setter.Property == Control.PaddingProperty)
                .Value;

            double row = 0;
            foreach (object item in box.Items)
            {
                var content = (FrameworkElement)box.ItemTemplate.LoadContent();
                content.DataContext = item;
                content.Measure(new Size(300, double.PositiveInfinity));
                row = Math.Max(row, content.DesiredSize.Height);
            }

            // Four pixels of padding either side of the popup's own border.
            double needed = (box.Items.Count * (row + padding.Top + padding.Bottom)) + 8;

            Assert.Equal(CrosshairAnimation.Menu.Count, box.Items.Count);
            Assert.True(
                needed <= box.MaxDropDownHeight,
                $"the effects need {needed:0}px of dropdown and are given {box.MaxDropDownHeight:0}px, "
                + "so the last one is cut off and the list scrolls");
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (DependencyObject below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    [Fact]
    public void The_OLED_guard_is_off_by_default_and_is_remembered_when_switched_on()
    {
        CrosshairTab.Run(h =>
        {
            Assert.False(h.Settings.OledGuard);
            Assert.False(h.Window.XhOledBox.IsChecked == true);

            h.Window.XhOledBox.IsChecked = true;

            Assert.True(h.Settings.OledGuard);

            h.Window.XhOledBox.IsChecked = false;
            Assert.False(h.Settings.OledGuard);
        });
    }

    [Fact]
    public void The_OLED_guard_comes_back_on_when_the_tab_is_reopened()
    {
        CrosshairTab.Run(
            h =>
            {
                Assert.True(h.Window.XhOledBox.IsChecked == true);
                Assert.Equal("Hidden", h.Window.XhStatusText.Text);
            },
            settings => settings.OledGuard = true);
    }
}
