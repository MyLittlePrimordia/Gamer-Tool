using System;
using System.Collections.Generic;
using System.IO;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Starting a game through the store that owns it.
/// <para>
/// The executable under a Steam or Epic install is a guess - the install folder
/// is searched for the largest file and that is the whole method - so for those
/// two stores Play asks the launcher instead. That is also the one place this app
/// hands a string from the profile to the shell, which is why the policy is a
/// closed list rather than a scheme check, and why every layer that can carry the
/// string checks it again: the picker, the import and the launch itself.
/// </para>
/// </summary>
public class LaunchUriTests
{
    private const string Exe = @"C:\Games\Arena\arena.exe";
    private const string SteamCs = "steam://rungameid/730";
    private const string EpicFortnite = "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true";

    // ---------- the allow list ----------

    [Fact]
    public void The_two_shapes_a_store_link_may_have_are_allowed()
    {
        Assert.True(LaunchUriPolicy.IsAllowed(SteamCs));
        Assert.True(LaunchUriPolicy.IsAllowed(EpicFortnite));
        Assert.True(LaunchUriPolicy.IsAllowed("steam://rungameid/1"));
        Assert.True(LaunchUriPolicy.IsAllowed("com.epicgames.launcher://apps/Fortnite.EGL?action=launch&silent=true"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("steam://rungameid/")]
    [InlineData("steam://rungameid/0")]
    [InlineData("steam://rungameid/0000000000")]
    [InlineData("steam://rungameid/12345678901")]
    [InlineData("steam://rungameid/730 extra")]
    [InlineData("steam://rungameid/730&calc")]
    [InlineData("steam://rungameid/730?x=1")]
    [InlineData("steam://rungameid/../730")]
    [InlineData("steam://run/730")]
    [InlineData("steam://rungameid/730 ")]
    [InlineData(" steam://rungameid/730")]
    [InlineData("STEAM://RUNGAMEID")] 
    [InlineData("ms-msdt:/id")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("steam://rungameid/1 \"&&calc\"")]
    [InlineData("com.epicgames.launcher://apps/?action=launch&silent=true")]
    [InlineData("com.epicgames.launcher://apps/a/b?action=launch&silent=true")]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch")]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=false")]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true&x=1")]
    [InlineData("com.epicgames.launcher://apps/Fortnite%20x?action=launch&silent=true")]
    public void Everything_else_is_refused(string? uri)
    {
        Assert.False(LaunchUriPolicy.IsAllowed(uri));
        Assert.Equal(LaunchVia.None, LaunchUriPolicy.ViaOf(uri));
    }

    [Fact]
    public void A_link_is_built_the_same_way_it_is_read()
    {
        Assert.Equal(SteamCs, LaunchUriPolicy.SteamUri("730"));
        Assert.Equal(SteamCs, LaunchUriPolicy.SteamUri(" 730 "));
        Assert.Equal(string.Empty, LaunchUriPolicy.SteamUri("730x"));
        Assert.Equal(string.Empty, LaunchUriPolicy.SteamUri(null));

        Assert.Equal(EpicFortnite, LaunchUriPolicy.EpicUri("Fortnite"));
        Assert.Equal(string.Empty, LaunchUriPolicy.EpicUri("Fortnite extra"));
        Assert.Equal(string.Empty, LaunchUriPolicy.EpicUri(null));

        // Whatever a scanner builds must survive the check it will face at launch
        // time. A builder that could produce something the policy refuses is a
        // Play that works once and then stops.
        Assert.True(LaunchUriPolicy.IsAllowed(LaunchUriPolicy.SteamUri("228980")));
        Assert.True(LaunchUriPolicy.IsAllowed(LaunchUriPolicy.EpicUri("Fortnite")));
    }

    [Fact]
    public void The_three_ways_to_start_a_game_have_three_names()
    {
        Assert.Equal("via Steam", LaunchUriPolicy.Describe(LaunchVia.Steam));
        Assert.Equal("via Epic", LaunchUriPolicy.Describe(LaunchVia.Epic));
        Assert.Equal("direct", LaunchUriPolicy.Describe(LaunchVia.None));
        Assert.Equal(LaunchVia.Steam, LaunchUriPolicy.ViaOf(SteamCs));
        Assert.Equal(LaunchVia.Epic, LaunchUriPolicy.ViaOf(EpicFortnite));
    }

    // ---------- the scanners ----------

