using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>The exit-restore commands for the user's own FxSound preset.</summary>
public class RestoreCommandTests
{
    [Theory]
    [InlineData("General", "--preset=\"General\"")]
    [InlineData("Bass Boost", "--preset=\"Bass Boost\"")]
    [InlineData("Name\\", "--preset=\"Name\"")]                          // trailing backslash dropped
    [InlineData("Evil\" --power=0", "--preset=\"Evil --power=0\"")]     // quote stripped: stays ONE argument
    public void A_preset_name_is_always_one_quoted_argument(string name, string expected)
        => Assert.Equal(expected, AudioService.PresetArgument(name));

    [Fact]
    public void Restore_selects_the_preset_before_powering_on()
    {
        var commands = EmergencyReset.BuildRestoreCommands(new FxSnapshot(true, "Bass Boost", string.Empty));
        Assert.Equal(new[] { "--preset=\"Bass Boost\"", "--power=1" }, commands);
    }
}
