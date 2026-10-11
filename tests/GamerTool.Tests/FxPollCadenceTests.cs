using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The FxSound status poll, and why its rate depends on whether anyone is
/// looking.
/// <para>
/// The tick is not a file read. It spawns <c>FxSound.exe --status</c>, a process
/// per tick, every tick, for as long as FxSound is installed. One and a half
/// seconds is right for the status line on the Settings tab, which is the only
/// thing reading it and which the user is looking at when they read it. This app
/// spends most of its life in the tray while a game runs, and at that rate that
/// is about forty process launches a minute for nothing.
/// </para>
/// </summary>
public class FxPollCadenceTests
{
    [Fact]
    public void OnScreenItPollsFastEnoughForTheStatusLine()
    {
        // The line has to feel live, which is the whole reason for the short
        // interval. Anything slower and switching EQ on inside FxSound's own
        // window reads "EQ SWITCHED OFF" until something else forces a refresh.
        Assert.Equal(TimeSpan.FromMilliseconds(1500), MainWindow.FxPollInterval(onScreen: true));
    }

    [Fact]
    public void HiddenItSlowsToAHeartbeat()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), MainWindow.FxPollInterval(onScreen: false));
    }

    [Fact]
    public void HiddenIsStrictlySlowerThanOnScreen()
    {
        // Stated as a relationship rather than as the two numbers again, because
        // this is the property the change is about: a single constant that happens
        // to be thirty seconds still passes the two tests above.
        Assert.True(
            MainWindow.FxPollInterval(onScreen: false) > MainWindow.FxPollInterval(onScreen: true),
            "hiding the window did not slow the poll down");
    }

    [Fact]
    public void TheSlowRateIsStillFrequentEnoughToBeUseful()
    {
        // A minute of staleness in the tray tooltip is a lie the user cannot see
        // the lie in. Thirty seconds keeps it honest while still being a
        // twentieth of the work.
        Assert.True(
            MainWindow.FxPollInterval(onScreen: false) <= TimeSpan.FromSeconds(30));
    }
}