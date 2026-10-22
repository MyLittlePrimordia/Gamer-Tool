using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Backup, Restore and Reset all share one row with the label that names them, and
/// the label used to be the thing that gave way: the buttons took their width first,
/// and what was left of the label was cut off mid word. There is no ellipsis and no
/// tooltip on it, so "Backup a" was all a user ever saw of "Backup and restore".
/// <para>
/// The row's buttons are sized by their content and the label column is a star, so
/// the failure is arithmetic rather than a wrong setting: one button too many and the
/// label loses. This measures the page the way the window lays it out and compares
/// the label with the width its own text needs.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class SettingsBackupRowTests
{
    /// <summary>The page's inner width: the window's 964, less its 14 either side.</summary>
    private const double PageWidth = 964 - (14 * 2);

    [Fact]
    public void The_backup_row_shows_its_label_whole_and_three_buttons()
    {
        WindowHarness.Run(MainWindowTabs.None, h =>
        {
            FrameworkElement page = LayOut(h);

            TextBlock label = Descendants(page)
                .OfType<TextBlock>()
                .First(t => string.Equals(t.Text, "Backup and restore", StringComparison.Ordinal));

            // The width the words need with nothing in their way, at the size and face
            // the label is actually drawn in.
            var probe = new TextBlock
            {
                Text = label.Text,
                FontFamily = label.FontFamily,
                FontSize = label.FontSize,
                FontWeight = label.FontWeight,
                FontStyle = label.FontStyle,
                FontStretch = label.FontStretch,
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.True(
                label.ActualWidth + 0.5 >= probe.DesiredSize.Width,
                $"the label is laid out {label.ActualWidth:0}px wide and its text needs "
                + $"{probe.DesiredSize.Width:0}px, so it is clipped. The row has room for "
                + "Backup, Restore and Reset all and no more.");
        });
    }

    [Fact]
    public void The_row_is_backup_restore_and_reset_all_in_that_order()
    {
        WindowHarness.Run(MainWindowTabs.None, h =>
        {
            FrameworkElement page = LayOut(h);

            Button reset = h.Window.FactoryResetButton;
            var row = Assert.IsType<Grid>(VisualTreeHelper.GetParent(reset));
            List<Button> buttons = row.Children.OfType<Button>().OrderBy(Grid.GetColumn).ToList();

            // The order the words are in on screen: the action that keeps a copy, the
            // one that reads one back, and the one that starts over at the end.
            Assert.Equal(3, buttons.Count);
            Assert.Equal("Reset all", ButtonText(reset));

            // Each has a column of its own, left to right, with a gap between them.
            Assert.Equal(new[] { 0, 2, 4 }, buttons.Select(Grid.GetColumn).ToArray());

            // And every one of them is drawn, not squeezed to nothing beside its
            // neighbour: a button that cannot be seen cannot be pressed.
            foreach (Button button in buttons)
            {
                Assert.True(button.ActualWidth >= 80, ButtonText(button) + " is only " + button.ActualWidth.ToString("0") + "px wide");
                Assert.Equal(Visibility.Visible, button.Visibility);
            }

            Assert.Same(page, TopOf(reset));
        });
    }

    private static string ButtonText(Button button) =>
        button.Content is string text
            ? text
            : string.Join(" ", Descendants(button).OfType<TextBlock>().Select(t => t.Text));

    /// <summary>Measure and arrange the settings page, which a window that was never shown does not do.</summary>
    private static FrameworkElement LayOut(WindowHarness h)
    {
        FrameworkElement page = TopOf(h.Window.FactoryResetButton);
        page.Measure(new Size(PageWidth, 790));
        page.Arrange(new Rect(0, 0, PageWidth, 790));
        page.UpdateLayout();
        return page;
    }

    /// <summary>The topmost visual ancestor of an element on the settings page.</summary>
    private static FrameworkElement TopOf(DependencyObject at)
    {
        while (VisualTreeHelper.GetParent(at) is { } up)
        {
            at = up;
        }

        return (FrameworkElement)at;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (DependencyObject deep in Descendants(child))
            {
                yield return deep;
            }
        }
    }
}
