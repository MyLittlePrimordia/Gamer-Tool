using GamerTool;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Whether the night filter has to be put back after the screen is stood down.
/// <para>
/// The bug this exists for: StandDownScreen calls _display.Reset(), which restores
/// the ORIGINAL gamma ramp rather than a neutral one, so it took the warm trim with
/// it. _nightApplied was left true, so EvaluateNightSchedule's equality check
/// believed the filter was still on and never re-pushed it. Quitting a game at ten
/// at night silently cancelled the evening filter until seven in the morning.
/// </para>
/// <para>
/// Pure decision rather than a window test, because the interesting part is which
/// of three callers behaves differently - and a test of that would need a display,
/// a timer and a profile to be standing up for what is one boolean expression.
/// </para>
/// </summary>
public class NightStandDownTests
{
    private static bool Reassert(bool nightApplied, bool suppressed, bool emergency) =>
        MainWindow.ShouldReassertNightAfterStandDown(nightApplied, suppressed, emergency);

    [Fact]
    public void TheFilterComesBackAfterAnOrdinaryStandDown()
    {
        // The bug. A game closed, a slot toggled off, a wildcard lost fullscreen:
        // all three are the app tidying up, and the evening filter is still what the
        // user asked for.
        Assert.True(Reassert(nightApplied: true, suppressed: false, emergency: false));
    }

    [Fact]
    public void TheFilterDoesNotComeBackAfterThePanicKey()
    {
        // The one deliberate difference. The panic key exists for a screen that is
        // already wrong and a user who cannot reach the window, so the schedule loses
        // to "I need to see what is happening".
        Assert.False(Reassert(nightApplied: true, suppressed: false, emergency: true));
    }

    [Fact]
    public void NothingToRestoreWhenTheFilterWasNotOn()
    {
        // Otherwise a stand-down at two in the afternoon would put a tint nobody
        // asked for onto the screen.
        Assert.False(Reassert(nightApplied: false, suppressed: false, emergency: false));
        Assert.False(Reassert(nightApplied: false, suppressed: false, emergency: true));
    }

    [Fact]
    public void NothingToRestoreWhenTheUserAlreadyTurnedItDown()
    {
        // A level picked by hand during the window suppresses the schedule until the
        // next boundary. A stand-down must not undo that decision - the user already
        // said what they wanted instead.
        Assert.False(Reassert(nightApplied: true, suppressed: true, emergency: false));
        Assert.False(Reassert(nightApplied: true, suppressed: true, emergency: true));
    }

    [Fact]
    public void OnlyThePanicKeyDiffersFromAnOrdinaryStandDown()
    {
        // Every combination, because the failure mode here is a new caller passing
        // the wrong default and nothing complaining. A stand-down path that forgot
        // the emergency argument would silently start cancelling the evening filter
        // again, which is the bug being fixed here rather than a new one.
        for (int applied = 0; applied <= 1; applied++)
        {
            for (int suppressed = 0; suppressed <= 1; suppressed++)
            {
                bool ordinary = Reassert(applied == 1, suppressed == 1, emergency: false);
                bool panic = Reassert(applied == 1, suppressed == 1, emergency: true);

                if (applied == 1 && suppressed == 0)
                {
                    Assert.True(ordinary);
                    Assert.False(panic);
                }
                else
                {
                    Assert.Equal(ordinary, panic);
                }
            }
        }
    }
}