    [Fact]
    public void A_steam_manifest_carries_the_app_id_the_link_needs()
    {
        // The id is in the same AppState section the scanner already reads, and it
        // is the only field in the file that names the game rather than its folder.
        const string manifest = """
            "AppState"
            {
            	"appid"		"228980"
            	"name"		"Half-Life 2"
            	"installdir"		"hl2"
            }
            """;

        Dictionary<string, Dictionary<string, string>> sections = Vdf.ParseSections(manifest);
        Assert.Equal("228980", sections["AppState"]["appid"]);
        Assert.Equal("steam://rungameid/228980", LaunchUriPolicy.SteamUri(sections["AppState"]["appid"]));
    }

    [Fact]
    public void Both_scanners_pass_their_id_to_the_policy()
    {
        // Source rather than behaviour, because a scan reads a registry key and a
        // folder that do not exist on a build machine. What is pinned is the part
        // that was missing before: the manifest's own id, not a guess made from
        // the install folder.
        string library = AppSource("Services/AppLibraryService.cs");

        Assert.Contains("state.TryGetValue(\"appid\", out string? appId)", library, StringComparison.Ordinal);
        Assert.Contains("LaunchUri = LaunchUriPolicy.SteamUri(appId)", library, StringComparison.Ordinal);
        Assert.Contains("LaunchUriPolicy.EpicUri(JsonString(root, \"AppName\"))", library, StringComparison.Ordinal);
    }

    // ---------- the checklist ----------

    private static HotkeySlot Slot() => new()
    {
        Id = "slot_arena",
        Name = "Arena",
        AppExePath = Exe,
        AppName = "Arena",
        DisplayPresetId = "screen_arena",
        AudioPresetId = "sound_arena",
        MonitorKey = "MONITOR\\ABC123",
    };

    private static PlayPreflight Check(HotkeySlot slot, bool targetExists) =>
        PlayPreflight.Build(
            slot,
            targetExists,
            displayExists: true,
            audioExists: true,
            crosshairExists: true,
            audioInstalled: true,
            monitorResolved: true);

    [Fact]
    public void A_store_slot_is_launched_through_the_store()
    {
        HotkeySlot slot = Slot();
        slot.LaunchUri = SteamCs;

        PlayPreflight check = Check(slot, targetExists: false);

        // The guessed executable can be missing - the scan picked the wrong file,
        // or the game validates itself through Steam - and the launcher can still
        // start the game, so the file is not what this launch depends on.
        Assert.True(check.CanLaunch);
        Assert.Empty(check.Warnings);
        Assert.Contains("via Steam", check.Tooltip, StringComparison.Ordinal);

        Assert.Equal(LaunchVia.Steam, PlayPreflight.ViaFor(slot, out string uri));
        Assert.Equal(SteamCs, uri);
    }

    [Fact]
    public void A_link_this_app_would_not_run_falls_back_to_the_file()
    {
        HotkeySlot slot = Slot();
        slot.LaunchUri = "steam://run/730";

        PlayPreflight check = Check(slot, targetExists: true);

        Assert.True(check.CanLaunch);
        Assert.Equal(LaunchVia.None, PlayPreflight.ViaFor(slot, out string uri));
        Assert.Equal(string.Empty, uri);
        Assert.Contains("launcher refused", check.Tooltip, StringComparison.Ordinal);
        Assert.Equal(Exe, PlayPreflight.ConfirmationTarget(slot, uri));
    }

    [Fact]
    public void The_launch_row_says_direct_when_there_is_no_store()
    {
        PlayPreflight check = Check(Slot(), targetExists: true);

        Assert.True(check.CanLaunch);
        Assert.Contains("direct", check.Tooltip, StringComparison.Ordinal);

        // How Play starts a game is not part of what the slot changes about the
        // machine, so it must not appear in the profile summary.
        Assert.Equal("screen + sound", check.Summary);
    }

