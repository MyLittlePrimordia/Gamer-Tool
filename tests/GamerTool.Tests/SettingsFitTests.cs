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
            .Count;

    /// <summary>The height the window leaves the settings page.</summary>
    private static double Available() => WindowHeight - TitleRow - StatusRow - PagePadding;

    [Fact]
    public void The_settings_list_still_fits_without_a_scrollbar()
    {
        int rows = RowCount();

        // The two numbers a row is made of, written here because the styles
        // cannot be loaded without the app's resource dictionaries, and because
        // a row that grows without anybody noticing is the other way this test
        // can be defeated.
        double row = WpfTestHost.Invoke(() =>
        {
            var probe = new StackPanel();

            for (int i = 0; i < rows; i++)
            {
                probe.Children.Add(new Border
                {
                    Height = 40,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            probe.Measure(new Size(620, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        });

        Assert.True(
            row <= Available(),
            "the settings list wants " + row.ToString("0")
            + " px in a window that leaves the page " + Available().ToString("0")
            + " px, so it scrolls with " + rows + " rows. "
            + "Either put the new control in a row that already exists, or take a row out; "
            + "a scrollbar here is a product rule broken, not a nuisance.");
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

        Assert.Contains("WithBlueLight(preset, ActiveBlueLightLevel)", source);
        Assert.Contains("WithBlueLight(_workDisplay, ActiveBlueLightLevel)", source);
        Assert.DoesNotContain("WithBlueLight(preset, _settings.BlueLightFilter)", source);
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
