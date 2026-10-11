using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The EDID decoder, driven from synthetic blocks.
/// <para>
/// None of this can be tested against a monitor, and all of it went wrong in
/// silence: the manufacturer and the panel size were right, so a display decoded
/// as plausible, and the two fields nobody looks at, the year and the serial,
/// were being built out of bytes that hold the EDID revision and the low half of
/// the product code. A real AOC AG276QZD2 with a manufacture year of 2025 and a
/// serial of 1175 was being reported as year 41746 and serial 4, and the year
/// was then filtered out of the summary as implausible, so the wrong answer was
/// invisible as well as wrong.
/// </para>
/// <para>
/// The block builder below writes the same layout the real thing has, so a
/// regression in the offsets shows up here rather than on somebody's desk.
/// </para>
/// </summary>
public class EdidParsingTests
{
    /// <summary>
    /// A 128 byte block with a valid header, the manufacturer packed as three
    /// five bit letters, and everything else settable.
    /// </summary>
    private static byte[] Block(string manufacturer = "AOC", ushort product = 0xA610)
    {
        byte[] edid = new byte[128];

        edid[0] = 0x00;
        for (int i = 1; i < 7; i++)
        {
            edid[i] = 0xFF;
        }

        edid[7] = 0x00;

        int packed = 0;
        for (int i = 0; i < 3; i++)
        {
            packed |= (char.ToUpperInvariant(manufacturer[i]) - 'A' + 1) << (10 - (i * 5));
        }

        edid[8] = (byte)(packed >> 8);
        edid[9] = (byte)packed;
        edid[10] = (byte)(product & 0xFF);
        edid[11] = (byte)(product >> 8);

        // A believable 27 inch panel, so the size check stays out of the way.
        edid[21] = 59;
        edid[22] = 33;

        return edid;
    }

    [Fact]
    public void The_manufacturer_decodes_as_three_letters()
    {
        EdidReading reading = HardwareBrightness.ParseEdid(Block("AOC"), "test");

        Assert.Equal("AOC", reading.Manufacturer);
    }

    [Fact]
    public void The_product_code_is_little_endian()
    {
        // 0xA610 stored low byte first is 0x10 then 0xA6. Reading it big endian
        // gives 0x10A6, which is a different panel.
        EdidReading reading = HardwareBrightness.ParseEdid(Block(product: 0xA610), "test");

        Assert.Equal((ushort)0xA610, reading.ProductCode);
    }

    [Fact]
    public void The_year_is_the_byte_after_the_manufacture_week()
    {
        byte[] edid = Block();
        edid[16] = 27;
        edid[17] = 35;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(2025, reading.Year);
        Assert.Equal(27, reading.Week);
    }

    [Fact]
    public void The_year_is_not_built_from_the_revision_or_the_product_code()
    {
        // The exact trap. These three bytes are what the old decoder used, and
        // with a real 1.4 revision and a real product code they produce a large
        // nonsense number rather than an obviously empty one.
        byte[] edid = Block(product: 0xA610);
        edid[18] = 1;
        edid[19] = 4;
        edid[17] = 35;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(2025, reading.Year);
    }

    [Fact]
    public void A_week_of_0xFF_is_the_model_year_flag_and_not_a_week()
    {
        byte[] edid = Block();
        edid[16] = 0xFF;
        edid[17] = 30;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        // The year is still real and still reported.
        Assert.Equal(2020, reading.Year);

        // 0xFF is not week 255, and reporting it as one is its own kind of lie.
        Assert.Null(reading.Week);
    }

    [Fact]
    public void An_impossible_week_is_dropped_rather_than_reported()
    {
        byte[] edid = Block();
        edid[16] = 99;
        edid[17] = 30;

        Assert.Null(HardwareBrightness.ParseEdid(edid, "test").Week);
    }

    [Fact]
    public void The_serial_is_all_four_little_endian_bytes()
    {
        // 1175 is 0x97 0x04, so the block holds 0x97 then 0x04. Reading only the
        // top two bytes, as this used to, gives 4.
        byte[] edid = Block();
        edid[12] = 0x97;
        edid[13] = 0x04;
        edid[14] = 0x00;
        edid[15] = 0x00;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(1175, reading.Serial);
    }

    [Fact]
    public void A_serial_that_uses_the_high_bytes_still_round_trips()
    {
        // 0xDEADBEEF, which a two byte read would turn into 0.
        byte[] edid = Block();
        edid[12] = 0xEF;
        edid[13] = 0xBE;
        edid[14] = 0xAD;
        edid[15] = 0xDE;

        Assert.Equal(unchecked((int)0xDEADBEEF), HardwareBrightness.ParseEdid(edid, "test").Serial);
    }

