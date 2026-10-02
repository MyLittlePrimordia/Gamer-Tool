using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The settings rows, as a screen reader and a keyboard meet them.
/// <para>
/// Nine switches in a row, every one of which announced as an unnamed check box,
/// and every one of which put two tab stops into the list. Both are template
/// problems rather than content problems: the label is rendered as a TextBlock
/// inside the ControlTemplate, which UI Automation does not read as a name, and
/// the row nests a second focusable ToggleButton bound to the same IsChecked.
/// </para>
/// <para>
/// These are assertions about the template actually producing what it is supposed
/// to produce, applied to a real measured control. A resource lint cannot see
/// either: both look like ordinary markup.
/// </para>
/// </summary>
[Collection("wpf")]
public class SettingsSwitchTests
{
    private static T OnSta<T>(Func<T> body) => WpfTestHost.Invoke(body);

    private static void OnSta(Action body) => WpfTestHost.Invoke(body);

    /// <summary>
    /// One settings row, built the way MainWindow.xaml builds all nine.
    /// <para>
    /// The theme is loaded and merged rather than assumed, because a Style cannot
    /// resolve the brushes and geometries its template refers to against an
    /// Application that has none, and the resulting failure would be measured here
    /// as a defect in the row. Loading it the same way
    /// XamlTemplateLoadTests does - compiled BAML, named explicitly - keeps the two
    /// tests reading the same markup.
    /// </para>
    /// </summary>
    private static OptionToggle Row(string label) => OnSta(() =>
        BuildRow(label));

    /// <summary>
    /// The body of <see cref="Row"/>, already on the STA thread.
    /// <para>
    /// Split out because several tests need to build a row and then read it on the
    /// same thread. A WPF control carries dispatcher affinity, so building one here
    /// and touching it from the test's own thread throws - which is a property of
    /// the harness, not of the row, and would otherwise be reported as a defect in
    /// the thing under test.
    /// </para>
    /// </summary>
    private static OptionToggle BuildRow(string label)
    {
        object loaded = Application.LoadComponent(
            new Uri("/GamerTool;component/UI/Theme.xaml", UriKind.Relative));

        if (loaded is not ResourceDictionary theme)
        {
            throw new InvalidOperationException(
                "UI/Theme.xaml did not load as a ResourceDictionary but as "
                + loaded?.GetType().FullName);
        }

        if (!Application.Current.Resources.MergedDictionaries.Contains(theme))
        {
            Application.Current.Resources.MergedDictionaries.Add(theme);
        }

        if (theme["OptionRow"] is not Style optionRow)
        {
            throw new InvalidOperationException(
                "OptionRow is not in the theme, so these tests would prove nothing");
        }

        var toggle = new OptionToggle
        {
            Label = label,
            ToolTip = label + " help",
            Glyph = "moon",
            Style = optionRow,
        };

        // Measured for real, because two of the assertions are about the visual
        // tree and one is about the TwoWay binding, none of which exist until the
        // template has been instantiated and laid out.
        toggle.Measure(new Size(600, 40));
        toggle.Arrange(new Rect(0, 0, 600, 40));
        toggle.UpdateLayout();
        return toggle;
    }

    /// <summary>
    /// The row's accessible name has to be its label.
    /// <para>
    /// A tooltip is not one: UIA surfaces it as HelpText, which a screen reader
    /// announces after the name rather than instead of it, and only once the user
    /// asks. Nine consecutive unnamed check boxes is what this was producing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_settings_row_is_named_after_its_label()
    {
        string? name = OnSta(() =>
            System.Windows.Automation.AutomationProperties.GetName(Row("Start with Windows")));

        Assert.Equal("Start with Windows", name);
    }

    /// <summary>
    /// Two different rows must not share a name, which they would if the name came
    /// from the style rather than from the row's own label.
    /// </summary>
    [Fact]
    public void Two_rows_with_different_labels_have_different_names()
    {
        string? first = OnSta(() =>
            System.Windows.Automation.AutomationProperties.GetName(Row("Gamma lock")));

        string? second = OnSta(() =>
            System.Windows.Automation.AutomationProperties.GetName(Row("Show the OSD")));

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// A row is one tab stop, not two.
    /// <para>
    /// The row is a CheckBox with a ToggleButton nested inside it and both bound to
    /// the same IsChecked, so tabbing the settings page walked eighteen stops to
    /// reach nine settings - and each stop toggled the same thing. IsTabStop on the
    /// inner switch is the whole fix; Focusable has to stay true, because a
    /// ToggleButton that is not focusable does not raise Click, which made the
    /// switch graphic itself inert under the mouse.
    /// </para>
    /// </summary>
    [Fact]
    public void A_settings_row_contains_exactly_one_tab_stop()
    {
        int stops = OnSta(() =>
        {
            int found = 0;
            CountTabStops(BuildRow("Hardware brightness"), ref found);
            return found;
        });

        Assert.Equal(1, stops);
    }

    /// <summary>
    /// The switch graphic inside the row still has to be clickable, which is what
    /// Focusable="False" broke. Asserted structurally rather than by synthesising a
    /// click, because the property that decides it is the one under test: a
    /// ToggleButton with Focusable false skips its pressed-and-clicked transition.
    /// </summary>
    [Fact]
    public void The_switch_inside_a_row_stays_focusable_so_a_click_on_it_still_works()
    {
        (bool Focusable, bool IsTabStop) inner = OnSta(() =>
        {
            SwitchToggle? found = FindSwitch(BuildRow("Distortion protection"));
            Assert.NotNull(found);
            return (found!.Focusable, found.IsTabStop);
        });

        Assert.True(
            inner.Focusable,
            "the nested switch must stay focusable or it stops responding to the mouse");
        Assert.False(
            inner.IsTabStop,
            "the nested switch is not a tab stop; the row itself is");
    }

    /// <summary>
    /// Toggling the inner switch still drives the row, because the TwoWay binding
    /// is what makes the graphic and the row one control rather than two.
    /// </summary>
    [Fact]
    public void The_switch_inside_a_row_still_toggles_the_row()
    {
        bool agrees = OnSta(() =>
        {
            OptionToggle toggle = BuildRow("Start in the tray");

            SwitchToggle? inner = FindSwitch(toggle);
            Assert.NotNull(inner);

            bool before = toggle.IsChecked == true;
            inner!.IsChecked = !(inner.IsChecked == true);

            return toggle.IsChecked == true == !before;
        });

        Assert.True(agrees, "the switch inside the row no longer drives the row's own IsChecked");
    }

    private static void CountTabStops(DependencyObject root, ref int stops)
    {
        if (root is not Visual visual)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(visual, i);

            if (child is Control { IsEnabled: true } control && control.IsTabStop)
            {
                stops++;
            }

            CountTabStops(child, ref stops);
        }
    }

    private static SwitchToggle? FindSwitch(DependencyObject root)
    {
        if (root is not Visual visual)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(visual, i);

            if (child is SwitchToggle found)
            {
                return found;
            }

            if (FindSwitch(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}