using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The Settings list, measured rather than counted.
/// <para>
/// The night schedule was nearly a scrollbar. Its hours went in as a second row
/// under the switch, which is the obvious layout, and the list has about seventy
/// pixels of slack in a 790 tall window while two rows is about ninety six. The
/// arithmetic says it does not fit and the arithmetic is the only thing that
/// says anything until a person looks at a window.
/// </para>
/// <para>
/// So the row count comes out of the XAML rather than out of anybody's memory,
/// and the height is measured by laying the rows out for real, against the height
/// the window actually leaves the page. Adding a fifteenth row breaks this, which
/// is the point: the failure it prevents is invisible in a build and invisible in
/// a diff, and it only shows up as a scrollbar on a screen nobody is looking at
/// during a test run.
/// </para>
/// <para>
/// Zero scroll is a product rule, so it is worth the same standing as one.
/// </para>
/// </summary>
[Collection("wpf")]
public class SettingsFitTests
{
    private const double WindowHeight = 790.0;
    private const double TitleRow = 58.0;
    private const double StatusRow = 30.0;
    private const double PagePadding = 14.0 * 2.0;

    /// <summary>
    /// The FxSound health header sits above the settings list and takes this much
    /// room off the page.
    /// <para>
    /// It was missing here, and that is how a settings tab could scroll while this
    /// file reported that it fitted. The budget said 674 pixels were available and
    /// the list wanted 616, so the test passed; the health header had taken 76 of
    /// those 674, leaving 598, and 616 does not fit in 598.
    /// </para>
    /// <para>
    /// A guard that does not know about the thing it is guarding does not guard it.
    /// So this is read out of the markup rather than written down, which means the
    /// day the header is resized or removed the budget follows it instead of quietly
    /// going back to being wrong.
    /// </para>
    /// </summary>
    private static double HealthHeaderHeight() => WpfTestHost.Invoke(() =>
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("Height=\"76\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the settings page no longer has a 76 pixel header above the list");
        return 76.0;
    });

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

    /// <summary>The settings page: from its scroll viewer to where it ends.</summary>
    private static string SettingsPage()
    {
        string xaml = Xaml();

        int start = xaml.IndexOf("x:Name=\"SettingsScroll\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the settings page has no scroll viewer to measure");

        int end = xaml.IndexOf("</ScrollViewer>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the settings page's scroll viewer is not closed");

        return xaml.Substring(start, end - start);
    }

