using System;
using System.Collections.Generic;
using System.IO;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The HDR decisions, without a compositor.
/// <para>
/// The hardware half is a delegate, so these tests can make a setter lie, make a
/// screen vanish, or make the user change the setting by hand - the three cases
/// that turn a plausible implementation into one that leaves somebody's monitor
/// in the wrong mode.
/// </para>
/// </summary>
public class HdrTests
{
    [Theory]
    [InlineData(0x01u, false, false, false, false, false, false)] // supported only
    [InlineData(0x02u, true, false, false, false, false, false)]  // active only
    [InlineData(0x08u, false, true, false, false, false, false)]  // limited by policy
    [InlineData(0x10u, false, false, true, false, false, false)]  // HDR supported
    [InlineData(0x20u, false, false, false, true, false, false)]  // HDR user enabled
    [InlineData(0x40u, false, false, false, false, true, false)]  // WCG supported
    [InlineData(0x80u, false, false, false, false, false, true)]  // WCG user enabled
    public void Info2BitsLandInTheirOwnFields(
        uint value,
        bool active,
        bool limited,
        bool hdrSupported,
        bool hdrUserEnabled,
        bool wcgSupported,
        bool wcgUserEnabled)
    {
        AdvancedColorInfo2 info = AdvancedColorInfo2.Decode(value, 0);

        Assert.Equal((value & 0x01) != 0, info.Supported);
        Assert.Equal(active, info.Active);
        Assert.Equal(limited, info.LimitedByPolicy);
        Assert.Equal(hdrSupported, info.HdrSupported);
        Assert.Equal(hdrUserEnabled, info.HdrUserEnabled);
        Assert.Equal(wcgSupported, info.WcgSupported);
        Assert.Equal(wcgUserEnabled, info.WcgUserEnabled);
    }

    [Fact]
    public void BitTwoIsReservedAndMustNotBeReadAsWideColour()
    {
        // The legacy struct uses bit 2 for "wide colour enforced". This struct
        // reserves it. A decoder copied from the old one would turn a reserved
        // bit into an SDR reading on a working HDR panel.
        AdvancedColorInfo2 info = AdvancedColorInfo2.Decode(0x04, 2);

        Assert.True(info.HdrActive);
        Assert.False(info.HdrSupported);
        Assert.False(info.WcgSupported);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    public void OnlyActiveModeTwoIsHdr(uint mode, bool hdrActive)
    {
        AdvancedColorInfo2 info = AdvancedColorInfo2.Decode(0x20, mode);

        Assert.Equal(hdrActive, info.HdrActive);
    }

    [Fact]
    public void AFlipIsAllowedWhenTheStateDiffersAndNothingIsInFlight()
    {
        HdrFlipPlan plan = HdrFlipDecision.Decide(
            current: false,
            wanted: true,
            lastFlipUtc: DateTime.MinValue,
            nowUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            inProgress: false);

        Assert.True(plan.Flip);
        Assert.Empty(plan.Reason);
    }

    [Fact]
    public void AFlipToTheSameStateIsANoOpRatherThanAFailure()
    {
        HdrFlipPlan plan = HdrFlipDecision.Decide(true, true, DateTime.MinValue, DateTime.UtcNow, false);

        Assert.False(plan.Flip);
        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Reason);
    }

    [Fact]
    public void ASecondFlipInsideTheIntervalIsRefused()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        HdrFlipPlan plan = HdrFlipDecision.Decide(false, true, now.AddSeconds(-5), now, false);