    [Fact]
    public void A_slot_with_no_target_at_all_still_cannot_launch()
    {
        HotkeySlot slot = Slot();
        slot.AppExePath = null;
        slot.AppName = "Arena";

        PlayPreflight check = Check(slot, targetExists: false);

        Assert.False(check.CanLaunch);
        Assert.Contains("no target", check.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_with_nothing_to_watch_is_not_a_launch_target()
    {
        // Hand-edited profile: a link and no path. Play would start the game and
        // then have no process to notice end, so the slot would never be put back.
        HotkeySlot slot = Slot();
        slot.AppExePath = null;
        slot.AppName = "Arena";
        slot.LaunchUri = SteamCs;

        Assert.Equal(LaunchVia.None, PlayPreflight.ViaFor(slot, out string uri));
        Assert.Equal(string.Empty, uri);
        Assert.False(Check(slot, targetExists: false).CanLaunch);

        // The link itself is left alone: it is the path the user has to restore.
        Assert.Equal(SteamCs, slot.LaunchUri);
    }

    // ---------- the profile ----------

    [Fact]
    public void A_link_only_survives_an_import_when_this_pc_has_that_game()
    {
        string path = TempExe();

        try
        {
            AppSettings settings = new();
            settings.Slots.Add(new HotkeySlot
            {
                Id = "1",
                Name = "Arena",
                AppExePath = path,
                AppName = "arena",
                LaunchUri = SteamCs,
                LaunchConfirmed = true,
            });

            RestoreReport report = new();
            new BackupService().Repair(
                settings,
                Array.Empty<MonitorChoice>(),
                Array.Empty<string>(),
                new List<AppCandidate>
                {
                    new() { Name = "Arena", ExePath = path, ProcessName = "arena", LaunchUri = SteamCs, Source = "STEAM" },
                },
                null,
                Array.Empty<string>(),
                report);

            HotkeySlot slot = Assert.Single(settings.Slots);
            Assert.Equal(SteamCs, slot.LaunchUri);
            Assert.Equal(0, report.SlotsLostLaunch);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_link_to_a_game_this_pc_does_not_have_is_dropped()
    {
        string path = TempExe();

        try
        {
            AppSettings settings = new();
            settings.Slots.Add(new HotkeySlot
            {
                Id = "1",
                Name = "Arena",
                AppExePath = path,
                AppName = "arena",
                LaunchUri = SteamCs,
            });

            RestoreReport report = new();
            new BackupService().Repair(
                settings,
                Array.Empty<MonitorChoice>(),
                Array.Empty<string>(),
                Array.Empty<AppCandidate>(),
                null,
                Array.Empty<string>(),
                report);

            HotkeySlot slot = Assert.Single(settings.Slots);
            Assert.Equal(string.Empty, slot.LaunchUri);
            Assert.Equal(1, report.SlotsLostLaunch);
            Assert.True(report.AnythingToReport);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_planted_link_is_dropped_even_when_it_claims_to_be_installed()
    {
        string path = TempExe();

        try
        {
            const string hostile = "steam://rungameid/730&calc";
            AppSettings settings = new();
            settings.Slots.Add(new HotkeySlot
            {
                Id = "1",
                Name = "Arena",
                AppExePath = path,
                AppName = "arena",
                LaunchUri = hostile,
            });

            RestoreReport report = new();
            new BackupService().Repair(
                settings,
                Array.Empty<MonitorChoice>(),
                Array.Empty<string>(),
                new List<AppCandidate>
                {
                    new() { Name = "Arena", ExePath = path, ProcessName = "arena", LaunchUri = hostile },
                },
                null,
                Array.Empty<string>(),
                report);

            Assert.Equal(string.Empty, Assert.Single(settings.Slots).LaunchUri);
            Assert.Equal(1, report.SlotsLostLaunch);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_link_with_no_executable_to_watch_is_dropped()
    {
        // Auto-apply matches process names and the launch watchdog waits for one,
        // so a slot that can be started but never recognised would change the
        // machine and never put it back.
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "1",
            Name = "Arena",
            AppExePath = null,
            AppName = "Arena",
            LaunchUri = SteamCs,
        });

        RestoreReport report = new();
        new BackupService().Repair(
            settings,
            Array.Empty<MonitorChoice>(),
            Array.Empty<string>(),
            new List<AppCandidate> { new() { Name = "Arena", LaunchUri = SteamCs } },
            null,
            Array.Empty<string>(),
            report);

        Assert.Equal(string.Empty, Assert.Single(settings.Slots).LaunchUri);
        Assert.Equal(1, report.SlotsLostLaunch);
    }

    [Fact]
    public void A_null_link_gets_its_written_form_and_a_copy_does_not_inherit_it()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot { Id = "1", Name = "Arena", LaunchUri = null });

        SlotService.Migrate(settings, brandNew: false);

        Assert.Equal(string.Empty, Assert.Single(settings.Slots).LaunchUri);

        HotkeySlot source = Slot();
        source.LaunchUri = SteamCs;

        // A duplicate has no target, so it has no link either: carrying it over
        // would let the copy's Play start the game it was copied from.
        HotkeySlot copy = SlotService.Duplicate(source, 2);
        Assert.Equal(string.Empty, copy.LaunchUri);
        Assert.Null(copy.AppExePath);
    }

    [Fact]
    public void A_slot_with_only_a_link_still_asks_before_play_runs_it()
    {
        HotkeySlot slot = new() { Id = "1", Name = "Arena", LaunchUri = SteamCs };

        Assert.True(slot.NeedsLaunchConfirmation);

        slot.LaunchConfirmed = true;

        Assert.False(slot.NeedsLaunchConfirmation);
    }

    private static string TempExe()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "GamerToolTests-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");

        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string AppSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", name));
    }
}
