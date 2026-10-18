using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// U-001 version gate + U-002 releases-API parse. Old engines fail silently
/// (ignored flags), so the gate must catch 1.2.9/1.2.10 and pass 1.2.11+.
/// </summary>
public class FxVersionGateTests
{
    [Theory]
    [InlineData("1.2.9", true)]
    [InlineData("1.2.9.0", true)]
    [InlineData("1.2.10", true)]
    [InlineData("1.2.11", false)]
    [InlineData("1.2.12", false)]
    [InlineData("1.2.15.0", false)]
    [InlineData("1.2.16", false)]
    [InlineData("", false)]
    public void IsFxSoundTooOld_GatesCorrectly(string version, bool expected)
    {
        Assert.Equal(expected, SetupService.IsFxSoundTooOld(version));
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v1.2.16\"}", "1.2.16")]
    [InlineData("{\"tag_name\":\"1.2.15.0\"}", "1.2.15.0")]
    [InlineData("{\"tag_name\":\"v1.2.17-beta\"}", "1.2.17")]
    [InlineData("{\"tag_name\":\"latest\"}", "")]
    [InlineData("{}", "")]
    [InlineData("not json", "")]
    public void ParseReleaseTag_ExtractsVersion(string json, string expected)
    {
        Assert.Equal(expected, SetupService.ParseReleaseTag(json));
    }
}
