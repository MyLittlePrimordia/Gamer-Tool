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
        // A floor rather than the exact figure. The number itself is a tuning
        // decision that moves whenever the cost of a poll changes, and a test that
        // pins it just gets updated instead of saying anything. What has to hold is
        // that hidden is slow enough to be the point - below this it is barely
        // different from the visible rate, and the whole reason for the split
        // disappears.
        Assert.True(
            MainWindow.FxPollInterval(onScreen: false) >= TimeSpan.FromSeconds(10),
            "hidden polling is not slow enough to be worth slowing down");
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
        // the lie in. This is a ceiling rather than the exact figure for the same
        // reason the floor above is not.
        Assert.True(
            MainWindow.FxPollInterval(onScreen: false) <= TimeSpan.FromMinutes(1));
    }
}