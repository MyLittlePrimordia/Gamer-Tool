using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// B-002: 1200x790 fixed window must scale to fit small / scaled displays.
/// Work areas in DIPs. 1920x1080@150% = 1280x720 DIP (work ~1232x680);
/// 1366x768@100% work ~1366x728; 1280x720@125% = 1024x576 DIP.
/// </summary>
public class WindowFitTests
{
    [Theory]
    // Fits: no scaling.
    [InlineData(1920, 1040, 1.0)]
    [InlineData(2560, 1390, 1.0)]
    [InlineData(1200, 790, 1.0)]
    // Audit configs: must shrink.
    [InlineData(1366, 728, 0.9215)] // 1366x768@100% work area
    [InlineData(1232, 680, 0.8608)] // 1920x1080@150% work area
    [InlineData(1024, 536, 0.6785)] // 1280x720@125% work area
    public void ScaleFor_MatchesExpected(double w, double h, double expected)
    {
        Assert.Equal(expected, WindowFit.ScaleFor(w, h), 3);
    }

    [Fact]
    public void ScaleFor_NeverBelowMinimum()
    {
        Assert.Equal(WindowFit.MinScale, WindowFit.ScaleFor(320, 200));
    }

    [Fact]
    public void ScaleFor_InvalidInputStaysFull()
    {
        Assert.Equal(1.0, WindowFit.ScaleFor(0, 0));
        Assert.Equal(1.0, WindowFit.ScaleFor(-10, 500));
    }
}