    [Fact]
    public void The_serial_is_never_in_the_summary()
    {
        // It is an identifier and this text gets pasted into public issues.
        byte[] edid = Block();
        edid[12] = 0xEF;
        edid[13] = 0xBE;
        edid[14] = 0xAD;
        edid[15] = 0xDE;

        string summary = HardwareBrightness.ParseEdid(edid, "AG276QZD2").Summary;

        Assert.DoesNotContain("3735928559", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_header_makes_the_block_suspicious()
    {
        byte[] edid = Block();
        edid[1] = 0x00;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(EdidVerdict.Suspicious, reading.Verdict);
        Assert.Contains("bad header", reading.Reasons);
    }

    [Fact]
    public void An_all_zero_block_is_unreadable_rather_than_merely_odd()
    {
        EdidReading reading = HardwareBrightness.ParseEdid(new byte[128], "test");

        Assert.Equal(EdidVerdict.Unreadable, reading.Verdict);
    }

    [Fact]
    public void A_block_shorter_than_128_bytes_is_unreadable()
    {
        Assert.Equal(
            EdidVerdict.Unreadable,
            HardwareBrightness.ParseEdid(new byte[64], "test").Verdict);
    }

    [Fact]
    public void A_manufacturer_that_is_not_letters_is_suspicious()
    {
        // Code 0 is reserved and reads as a space, which is how a panel with a
        // blank manufacturer field gets caught.
        byte[] edid = Block();
        edid[8] = 0x00;
        edid[9] = 0x00;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(EdidVerdict.Suspicious, reading.Verdict);
        Assert.Contains(reading.Reasons, r => r.Contains("not letters", StringComparison.Ordinal));
    }

    [Fact]
    public void An_absurdly_small_panel_is_suspicious()
    {
        byte[] edid = Block();
        edid[21] = 1;
        edid[22] = 1;
        edid[17] = 30;

        Assert.Equal(EdidVerdict.Suspicious, HardwareBrightness.ParseEdid(edid, "test").Verdict);
    }

    [Fact]
    public void A_missing_size_is_tolerated_rather_than_treated_as_suspicious()
    {
        // No size at all is not evidence of a dangerous panel, only of a panel
        // that did not fill the field in. Blocking on it would refuse hardware
        // that is otherwise fine.
        byte[] edid = Block();
        edid[21] = 0;
        edid[22] = 0;
        edid[17] = 30;

        Assert.Equal(EdidVerdict.Plausible, HardwareBrightness.ParseEdid(edid, "test").Verdict);
    }

    [Fact]
    public void The_largest_size_two_bytes_can_hold_still_reads_as_believable()
    {
        // Worth pinning down because it is a real gap rather than an oversight in
        // the test. The size check rejects anything over 150 inches, and the
        // biggest diagonal two bytes can express is 255cm by 255cm, which is
        // about 142 inches. So the upper bound can never fire and the check is
        // only ever doing its lower job. The bound is left exactly as it was,
        // because the pre-flight conditions are not to be loosened on the
        // strength of a theory; this test records the shape of the limit so the
        // next person to look at it knows which half of it is live.
        byte[] edid = Block();
        edid[21] = 255;
        edid[22] = 255;
        edid[17] = 30;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal(EdidVerdict.Plausible, reading.Verdict);
        Assert.Equal(142, Math.Round(reading.DiagonalInches!.Value));
    }

    [Fact]
    public void A_healthy_block_is_plausible_and_says_so()
    {
        byte[] edid = Block();
        edid[16] = 27;
        edid[17] = 35;
        edid[12] = 0x97;
        edid[13] = 0x04;

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "AG276QZD2");

        Assert.Equal(EdidVerdict.Plausible, reading.Verdict);
        Assert.Equal(2025, reading.PlausibleYear);
        Assert.Contains("AOC", reading.Summary, StringComparison.Ordinal);
        Assert.Contains("A610", reading.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2025", reading.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_header_of_a_real_aoc_block_parses()
    {
        // The first 32 bytes of the EDID this machine actually has, so the
        // manufacturer packing is checked against a value nobody typed in.
        byte[] edid = Block();
        byte[] real = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x05, 0xE3, 0x10, 0xA6, 0x97, 0x04, 0x00, 0x00 };
        Array.Copy(real, edid, real.Length);

        EdidReading reading = HardwareBrightness.ParseEdid(edid, "test");

        Assert.Equal("AOC", reading.Manufacturer);
        Assert.Equal((ushort)0xA610, reading.ProductCode);
        Assert.Equal(1175, reading.Serial);
    }

    [Fact]
    public void An_edid_read_from_the_windows_cache_is_flagged_as_such()
    {
        // What the diagnosis leans on. A missing DDC/CI handle on its own is
        // ambiguous; a driver that also will not name the monitor is not, and this
        // is the property that carries that.
        //
        // This asserted true for "none" as well, on the grounds that an unprobed
        // display should be treated as the case worth investigating. That was
        // arranged by the predicate being `!= "driver"`, which was true of every
        // display on every machine because the live EDID request could never be
        // satisfied - so it was not a safe default, it was a constant. A display
        // with no EDID anywhere is now distinguishable from one Windows had a
        // cached block for, which is what a support log actually needs.
        Assert.True(new MonitorProbe { EdidSource = "registry" }.EdidWithheld);
        Assert.False(new MonitorProbe { EdidSource = "none" }.EdidWithheld);
    }

    [Fact]
    public void An_edid_the_driver_supplied_is_not_flagged_as_cached()
    {
        Assert.False(new MonitorProbe { EdidSource = "driver" }.EdidWithheld);
    }

    [Fact]
    public void An_unprobed_display_is_not_claimed_to_have_a_cached_edid()
    {
        // The default is "none", and it now says so rather than asserting a cache
        // hit that has not happened. The flag only ever added corroboration to a
        // verdict reached by other means, so losing it on an unprobed display costs
        // nothing; claiming it would have cost the log its credibility.
        Assert.False(new MonitorProbe().EdidWithheld);
    }
}
