using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Proves the test project can see the app's types. Everything else in this
/// project depends on that working, so it is asserted rather than assumed.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Flat_display_preset_is_neutral()
    {
        DisplayPreset flat = DisplayPreset.Flat();

        Assert.Equal("flat", flat.Id);
        Assert.Equal(1.00, flat.Gamma);
        Assert.Equal(0.0, flat.ShadowBoost);
        Assert.Equal(1.00, flat.RedGain);
    }

    [Fact]
    public void Flat_audio_preset_is_silent()
    {
        AudioPreset flat = AudioPreset.Flat();

        Assert.Equal("flat", flat.Id);
        Assert.Equal(0.0, flat.MasterGain);
        Assert.Equal(1.0, flat.FilterQ);
        Assert.All(flat.Bands, band => Assert.Equal(0.0, band));
    }
}
