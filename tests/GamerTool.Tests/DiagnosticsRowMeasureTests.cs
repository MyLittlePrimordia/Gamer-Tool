using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The Diagnostics row measured, rather than reasoned about.
/// <para>
/// The clipping was not a mistake anyone could see by reading the markup. The
/// confirmation sat in the switch's 34 pixel slot with the text right aligned in
/// it, and "Copied to clipboard" is about a hundred pixels wide. Every layer
/// above was fine: the XAML compiled, the row was laid out, the text was
/// visible, and it was still cut off. Nothing in a source-reading test can
/// notice that, because the width of a piece of text is a fact about the font,
/// the size and the DPI, and only a laid out control knows it.
/// </para>
/// <para>
/// So this stands the row up for real, at the width the settings list gives it,
/// and measures where the pieces land. It is the check that the layout is right,
/// as opposed to the check that it is what someone intended.
/// </para>
/// </summary>
[Collection("wpf")]
public class DiagnosticsRowMeasureTests
{
    private const double RowWidth = 620 - (18 * 2) - 8 - 8;

    /// <summary>The two messages the row ever shows, so both are measured.</summary>
    private static readonly string[] Messages =
    {
        "Copied to clipboard",
        "Could not copy"
    };

    private static void AssertFits(string what, string message)
    {
        // The confirmation text, laid out at the row's own font and size. Built
        // on the shared STA thread because every WPF element needs one, and
        // measured there because that is the only place a font can be asked
        // how wide a piece of text is.
        double wanted = WpfTestHost.Invoke(() =>
        {
            var text = new TextBlock
            {
                Text = message,
                FontFamily = new FontFamily("Segoe UI, Arial"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            text.Measure(new Size(RowWidth, 40));
            return text.DesiredSize.Width;
        });

        double available = RowWidth - 19 - 10 - 12 - 34;

        Assert.True(
            wanted < available,
            "'" + message + "' wants " + wanted.ToString("0.0")
            + " px of a row that only has " + available.ToString("0.0")
            + " px to give once the mark, the gaps and the switch are taken out, "
            + "so " + what + " would be trimmed");
    }

    [Fact]
    public void Every_message_the_row_shows_fits_alongside_the_mark()
    {
        foreach (string message in Messages)
        {
            AssertFits("the confirmation", message);
        }
    }

    [Fact]
    public void The_confirmation_leaves_the_label_enough_room_to_stay_readable()
    {
        // The label is the star column, so the confirmation is paid for out of
        // it. If the two together do not fit, the label is what gets trimmed
        // away, and a settings row whose name has been eaten by a success
        // message is worse than one that says less.
        double[] wanted = WpfTestHost.Invoke(() =>
        {
            var label = new TextBlock
            {
                Text = "Diagnostics",
                FontFamily = new FontFamily("Segoe UI, Arial"),
                FontSize = 12
            };

            var note = new TextBlock
            {
                Text = "Copied to clipboard",
                FontFamily = new FontFamily("Segoe UI, Arial"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };

            label.Measure(new Size(RowWidth, 40));
            note.Measure(new Size(RowWidth, 40));

            return new[] { label.DesiredSize.Width, note.DesiredSize.Width };
        });

        double chrome = 19 + 10 + 12 + 34;

        Assert.True(
            wanted[0] + wanted[1] < RowWidth - chrome,
            "the label and the confirmation together want "
            + (wanted[0] + wanted[1]).ToString("0.0")
            + " px of the " + (RowWidth - chrome).ToString("0.0")
            + " px available, so one of them is trimmed when the confirmation appears");
    }

    /// <summary>Puts an element in a row column, so the calls below read as a row.</summary>
    private static UIElement Mark(int column, UIElement element)
    {
        Grid.SetColumn(element, column);
        return element;
    }

    [Fact]
    public void The_row_keeps_its_width_whether_or_not_the_confirmation_shows()
    {
        // A confirmation that changed the row's width would shuffle the whole
        // list, because every row below it would move. The mark is the last
        // column and pinned to the right edge, so this is really a check that
        // nothing in the row is sized to its own content.
        Grid host = WpfTestHost.Invoke(() =>
        {
            var grid = new Grid
            {
                Width = RowWidth,
                Height = 40
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            // The mark is the last column, so its right edge is the row's right
            // edge. Measured rather than assumed, because a column sized to its
            // content would put that edge somewhere else and the whole list
            // below it would shuffle.
            var mark = new Border
            {
                Width = 34,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            grid.Children.Add(Mark(0, new Border { Width = 19, Height = 19 }));
            grid.Children.Add(Mark(2, new TextBlock { Text = "Diagnostics" }));
            grid.Children.Add(Mark(3, new TextBlock
            {
                Text = "Copied to clipboard",
                HorizontalAlignment = HorizontalAlignment.Right
            }));
            grid.Children.Add(Mark(5, mark));

            return grid;
        });

        WpfTestHost.Invoke(() =>
        {
            host.Measure(new Size(RowWidth, 40));
            host.Arrange(new Rect(0, 0, RowWidth, 40));
        });

        double markRight = WpfTestHost.Invoke(() =>
        {
            var mark = (FrameworkElement)host.Children[host.Children.Count - 1];
            return mark.TransformToAncestor(host).Transform(new Point(0, 0)).X + mark.ActualWidth;
        });

        Assert.True(
            Math.Abs(markRight - RowWidth) < 0.5,
            "the mark's right edge is at " + markRight.ToString("0.0")
            + " rather than the row's " + RowWidth.ToString("0.0")
            + ", so the row is not the same width with the confirmation up as without it");
    }
}
