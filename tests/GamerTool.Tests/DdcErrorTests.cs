using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Naming the DDC/CI failures.
/// <para>
/// An I2C failure is not a Win32 error. The driver reports one as an HRESULT
/// shaped value sitting in the high half of the DWORD, so printing the number
/// gives something like "3221225856", which is a value nobody can look up and
/// every reader has to guess at. The four codes that actually turn up are named
/// here so the log says what went wrong rather than how big a number went wrong.
/// </para>
/// <para>
/// Verified against winerror.h. The mapping is a lookup with no fallback, so an
/// unrecognised code stays hex instead of acquiring a plausible wrong name.
/// </para>
/// </summary>
public class DdcErrorTests
{
    [Theory]
    [InlineData(unchecked((int)0xC0262580u), "I2C_DEVICE_DOES_NOT_EXIST")]
    [InlineData(unchecked((int)0xC0262581u), "I2C_NOT_SUPPORTED")]
    [InlineData(unchecked((int)0xC0262582u), "I2C_ERROR_TRANSMITTING_DATA")]
    [InlineData(unchecked((int)0xC0262583u), "I2C_ERROR_RECEIVING_DATA")]
    public void The_i2c_bus_codes_are_named(int error, string expected)
    {
        Assert.Equal(expected, DdcErrors.Name(error));
    }

    [Theory]
    [InlineData(0x00000087, "ERROR_INVALID_PARAMETER")]
    [InlineData(0x00000006, "ERROR_INVALID_HANDLE")]
    [InlineData(0x000004D9, "ERROR_NOT_FOUND")]
    public void The_common_win32_codes_are_named(int error, string expected)
    {
        Assert.Equal(expected, DdcErrors.Name(error));
    }

    [Fact]
    public void An_unknown_code_gets_no_invented_name()
    {
        // The whole point of the lookup. A wrong name is worse than a hex value,
        // because it stops the next person looking.
        Assert.Null(DdcErrors.Name(unchecked((int)0xC0262599u)));
    }

    [Fact]
    public void Success_is_named_rather_than_shown_as_zero()
    {
        Assert.Equal("SUCCESS", DdcErrors.Name(0));
        Assert.Equal("0x00000000 SUCCESS", DdcErrors.Describe(0));
    }

    [Fact]
    public void A_named_error_carries_both_the_hex_and_the_name()
    {
        string described = DdcErrors.Describe(unchecked((int)0xC0262581u));

        Assert.Contains("0xC0262581", described, StringComparison.Ordinal);
        Assert.Contains("I2C_NOT_SUPPORTED", described, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unnamed_error_is_still_shown_in_hex()
    {
        string described = DdcErrors.Describe(unchecked((int)0xC0262599u));

        Assert.Contains("0xC0262599", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#AOCA610#7&272e3773&0&UID264#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "AOCA610")]
    [InlineData(@"MONITOR\AOCA610\{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}\0001", "AOCA610")]
    [InlineData(@"\\?\DISPLAY#DEL4170#4&2f3a1b9c&0&UID0#{...}", "DEL4170")]
    public void The_registry_panel_key_is_recovered_from_a_device_id(string deviceId, string expected)
    {
        // This is what lets the registry EDID be matched exactly rather than by
        // the name, which is "Generic PnP Monitor" for half the monitors in the
        // world and identical across a monitor that is still plugged in and one
        // that was unplugged weeks ago.
        Assert.Equal(expected, HardwareBrightness.PanelKeyOf(deviceId));
    }

    [Fact]
    public void An_unrecognisable_device_id_yields_no_key()
    {
        Assert.Equal(string.Empty, HardwareBrightness.PanelKeyOf(string.Empty));
        Assert.Equal(string.Empty, HardwareBrightness.PanelKeyOf("nonsense"));
    }
}
