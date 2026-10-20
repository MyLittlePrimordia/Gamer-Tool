using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The sizes Windows validates, measured on the production structs rather than
/// on a restatement of them.
/// <para>
/// The legacy struct was once four bytes short and every advanced-colour query
/// failed with ERROR_INVALID_PARAMETER. A test that restates the layout can drift
/// with the production copy; these properties read the real thing.
/// </para>
/// </summary>
public class HdrLayoutTests
{
    [Fact]
    public void TheDeviceInfoHeaderIsTwentyBytes() =>
        Assert.Equal(20, HardwareBrightness.DeviceInfoHeaderSize);

    [Fact]
    public void TheInfo2StructIsThirtySixBytes() =>
        Assert.Equal(36, HardwareBrightness.AdvancedColorInfo2Size);

    [Fact]
    public void TheHdrSwitchIsTwentyFourBytes() =>
        Assert.Equal(24, HardwareBrightness.SetHdrStateSize);

    [Fact]
    public void TheLegacySwitchIsTwentyFourBytes() =>
        Assert.Equal(24, HardwareBrightness.SetAdvancedColorStateSize);
}
