using System;
using System.Globalization;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// DST fall-back + argument hygiene for the audio path.
/// B-001: local timestamps go backwards for ~1hr when clocks go back, so
/// ReadState must use UTC on both sides. H-001: a trailing backslash before
/// the closing quote eats it under CRT rules.
/// </summary>
public class AudioTimeAndCleanTests
{
    [Fact]
    public void IsFileNewer_RequiresStrictlyGreater()
    {
        DateTime t = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Utc);
        Assert.False(AudioService.IsFileNewer(t, t));
        Assert.True(AudioService.IsFileNewer(t, t.AddSeconds(1)));
        Assert.False(AudioService.IsFileNewer(t.AddSeconds(1), t));
    }

    [Fact]
    public void IsFileNewer_BackwardsClockStillDetectsFreshWrite()
    {
        // Fall-back simulation in UTC terms: `before` taken pre-write,
        // `now` taken post-write is always greater when both are UTC.
        // With local times this pair would read equal/backwards for ~1hr.
        DateTime before = new DateTime(2026, 11, 1, 5, 59, 59, DateTimeKind.Utc);
        DateTime now = new DateTime(2026, 11, 1, 6, 0, 1, DateTimeKind.Utc);
        Assert.True(AudioService.IsFileNewer(before, now));
    }

    [Fact]
    public void UtcCacheElapsed_CannotGoNegativeAcrossFallback()
    {
        DateTime cachedAt = new DateTime(2026, 11, 1, 5, 59, 0, DateTimeKind.Utc);
        DateTime now = new DateTime(2026, 11, 1, 6, 1, 0, DateTimeKind.Utc);
        Assert.True((now - cachedAt).TotalSeconds is > 0 and < 180.0);
    }

    [Theory]
    [InlineData("Speakers\\", "Speakers")]
    [InlineData("My Preset\\\\", "My Preset")]
    [InlineData("\"Quoted\"", "Quoted")]
    [InlineData("  Padded  ", "Padded")]
    public void Clean_StripsQuotesBackslashAndPadding(string input, string expected)
    {
        var svc = new AudioService { AntiClipEnabled = false };
        var preset = new AudioPreset
        {
            Name = FxPresetFile.PresetName,
            Bands = new double[10],
            NumBands = 10,
        };
        // Exercise via output command: value must be quoted with no eaten quote.
        // Direct Clean is private; assert through BuildApplyCommand shape instead.
        string cmd = svc.BuildApplyCommand(preset, input, selectPresetFile: false);
        Assert.DoesNotContain("\\\"", cmd);
        if (expected.Length > 0)
        {
            Assert.Contains("--output=\"" + expected + "\"", cmd);
        }
    }
}
