using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Finding out whether AMD's HDCP setting is saved somewhere the driver reads.
/// <para>
/// The classification is the whole point of the file and it is pure, so it is
/// pinned here rather than only observable on the one machine that has the
/// problem. Getting it wrong in either direction is costly: calling a correctly
/// placed setting "legacy" sends somebody to edit a registry key that was never
/// the issue, and calling a misplaced one "fine" leaves them restarting their
/// computer for the fourth time.
/// </para>
/// </summary>
public class AmdProtectionOverrideTests
{
    private static ProtectionOverrideReading LegacyOnly() => new()
    {
        State = ProtectionOverrideState.LegacyOnly,
        AdapterKey = "0000",
        LegacyPath = @"DAL2_DATA__2_0\DisplayPath_8\EDID_E305_A610\Option",
        CurrentPath = @"DAL3_DATA\common\EDID_58117_42512_AG276QZD2_8",
        Value = new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }
    };

    [Fact]
    public void An_override_only_in_the_legacy_tree_is_reported_as_such()
    {
        // The exact shape of the machine this was written for: saved, and saved
        // where the driver does not look.
        Assert.Equal(
            ProtectionOverrideState.LegacyOnly,
            AmdProtectionOverride.Classify(legacyFound: true, currentFound: false, currentKeyPresent: true));
    }

    [Fact]
    public void An_override_in_the_current_tree_is_reported_as_current()
    {
        Assert.Equal(
            ProtectionOverrideState.Current,
            AmdProtectionOverride.Classify(legacyFound: true, currentFound: true, currentKeyPresent: true));
    }

    [Fact]
    public void An_override_the_driver_uses_is_current_even_with_no_legacy_copy()
    {
        Assert.Equal(
            ProtectionOverrideState.Current,
            AmdProtectionOverride.Classify(legacyFound: false, currentFound: true, currentKeyPresent: true));
    }

    [Fact]
    public void No_override_at_all_is_none()
    {
        Assert.Equal(
            ProtectionOverrideState.None,
            AmdProtectionOverride.Classify(legacyFound: false, currentFound: false, currentKeyPresent: true));
    }

    [Fact]
    public void A_legacy_override_with_no_current_key_is_not_called_legacy_only()
    {
        // The careful one. Without a current per-display key there is nowhere for
        // the driver to read an override from at all, so the absence of the value
        // there is not evidence of anything. Reporting LegacyOnly here would be
        // accusing a driver of ignoring a setting on the strength of a key that
        // does not exist.
        Assert.Equal(
            ProtectionOverrideState.Unknown,
            AmdProtectionOverride.Classify(legacyFound: true, currentFound: false, currentKeyPresent: false));
    }

    [Fact]
    public void No_keys_at_all_is_unknown_rather_than_none()
    {
        // A non-AMD machine. "None" would claim the driver is on its default,
        // which is a statement about an AMD driver on a machine that has none.
        Assert.Equal(
            ProtectionOverrideState.Unknown,
            AmdProtectionOverride.Classify(legacyFound: false, currentFound: false, currentKeyPresent: false));
    }

    [Fact]
    public void The_current_key_wins_over_the_legacy_one_when_both_are_present()
    {
        Assert.Equal(
            ProtectionOverrideState.Current,
            AmdProtectionOverride.Classify(legacyFound: true, currentFound: true, currentKeyPresent: false));
    }

    // Three tests that used to sit here drove LegacyValuePath, a helper that built
    // the registry path as a string and had no caller. The real walk does not
    // concatenate paths at all: it opens each level with OpenSubKey, so a path
    // that does not exist fails visibly rather than reading a key one level too
    // high. That left the tests asserting a shape the app never uses, which is
    // worse than no test - the EDID-prefix guard they were nominally about is
    // real, and it lives in EdidKeys, which is what the walk calls.

    [Fact]
    public void The_summary_says_the_setting_is_saved_in_the_wrong_place()
    {
        // This is the line that goes into a bug report, so the words that matter
        // are the ones that distinguish "not set" from "set somewhere useless".
        string summary = LegacyOnly().Summary;

        Assert.Contains("legacy", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not read", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1,0,0,0,1,0,0,0", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raw_bytes_are_in_the_summary_because_amd_does_not_document_them()
    {
        // Community scripts set this value to disable protection, and that is not
        // documentation. A report saying what the bytes actually were is worth more
        // than one asserting what they mean.
        Assert.Contains(
            "1,0,0,0,1,0,0,0",
            new ProtectionOverrideReading
            {
                State = ProtectionOverrideState.Current,
                Value = new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }
            }.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_reading_says_it_could_not_be_read()
    {
        // The default is what a machine with no AMD card gets, and it must not
        // claim the setting is unset.
        Assert.Equal("could not be read", new ProtectionOverrideReading().Summary);
    }

    /// <summary>
    /// Every shape the real walk hits, including the two that are not about AMD at
    /// all. These are the bugs the walk actually had: it aborted on a denied
    /// subkey, it asked the adapter for the display paths instead of the tree they
    /// live under, and it returned whichever adapter it reached first.
    /// </summary>
    [Theory]
    [InlineData("0000", true)]
    [InlineData("0001", true)]
    [InlineData("0012", true)]
    [InlineData("000", false)]
    [InlineData("00000", false)]
    [InlineData("Properties", false)]
    [InlineData("Configuration", false)]
    [InlineData("", false)]
    [InlineData("00A0", false)]
    public void Only_the_numbered_adapter_slots_are_walked(string name, bool expected)
    {
        // The display class root also holds Properties and Configuration. Opening
        // Properties throws SecurityException, and a walk that does not filter
        // first dies there and reports "could not be read" on a machine whose
        // answer is sitting in 0000.
        Assert.Equal(expected, AmdProtectionOverride.IsAdapterKey(name));
    }

    [Fact]
    public void Reading_this_machine_finds_the_override_where_it_actually_is()
    {
        // The one test that depends on the hardware, and it is here because the
        // bug it guards is invisible on any other machine: the registry walk found
        // nothing at all on the very machine the file was written for, and every
        // pure test still passed. Asserted only when the machine really does have
        // the shape, so it stays honest on a build agent.
        ProtectionOverrideReading reading = AmdProtectionOverride.Read();

        if (reading.State != ProtectionOverrideState.LegacyOnly)
        {
            return;
        }

        Assert.Equal("0000", reading.AdapterKey);
        Assert.Contains(@"DAL2_DATA__2_0\DisplayPath_8", reading.LegacyPath, StringComparison.Ordinal);
        Assert.Contains("EDID_E305_A610", reading.LegacyPath, StringComparison.Ordinal);
        Assert.StartsWith(@"DAL3_DATA\common\EDID_", reading.CurrentPath, StringComparison.Ordinal);
        Assert.NotNull(reading.Value);
    }

    [Fact]
    public void Reading_never_throws_and_never_claims_more_than_it_knows()
    {
        // Called from inside a probe, so it has to be safe on any machine. The
        // assertion is deliberately loose about which state comes back, because
        // the answer depends on the hardware: what must hold is that it returns
        // and that it is not claiming a misplaced setting.
        ProtectionOverrideReading reading = AmdProtectionOverride.Read();

        Assert.NotNull(reading);
        Assert.NotEqual(string.Empty, reading.Summary);
    }
}
