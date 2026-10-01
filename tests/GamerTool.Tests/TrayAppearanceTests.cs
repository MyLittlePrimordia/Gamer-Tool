using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The tray: the one surface the app owns that Windows draws for it.
/// <para>
/// Both halves of this came from looking at it. The context menu was a white box
/// on a black app, because a WinForms <see cref="ContextMenuStrip"/> is painted
/// by the Windows theme and none of the app's own theme reaches it. And the
/// tooltip ran the app's name into the state, so the useful half of it was off
/// the end of a line that also said "Gamer Tool".
/// </para>
/// <para>
/// The renderer and the tip text are the parts that can be checked without a
/// screen, which is most of them. What cannot be checked here is how the shell
/// actually paints the result; that needs a person looking at a taskbar.
/// </para>
/// </summary>
[Collection("wpf")]
public class TrayAppearanceTests
{
    [Fact]
    public void The_menu_is_not_left_to_the_windows_theme()
    {
        using ContextMenuStrip menu = DarkTrayMenuRenderer.Menu();

        // Asserted on the renderer rather than on RenderMode. Assigning a
        // Renderer flips the strip to Custom whatever the mode was set to, so
        // checking the enum would be checking a value the assignment overwrites.
        // What matters is that it is one of ours, and that it is built on the
        // professional renderer so the geometry and DPI scaling still come from
        // WinForms rather than being hand drawn.
        Assert.IsType<DarkTrayMenuRenderer>(menu.Renderer);
        Assert.IsAssignableFrom<ToolStripProfessionalRenderer>(menu.Renderer);
    }

    [Fact]
    public void The_menu_surface_is_the_apps_own_black()
    {
        using ContextMenuStrip menu = DarkTrayMenuRenderer.Menu();

        Assert.Equal(TrayColours.Surface, menu.BackColor);

        // Not pure black. A menu painted exactly the colour of the taskbar behind
        // it reads as a hole rather than as a panel.
        Assert.True(menu.BackColor.R < 32, "the menu surface is not dark");
        Assert.True(menu.BackColor.R > 0, "the menu is the same black as the taskbar behind it");
    }

    [Fact]
    public void The_menu_surface_matches_the_apps_elevated_colour()
    {
        // Read out of the theme rather than restated, so the tray cannot drift
        // away from the window if the palette is ever changed.
        string theme = File.ReadAllText(FindApp("UI", "Theme.xaml"));

        Assert.Contains("x:Key=\"Elevated\"", theme, StringComparison.Ordinal);
        Assert.Contains("\"#0E0E0E\"", theme, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ColorTranslator.FromHtml("#0E0E0E").ToArgb(),
            TrayColours.Surface.ToArgb());
    }

    [Fact]
    public void Every_menu_item_is_told_what_colour_its_text_is()
    {
        // The colour table paints backgrounds only. Item text comes from the
        // system unless the item is told otherwise, which is why a themed menu
        // otherwise comes out dark grey on near black.
        using ToolStripMenuItem item = DarkTrayMenuRenderer.Item("Open", (s, e) => { });

        Assert.Equal(TrayColours.Text, item.ForeColor);
        Assert.Equal(TrayColours.Surface, item.BackColor);
    }

    [Fact]
    public void The_menu_text_colour_is_the_apps_own_bright_text()
    {
        string theme = File.ReadAllText(FindApp("UI", "Theme.xaml"));

        Assert.Contains("x:Key=\"TextHi\"", theme, StringComparison.Ordinal);
        Assert.Contains("\"#EDEDED\"", theme, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ColorTranslator.FromHtml("#EDEDED").ToArgb(),
            TrayColours.Text.ToArgb());
    }

    [Fact]
    public void The_hover_row_is_lighter_than_the_surface_so_it_is_visible()
    {
        Assert.True(
            TrayColours.SurfaceHover.R > TrayColours.Surface.R,
            "the hover row is the same colour as the surface, so hovering does nothing");
    }

    [Fact]
    public void The_tip_is_the_app_name_on_its_own_line_then_the_state()
    {
        // Composed the way the tray composes it. The name, a blank line, then the
        // state: one line runs the two together and pushes the useful half off
        // the end on a narrow taskbar.
        string tip = TrayService.ComposeTip("Tactical FPS - Competitive + Footsteps");

        string[] lines = tip.Split("\r\n");

        Assert.Equal(3, lines.Length);
        Assert.Equal("Gamer Tool", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("Tactical FPS - Competitive + Footsteps", lines[2]);
    }

    [Fact]
    public void An_empty_state_leaves_the_name_alone()
    {
        string tip = TrayService.ComposeTip(string.Empty);

        Assert.Equal("Gamer Tool", tip);
    }

    [Fact]
    public void A_long_state_is_clamped_rather_than_thrown()
    {
        // The shell allows 128 including the terminator and throws past it. Two
        // long custom names plus a long slot name will do it, so the clamp is the
        // difference between a short tip and a failed call.
        string tip = TrayService.ComposeTip(new string('W', 400));

        Assert.True(tip.Length <= 127, "the tip is " + tip.Length + " characters");
        Assert.StartsWith("Gamer Tool", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moderate_state_is_kept_whole()
    {
        // The clamp must not cost anything at realistic lengths, which is what the
        // old 63 character limit was doing to a long preset name.
        string tip = TrayService.ComposeTip(new string('W', 60));

        Assert.Equal(60 + "Gamer Tool\r\n\r\n".Length, tip.Length);
    }

    [Fact]
    public void The_active_slot_is_named_by_asking_what_is_loaded()
    {
        // Not by remembering which slot was pressed. A slot can be loaded by a
        // hotkey, by clicking its row, by auto-switch, or by restoring a profile,
        // and one remembered field is right for three of those and quietly wrong
        // for the fourth.
        HotkeySlot screen = new()
        {
            Id = "a",
            Name = "Racing",
            Enabled = true,
            DisplayPresetId = "racing",
            AudioPresetId = string.Empty
        };

        Assert.True(SlotService.IsLoaded(screen, "racing", string.Empty));
        Assert.False(SlotService.IsLoaded(screen, "flat", string.Empty));
    }

    [Fact]
    public void A_slot_with_neither_half_set_is_not_reported_as_loaded()
    {
        // Otherwise a blank slot would match everything and the tooltip would
        // name the first one in the list regardless of what was pressed.
        HotkeySlot blank = new() { Id = "b", Name = "Empty", Enabled = true };

        Assert.False(SlotService.IsLoaded(blank, "flat", "footstep"));
    }

    [Fact]
    public void The_tray_still_clears_its_system_default_tooltip()
    {
        // The starting text, before any state is known, is the bare name and not
        // the old "Gamer Tool - " prefix.
        Assert.Equal("Gamer Tool", TrayService.Title);
    }

    /// <summary>
    // The tip is composed by the real TrayService, not by a copy of it here.

    private static string FindApp(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "app" }.Concat(parts).ToArray());
    }
}
