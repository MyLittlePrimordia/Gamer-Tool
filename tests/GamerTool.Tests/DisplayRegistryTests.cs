using System;
using System.IO;
using System.Text;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The two shared pieces of display-registry knowledge, and the read ceiling.
/// <para>
/// Both were written more than once in the app and both copies were needed: the
/// value decoder because a single-shape test silently discarded the shape the
/// registry actually returns, and the device-id parser because two files each
/// wanted to turn a device id into a key under Enum\DISPLAY. Consolidating them
/// is only worth anything if the consolidated version handles every form the
/// callers actually hand it, which is what these pin down.
/// </para>
/// <para>
/// Nothing here touches the registry. The functions under test are pure.
/// </para>
/// </summary>
public class DisplayRegistryTests
{
    [Theory]
    // The form EnumDisplayDevices hands back with EDD_GET_DEVICE_INTERFACE_NAME.
    [InlineData(@"\\?\DISPLAY#AOCA610#7&272e3773&0&UID264#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "AOCA610")]
    // The form it hands back without that flag, which is what the display list asks for.
    [InlineData(@"MONITOR\AOCA610\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000", "AOCA610")]
    [InlineData(@"MONITOR\DEL4061\{11111111-2222-3333-4444-555555555555}\0001", "DEL4061")]
    public void A_device_id_yields_the_panel_key_whatever_spelling_the_driver_used(
        string deviceId,
        string expected)
    {
        Assert.Equal(expected, DisplayRegistry.HardwareKeyOf(deviceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\?\DISPLAY#")]
    [InlineData("AOCA610")]
    public void Something_that_names_no_panel_yields_no_key(string? deviceId)
    {
        // A bare model code with no path is not a key anything exists under, and
        // guessing one would put the EDID lookup somewhere it can only miss.
        Assert.Equal(string.Empty, DisplayRegistry.HardwareKeyOf(deviceId));
    }

    [Fact]
    public void The_interface_name_spelling_is_not_the_same_as_the_hardware_id_spelling()
    {
        // Both name one panel. This is the case the parser that took everything
        // after the first backslash could not handle: it produced a key starting
        // with "?\DISPLAY#", which is not a key anything exists under. Its only
        // caller never supplied that form, so the difference was never visible -
        // which is exactly what makes it worth a test now that there is one parser.
        string viaInterface = @"\\?\DISPLAY#AOCA610#7&272e3773&0&UID264#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        string viaHardware = @"MONITOR\AOCA610\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000";

        Assert.Equal("AOCA610", DisplayRegistry.HardwareKeyOf(viaInterface));
        Assert.Equal(DisplayRegistry.HardwareKeyOf(viaInterface), DisplayRegistry.HardwareKeyOf(viaHardware));
    }

    [Fact]
    public void The_panel_key_of_still_agrees_with_the_shared_parser()
    {
        // PanelKeyOf is the public name and is kept, because it is the app's
        // answer to "what is this panel called in the registry". It must not drift
        // from the one implementation.
        foreach (string deviceId in new[]
        {
            @"\\?\DISPLAY#AOCA610#7&272e3773&0&UID264#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            @"MONITOR\AOCA610\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000",
            string.Empty,
        })
        {
            Assert.Equal(
                DisplayRegistry.HardwareKeyOf(deviceId),
                HardwareBrightness.PanelKeyOf(deviceId));
        }
    }

    [Fact]
    public void An_edid_arrives_as_either_shape_and_both_are_read()
    {
        byte[] block = new byte[128];
        block[0] = 0x00;
        block[1] = 0xFF;

        object[] boxed = new object[128];
        for (int i = 0; i < 128; i++)
        {
            boxed[i] = block[i];
        }

        Assert.Same(block, DisplayRegistry.EdidBytes(block));
        Assert.Equal(block, DisplayRegistry.EdidBytes(boxed));
    }

    [Fact]
    public void A_non_byte_inside_the_boxed_shape_is_zero_rather_than_a_crash()
    {
        // The registry is not a documented shape and a driver can put anything in
        // there. Zeroing the offender keeps the length right so the EDID parser's
        // own checks still apply, which is where a malformed block belongs.
        object[] boxed = new object[128];
        boxed[0] = "not a byte";
        for (int i = 1; i < 128; i++)
        {
            boxed[i] = (byte)0;
        }

        byte[]? decoded = DisplayRegistry.EdidBytes(boxed);

        Assert.NotNull(decoded);
        Assert.Equal(128, decoded!.Length);
        Assert.Equal(0, decoded[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("EDID")]
    public void Something_that_is_not_an_edid_is_not_one(object? raw)
    {
        Assert.Null(DisplayRegistry.EdidBytes(raw));
    }

    [Fact]
    public void A_block_shorter_than_a_base_edid_is_not_one()
    {
        // Under 128 bytes is not a short EDID, it is some other value that happens
        // to be called EDID. Accepting it means indexing a buffer as a descriptor
        // block that is not there.
        Assert.Null(DisplayRegistry.EdidBytes(new byte[64]));
        Assert.Null(DisplayRegistry.EdidBytes(new object[3]));
        Assert.Null(DisplayRegistry.EdidBytes(Array.Empty<byte>()));
    }

    [Fact]
    public void A_file_over_the_ceiling_is_refused_rather_than_truncated()
    {
        string path = Path.Combine(Path.GetTempPath(), "gamertool-bounded-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, new string('x', 4096));

            // Under the ceiling: read whole.
            Assert.Equal(4096, BoundedRead.AllText(path, ceilingBytes: 8192).Length);

            // Over it: refused. Not returned as the first N characters, because a
            // truncated profile is not a profile missing a value - it parses as
            // something the user never wrote.
            IOException over = Assert.Throws<IOException>(
                () => BoundedRead.AllText(path, ceilingBytes: 1024));
            Assert.Contains("ceiling", over.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_shared_read_succeeds_while_something_else_holds_the_file_open_for_writing()
    {
        // The engine rewrites its status file continuously and the app reads it on
        // a timer, so the read races a writer. File.ReadAllText defaults to
        // FileShare.Read and fails the moment the writer has it open, which is a
        // race the app does not choose the timing of.
        string path = Path.Combine(Path.GetTempPath(), "gamertool-shared-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"a\":1}");

            using FileStream held = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            Assert.Equal("{\"a\":1}", BoundedRead.AllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_utf8_file_with_a_byte_order_mark_reads_without_it()
    {
        // settings.json is written with a BOM by System.Text.Json's default writer
        // on some paths, and a leading U+FEFF in a JSON document is a parse error
        // rather than whitespace to a strict reader.
        string path = Path.Combine(Path.GetTempPath(), "gamertool-bom-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"a\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.Equal("{\"a\":1}", BoundedRead.AllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}