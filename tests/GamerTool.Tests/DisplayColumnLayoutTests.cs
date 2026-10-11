using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Where the Display tab's sections sit, and what absorbs the spare height.
/// <para>
/// The panel had a band of dead black in the middle of it, between the four picture
/// sliders and the colour trim. It was not a missing control and not the hardware
/// brightness section collapsing - those rules were already collapsed. It was the
/// right hand column's first grid row being a star row with the sliders top aligned
/// inside it, so every spare pixel the tall preview on the left forced into the
/// column pooled under the sliders rather than off the end.
/// </para>
/// <para>
/// A gap in the middle of a stack reads as a layout that failed to load. The same
/// space at the foot of the column reads as space, and it is where the backlight
/// rows appear when the feature is switched on, so turning it on fills room that
/// was going to be empty anyway.
/// </para>
/// <para>
/// These are source assertions. The alternative is measuring a laid out window, and
/// a WPF window cannot be brought up in a test runner without the display and the
/// dispatcher this suite already has for other reasons. What is worth pinning is
/// the arrangement, because the arrangement is what went wrong.
/// </para>
/// </summary>
public class DisplayColumnLayoutTests
{
    private static string Xaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", "MainWindow.xaml"));
    }

    /// <summary>The Display tab's right hand column: its opening grid tag and row list.</summary>
    private static (string OpenTag, MatchCollection Rows) RightColumn()
    {
        string xaml = Xaml();

        int at = xaml.IndexOf("<Grid Grid.Column=\"2\" Margin=\"0,18,0,18\">", StringComparison.Ordinal);
        Assert.True(at >= 0, "the Display tab's right hand column is gone");

        int close = xaml.IndexOf("</Grid.RowDefinitions>", at, StringComparison.Ordinal);
        Assert.True(close > at, "the right hand column has no row definitions");

        string openTag = xaml.Substring(at, close - at);
        var rows = Regex.Matches(openTag, @"<RowDefinition\s+Height\s*=\s*""([^""]*)""");

        return (openTag, rows);
    }

    [Fact]
    public void The_star_row_is_at_the_bottom_and_not_the_top()
    {
        // The whole defect in one assertion. A star row first meant spare height
        // pooled under the sliders, in the middle of the stack.
        var rows = RightColumn().Rows;
        Assert.NotEmpty(rows);

        var heights = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            heights[i] = rows[i].Groups[1].Value;
        }

        Assert.Equal("*", heights[heights.Length - 1]);

        for (int i = 0; i < heights.Length - 1; i++)
        {
            Assert.Equal("Auto", heights[i]);
        }
    }

    [Fact]
    public void Colour_trim_sits_directly_under_the_four_sliders()
    {
        // The reordering. Colour trim is row one, the four sliders are row zero,
        // and nothing sits between them, which is what closes the hole.
        string xaml = Xaml();

        int sliders = xaml.IndexOf("x:Name=\"ContrastSlider\"", StringComparison.Ordinal);
        int colour = xaml.IndexOf("x:Name=\"RedSlider\"", StringComparison.Ordinal);

        Assert.True(sliders >= 0, "the contrast slider is gone");
        Assert.True(colour > sliders, "colour trim is above the four sliders");

        // And the colour block that holds it is the very next row.
        int colourGrid = xaml.LastIndexOf("<Grid Grid.Row=\"1\"", colour, StringComparison.Ordinal);
        Assert.True(colourGrid > sliders, "colour trim is not the row directly after the sliders");
    }

    [Fact]
    public void The_backlight_rows_come_after_the_colour_section()
    {
        // Hardware brightness is a different layer from the curve, and it is the
        // only section that appears and disappears, so it goes last.
        string xaml = Xaml();

        int blue = xaml.IndexOf("x:Name=\"BlueLightBox\"", StringComparison.Ordinal);
        int panel = xaml.IndexOf("x:Name=\"BacklightPanel\"", StringComparison.Ordinal);

        Assert.True(blue >= 0, "the blue light filter is gone");
        Assert.True(panel > blue, "hardware brightness is above the colour section");
    }

    [Fact]
    public void The_backlight_section_has_no_dividers_around_it()
    {
        // It had a hairline above and below. Every control on the Display tab is a
        // display slider, so a rule between two sets of them divides a list that was
        // never in two - and the rules were the reason the section needed collapsing
        // to avoid leaving a blank band when the feature was off.
        string xaml = Xaml();

        Assert.DoesNotContain("BacklightTopRule", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BacklightBottomRule", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backlight_section_is_the_row_before_the_trailing_slack()
    {
        string xaml = Xaml();

        var rows = RightColumn().Rows;
        int panelRow = int.Parse(
            Regex.Match(xaml, @"x:Name=""BacklightPanel""\s+Grid\.Row=""(\d+)""").Groups[1].Value);

        Assert.Equal(rows.Count - 2, panelRow);
    }

    [Fact]
    public void The_three_section_headings_match_the_slider_labels()
    {
        // Colour trim, blue light filter and hardware brightness were SectionHeader:
        // 9.5 point, semi-bold, in the dimmest text colour, so three of the seven
        // labels in one column were smaller, dimmer and a heavier weight than the
        // four beside them. Gamma, Shadow boost, Brightness and Contrast all use
        // Label, and a slider group is not a section.
        string xaml = Xaml();

        foreach (string caption in new[] { "Colour trim", "Blue light filter" })
        {
            // Anchored on the attribute, not the bare words. Both phrases appear in
            // the comments explaining why they moved, and a search for the words
            // finds one of those and then attributes the style of whatever
            // TextBlock happened to come before it.
            string needle = "Text=\"" + caption + "\"";
            int at = xaml.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, caption + " is gone");

            int tag = xaml.LastIndexOf("<TextBlock", at, StringComparison.Ordinal);
            int close = xaml.IndexOf('>', at);
            string own = xaml.Substring(tag, close - tag);

            Assert.Contains("Style=\"{StaticResource Label}\"", own, StringComparison.Ordinal);
        }

        // And neither is still a SectionHeader.
        Assert.DoesNotContain("Style=\"{StaticResource SectionHeader}\" Text=\"COLOUR TRIM", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource SectionHeader}\" Text=\"BLUE LIGHT FILTER", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_blue_light_filter_lines_up_with_the_contrast_slider()
    {
        // Both sit in the third column of a star, 40, star grid spanning the same
        // width, so their left edges are the same place by construction. The box
        // was a fixed 232, which pinned it to the right edge and left its caption
        // about 120 pixels right of Contrast's with nothing holding the two
        // together.
        string xaml = Xaml();

        int sliders = xaml.IndexOf("x:Name=\"ContrastSlider\"", StringComparison.Ordinal);
        int slidersGrid = xaml.LastIndexOf(
            "<Grid Grid.Row=\"0\" VerticalAlignment=\"Top\">",
            sliders,
            StringComparison.Ordinal);
        Assert.True(slidersGrid >= 0, "the four slider grid is gone");

        int colourGrid = xaml.IndexOf("<Grid Grid.Row=\"1\" Margin=\"0,22,0,0\">", StringComparison.Ordinal);
        Assert.True(colourGrid > slidersGrid, "the colour row is not after the four sliders");

        // Both grids declare the same three proportions. Measured from the column
        // definitions rather than from the opening tag, because the opening tags
        // differ by design - one is row zero, the other row one with a margin.
        string Proportions(int grid) => xaml.Substring(
            xaml.IndexOf("<Grid.ColumnDefinitions>", grid, StringComparison.Ordinal),
            xaml.IndexOf("</Grid.ColumnDefinitions>", grid, StringComparison.Ordinal)
            - xaml.IndexOf("<Grid.ColumnDefinitions>", grid, StringComparison.Ordinal));

        string a = Proportions(slidersGrid);
        string b = Proportions(colourGrid);

        Assert.Equal(
            CountOf(a, "<ColumnDefinition"),
            CountOf(b, "<ColumnDefinition"));

        // All whitespace, not just spaces: the two grids are indented to different
        // depths, so stripping spaces alone leaves the line breaks and the leading
        // indentation in and the two never compare equal.
        static string Flatten(string s) => Regex.Replace(s, @"\s+", string.Empty);

        Assert.Equal(Flatten(a), Flatten(b));
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        int at = 0;
        while (true)
        {
            int found = haystack.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return n;
            }

            n++;
            at = found + 1;
        }
    }

    [Fact]
    public void The_colour_row_has_room_under_the_sliders_without_a_rule()
    {
        string xaml = Xaml();

        Assert.Contains("<Grid Grid.Row=\"1\" Margin=\"0,22,0,0\">", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sections_are_in_the_order_they_are_read()
    {
        // The elements were physically moved rather than only renumbered. Grid.Row
        // decides where something is drawn and the markup decides what Tab reaches
        // next, so renumbering alone would have left the keyboard jumping down to
        // the backlight and back up to the colour trim.
        string xaml = Xaml();

        int sliders = xaml.IndexOf("x:Name=\"ContrastSlider\"", StringComparison.Ordinal);
        int colour = xaml.IndexOf("x:Name=\"RedSlider\"", StringComparison.Ordinal);
        int blue = xaml.IndexOf("x:Name=\"BlueLightBox\"", StringComparison.Ordinal);
        int panel = xaml.IndexOf("x:Name=\"BacklightPanel\"", StringComparison.Ordinal);

        Assert.True(sliders < colour, "markup order: sliders, colour trim");
        Assert.True(colour < blue, "markup order: colour trim, blue light filter");
        Assert.True(blue < panel, "markup order: colour section, hardware backlight");
    }

    [Fact]
    public void Nothing_still_describes_the_slack_as_collecting_underneath()
    {
        // The note that stood on the star row described the gap as the intended
        // behaviour, which is how it survived. A comment that argues for the layout
        // is worth contradicting explicitly.
        string xaml = Xaml();

        Assert.DoesNotContain(
            "collects underneath",
            xaml,
            StringComparison.OrdinalIgnoreCase);
    }
}
