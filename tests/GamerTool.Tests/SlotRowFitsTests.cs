using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The slot card's arithmetic, on both of its rows.
/// <para>
/// Every other page either has room or is allowed to grow. This one has no
/// horizontal scrollbar, so a column added without a column taken is invisible
/// until somebody with a 1200 pixel window finds that the game picker has been
/// pushed off the end. There is nothing to notice in a build and nothing to
/// notice in a diff.
/// </para>
/// <para>
/// Which is what happened twice before. A delete button was considered for the
/// single row and the note beside it explains why it went to a context menu
/// instead. A second time it would be a scrollbar nobody asked for, which is
/// what a hundred and fifty pixel game picker was: every installed program's
/// name clipped to "Cyberpunk 2".
/// </para>
/// <para>
/// The card is two rows now, and this pins both the width of the tuning row and
/// how many of them fit vertically, because the second is the trade the split
/// was made for. The widths are read out of the source rather than repeated
/// here, so changing one without redoing the arithmetic fails the build instead
/// of quietly overflowing.
/// </para>
/// </summary>
public class SlotRowFitsTests
{
    private const double WindowWidth = 1200.0;

    /// <summary>What SlotScroll's own padding takes off the width.</summary>
    private const double ScrollPadding = 18.0 + 6.0;

    /// <summary>What the card's own padding takes off the width.</summary>
    private const double CardPadding = 12.0 * 2.0;

    /// <summary>
    /// What has to be left for the game picker.
    /// <para>
    /// 190, not the 150 this was squeezed to on the single row before the output
    /// picker existed, and not the 300 the two row layout managed. Enough for an
    /// installed program's name with the marquee for the rest, which is what
    /// decides whether this row is usable or merely complete.
    /// </para>
    /// </summary>
    private const double MinimumGameColumn = 190.0;

    /// <summary>What the page leaves for the slot rows, after its own header.</summary>
    private const double AvailableHeight = 910.0;

