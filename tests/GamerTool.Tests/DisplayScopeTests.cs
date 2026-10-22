using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The Display tab's saved screen scope, checked against what is attached.
/// <para>
/// The failure this guards against is a device name being reused: a profile saved
/// on a desk with two screens must not go on applying to "the second one" after
/// the user has moved to a laptop, because on that machine the name may be the
/// built in panel or nothing at all. Falling back to every screen is the honest
/// answer; picking a different one is not.
/// </para>
/// </summary>
public class DisplayScopeTests
{
    private static readonly string[] Attached = { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };

    [Fact]
    public void Empty_means_every_screen()
    {
        Assert.Equal(string.Empty, DisplayScope.Resolve(string.Empty, Attached));
        Assert.Equal(string.Empty, DisplayScope.Resolve(null, Attached));
        Assert.Equal(string.Empty, DisplayScope.Resolve("   ", Attached));
    }

    [Fact]
    public void A_saved_screen_that_is_still_here_is_kept()
    {
        Assert.Equal(@"\\.\DISPLAY2", DisplayScope.Resolve(@"\\.\DISPLAY2", Attached));
    }

    [Fact]
    public void A_saved_screen_that_is_gone_falls_back_to_all()
    {
        Assert.Equal(string.Empty, DisplayScope.Resolve(@"\\.\DISPLAY3", Attached));
    }

    [Fact]
    public void Matching_ignores_case_because_the_device_name_does()
    {
        Assert.Equal(@"\\.\DISPLAY2", DisplayScope.Resolve(@"\\.\display2", Attached));
    }
}
