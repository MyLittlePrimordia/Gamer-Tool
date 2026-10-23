using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GamerTool.Models;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The slot cards as the layout engine actually receives them.
/// <para>
/// <see cref="SlotRowFitsTests"/> reads the card's source to check the arithmetic of
/// its columns, which is the only way to notice that the row is forty pixels too
/// wide. What it cannot notice is a control placed in a column that does not exist:
/// the source is perfectly consistent about it, and the arithmetic agrees, because
/// the width it never had is simply not in the sum. This builds the real board and
/// reads the real grid instead, so the two halves of the same mistake are covered
/// where they differ - the widths on paper, and where the controls land.
/// </para>
/// <para>
/// The second test goes one step further and runs a real layout pass over the
/// board, then compares the widths the layout engine resolved with the widths
/// <c>SlotColumns</c> declares. On paper a column and the control in it cannot
/// disagree; what can disagree is a control that sets its own Width large enough
/// to spill out of its column, which is invisible until somebody looks at the
/// built card.
/// </para>
/// <para>
/// The third does the same for the other axis: the card is measured at the size
/// the window gives it and every control is checked against the height of its row
/// and the bottom edge of the card, because a control that outgrows the row makes
/// every slot taller and the ones below it scroll out of view.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class SlotCardGridTests
{
    /// <summary>The width of the spacer columns between the card's controls.</summary>
    private const double Gap = 6.0;

    /// <summary>
    /// How far a laid-out width may be from the number it should be. Layout rounds
    /// to whole pixels, and the star column takes whatever is left over after all
    /// the rounding, so an exact comparison would fail on a correct card.
    /// </summary>
    private const double Tolerance = 0.5;

    /// <summary>
    /// The slack a height comparison allows: one device independent pixel. Layout
    /// rounds to whole device pixels, so on a scaled display a 30px row can be laid
    /// out at 30.7 and back again; the difference is under a pixel and is not what
    /// these tests are looking for.
    /// </summary>
    private const double Pixel = 1.0;

    /// <summary>
    /// The tallest thing a slot row is built for, which is the keycap. Everything
    /// else on the card is 28 or less - the delete button included, since it is
    /// pinned to the keycap too. Pinned because the board's density is decided by
    /// it: eleven slots fit the page at this height, and a control two pixels
    /// taller takes one of them away without showing anything for it.
    /// </summary>
    private const double RowHeight = 28.0;

    [Fact]
    public void Every_control_on_a_card_lands_in_a_column_that_exists()
    {
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                Grid header = Assert.IsType<Grid>(h.Window.SlotList.Children.OfType<Grid>().First());
                Border[] cards = h.Window.SlotList.Children.OfType<Border>().ToArray();

                Assert.Equal(2, cards.Length);

                foreach (Border card in cards)
                {
                    Grid row = Assert.IsType<Grid>(card.Child);

                    // Every card is built from the same array as the caption strip
                    // above it, so a card with a different number of columns is a card
                    // whose captions no longer sit over its controls.
                    Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);

                    int[] columns = row.Children.Cast<UIElement>().Select(Grid.GetColumn).ToArray();

                    foreach (int column in columns)
                    {
                        Assert.True(
                            column >= 0 && column < row.ColumnDefinitions.Count,
                            "a control is in column " + column + " of " + row.ColumnDefinitions.Count
                            + ". The grid draws it past the last column rather than anywhere it was "
                            + "measured for, and nothing in the build or in the width arithmetic says "
                            + "so - the column it needed was never added to SlotColumns.");
                    }

                    // The odd columns are the gaps between the controls, so a control
                    // sitting in one is a control half a gap wide.
                    foreach (int column in columns)
                    {
                        Assert.True(
                            row.ColumnDefinitions[column].Width.Value != Gap,
                            "a control is in column " + column + ", which is one of the "
                            + Gap.ToString("0") + "px gaps between the controls rather than a column "
                            + "of its own.");
                    }

                    // And one control per column: two in the same cell draw over each
                    // other, and the one underneath cannot be clicked.
                    Assert.Equal(columns.Length, columns.Distinct().Count());
                }
            },
            setup =>
            {
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-alpha", Name = "Alpha" });
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-bravo", Name = "Bravo" });
            });
    }

    [Fact]
    public void Every_control_is_as_wide_as_the_column_it_sits_in()
    {
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                // Nothing in the window has ever been laid out - it is built and
                // never shown - so every ActualWidth is still zero until this pass
                // runs. One Measure and one Arrange over the board is enough to
                // resolve every column and control width, at the width the window's
                // own size leaves the strip: the declared window width less the
                // strip's padding.
                LayOutTheBoard(h);

                GridLength[] declared = DeclaredColumns();
                Assert.Equal(1, declared.Count(width => width.IsStar));

                Border[] cards = h.Window.SlotList.Children.OfType<Border>().ToArray();
                Assert.Equal(2, cards.Length);

                foreach (Border card in cards)
                {
                    Grid row = Assert.IsType<Grid>(card.Child);

                    Assert.True(
                        row.ActualWidth > 0.0,
                        "the card was laid out at zero width, so the pass did not reach it and "
                        + "nothing below is being compared to anything");

                    Assert.Equal(declared.Length, row.ColumnDefinitions.Count);

                    double fixedWidth = declared.Where(width => !width.IsStar).Sum(width => width.Value);
                    double starWidth = row.ActualWidth - fixedWidth;

                    Assert.True(
                        starWidth > 0.0,
                        "the fixed columns come to " + fixedWidth.ToString("0")
                        + " px and the card is " + row.ActualWidth.ToString("0")
                        + " px wide, so the game picker has no room left to take and the row "
                        + "overflows the card");

                    // What the declaration says each column should be, after the one
                    // star column has taken the leftover. Every column has to be that
                    // width for the row to be the row that was measured.
                    for (int column = 0; column < declared.Length; column++)
                    {
                        double expected = declared[column].IsStar ? starWidth : declared[column].Value;
                        double actual = row.ColumnDefinitions[column].ActualWidth;

                        Assert.True(
                            Math.Abs(actual - expected) <= Tolerance,
                            "column " + column + " was laid out " + actual.ToString("0.0")
                            + " px wide and SlotColumns declares " + expected.ToString("0.0")
                            + " px: the card is not the row the header above it was built for");
                    }

                    foreach (UIElement child in row.Children)
                    {
                        FrameworkElement control = Assert.IsAssignableFrom<FrameworkElement>(child);
                        int column = Grid.GetColumn(child);
                        string described = control.GetType().Name + " in column " + column;
                        double expected = declared[column].IsStar ? starWidth : declared[column].Value;

                        if (control.HorizontalAlignment == HorizontalAlignment.Stretch)
                        {
                            Assert.True(
                                Math.Abs(control.ActualWidth - expected) <= Tolerance,
                                described + " is " + control.ActualWidth.ToString("0.0")
                                + " px wide in a " + expected.ToString("0.0")
                                + " px column, so it is not filling the width it was given");

                            continue;
                        }

                        // A control that sizes itself - the name plate, the two icon
                        // buttons - only has to stay inside its column. Spilling out
                        // of it is how a row gets wider than the columns it was
                        // measured against, without any column changing.
                        Assert.True(
                            control.ActualWidth > 0.0,
                            described + " was laid out at zero width");

                        Assert.True(
                            control.ActualWidth <= expected + Tolerance,
                            described + " is " + control.ActualWidth.ToString("0.0")
                            + " px wide in a " + expected.ToString("0.0")
                            + " px column, so it reaches past the column and over the control "
                            + "beside it");
                    }
                }
            },
            setup =>
            {
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-alpha", Name = "Alpha" });
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-bravo", Name = "Bravo" });
            });
    }

    [Fact]
    public void Every_control_stays_inside_the_card_it_sits_in()
    {
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                LayOutTheBoard(h);

                Border[] cards = h.Window.SlotList.Children.OfType<Border>().ToArray();
                Assert.Equal(2, cards.Length);

                foreach (Border card in cards)
                {
                    Grid row = Assert.IsType<Grid>(card.Child);

                    Assert.True(
                        row.ActualHeight > 0.0,
                        "the card was laid out at zero height, so the pass did not reach it and "
                        + "nothing below is being compared to anything");

                    // The card grows to hold its row, and the row is what decides how
                    // tall that is. Pinned as an equality rather than a minimum: a card
                    // with a height of its own is a card that either clips its content
                    // or leaves a blank strip under every slot.
                    double frame = card.Padding.Top + card.Padding.Bottom
                        + card.BorderThickness.Top + card.BorderThickness.Bottom;

                    Assert.True(
                        Math.Abs(card.ActualHeight - (row.ActualHeight + frame)) <= Pixel,
                        "the card is " + card.ActualHeight.ToString("0.0")
                        + " px tall for a " + row.ActualHeight.ToString("0.0")
                        + " px row and " + frame.ToString("0.0")
                        + " px of padding, so the two are not the height of one another");

                    // And the row is the height the board is built for. Without this a
                    // taller control simply makes every card taller, which is not a
                    // clipped control but is still a slot list that holds fewer slots.
                    Assert.True(
                        row.ActualHeight <= RowHeight + Pixel,
                        "the row is " + row.ActualHeight.ToString("0.0")
                        + " px tall where the board is built for " + RowHeight.ToString("0")
                        + ". A control taller than the row is built for sets the height "
                        + "of every card, so the slots below it move down the page instead "
                        + "of the control being noticed");

                    foreach (UIElement child in row.Children)
                    {
                        FrameworkElement control = Assert.IsAssignableFrom<FrameworkElement>(child);
                        string described = control.GetType().Name + " in column " + Grid.GetColumn(child);

                        Assert.True(
                            control.ActualHeight <= row.ActualHeight + Pixel,
                            described + " is " + control.ActualHeight.ToString("0.0")
                            + " px tall in a " + row.ActualHeight.ToString("0.0")
                            + " px row, so it hangs over the row below it");

                        // Measured in the card's own coordinates rather than compared
                        // as two heights: this is where the control actually ends up
                        // after alignment, so a centred control taller than the space
                        // it was given is caught too.
                        double bottom = control.TransformToAncestor(card)
                            .Transform(new Point(0, 0)).Y + control.ActualHeight;

                        Assert.True(
                            bottom <= card.ActualHeight + Pixel,
                            described + " ends " + bottom.ToString("0.0")
                            + " px down a " + card.ActualHeight.ToString("0.0")
                            + " px card, so it reaches past the bottom edge and is cut off "
                            + "by it");
                    }
                }
            },
            setup =>
            {
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-alpha", Name = "Alpha" });
                setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-bravo", Name = "Bravo" });
            });
    }

    /// <summary>
    /// How many slot cards fit the page without a scrollbar: eleven. The strip's
    /// viewport is 610 - the panel's 634 less the 24 its padding takes, 16 above
    /// the caption strip and 8 below the last card - the caption strip takes 20.7
    /// of that, and eleven cards at 52 each come to 592.7, leaving 17 of slack. The
    /// twelfth would need 644.7, so it is the one that scrolls.
    /// <para>
    /// Pinned because it is where a row height is really spent, and it fails on
    /// nothing else: the cards still fit their columns and their own rows while the
    /// list quietly holds one fewer. It is also what the delete button's 28 buys -
    /// at its style's 30 a slot is 54 and eleven of them need 614.7 of the 610,
    /// which is ten cards and a scrollbar.
    /// </para>
    /// </summary>
    private const int SlotsOnAPage = 11;

    [Fact]
    public void Eleven_slots_fit_the_page_and_the_twelfth_asks_for_a_scrollbar()
    {
        // Two sides of the same line, because one count can be satisfied two ways:
        // a strip that grew a scrollbar and still showed eleven would pass a test
        // that only counted eleven, and a strip that shrank to twelve-a-page would
        // pass one that only counted twelve.
        double eleven = ScrollableWith(SlotsOnAPage);
        Assert.True(
            eleven <= Pixel,
            SlotsOnAPage + " slots need " + eleven.ToString("0") + " px of scrolling, so the page "
            + "no longer holds the " + SlotsOnAPage + " it is sized for: a slot card got taller");

        double twelve = ScrollableWith(SlotsOnAPage + 1);
        Assert.True(
            twelve > Pixel,
            "twelve slots fit the page without a scrollbar, so the page holds more than the "
            + SlotsOnAPage + " they are counted as and the cards are shorter than they "
            + "were measured at");
    }

    /// <summary>
    /// How far the slot list scrolls with <paramref name="slots"/> cards on it, on
    /// the real page at the size the window declares. Zero means every slot is on
    /// screen at once.
    /// </summary>
    private static double ScrollableWith(int slots)
    {
        double scrollable = -1.0;

        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                LayOutTheSlotsPage(h);

                Assert.True(
                    h.Window.SlotScroll.ViewportHeight > 0.0,
                    "the slot strip was laid out with no height, so the page pass did not "
                    + "reach it and the scrollbar means nothing");

                scrollable = h.Window.SlotScroll.ScrollableHeight;
            },
            setup =>
            {
                for (int i = 0; i < slots; i++)
                {
                    setup.Settings.Slots.Add(new HotkeySlot { Id = "slot-" + i, Name = "Slot " + i });
                }
            });

        return scrollable;
    }

    /// <summary>
    /// Lay the whole page out, not just the strip, so the slot list gets the height
    /// the window actually leaves it. The TabControl realises the selected tab and
    /// nothing else, so the slots tab has to be up for the strip to be measured at
    /// all - with the Display tab showing, its viewport is zero.
    /// </summary>
    private static void LayOutTheSlotsPage(WindowHarness h)
    {
        TabControl pages = h.Window.Pages;

        pages.SelectedItem = pages.Items.Cast<TabItem>()
            .Single(page => IsInside(h.Window.SlotScroll, page));

        FrameworkElement root = (FrameworkElement)h.Window.Content;
        root.Measure(new Size(h.Window.Width, h.Window.Height));
        root.Arrange(new Rect(0, 0, h.Window.Width, h.Window.Height));
        root.UpdateLayout();
    }

    /// <summary>Whether <paramref name="node"/> sits under <paramref name="ancestor"/>.</summary>
    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? step = node; step is not null; step = LogicalTreeHelper.GetParent(step))
        {
            if (ReferenceEquals(step, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Run the one layout pass the harness needs: the window is built and never
    /// shown, so every ActualWidth and ActualHeight is still zero until the board
    /// is measured and arranged at the size the window declares.
    /// </summary>
    private static void LayOutTheBoard(WindowHarness h)
    {
        ScrollViewer scroll = h.Window.SlotScroll;
        scroll.Measure(new Size(h.Window.Width, h.Window.Height));
        scroll.Arrange(new Rect(0, 0, h.Window.Width, h.Window.Height));
        scroll.UpdateLayout();
    }

    /// <summary>
    /// The column widths as <see cref="MainWindow"/> declares them, read from the
    /// field itself rather than parsed out of the source, so the numbers the layout
    /// engine was given and the numbers this test compares against are the same
    /// object.
    /// </summary>
    private static GridLength[] DeclaredColumns()
    {
        FieldInfo field = typeof(MainWindow).GetField(
            "SlotColumns",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "MainWindow.SlotColumns is gone; the card layout test needs updating.");

        return (GridLength[])field.GetValue(null)!;
    }
}
