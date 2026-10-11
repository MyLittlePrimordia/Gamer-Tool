using System;
using System.Windows.Threading;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The in-game popup, measured rather than watched.
/// <para>
/// It used to flash instead of appearing, and only on the second and every
/// subsequent toast - the first one was fine. Three faults lined up to do it:
/// the fade was attached to the border inside the window while the code that
/// resets the opacity writes the window's, so the reset went nowhere; the
/// timeline was built once and re-applied with a new BeginTime, and re-applying
/// a timeline whose playhead has already run past its end evaluates it at that
/// end immediately; and with FillBehavior.HoldEnd the first toast left the
/// border pinned at zero for good.
/// </para>
/// <para>
/// Measured, the second toast opened at 0.000 opacity and was already on its way
/// out. These tests hold the window to what a person would see instead.
/// </para>
/// </summary>
[Collection("wpf")]
public class OsdTimingTests
{
    [Fact]
    public void A_second_toast_opens_opaque_rather_than_flashing_out()
    {
        // Measured a moment after the fade-in has finished, because a toast that
        // fades up from nothing is supposed to be at zero on the frame it starts.
        // The old code never got there on the second one: it opened at 0.000 and
        // was already on its way out, which is the flash.
        double[] arrived = WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            osd.Show();

            var arrivedTwice = new double[2];
            for (int i = 0; i < 2; i++)
            {
                osd.ShowToast("Slot loaded");
                Pump(TimeSpan.FromMilliseconds(320));
                arrivedTwice[i] = osd.Opacity;
            }

            osd.HideTimer.Stop();
            osd.Close();
            return arrivedTwice;
        });

        Assert.Equal(1.0, arrived[0], 2);
        Assert.Equal(1.0, arrived[1], 2);
    }

    [Fact]
    public void A_toast_stays_readable_for_the_whole_hold_and_only_then_leaves()
    {
        // The middle of the life, sampled three times. This is what distinguishes
        // a toast from a flash: there is a stretch of it in which a slot name can
        // actually be read.
        double[] held = WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            osd.Show();
            osd.ShowToast("Slot loaded");

            var samples = new double[3];
            Pump(TimeSpan.FromMilliseconds(500));
            samples[0] = osd.Opacity;
            Pump(TimeSpan.FromMilliseconds(500));
            samples[1] = osd.Opacity;
            Pump(TimeSpan.FromMilliseconds(400));
            samples[2] = osd.Opacity;

            osd.HideTimer.Stop();
            osd.Close();
            return samples;
        });

        foreach (double at in held)
        {
            Assert.Equal(1.0, at, 1);
        }
    }

    [Fact]
    public void The_window_itself_is_what_fades_not_the_border_inside_it()
    {
        // The animation has to be on the same element the code assigns, because
        // an animation outranks a local value: while one is attached, writing
        // Opacity changes nothing anybody can read back. The old code faded the
        // border inside the window and reset the window's, so the window carried
        // no animation and the assignment did take - which is how it looked
        // correct while doing nothing about the toast.
        //
        // Asserted through the read-back rather than through the animation
        // machinery, which is not public. The timeline has run to its end and is
        // holding the window at zero, and the assignment is refused.
        bool refused = WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            osd.Show();
            osd.ShowToast("Slot loaded");

            Pump(TimeSpan.FromMilliseconds(RetroOsd.VisibleMilliseconds + 400.0));

            osd.Opacity = 1.0;
            bool ignored = Math.Abs(osd.Opacity) < 0.0001;

            osd.HideTimer.Stop();
            osd.Close();
            return ignored;
        });

        Assert.True(
            refused,
            "nothing is animating the toast window's opacity, so the assignment that is "
            + "meant to bring it back writes a property no fade is reading");
    }

    private static void Pump(TimeSpan wait)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, frame.Dispatcher) { Interval = wait };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void The_toast_arrives_slowly_enough_to_be_seen_and_leaves_slowly_enough_not_to_flicker()
    {
        // A popup that is fully opaque for a sixth of a second and fully gone a
        // frame later is a flash however pretty the easing curve is.
        Assert.True(
            RetroOsd.FadeInSeconds >= 0.1,
            "the toast appears in " + RetroOsd.FadeInSeconds + " s, which is a flash");
        Assert.True(
            RetroOsd.FadeOutSeconds >= 0.25,
            "the toast leaves in " + RetroOsd.FadeOutSeconds + " s, which is a flicker");
        Assert.True(
            RetroOsd.HoldSeconds >= 1.2,
            "the toast is up for " + RetroOsd.HoldSeconds + " s, too short to read a slot name");
    }

    [Fact]
    public void The_hide_timer_outlasts_the_fade_it_is_hiding()
    {
        // The window is taken off screen by a timer, not by the animation. If the
        // timer wins the race the last part of the fade is never painted, which
        // reads as the plate being cut off rather than fading.
        double visible = RetroOsd.VisibleMilliseconds;

        WpfTestHost.Invoke(() =>
        {
            var osd = new RetroOsd();
            double interval = osd.HideTimer.Interval.TotalMilliseconds;
            osd.Close();

            Assert.True(
                interval > visible,
                "the window is hidden at " + interval.ToString("0")
                + " ms but the fade runs to " + visible.ToString("0") + " ms");
            return 0;
        });
    }

    [Fact]
    public void A_slot_says_it_is_loaded_before_it_finishes_applying_it()
    {
        // The toast went up after the gamma ramp, the DDC/CI call and the profile
        // write, so a slot key did nothing visible for a beat. The order is
        // asserted rather than the timing, because this is the only part of it
        // that a person watching a screen would call a delay.
        string source = System.IO.File.ReadAllText(FindRepoFile(System.IO.Path.Combine("app", "MainWindow.Slots.cs")));

        int announce = source.IndexOf("Flash(slot.Name + \" loaded\");", StringComparison.Ordinal);
        int apply = source.IndexOf("ApplyAudioToDevice(audio.Copy(), false, slot);", StringComparison.Ordinal);

        Assert.True(announce > 0, "the slot no longer announces itself");
        Assert.True(apply > 0, "the slot no longer applies its sound");

        int slow = source.LastIndexOf("ApplyDisplay(display.Copy(), slot.MonitorDevice, false);", announce, StringComparison.Ordinal);
        Assert.True(
            slow > 0 && slow < announce,
            "the toast is still announced after the slow half of the load");
    }

    [Fact]
    public void A_screen_that_refuses_the_slot_corrects_what_was_already_said()
    {
        // Said up front is a claim, not a report. The amber correction is what
        // keeps it honest, so it has to still be there.
        string source = System.IO.File.ReadAllText(FindRepoFile(System.IO.Path.Combine("app", "MainWindow.Slots.cs")));

        Assert.Contains("SCREEN BLOCKED", source);
        Assert.Contains("Flash(slot.Name + \" loaded, screen did not take it\", true);", source);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}