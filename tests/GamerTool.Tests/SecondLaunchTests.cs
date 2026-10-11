using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Starting the app when it is already running.
/// <para>
/// This app lives in the tray, so the second launch is the normal way a user gets
/// it back - and it used to be a dead end: five seconds of waiting, then a modal
/// saying "already running" that tells the user something they already know and
/// does nothing about where the window is. The running copy is the one that owns
/// the hotkeys, the ramp and the state, so the second copy's whole job is to ask
/// it to come forward and then get out of the way.
/// </para>
/// </summary>
public class SecondLaunchTests
{
    [Fact]
    public void TheShowMeMessageHasAnId()
    {
        // Registered, not a constant this app chose. Windows hands back an id or
        // zero, and zero means the registration failed - which would leave the
        // first copy silently deaf and the feature quietly dead.
        Assert.NotEqual(0u, WindowMessage.ShowMe);
    }

    [Fact]
    public void TheShowMeMessageIsTheSameEveryTimeItIsAskedFor()
    {
        // Two processes have to agree on this number without shipping it between
        // them. If it moved between reads, the second copy would be talking to a
        // message the first one is not listening for.
        uint first = WindowMessage.ShowMe;
        uint second = WindowMessage.ShowMe;

        Assert.Equal(first, second);
    }

    [Fact]
    public void OnlyTheShowMeMessageMeansShowMe()
    {
        Assert.True(WindowMessage.IsShowMe(unchecked((int)WindowMessage.ShowMe)));

        // And nothing else does. A display change is still a display change, and a
        // handler that answered "show myself" to a WM_DEVICECHANGE would bring the
        // app to the front every time a mouse moved.
        Assert.False(WindowMessage.IsShowMe(WindowMessage.WmDisplayChange));
        Assert.False(WindowMessage.IsShowMe(WindowMessage.WmDeviceChange));
        Assert.False(WindowMessage.IsShowMe(0));
    }

    [Fact]
    public void ASecondLaunchFindsTheRunningWindow()
    {
        // The message reaches a window that is listening and is harmless to one
        // that is not, which is what makes HWND_BROADCAST the right target: the
        // app's own window answers, and every other window on the desktop ignores
        // an id it has never registered.
        Assert.True(WindowMessage.BroadcastShowMe());
    }

    [Fact]
    public void TheSecondCopyAsksAndLeavesRatherThanShowingADialog()
    {
        // An architecture guard on App's single-instance branch, which cannot be
        // driven from a test: reaching it means launching a second copy of a WPF
        // app and watching a modal appear.
        //
        // Both halves matter. Without the broadcast the feature does not exist and
        // the second launch is a no-op the user cannot tell from a crash. Without
        // the shutdown, two copies run: two sets of hotkeys, two of everything.
        // And the dialog has to stay gone - putting it back would put back the dead
        // end this whole item is about.
        string app = File.ReadAllText(Path.Combine(AppRoot(), "app", "App.xaml.cs"));

        int branch = app.IndexOf("if (!claimed)", StringComparison.Ordinal);
        Assert.True(branch >= 0, "the single-instance branch has moved or gone");

        int end = app.IndexOf("MessageBox.Show", branch, StringComparison.Ordinal);

        string body = end >= 0 && end - branch < 900
            ? app[branch..end]
            : app[branch..Math.Min(app.Length, branch + 900)];

        Assert.Contains("WindowMessage.BroadcastShowMe()", body, StringComparison.Ordinal);
        Assert.Contains("Shutdown()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ALREADY RUNNING", body, StringComparison.Ordinal);

        // The wait that gates all of this. Five seconds was the old value, and the
        // most noticeable thing about a second launch was that long of nothing
        // happening before the user was told what they already knew.
        Assert.Contains(
            "WaitOne(TimeSpan.FromMilliseconds(1500))",
            app,
            StringComparison.Ordinal);
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}