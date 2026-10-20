using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The file that makes a hard kill recoverable. The important rules are
/// first-capture-wins for the original value, last-after for the applied one, and
/// an entry only removed when the caller says the value is back.
/// </summary>
public class SystemRestoreJournalTests
{
    [Fact]
    public void AnEntrySurvivesANewInstance()
    {
        string path = TempPath();

        try
        {
            SystemRestoreJournal first = new(path);
            first.Record(SystemRestoreKinds.AudioPlayback, "Playback", "headset", "speakers");

            SystemRestoreJournal second = new(path);

            Assert.Single(second.Entries);
            Assert.Equal("headset", second.Entries[0].Before);
            Assert.Equal("speakers", second.Entries[0].After);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TheFirstBeforeAndTheLastAfterWin()
    {
        string path = TempPath();

        try
        {
            SystemRestoreJournal journal = new(path);

            journal.Record(SystemRestoreKinds.AudioPlayback, "Playback", "headset", "speakers");
            journal.Record(SystemRestoreKinds.AudioPlayback, "Playback", "speakers", "tv");

            Assert.Single(journal.Entries);
            Assert.Equal("headset", journal.Entries[0].Before);
            Assert.Equal("tv", journal.Entries[0].After);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ALandingBackOnTheOriginalValueLeavesNothingToUndo()
    {
        string path = TempPath();

        try
        {
            SystemRestoreJournal journal = new(path);

            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "off", "on");
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "on", "off");

            // Before and after are both "off", so the entry must go rather than
            // asking the next launch to set the value the user is already on.
            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void RemovePersists()
    {
        string path = TempPath();

        try
        {
            SystemRestoreJournal first = new(path);
            first.Record(SystemRestoreKinds.AudioCapture, "Recording", "mic-a", "mic-b");
            first.Remove(SystemRestoreKinds.AudioCapture, "Recording");

            Assert.Empty(new SystemRestoreJournal(path).Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ACorruptFileLoadsEmptyRatherThanHalfAnEntry()
    {
        string path = TempPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{not json");

            SystemRestoreJournal journal = new(path);

            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void EmptyOrNoOpEntriesAreNotWritten()
    {
        string path = TempPath();

        try
        {
            SystemRestoreJournal journal = new(path);

            journal.Record(string.Empty, "target", "a", "b");
            journal.Record(SystemRestoreKinds.Hdr, string.Empty, "a", "b");
            journal.Record(SystemRestoreKinds.Hdr, "panel-a", "on", "on");

            Assert.Empty(journal.Entries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData(WindowsAudioFlow.Playback, SystemRestoreKinds.AudioPlayback)]
    [InlineData(WindowsAudioFlow.PlaybackCommunications, SystemRestoreKinds.AudioPlaybackCommunications)]
    [InlineData(WindowsAudioFlow.Recording, SystemRestoreKinds.AudioCapture)]
    [InlineData(WindowsAudioFlow.RecordingCommunications, SystemRestoreKinds.AudioCaptureCommunications)]
    public void EveryFlowRoundTripsThroughItsStoredName(WindowsAudioFlow flow, string kind)
    {
        Assert.Equal(kind, SystemRestoreKinds.For(flow));
        Assert.True(SystemRestoreKinds.TryFlow(kind, out WindowsAudioFlow parsed));
        Assert.Equal(flow, parsed);
    }

    [Fact]
    public void AnUnknownKindIsNotGuessedIntoAFlow()
    {
        Assert.False(SystemRestoreKinds.TryFlow("something-else", out _));
    }

    private static string TempPath() =>
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
}