/// <summary>How many settings rows there are, counted from the markup.</summary>
    private static int RowCount() =>
        Regex.Matches(SettingsPage(), @"ui:OptionToggle|<Border[^>]*Style=""{StaticResource ActionRow}""")
            .Count();

    /// <summary>
    /// The list is two columns, so the height that matters is the taller of the two,
    /// not the sum. Rows are distributed evenly rather than left to whatever order
    /// they happen to sit in, so this reads the split out of the markup instead of
    /// trusting that whoever adds the next row puts it in the shorter column.
    /// </summary>
    private static int[] RowCountsPerColumn()
    {
        string page = SettingsPage();

        int leftAt = page.IndexOf("<StackPanel Grid.Column=\"0\">", StringComparison.Ordinal);
        int rightAt = page.IndexOf("<StackPanel Grid.Column=\"2\">", StringComparison.Ordinal);
        Assert.True(leftAt > 0 && rightAt > leftAt, "the settings list is not two columns");

        int leftEnd = page.IndexOf("</StackPanel>", leftAt, StringComparison.Ordinal);
        int rightEnd = page.IndexOf("</StackPanel>", rightAt, StringComparison.Ordinal);

        return new[]
        {
            RowsBetween(page, leftAt, leftEnd),
            RowsBetween(page, rightAt, rightEnd)
        };
    }

    private static int RowsBetween(string page, int from, int to) =>
        Regex.Matches(page.Substring(from, to - from), @"ui:OptionToggle|<Border[^>]*Style=""{StaticResource ActionRow}""")
            .Count();

    /// <summary>The height the window leaves the settings page.</summary>
    private static double Available() =>
        WindowHeight - TitleRow - StatusRow - PagePadding - HealthHeaderHeight();

    [Fact]
    public void The_settings_list_still_fits_without_a_scrollbar()
    {
        int[] columns = RowCountsPerColumn();

        // The two numbers a row is made of, written here because the styles
        // cannot be loaded without the app's resource dictionaries, and because
        // a row that grows without anybody noticing is the other way this test
        // can be defeated.
        double tallest = WpfTestHost.Invoke(() =>
        {
            var probe = new StackPanel();

            for (int i = 0; i < columns.Max(); i++)
            {
                probe.Children.Add(new Border
                {
                    Height = 40,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            probe.Measure(new Size(545, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        });

        Assert.True(
            tallest <= Available(),
            "the taller settings column wants " + tallest.ToString("0")
            + " px in a window that leaves the page " + Available().ToString("0")
            + " px, so it scrolls. The columns hold "
            + columns[0] + " and " + columns[1] + " rows. "
            + "Either put the new control in the shorter column, or take a row out; "
            + "a scrollbar here is a product rule broken, not a nuisance.");
    }

    [Fact]
    public void The_two_settings_columns_are_the_same_width()
    {
        // "Symmetrically" is the word the layout was asked for, and it is not a
        // word a person can check by eye once every row has been clicked. Both
        // columns are star widths either side of one fixed gutter, which is what
        // makes them equal without a hard coded number to drift out of date.
        string page = SettingsPage();

        // Only the outer grid's own column definitions. The rows below it contain
        // nested grids with their own star columns, and counting all of them
        // measures the whole page rather than the two columns being compared.
        int gridAt = page.IndexOf("<Grid MaxWidth=\"1120\"", StringComparison.Ordinal);
        Assert.True(gridAt > 0, "the two column grid is gone");

        int defsAt = page.IndexOf("<Grid.ColumnDefinitions>", gridAt, StringComparison.Ordinal);
        int defsEnd = page.IndexOf("</Grid.ColumnDefinitions>", defsAt, StringComparison.Ordinal);
        Assert.True(defsAt > 0 && defsEnd > defsAt, "the two column grid has no column definitions");

        string defs = page.Substring(defsAt, defsEnd - defsAt);

        int stars = Regex.Matches(defs, @"Width=""\*""").Count;
        Assert.Equal(2, stars);

        Assert.Contains("Width=\"30\"", defs);
    }

    [Fact]
    public void No_settings_column_is_empty_and_they_are_balanced()
    {
        // A row added to a full column makes it taller than the other one, which is
        // how the next scrollbar arrives without anybody adding a sixteenth row.
        // One apart is the most the page can be out and still read as two columns.
        int[] columns = RowCountsPerColumn();

        Assert.True(columns[0] > 0, "the first settings column is empty");
        Assert.True(columns[1] > 0, "the second settings column is empty");
        Assert.True(
            Math.Abs(columns[0] - columns[1]) <= 1,
            "the settings columns hold " + columns[0] + " and " + columns[1]
            + " rows, which is too far apart to read as one list.");
    }

    [Fact]
    public void The_list_does_not_say_Settings_under_the_pill_that_says_Settings()
    {
        // The nav pill names the page, in the same accent, one row higher, with
        // the other three names beside it. A second copy of the same word says
        // nothing, and it was saying it in the most expensive way available:
        // about twenty six pixels of a list that has about seventy to give.
        //
        // Those twenty six are what the night schedule's row was short of, and
        // this row is a setting while the header was a label for the page.
        Assert.DoesNotContain(">SETTINGS<", SettingsPage());
    }

    [Fact]
    public void The_other_tabs_still_name_their_own_sections()
    {
        // The setting was a header that named the page, and no other tab has one
        // - the others label what is inside them, and the nav pill says which
        // tab is open. So removing it makes Settings match the rest rather than
        // leaving it the odd one out.
        string xaml = Xaml();

        Assert.Contains("Text=\"PREVIEW\"", xaml);
        Assert.Contains("Text=\"SPECTRUM\"", xaml);
        Assert.Contains("Text=\"EFFECTS AND ENGINE\"", xaml);
        Assert.Contains("Text=\"EQUALIZER\"", xaml);
        Assert.Contains("x:Name=\"SlotCountText\"", xaml);
    }

    [Fact]
    public void The_night_schedule_is_one_row_and_not_two()
    {
        // The whole reason it fits. Split into a switch and a row of times it
        // would not, and the fix is not to raise the window.
        Assert.Single(Regex.Matches(SettingsPage(), "x:Name=\"NightRow\""));
        Assert.Single(Regex.Matches(SettingsPage(), "x:Name=\"NightBox\""));
        Assert.Single(Regex.Matches(SettingsPage(), "x:Name=\"NightStartBox\""));
        Assert.Single(Regex.Matches(SettingsPage(), "x:Name=\"NightEndBox\""));
    }

    [Fact]
    public void The_night_row_offers_only_real_times()
    {
        // A free text box would let 25:99 into a schedule, and the difference
        // between that and a working filter is a picker rather than a field.
        Assert.DoesNotContain("TextChanged", SettingsPage());
        Assert.Contains("OnNightTimeChanged", SettingsPage());
        Assert.Contains("Style=\"{StaticResource ModernCombo}\"", SettingsPage());
    }

    [Fact]
    public void The_night_row_says_what_it_is_actually_doing()
    {
        // The tooltip is where the two questions get answered that the row
        // itself cannot: that this is the app's own filter rather than Windows
        // Night Light, and that a game loading its own slot is unaffected.
        string page = SettingsPage();
        Assert.Contains("Windows Night Light", page);
        Assert.Contains("own filter", page);
    }

    [Fact]
    public void The_night_switch_is_the_green_one_every_other_settings_switch_is()
    {
        // Rose or cyan in a column of green would read as a different kind of
        // setting, and it is not one.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"NightBox\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the night switch is gone");

        int end = xaml.IndexOf("/>", at, StringComparison.Ordinal);
        Assert.Contains("Accent=\"{StaticResource AccentSettings}\"", xaml.Substring(at, end - at));
    }

    [Fact]
    public void The_display_tab_keeps_reading_the_schedules_level()
    {
        // The two halves of this feature meet in one line. If the apply path went
        // back to the stored setting, a game that loaded a slot during the
        // evening would be the one tune on the screen without the warm trim, and
        // nothing in the schedule would notice.
        var file = new FileInfo(FindRepoFile(Path.Combine("app", "MainWindow.Display.cs")));
        string source = File.ReadAllText(file.FullName);

        // Both halves carry the strength as well as the level. The preview is what the
        // user judges the picture by and the apply is what the monitor gets, so a
        // fade that reached only one of them would show a thumbnail at full strength
        // all evening while the screen ramped in.
        //
        // Compared against whitespace-collapsed source, because the calls are wrapped
        // across two lines. Asserting on the raw text would fail on formatting alone,
        // and a test that breaks when someone wraps a line is a test that gets
        // "fixed" by deleting the assertion rather than by fixing the code.
        string flat = System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ");

        // The space after the open paren is the wrapped-line indent that survives
        // collapsing, so it is in the needle too. Pinned rather than stripped so a
        // reader who changes the wrapping sees this fail and updates it, instead of
        // the test quietly accepting any spacing at all.
        Assert.Contains(
            "WithBlueLight( preset, ActiveBlueLightLevel, ActiveBlueLightStrength)",
            flat,
            StringComparison.Ordinal);
        Assert.Contains(
            "WithBlueLight( _workDisplay, ActiveBlueLightLevel, ActiveBlueLightStrength)",
            flat,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WithBlueLight(preset, _settings.BlueLightFilter)", flat);
    }

    [Fact]
    public void The_blue_light_dropdown_cannot_read_the_schedules_write_back()
    {
        // The schedule pushes the level it is applying into the dropdown so the
        // two cannot disagree. Without the guard that write came straight back
        // down the handler and was saved as the user's own choice, which is how a
        // filter that was switched on for one evening became the remembered
        // setting for good.
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Display.cs")));

        int at = source.IndexOf("private void OnBlueLightChanged", StringComparison.Ordinal);
        Assert.True(at > 0, "the blue light handler is gone");

        int guard = source.IndexOf("_updating", at, StringComparison.Ordinal);
        int check = source.IndexOf("_settings.BlueLightFilter = level", at, StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < check, "the handler no longer ignores the app's own writes");
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
