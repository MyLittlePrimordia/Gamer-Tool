using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A slot follows the monitor it was bound to, not the number Windows happens to
/// have given that monitor today.
/// <para>
/// <c>\\.\DISPLAYn</c> is a slot in the adapter list. Undocking a laptop, moving a
/// cable or changing which port a screen is on can renumber it, and a slot keyed
/// on the old name then either stops matching or - the worse case - matches
/// different hardware. The key is the panel's PnP id; these tests pin the order
/// the two are consulted in and what a disagreement means.
/// </para>
/// </summary>
public class MonitorKeyTests
{
    private static MonitorChoice Screen(string device, string key) =>
        new() { Device = device, Key = key, Name = device };

    [Fact]
    public void TheKeyWinsOverTheDeviceName()
    {
        MonitorChoice? match = DisplayService.MatchMonitor(
            new[] { Screen(@"\\.\DISPLAY1", "panel-a"), Screen(@"\\.\DISPLAY2", "panel-b") },
            key: "panel-b",
            device: @"\\.\DISPLAY1");

        Assert.NotNull(match);
        Assert.Equal(@"\\.\DISPLAY2", match!.Device);
    }

    [Fact]
    public void ARenumberedScreenIsStillFoundByItsKey()
    {
        // The same panel, now DISPLAY1. The saved name matches nothing.
        MonitorChoice? match = DisplayService.MatchMonitor(
            new[] { Screen(@"\\.\DISPLAY1", "panel-a") },
            key: "panel-a",
            device: @"\\.\DISPLAY2");

        Assert.NotNull(match);
        Assert.Equal(@"\\.\DISPLAY1", match!.Device);
    }

    [Fact]
    public void ANameThatNowBelongsToAnotherPanelIsNotAMatch()
    {
        // DISPLAY2 is attached, but the panel behind it is not the one the slot
        // was saved against. Applying here would tint hardware nobody chose.
        MonitorChoice? match = DisplayService.MatchMonitor(
            new[] { Screen(@"\\.\DISPLAY2", "panel-b") },
            key: "panel-a",
            device: @"\\.\DISPLAY2");

        Assert.Null(match);
    }

    [Fact]
    public void AProfileWithNoKeyStillResolvesByDeviceName()
    {
        MonitorChoice? match = DisplayService.MatchMonitor(
            new[] { Screen(@"\\.\DISPLAY2", "panel-b") },
            key: string.Empty,
            device: @"\\.\DISPLAY2");

        Assert.NotNull(match);
        Assert.Equal(@"\\.\DISPLAY2", match!.Device);
    }

    [Fact]
    public void AnUnmatchedScreenResolvesToNothingTheEngineCanUse()
    {
        // Not to an empty device, which Apply reads as "every screen", and not to
        // the name, which may be attached to other hardware. The service answers
        // with the key so the existing NO SUCH SCREEN path refuses the write.
        var display = new DisplayService();

        Assert.Equal("panel-a", display.ResolveDevice("panel-a", @"\\.\DISPLAY2"));
    }

    [Fact]
    public void MigrationFillsTheKeyFromTheDeviceName()
    {
        AppSettings settings = new() { Schema = 2 };
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", MonitorDevice = @"\\.\DISPLAY2" });

        settings.Migrate(_ => "panel-b");

        Assert.Equal("panel-b", Assert.Single(settings.Slots).MonitorKey);
    }

    [Fact]
    public void SchemaThreeStillGetsTheKeyStep()
    {
        // Three is the number a removed development build wrote and is still on
        // disk on the machine this was written on. Reusing it would make that
        // profile look current, so the step would never run there.
        AppSettings settings = new() { Schema = 3 };
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", MonitorDevice = @"\\.\DISPLAY2" });

        settings.Migrate(_ => "panel-b");

        Assert.Equal("panel-b", Assert.Single(settings.Slots).MonitorKey);
        Assert.Equal(AppSettings.CurrentSchema, settings.Schema);
    }