        Assert.False(plan.Flip);
        Assert.False(plan.IsNoOp);
        Assert.Contains("moment ago", plan.Reason);
    }

    [Fact]
    public void AFlipAfterTheIntervalIsAllowed()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        HdrFlipPlan plan = HdrFlipDecision.Decide(false, true, now.AddSeconds(-11), now, false);

        Assert.True(plan.Flip);
    }

    [Fact]
    public void AFlipAlreadyInProgressIsRefused()
    {
        HdrFlipPlan plan = HdrFlipDecision.Decide(false, true, DateTime.MinValue, DateTime.UtcNow, true);

        Assert.False(plan.Flip);
        Assert.Contains("already switching", plan.Reason);
    }

    [Fact]
    public void TheRestoreMemoryKeepsTheFirstBeforeAndTheLastAfter()
    {
        HdrRestoreMemory memory = new();

        memory.Remember("panel-a", @"\\.\DISPLAY1", before: false, after: true);
        memory.Remember("panel-a", @"\\.\DISPLAY1", before: true, after: false);

        Assert.True(memory.TryGet("panel-a", out HdrRestoreMemory.Entry entry));
        Assert.False(entry.Before);
        Assert.False(entry.After);
    }

    [Fact]
    public void ANoOpRememberWritesNothing()
    {
        HdrRestoreMemory memory = new();

        memory.Remember("panel-a", @"\\.\DISPLAY1", before: true, after: true);

        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void AHandChangeIsNotRestored()
    {
        HdrRestoreMemory memory = new();
        memory.Remember("panel-a", @"\\.\DISPLAY1", before: false, after: true);

        Assert.False(memory.ShouldRestore("panel-a", current: false, out _));
        Assert.True(memory.ShouldRestore("panel-a", current: true, out HdrRestoreMemory.Entry entry));
        Assert.False(entry.Before);
    }

    [Fact]
    public void AnApplyThatIsNotKeptIsAFailureRatherThanARestoreRecord()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
        fake.SucceedWithoutChanging = true;

        HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        HdrApplyResult result = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On);

        Assert.False(result.Applied);
        Assert.False(result.Skipped);
        Assert.Contains("did not switch", result.Warning);
        Assert.Equal(0, control.Memory.Count);
    }

    [Fact]
    public void AnApplyThatIsKeptIsRememberedWithTheBeforeState()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);

        HdrControl control = fake.Control(now);

        HdrApplyResult result = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On);

        Assert.True(result.Applied);
        Assert.True(control.Memory.TryGet("panel-a", out HdrRestoreMemory.Entry entry));
        Assert.False(entry.Before);
        Assert.True(entry.After);
        Assert.Equal((@"\\.\DISPLAY1", true), fake.Sets[0]);
    }

    [Fact]
    public void ASecondApplyInsideTheIntervalIsRefusedAndNothingIsWritten()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
        HdrControl control = fake.Control(now);

        Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

        HdrApplyResult second = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.Off);

        Assert.True(second.Skipped);
        Assert.Contains("moment ago", second.Warning);
        Assert.Single(fake.Sets);
    }

    [Fact]
    public void AReentrantApplyOnTheSamePanelIsRefused()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);

        HdrControl? control = null;
        HdrApplyResult? reentrant = null;

        fake.OnSet = (device, enable) =>
        {
            reentrant = control!.Apply(device, "panel-a", SlotHdrMode.Off);
            fake.State[device] = Hdr(active: enable, supported: true);
            return HdrSetResult.Success;
        };

        control = fake.Control(DateTime.UtcNow);
        HdrApplyResult first = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On);

        Assert.True(first.Applied);
        Assert.NotNull(reentrant);
        Assert.True(reentrant!.Skipped);
        Assert.Contains("already switching", reentrant.Warning);
    }

    [Fact]
    public void RestorePutsTheBeforeStateBackAndClearsTheRecord()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
        HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

        IReadOnlyList<string> warnings = control.RestoreAll((key, device) => device);

        Assert.Empty(warnings);
        Assert.Equal((@"\\.\DISPLAY1", false), fake.Sets[^1]);
        Assert.Equal(0, control.Memory.Count);
    }

    [Fact]
    public void RestoreLeavesAHandChangedScreenAloneAndForgetsIt()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
        HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

        // The user turned it off themselves after the slot applied it.
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);

        IReadOnlyList<string> warnings = control.RestoreAll((key, device) => device);

        Assert.Single(warnings);
        Assert.Contains("changed by hand", warnings[0]);
        Assert.Equal(0, control.Memory.Count);
        Assert.Single(fake.Sets);
    }

    [Fact]
    public void RestoreDropsAScreenThatIsNoLongerAttached()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
        HdrControl control = fake.Control(DateTime.UtcNow);

        Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

        IReadOnlyList<string> warnings = control.RestoreAll((key, device) => "panel-a");

        Assert.Single(warnings);
        Assert.Contains("not connected", warnings[0]);
        Assert.Equal(0, control.Memory.Count);
    }

    [Fact]
    public void TheLegacyPathIsReportedSoTheUiCanStayAdvisory()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = new HdrReading(true, true, false, false, false, HdrReadSource.Legacy);
        HdrControl control = fake.Control(DateTime.UtcNow);

        HdrApplyResult result = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On);

        Assert.True(result.Applied);
        Assert.True(result.LegacyPath);
    }

    [Fact]
    public void AnUnsupportedScreenIsSkippedRatherThanSwitched()
    {
        FakeHdr fake = new();
        fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: false);
        HdrControl control = fake.Control(DateTime.UtcNow);

        HdrApplyResult result = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On);

        Assert.True(result.Skipped);
        Assert.Contains("does not support", result.Warning);
        Assert.Empty(fake.Sets);
    }

    [Fact]
    public void AScreenPresetIsOnlyEffectiveWhenHdrIsOff()
    {
        Assert.True(ScreenPresetEffective.Applies(hdrActive: false, hasScreenPreset: true));
        Assert.False(ScreenPresetEffective.Applies(hdrActive: true, hasScreenPreset: true));
        Assert.False(ScreenPresetEffective.Applies(hdrActive: false, hasScreenPreset: false));
    }

    [Fact]
    public void TheSlotNoteOnlyAppearsWhenBothAreSet()
    {
        Assert.NotEmpty(ScreenPresetEffective.SlotNote(hdrActive: true, hasScreenPreset: true));
        Assert.Empty(ScreenPresetEffective.SlotNote(hdrActive: false, hasScreenPreset: true));
        Assert.Empty(ScreenPresetEffective.SlotNote(hdrActive: true, hasScreenPreset: false));
    }

    [Fact]
    public void TheDisplayNotesSayWhatTheLimitationIs()
    {
        Assert.Contains("gamma ramps", HdrNotes.HdrOn);
        Assert.Contains("Can't tell", HdrNotes.Unknown);
        Assert.Contains("All screens", HdrNotes.SlotWithoutMonitor);
        Assert.Contains("does not report HDR", HdrNotes.Unsupported);
    }

    [Fact]
    public void AnApplyWritesAJournalEntryWithTheSameBeforeAndAfter()
    {
        string path = TempJournal();

        try
        {
            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            SystemRestoreJournal journal = new(path);
            HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), journal);

            Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

            Assert.Single(journal.Entries);
            Assert.Equal(SystemRestoreKinds.Hdr, journal.Entries[0].Kind);
            Assert.Equal("panel-a", journal.Entries[0].Target);
            Assert.Equal("off", journal.Entries[0].Before);
            Assert.Equal("on", journal.Entries[0].After);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AFailedSecondApplyRestoresTheEarlierJournalEntry()
    {
        string path = TempJournal();

        try
        {
            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            SystemRestoreJournal journal = new(path);
            DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            HdrControl control = new(fake.Read, fake.Set, () => now, journal);

            Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

            now = now.AddSeconds(11);
            fake.SucceedWithoutChanging = true;

            HdrApplyResult second = control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.Off);

            Assert.False(second.Applied);
            Assert.True(journal.TryGet(SystemRestoreKinds.Hdr, "panel-a", out SystemRestoreEntry entry));
            Assert.Equal("off", entry.Before);
            Assert.Equal("on", entry.After);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AVerifiedRestoreRemovesTheJournalEntry()
    {
        string path = TempJournal();

        try
        {
            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            SystemRestoreJournal journal = new(path);
            HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), journal);

            Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);
            control.RestoreAll((key, device) => device);

            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AHandChangeDropsTheJournalEntry()
    {
        string path = TempJournal();

        try
        {
            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            SystemRestoreJournal journal = new(path);
            HdrControl control = fake.Control(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), journal);

            Assert.True(control.Apply(@"\\.\DISPLAY1", "panel-a", SlotHdrMode.On).Applied);

            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            control.RestoreAll((key, device) => device);

            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ARestartRestoresFromTheJournalAndClearsIt()
    {
        string path = TempJournal();

        try
        {
            SystemRestoreJournal journal = new(path);
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "off", "on");

            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: true, supported: true);
            HdrControl control = fake.Control(DateTime.UtcNow, journal);

            HdrJournalOutcome outcome = control.RestoreJournalEntry(
                "panel-a",
                @"\\.\DISPLAY1",
                before: false,
                after: true,
                out string error);

            Assert.Equal(HdrJournalOutcome.Restored, outcome);
            Assert.Empty(error);
            Assert.False(fake.State[@"\\.\DISPLAY1"].Active);
            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AnUnansweredJournalEntryStaysForTheNextLaunch()
    {
        string path = TempJournal();

        try
        {
            SystemRestoreJournal journal = new(path);
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "off", "on");

            FakeHdr fake = new(); // no state: the screen does not answer
            HdrControl control = fake.Control(DateTime.UtcNow, journal);

            HdrJournalOutcome outcome = control.RestoreJournalEntry(
                "panel-a",
                @"\\.\DISPLAY1",
                before: false,
                after: true,
                out _);

            Assert.Equal(HdrJournalOutcome.Failed, outcome);
            Assert.Single(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AJournalEntryTheUserAlreadyChangedIsDropped()
    {
        string path = TempJournal();

        try
        {
            SystemRestoreJournal journal = new(path);
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "off", "on");

            FakeHdr fake = new();
            fake.State[@"\\.\DISPLAY1"] = Hdr(active: false, supported: true);
            HdrControl control = fake.Control(DateTime.UtcNow, journal);

            HdrJournalOutcome outcome = control.RestoreJournalEntry(
                "panel-a",
                @"\\.\DISPLAY1",
                before: false,
                after: true,
                out _);

            Assert.Equal(HdrJournalOutcome.AlreadyDifferent, outcome);
            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void AJournalEntryForADetachedScreenIsDropped()
    {
        string path = TempJournal();

        try
        {
            SystemRestoreJournal journal = new(path);
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "off", "on");

            FakeHdr fake = new();
            HdrControl control = fake.Control(DateTime.UtcNow, journal);

            HdrJournalOutcome outcome = control.RestoreJournalEntry(
                "panel-a",
                "panel-a", // not a \\.\DISPLAYn, so it is not attached
                before: false,
                after: true,
                out _);

            Assert.Equal(HdrJournalOutcome.NotConnected, outcome);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static HdrReading Hdr(bool active, bool supported) =>
        new(true, supported, active, active, false, HdrReadSource.Info2);

    private static string TempJournal() =>
        Path.Combine(Path.GetTempPath(), "GamerToolTests", Guid.NewGuid().ToString("N"), "pending-restore.json");

    private static void Cleanup(string path)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeHdr
    {
        public Dictionary<string, HdrReading> State { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Device, bool Enable)> Sets { get; } = new();

        public bool SucceedWithoutChanging { get; set; }

        public Func<string, bool, HdrSetResult>? OnSet { get; set; }

        public HdrControl Control(DateTime now, SystemRestoreJournal? journal = null) =>
            new(Read, Set, () => now, journal);

        public HdrReading Read(string device) =>
            State.TryGetValue(device, out HdrReading reading) ? reading : HdrReading.Unknown;

        public HdrSetResult Set(string device, bool enable)
        {
            Sets.Add((device, enable));

            if (OnSet is not null)
            {
                return OnSet(device, enable);
            }

            if (!SucceedWithoutChanging)
            {
                HdrReading before = Read(device);
                State[device] = before with
                {
                    Known = true,
                    Supported = true,
                    Active = enable,
                    UserEnabled = enable,
                    Source = before.Source == HdrReadSource.Unknown ? HdrReadSource.Info2 : before.Source,
                };
            }

            return HdrSetResult.Success;
        }
    }
}
