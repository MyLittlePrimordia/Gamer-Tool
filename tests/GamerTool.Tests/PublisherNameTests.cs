using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>Who counts as FxSound when the installer's signature is read.</summary>
public class PublisherNameTests
{
    [Theory]
    [InlineData("FxSound", true)]
    [InlineData("FxSound, LLC", true)]     // what a certificate carries
    [InlineData("FxSound LLC", true)]
    [InlineData("fxsound, llc", true)]
    [InlineData("FxSound Impostor Ltd", false)]
    [InlineData("Not FxSound, LLC", false)]
    [InlineData("FxSounds", false)]
    [InlineData("LLC", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Publisher_names_compare_on_the_normalised_form(string? signer, bool expected)
        => Assert.Equal(expected, SetupService.SamePublisher(signer, SetupService.PublisherHint));
}
