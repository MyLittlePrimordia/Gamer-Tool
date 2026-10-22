using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What a slot's Play hands the game beyond the executable.
/// <para>
/// The text typed here travels to another process, so it is normalised on the way in
/// and again on the way out: a pasted line break is a space, a tab is a space, and a
/// field left alone is empty rather than whitespace. The dialog is driven for real
/// here, because the field is made in code and nothing in the markup would catch a
/// Save that wrote the wrong string.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class SlotLaunchArgumentsTests
{
    // ------------------------------------------------------------ normalising

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("-dx12", "-dx12")]
    [InlineData("  -dx12  ", "-dx12")]
    [InlineData("-dx12\r\n-skipintro", "-dx12  -skipintro")]
    [InlineData("-name\t\"Player One\"", "-name \"Player One\"")]
    public void Arguments_arrive_as_one_trimmed_line(string? raw, string expected)
    {
        Assert.Equal(expected, LaunchArguments.Normalise(raw));
    }

    [Fact]
    public void A_long_paste_is_cut_to_the_field_it_came_from()
    {
        string raw = new('-', LaunchArguments.Limit + 40);
        string clean = LaunchArguments.Normalise(raw);

        Assert.Equal(LaunchArguments.Limit, clean.Length);

        // And a cut never leaves the trailing space that the cut itself made, which
        // would hand the game an empty argument.
        string trailing = new string('a', LaunchArguments.Limit - 1) + " tail";
        Assert.Equal(LaunchArguments.Limit - 1, LaunchArguments.Normalise(trailing).Length);
    }

    // -------------------------------------------------------------- the model

    [Fact]
    public void A_duplicated_slot_keeps_its_arguments()
    {
        HotkeySlot slot = new()
        {
            Id = "slot-1",
            Name = "Game",
            AppExePath = @"C:\Games\game.exe",
            LaunchArguments = "-dx12",
        };

        HotkeySlot copy = slot.Copy();

        Assert.Equal("-dx12", copy.LaunchArguments);
        Assert.Equal(slot.AppExePath, copy.AppExePath);
    }

    [Fact]
    public void The_arguments_survive_a_save_and_a_load()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gamertool-args-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            ProfileManager profiles = new(folder);
            AppSettings settings = profiles.Defaults();
            settings.Slots.Add(new HotkeySlot
            {
                Id = "slot-1",
                Name = "Game",
                AppExePath = @"C:\Games\game.exe",
                LaunchArguments = "-windowed -dx12",
            });

            profiles.Save(settings);

            AppSettings loaded = new ProfileManager(folder).Load();
            HotkeySlot back = loaded.Slots.Single(s => s.Id == "slot-1");

            Assert.Equal("-windowed -dx12", back.LaunchArguments);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------- the dialog

    [Fact]
    public void The_slot_dialog_saves_what_was_typed_as_one_line()
    {
        WindowHarness.Run(
            MainWindowTabs.Slots,
            h =>
            {
                HotkeySlot slot = h.Settings.Slots.Single(s => s.Id == "slot-args");

                h.Invoke("ShowLaunchOptionsDialog", slot);

                TextBox field = h.Read<Panel>("_modalCustom")
                    .Children.OfType<Panel>()
                    .SelectMany(p => p.Children.OfType<TextBox>())
                    .Single();

                Assert.Equal("-dx12", field.Text);

                field.Text = "  -dx12 \r\n -skipintro  ";
                WindowHarness.Click(h.Read<Button>("_modalSaveButton"));

                // Saved as the clipped, single line the game will actually be handed:
                // the space, the line break and the space after it are three spaces'
                // worth of arguments the game ignores, and trimming the ends is what
                // keeps an empty field empty.
                Assert.Equal("-dx12    -skipintro", slot.LaunchArguments);

                // And emptied rather than kept as spaces, because a slot with no
                // arguments must start the game exactly as it did before the field
                // existed.
                h.Invoke("ShowLaunchOptionsDialog", slot);
                TextBox again = h.Read<Panel>("_modalCustom")
                    .Children.OfType<Panel>()
                    .SelectMany(p => p.Children.OfType<TextBox>())
                    .Single();
                again.Text = "   ";
                WindowHarness.Click(h.Read<Button>("_modalSaveButton"));

                Assert.Equal(string.Empty, slot.LaunchArguments);
            },
            setup => setup.Settings.Slots.Add(new HotkeySlot
            {
                Id = "slot-args",
                Name = "Args",
                AppExePath = @"C:\Games\game.exe",
                LaunchArguments = "-dx12",
            }));
    }
}
