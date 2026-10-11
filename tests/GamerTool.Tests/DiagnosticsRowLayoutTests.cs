using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The diagnostics row has to read as one of the toggle rows above it: mark on
/// the left, action on the right.
/// <para>
/// It broke in a way that no existing test could see, because nothing about it
/// was invalid. The clipboard sat in its own grid that carried no
/// <c>Grid.Column</c> at all, and an element with no column set goes in column
/// zero - the same column as the ladybug. The XAML compiled, every test passed,
/// and the result was a screenshot with the two marks stacked on each other on
/// the left and an empty gap where the switch column should have been.
/// </para>
/// <para>
/// So this reads the markup rather than the visual tree. The check that matters
/// is not that the row looks right in some arrangement, it is that the action is
/// in the last column and the mark in the first, which is the arrangement the
/// option rows use and the one that cannot silently collapse.
/// </para>
/// </summary>
public class DiagnosticsRowLayoutTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The Diagnostics Border and everything inside it, as source text.</summary>
    private static string DiagnosticsRow()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "app", "MainWindow.xaml"));

        int start = xaml.IndexOf("x:Name=\"DiagRow\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the Diagnostics row is no longer in MainWindow.xaml");

        // Walked over whole tags, counting opens against closes, so the nested
        // Grid and Border inside the row cannot end the search early. The
        // element's own opening tag is counted before the walk rather than by
        // it: the walk has to resume from the tag's end, so searching for the
        // next '<' from the tag's start finds the one after it and the row's own
        // opening is never counted. Miss that and the depth returns to zero one
        // tag early, and the extracted text runs on into the row below.
        int open = xaml.LastIndexOf('<', start);

        int depth = 1;
        int i = open;
        while (i < xaml.Length)
        {
            int next = xaml.IndexOf('<', i + 1);
            if (next < 0)
            {
                break;
            }

            int close = xaml.IndexOf('>', next);
            if (close < 0)
            {
                break;
            }

            string tag = xaml.Substring(next + 1, close - next - 1);

            bool closing = tag.StartsWith("/", StringComparison.Ordinal);
            bool selfClosing = tag.EndsWith("/", StringComparison.Ordinal);
            bool isBorder = tag.Contains("Border", StringComparison.Ordinal);

            if (isBorder)
            {
                if (closing)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return xaml.Substring(open, close + 1 - open);
                    }
                }
                else if (!selfClosing)
                {
                    depth++;
                }
            }

            i = close;
        }

        Assert.Fail("the Diagnostics row is not closed, so it cannot be read");
        return string.Empty;
    }

    /// <summary>
    /// The grid column the element is placed in, which is the one on the
    /// element's own tag or, for a plain <c>Grid</c> wrapping it, on that grid.
    /// </summary>
    private static int ColumnOf(string row, string element)
    {
        int at = row.IndexOf(element, StringComparison.Ordinal);
        Assert.True(at >= 0, element + " is missing from the Diagnostics row");

        // The tag the element itself lives in.
        int tagStart = row.LastIndexOf('<', at);
        int tagEnd = row.IndexOf('>', at);
        string tag = row.Substring(tagStart, tagEnd - tagStart);

        Match own = Regex.Match(tag, @"Grid\.Column\s*=\s*""(\d+)""");
        if (own.Success)
        {
            return int.Parse(own.Groups[1].Value);
        }

        // Otherwise it is placed by whatever grid encloses it. The column can
        // sit on the element's own tag, or on a plain Grid wrapped around it to
        // host a second element, and which of those it is has changed as the row
        // was edited. Both are checked so neither arrangement passes by accident
        // and neither fails on a change that did not move anything.
        if (tag.StartsWith("Grid", StringComparison.Ordinal))
        {
            Match parent = Regex.Match(tag, @"Grid\.Column\s*=\s*""(\d+)""");
            Assert.True(
                parent.Success,
                "the Grid holding " + element + " sets no Grid.Column, so it defaults to column zero on top of the mark");

            return int.Parse(parent.Groups[1].Value);
        }

        int gridStart = row.LastIndexOf("<Grid", tagStart, StringComparison.Ordinal);
        if (gridStart >= 0)
        {
            int gridEnd = row.IndexOf('>', gridStart);
            string gridTag = row.Substring(gridStart, gridEnd - gridStart);

            Match parent = Regex.Match(gridTag, @"Grid\.Column\s*=\s*""(\d+)""");
            if (parent.Success)
            {
                return int.Parse(parent.Groups[1].Value);
            }
        }

        Assert.Fail(
            element + " is placed by neither its own tag nor an enclosing grid's, "
            + "so it defaults to column zero on top of the mark");
        return -1;
    }

    [Fact]
    public void The_clipboard_sits_in_the_column_the_switches_use()
    {
        string row = DiagnosticsRow();

        int mark = ColumnOf(row, "CopyDiagButton");
        Assert.True(
            mark > 0,
            "the clipboard is in column zero, so it draws on top of the mark instead of opposite it");
    }

    [Fact]
    public void The_mark_and_the_action_are_at_opposite_ends_of_the_row()
    {
        string row = DiagnosticsRow();

        int bug = ColumnOf(row, "Glyph=\"bug\"");
        int clipboard = ColumnOf(row, "CopyDiagButton");

        Assert.True(
            clipboard > bug,
            "the action has to be right of the mark, not on top of it");
    }

    [Fact]
    public void The_action_column_is_the_last_one_in_the_row()
    {
        string row = DiagnosticsRow();

        // Count the columns the row actually defines, then insist the clipboard
        // is in the final one. This is what keeps the row's right edge lined up
        // with the switch column of every toggle above it.
        int columns = Regex.Matches(row, "<ColumnDefinition").Count;
        Assert.True(columns >= 5, "the Diagnostics row lost its action column");

        int clipboard = ColumnOf(row, "CopyDiagButton");
        Assert.True(
            clipboard == columns - 1,
            "the clipboard is in column " + clipboard + " of " + columns
            + ", so it does not line up with the switches");
    }

    [Fact]
    public void The_action_column_is_the_width_of_a_switch_track()
    {
        string row = DiagnosticsRow();

        // The whole reason the row is allowed to sit in the toggle list is that
        // it has the same silhouette. A clipboard in an Auto column is 34 wide
        // while it shows and as wide as its own confirmation text the moment
        // copying, which shoves the mark sideways on every copy.
        //
        // So the last ColumnDefinition is read and has to be the switch track's
        // own width, not a pattern that assumes where in the row it sits.
        Match? last = Regex.Matches(row, @"<ColumnDefinition\s+Width\s*=\s*""([^""]*)""")
            .Cast<Match>()
            .LastOrDefault();

        Assert.True(last is not null, "the Diagnostics row has no columns to check");
        Assert.True(
            last!.Groups[1].Value == "34",
            "the action column is " + last.Groups[1].Value + " wide, not the 34 of a switch track, "
            + "so the row changes width when the confirmation shows");
    }

    [Fact]
    public void The_confirmation_is_not_inside_the_switch_sized_column()
    {
        string row = DiagnosticsRow();

        // The clipping came from the confirmation being right aligned in the
        // switch's 34 pixel slot. "Copied to clipboard" is roughly a hundred
        // pixels of text, so in a 34 pixel slot it was cut off after the first
        // few letters. It has to have a column of its own, to the left of the
        // mark, and that column has to be able to take its width.
        int confirmation = ColumnOf(row, "DiagCopiedText");
        int mark = ColumnOf(row, "CopyDiagButton");

        Assert.True(
            confirmation < mark,
            "the confirmation is in column " + confirmation + " and the mark in " + mark
            + ", so the text is being drawn where the mark is and is clipped by it");
    }

    [Fact]
    public void The_confirmation_column_can_grow_to_hold_the_text()
    {
        string row = DiagnosticsRow();

        // Sized to its content, which is what lets the text be as long as it
        // likes without being cut off. The label column is the star, so the width
        // comes out of that and the mark stays pinned to the right edge.
        List<string> widths = Regex.Matches(
                row,
                @"<ColumnDefinition\s+Width\s*=\s*""([^""]*)""\s*/>")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(widths);
        Assert.True(
            widths.Count(m => m == "*") == 1,
            "the row needs exactly one star column, which is the one that gives way to the confirmation");
    }

    [Fact]
    public void The_mark_is_not_hidden_while_the_confirmation_shows()
    {
        // The mark is the only thing on the row that says which action was
        // taken. Hiding it to announce that the action had worked took away the
        // answer at the moment it was wanted, and the code that did it was two
        // lines naming the mark.
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "app", "MainWindow.Backlight.cs"));

        Assert.DoesNotContain(
            "CopyDiagButton.Visibility = Visibility.Collapsed",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "CopyDiagButton.Visibility = Visibility.Visible",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_button_is_gone_and_only_copying_is_offered()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "app", "MainWindow.xaml"));

        Assert.DoesNotContain("OpenLogFolder", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnCopyDiagnosticsClick", xaml, StringComparison.Ordinal);    }
}