    [Fact]
    public void AScreenThatIsNotAttachedIsLeftKeyless()
    {
        // The enumeration falls back to the device name for an absent screen.
        // Storing that would be storing exactly what the key exists to replace.
        AppSettings settings = new() { Schema = 2 };
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", MonitorDevice = @"\\.\DISPLAY9" });

        settings.Migrate(device => device);

        Assert.Equal(string.Empty, Assert.Single(settings.Slots).MonitorKey);
    }

    [Fact]
    public void CopyAndDuplicateBothCarryTheKey()
    {
        HotkeySlot slot = new()
        {
            Id = "s1",
            Name = "One",
            MonitorDevice = @"\\.\DISPLAY2",
            MonitorKey = "panel-b"
        };

        Assert.Equal("panel-b", slot.Copy().MonitorKey);
        Assert.Equal("panel-b", SlotService.Duplicate(slot, 2).MonitorKey);
    }

    [Fact]
    public void OldJsonWithoutTheFieldLoadsWithAnEmptyKey()
    {
        const string json = """
            { "Schema": 2, "Slots": [ { "Id": "s1", "Name": "One", "MonitorDevice": "\\\\.\\DISPLAY1" } ] }
            """;

        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        HotkeySlot slot = Assert.Single(loaded!.Slots);
        Assert.Equal(@"\\.\DISPLAY1", slot.MonitorDevice);
        Assert.Equal(string.Empty, slot.MonitorKey);
    }

    [Fact]
    public void RestoreFollowsTheKeyToTheScreensNewName()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "s1",
            Name = "One",
            AudioPresetId = "footstep",
            MonitorDevice = @"\\.\DISPLAY2",
            MonitorKey = "panel-a"
        });

        RestoreReport report = new();
        new BackupService().Repair(
            settings,
            new[] { Screen(@"\\.\DISPLAY1", "panel-a") },
            Array.Empty<string>(),
            Array.Empty<AppCandidate>(),
            null,
            report);

        HotkeySlot slot = Assert.Single(settings.Slots);
        Assert.Equal(@"\\.\DISPLAY1", slot.MonitorDevice);
        Assert.Equal("panel-a", slot.MonitorKey);
        Assert.Equal(0, report.ScreensReset);
    }

    [Fact]
    public void RestoreClearsASlotWhoseKeyHasGone()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "s1",
            Name = "One",
            AudioPresetId = "footstep",
            MonitorDevice = @"\\.\DISPLAY2",
            MonitorKey = "panel-a"
        });

        RestoreReport report = new();
        new BackupService().Repair(
            settings,
            new[] { Screen(@"\\.\DISPLAY2", "panel-b") },
            Array.Empty<string>(),
            Array.Empty<AppCandidate>(),
            null,
            report);

        HotkeySlot slot = Assert.Single(settings.Slots);
        Assert.Equal(string.Empty, slot.MonitorDevice);
        Assert.Equal(string.Empty, slot.MonitorKey);
        Assert.Equal(1, report.ScreensReset);
    }

    [Fact]
    public void IdenticalPanelsStayDistinct()
    {
        // Two screens of the same model. The scan disambiguates their keys with an
        // ordinal, and a slot bound to the second must not land on the first.
        List<MonitorChoice> monitors = new()
        {
            Screen(@"\\.\DISPLAY1", "MONITOR\\ACME123\\{id}\\0001"),
            Screen(@"\\.\DISPLAY2", "MONITOR\\ACME123\\{id}\\0001#2")
        };

        MonitorChoice? match = DisplayService.MatchMonitor(
            monitors,
            key: "MONITOR\\ACME123\\{id}\\0001#2",
            device: @"\\.\DISPLAY1");

        Assert.NotNull(match);
        Assert.Equal(@"\\.\DISPLAY2", match!.Device);
        Assert.Equal(2, monitors.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