    /// <summary>
    /// How many slots have to be on screen without scrolling, which is what
    /// stops the split from quietly becoming a worse layout than the one it
    /// replaced.
    /// </summary>
    private const int FactorySlots = 6;

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", "MainWindow.Slots.cs"));
    }

    /// <summary>
    /// The column widths of one of the card's two rows, in order, exactly as
    /// they are declared. A star column comes back as null because it is not a
    /// width.
    /// </summary>
    private static double?[] Columns(string array)
    {
        string source = Source();

        int at = source.IndexOf(array, StringComparison.Ordinal);
        Assert.True(at >= 0, array + " is gone");

        int open = source.IndexOf("new(", at, StringComparison.Ordinal);
        int close = source.IndexOf("};", open, StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, array + " is not readable");

        MatchCollection matches = Regex.Matches(source.Substring(open, close - open), @"new\(([^)]*)\)");
        double?[] widths = new double?[matches.Count];

        for (int i = 0; i < matches.Count; i++)
        {
            string argument = matches[i].Groups[1].Value.Trim();

            if (argument.Contains("GridUnitType.Star", StringComparison.Ordinal))
            {
                widths[i] = null;
                continue;
            }

            widths[i] = double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.NotEmpty(widths);
        return widths;
    }

    /// <summary>
    /// The width the flexible column ends up with, and asserts there is exactly
    /// one of them.
    /// </summary>
    private static double StarWidthOf(double?[] columns)
    {
        double fixedWidth = 0.0;
        int stars = 0;

        foreach (double? width in columns)
        {
            if (width is null)
            {
                stars++;
                continue;
            }

            fixedWidth += width.Value;
        }

        Assert.Equal(1, stars);
        return Available() - fixedWidth;
    }

    /// <summary>
    /// How much of the width a row of all fixed columns leaves over. Negative
    /// means it does not fit at all.
    /// </summary>
    private static double SlackIn(double?[] columns)
    {
        double fixedWidth = 0.0;

        foreach (double? width in columns)
        {
            if (width is not null)
            {
                fixedWidth += width.Value;
            }
        }

        return Available() - fixedWidth;
    }

    private static double Available() => WindowWidth - ScrollPadding - CardPadding;

    [Fact]
    public void The_game_picker_gets_a_real_width_even_on_one_row()
    {
        // 194px, which is more than the 150 this had when the row was being
        // called too tight to use. The first answer to "can this be one row" was
        // that it could not, and that answer came from minimums that were too
        // generous: the name only has to hold a word, not a field, and the game
        // picker has the marquee.
        double game = StarWidthOf(Columns("SlotColumns"));

        Assert.True(
            game >= MinimumGameColumn,
            "the row leaves the game picker " + game.ToString("0")
            + " px, and below " + MinimumGameColumn.ToString("0")
            + " px it is clipping installed program names rather than showing them.");
    }

    [Fact]
    public void The_row_fits_without_a_horizontal_scrollbar()
    {
        // The strip does not scroll sideways, so a row that does not fit is a
        // control pushed off the end of the card rather than something the user
        // can reach. The game picker is the flexible column, so this is really a
        // check on the seven fixed ones.
        double slack = SlackIn(Columns("SlotColumns"));

        Assert.True(
            slack >= 0.0,
            "the row's fixed columns are " + (-slack).ToString("0")
            + " px wider than the panel, so the game picker is being pushed off "
            + "the end of the card and there is no scrollbar to find it with");
    }

    [Fact]
    public void The_output_picker_gets_a_column_of_its_own()
    {
        // Column 10, between the monitor and the flexible game picker. A fixed
        // column after the star is the one that gets the leftover rather than
        // sharing it, which is how a row ends up looking correct and being 40px
        // too wide.
        double?[] row = Columns("SlotColumns");

        Assert.NotNull(row[10]);
        Assert.Null(row[12]);
    }

    [Fact]
    public void The_auto_switch_is_gone_and_the_game_dropdown_is_what_decides_auto_apply()
    {
        // The switch could only ever turn auto-apply off from a state the game
        // dropdown had just set: choosing an app already forces it on, and "No
        // game" already forces it off. So the switch was a second control saying
        // one thing, and the two could disagree - which is exactly what its own
        // comment recorded happening.
        //
        // What is left has to carry the explanation instead, and the game picker
        // is the only place the answer can now be read from.
        string source = Source();

        Assert.DoesNotContain("autoBox", source);
        Assert.DoesNotContain("canAuto", source);
        Assert.DoesNotContain("SetSlotFlag", source);

        // The switch's caption is gone. Matched with its own quotes rather than
        // as a bare word, because the autostart column it is now named by starts
        // with the same four letters.
        Assert.DoesNotContain("\"AUTO\"", CaptionText());

        // The dropdown carries the auto state in its tooltip.
        Assert.Contains("ToolTip = AutoToolTip(slot)", source);
    }

    /// <summary>
    /// Every caption the strip puts above a card, in the order it places them.
    /// <para>
    /// Read out of the strip rather than the caption helper, because the helper
    /// is where a caption is built and the calls are what decides which control
    /// it lands over. Reading the helper alone would pass with every caption on
    /// the wrong column.
    /// </para>
    /// </summary>
    private static string CaptionText()
    {
        string source = Source();

        // Bounded by the tuple list itself rather than by the next declaration.
        // The caption columns are the thing worth pinning, and anchoring on
        // whatever happens to follow the method makes this break every time
        // something is inserted nearby.
        int at = source.IndexOf("(int Column, string Text)[] captions", StringComparison.Ordinal);
        Assert.True(at > 0, "the caption list is gone");

        return string.Join(" ", Regex.Matches(source.Substring(at, 1200), @"\((\d+), ""([A-Z]+)""\)")
            .Select(m => m.Groups[2].Value));
    }

    [Fact]
    public void The_caption_strip_names_every_control_except_the_delete_button()
    {
        // One strip for the whole list, so it has to use the same columns as the
        // card or the labels sit over the wrong controls.
        string source = Source();

        Assert.Contains("ApplySlotColumns(head);", source);
        Assert.Equal("NAME HOTKEY DISPLAY SOUND MONITOR OUTPUT AUTOSTART", CaptionText());

        // Nothing is captioned over the delete button. A caption there would name
        // what the icon beside it has always said, and a label that repeats a
        // control is a line of the layout spent saying nothing.
        Assert.DoesNotContain("DELETE", CaptionText());
    }

    [Fact]
    public void The_autostart_column_is_named_for_what_it_decides()
    {
        // "Game" named the thing the dropdown happens to hold. "Autostart" names
        // what picking a value in it does, which is the only thing about that
        // column the user has to be told - the switch that used to sit beside it
        // is gone, so the caption is the last place that says this column loads
        // the slot by itself.
        Assert.Contains("(12, \"AUTOSTART\")", Source());
        Assert.DoesNotContain("\"GAME\"", Source());
    }

    [Fact]
    public void The_name_plate_is_built_to_the_same_measurements_as_the_dropdowns()
    {
        // It is a box on a row of boxes now, so it has to be the same box. It
        // was a hairline in LineStrong where the combo uses Line, with 11 pixels
        // of padding where the combo has 9, and no height at all - which is three
        // ways of reading as a mistake rather than as a name.
        //
        // Compared against the combo's real values rather than written out again
        // here, so a change to either style that breaks the match fails the build
        // instead of being noticed on a taskbar.
        string theme = File.ReadAllText(FindRepoFile(Path.Combine("app", "UI", "Theme.xaml")));

        string plate = StyleBody(theme, "x:Key=\"NamePlate\"");
        string combo = StyleBody(theme, "x:Key=\"ModernCombo\"");

        foreach (string metric in new[] { "WellBg", "Line", "RadiusControl" })
        {
            Assert.Contains(metric, plate);
            Assert.Contains(metric, combo);
        }

        Assert.Contains("Height\" Value=\"28\"", plate);
        Assert.Contains("Height\" Value=\"28\"", combo);

        // The value rather than the whole setter, because the two styles get
        // there differently: the plate is a Button whose padding is a setter,
        // and the combo's is on the Border inside its template. Same number,
        // two places, and comparing the text either one happens to use would
        // pass for a mismatch.
        Assert.Contains("\"9,0\"", plate);
        Assert.Contains("\"9,0\"", combo);

        Assert.Contains("FontSize\" Value=\"11.5\"", plate);
        Assert.Contains("FontSize\" Value=\"11.5\"", combo);

        // The one thing it does not share. The chevron says there is a list
        // under here, and the plate opens a rename box, so putting one on it
        // would be a small lie told for the sake of a row that looks even.
        // Matched on the path's name rather than the word, because the plate's
        // disabled state does set a cursor called Arrow.
        Assert.DoesNotContain("x:Name=\"Arrow\"", plate);
        Assert.Contains("x:Name=\"Arrow\"", combo);
    }

    /// <summary>The setters and template of one named style, as text.</summary>
    private static string StyleBody(string theme, string key)
    {
        int at = theme.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at > 0, key + " is gone from the theme");

        int open = theme.IndexOf('>', at);
        int close = theme.IndexOf("</Style>", at, StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, key + " is not a readable style");

        return theme.Substring(open, close - open);
    }

    [Fact]
    public void The_name_plate_is_just_wide_enough_for_a_name()
    {
        // It only has to hold a word. It was a star column on the two row
        // layout, which gave it nine hundred pixels and made it look like a
        // field rather than a name.
        double name = Columns("SlotColumns")[0]!.Value;

        Assert.True(
            name >= 120.0 && name <= 160.0,
            "the name plate is " + name.ToString("0") + " px wide, which is too narrow for the "
            + "shipped slot names or too wide to read as a name");
    }

    [Fact]
    public void The_six_slots_the_app_ships_with_still_fit_without_scrolling()
    {
        // One line again, so this is no longer tight. It is pinned anyway because
        // the two row layout that briefly replaced it fitted six by about twenty
        // pixels, which is not a margin anybody would have noticed going wrong.
        double row = Const("KeyCapHeight");
        int visible = (int)(AvailableHeight / (row + 18.0 + 8.0));

        Assert.True(
            visible >= FactorySlots * 2,
            "a slot card is " + (row + 26.0).ToString("0") + " px tall, which leaves "
            + " room for only " + visible + ". The app ships with " + FactorySlots
            + ", and a single row is supposed to have more room than the two row "
            + "layout it replaced, not less.");
    }


    private const double TuningRowHeight = 32.0;

    /// <summary>A double constant declared in the slot source, read as its value.</summary>
    private static double Const(string name)
    {
        string source = Source();
        Match match = Regex.Match(source, name + @"\s*=\s*([0-9.]+);");

        Assert.True(match.Success, name + " is gone or is no longer a plain number");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void The_output_picker_scrolls_rather_than_clipping()
    {
        // Output device names come off drivers and are the longest strings on
        // this row. A hundred and fifty pixels of fixed column with clipping
        // shows "Headset (Hyper" and leaves the user to guess.
        string source = Source();

        int at = source.IndexOf("private ComboBox OutputCombo", StringComparison.Ordinal);
        Assert.True(at >= 0, "the output picker is gone");

        int end = source.IndexOf("private void OnSlotOutputChanged", at, StringComparison.Ordinal);
        Assert.True(end > at, "the output picker has no end");

        Assert.Contains("MarqueeBox.SetAllowMarquee(box, true)", source.Substring(at, end - at));
    }

    [Fact]
    public void Every_picker_that_can_carry_a_long_name_scrolls()
    {
        // The row is one line and four of these carry text the user wrote or a
        // driver supplied. Without the marquee the width the row was made to fit
        // would come straight out of the names, turning this back into the
        // clipping it was meant to avoid.
        //
        // Four of them: the display preset, the monitor, the output device and
        // the game. The sound picker is the fifth name on the row and is
        // deliberately not one, because those names are all short and set by the
        // app.
        string source = Source();

        Assert.Equal(4, Regex.Matches(source, "MarqueeBox.SetAllowMarquee\\(box, true\\)").Count);
    }

    [Fact]
    public void The_output_picker_starts_on_the_same_words_as_the_settings_dropdown()
    {
        // The first entry, and the default selection for every slot that has
        // never been set otherwise. It has to read as the same choice as the
        // setting it follows: "Follow the app" described the mechanism rather
        // than naming the option, so the two pickers looked like two unrelated
        // things rather than a row and the setting it defers to.
        string source = Source();

        int at = source.IndexOf("private ComboBox OutputCombo", StringComparison.Ordinal);
        int end = source.IndexOf("private void OnSlotOutputChanged", at, StringComparison.Ordinal);
        string body = source.Substring(at, end - at);

        Assert.Contains("Name = DeviceChoice.SystemDefaultName", body);
        Assert.Contains("box.SelectedIndex = index < 0 ? 0 : index", body);
    }

    [Fact]
    public void The_system_default_label_is_written_once_and_used_by_both_pickers()
    {
        // One constant rather than two literals that happen to match. The
        // Settings dropdown and every slot's output picker have to keep saying
        // the same thing, and a string written in both places is a string that
        // can stop matching.
        string slots = Source();
        string audio = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Audio.cs")));
        string window = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.xaml.cs")));

        Assert.Contains("public const string SystemDefaultName = ", window);
        Assert.Contains("DeviceChoice.SystemDefaultName", slots);
        Assert.Contains("DeviceChoice.SystemDefaultName", audio);

        // And neither picker carries its own wording any more. Checked on the
        // code alone, because the comment explaining the rename quotes the old
        // wording on purpose and there is nothing wrong with a sentence saying it.
        Assert.DoesNotContain("Follow the app", Code(slots));
        Assert.DoesNotContain("Name = \"System default\"", Code(audio));
    }

    /// <summary>A source file with its line comments taken out.</summary>
    private static string Code(string source) =>
        string.Join(
            "\n",
            source.Split('\n')
                .Select(line =>
                {
                    int at = line.IndexOf("//", StringComparison.Ordinal);
                    return at < 0 ? line : line[..at];
                }));

    [Fact]
    public void The_slot_name_is_a_plate_and_only_the_plate_renames()
    {
        // It was a transparent TextBlock, so the click target was the width of
        // the word with nothing drawn on it and the only clue it was live was
        // the pointer changing. Everything either side of it did nothing, which
        // reads as the card being clickable and is not.
        string source = Source();

        Assert.Contains("Style = (Style)FindResource(\"NamePlate\")", source);
        Assert.Contains("namePlate.Click += (s, e) => RenameSlot(slot);", source);

        // And the rename is not hung off the card or the row any more, which is
        // the part that made it feel like clicking the slot did something.
        Assert.DoesNotContain("card.MouseLeftButtonUp", source);
    }

    [Fact]
    public void A_built_in_slot_name_is_disabled_rather_than_looking_editable()
    {
        // A name that looked editable and then refused would be worse than one
        // that never looked editable, so the plate is disabled for the shipped
        // slots and the dimmed row is what says the name is fixed.
        string source = Source();

        Assert.Contains("IsEnabled = !slot.BuiltIn", source);
    }

    [Fact]
    public void The_name_plate_is_a_button_so_renaming_works_from_the_keyboard()
    {
        // A TextBlock that only answered a click was not reachable at all
        // without a mouse, on the one control in the app whose name the user
        // typed themselves.
        string theme = File.ReadAllText(FindRepoFile(Path.Combine("app", "UI", "Theme.xaml")));

        Assert.Contains(@"<Style x:Key=""NamePlate"" TargetType=""Button"">", theme);
    }

    [Fact]
    public void The_context_menu_is_back_to_just_duplicate()
    {
        // The route was a menu item first, which was wrong: the cards are built
        // when the tab opens and the device list arrives afterwards, so the menu
        // was baked with whatever existed at that moment and could be empty. A
        // control that shows its current value without being reopened is a
        // column, not a menu item.
        string source = Source();

        Assert.DoesNotContain("BuildSlotOutputMenu", source);
        Assert.DoesNotContain("Sound output", source);
    }

    [Fact]
    public void The_device_list_refreshes_the_rows_it_is_needed_by()
    {
        // The same timing problem, the other half of the fix. A row built before
        // the device read finished has to be rebuilt once it has, or the picker
        // is showing a stale list for as long as the tab stays open.
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Audio.cs")));

        Assert.Contains("Pages.SelectedIndex == 2", source);
        Assert.Contains("BuildSlots();", source);
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


