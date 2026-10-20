using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What happens when a Play click applies a slot and the game never starts.
/// <para>
/// Play changes the screen, the Windows audio routes and HDR before it launches,
/// and every other path that undoes those changes is driven by the game exiting.
/// A launch that produced no process has no exit, so this is the one failure mode
/// that would leave a user's machine changed with nothing watching it. The
/// decision is a pure function here; the wiring is asserted by source, the way
/// the other window-only recovery paths are.
/// </para>
/// </summary>
public class PlayLaunchTests
{
    // ---------- the decision ----------

    [Fact]
    public void A_launch_is_still_waiting_before_its_time_is_up()
    {
        Assert.Equal(
            PlayLaunchWatchAction.Wait,
            PlayLaunchWatch.Decide(TimeSpan.Zero, processSeen: false, PlayLaunchKind.Direct));

        Assert.Equal(
            PlayLaunchWatchAction.Wait,
            PlayLaunchWatch.Decide(TimeSpan.FromSeconds(89), processSeen: false, PlayLaunchKind.Direct));
    }

    [Fact]
    public void A_direct_launch_gives_up_at_its_timeout()
    {
        Assert.Equal(
            PlayLaunchWatchAction.GiveUp,
            PlayLaunchWatch.Decide(
                TimeSpan.FromSeconds(PlayLaunchWatch.DirectTimeoutSeconds),
                processSeen: false,
                PlayLaunchKind.Direct));

        Assert.Equal(
            PlayLaunchWatchAction.GiveUp,
            PlayLaunchWatch.Decide(TimeSpan.FromMinutes(30), processSeen: false, PlayLaunchKind.Direct));
    }

    [Fact]
    public void A_launcher_gets_much_longer_than_a_direct_launch()
    {
        // Steam and Epic will spend minutes updating or signing in before the game
        // exists. Standing a working slot down for that would be worse than the
        // wait, so the two kinds cannot share one timeout.
        Assert.Equal(
            PlayLaunchWatchAction.Wait,
            PlayLaunchWatch.Decide(TimeSpan.FromMinutes(5), processSeen: false, PlayLaunchKind.Launcher));

        Assert.Equal(
            PlayLaunchWatchAction.GiveUp,
            PlayLaunchWatch.Decide(
                TimeSpan.FromSeconds(PlayLaunchWatch.LauncherTimeoutSeconds),
                processSeen: false,
                PlayLaunchKind.Launcher));

        Assert.True(
            PlayLaunchWatch.TimeoutFor(PlayLaunchKind.Launcher) > PlayLaunchWatch.TimeoutFor(PlayLaunchKind.Direct));
    }

    [Fact]
    public void A_process_that_appears_late_is_still_a_success()
    {
        // Asked first, so the question is "did this ever start" rather than "was it
        // fast enough". A game that arrives exactly on the boundary is running, and
        // standing its slot down there would undo a launch that worked.
        Assert.Equal(
            PlayLaunchWatchAction.Started,
            PlayLaunchWatch.Decide(TimeSpan.FromHours(1), processSeen: true, PlayLaunchKind.Direct));

        Assert.Equal(
            PlayLaunchWatchAction.Started,
            PlayLaunchWatch.Decide(TimeSpan.Zero, processSeen: true, PlayLaunchKind.Launcher));
    }

    [Fact]
    public void The_give_up_line_names_the_game_and_says_what_happened()
    {
        Assert.Equal("Elden Ring didn't start, settings put back", PlayLaunchWatch.GiveUpMessage("Elden Ring"));

        // A slot with no name is possible from a hand-edited profile, and an OSD
        // line that begins with a space reads as a rendering bug.
        Assert.Equal("The game didn't start, settings put back", PlayLaunchWatch.GiveUpMessage(string.Empty));
        Assert.Equal("The game didn't start, settings put back", PlayLaunchWatch.GiveUpMessage("   "));
    }

    // ---------- the wiring ----------

    [Fact]
    public void A_launch_that_throws_puts_the_slot_back_instead_of_clearing_the_claim()
    {
        // This used to empty the auto ids and stop. The applied screen, routes and
        // HDR stayed exactly where the slot had put them, with nothing left to
        // revert them, because the revert is driven by the game exiting.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private async Task PlaySlotWithLaunchAsync(HotkeySlot slot)");

        Assert.Contains("RevertFailedPlay(slot, \"start failed\", failure)", body, StringComparison.Ordinal);
        Assert.Contains("ArmPlayWatchdog(slot, processName, PlayLaunchKind.Direct)", body, StringComparison.Ordinal);

        // The old behaviour, named so it cannot come back.
        Assert.DoesNotContain("Profile loaded, game did not start", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Putting_a_failed_play_back_takes_the_same_three_steps_as_an_exit()
    {
        // Screen first, because the stand-down is what releases the claim; then the
        // user's own preset; then the sound, which restores the Windows routes on
        // its way through. A different order would revert half of it.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private void RevertFailedPlay(HotkeySlot slot, string reason, string detail)");

        int down = body.IndexOf("GoScreenStandDown();", StringComparison.Ordinal);
        int restore = body.IndexOf("RevertToPreGame(slot);", StringComparison.Ordinal);
        int sound = body.IndexOf("_ = GoSoundNeutralAsync();", StringComparison.Ordinal);

        Assert.True(down >= 0, "a failed play no longer stands the screen down");
        Assert.True(restore > down, "the pre-game restore runs before the claim is released");
        Assert.True(sound > restore, "the sound is reset before the user's own preset is put back");

        // Guarded, like every other revert: a slot already stood down has nothing
        // left that is ours to undo.
        Assert.Contains("string.Equals(_autoSlotId, slot.Id", body, StringComparison.Ordinal);
        Assert.Contains("CancelPlayWatch();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_watchdog_stops_itself_when_the_claim_is_gone()
    {
        // A stand-down, the slot key, the panic key, quitting and another Play all
        // change the claim. The tick has to notice, or a watchdog for a launch that
        // was already dealt with would revert whatever came next.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private void OnPlayWatchTick(object? sender, EventArgs e)");

        Assert.Contains("if (_quitting)", body, StringComparison.Ordinal);
        Assert.Contains("string.Equals(_autoSlotId, _playWatchSlotId", body, StringComparison.Ordinal);
        Assert.Contains("string.Equals(_autoProcess, _playWatchProcess", body, StringComparison.Ordinal);
        Assert.Contains("RevertFailedPlay(slot, \"did not start\", string.Empty)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_game_is_started_the_way_a_game_expects_to_be_started()
    {
        // UseShellExecute false makes a manifest that asks for administrator fail
        // with 740 instead of showing the UAC prompt, and no working directory
        // breaks the games that load files relative to their own folder. No
        // arguments at all: Play launches the program the user bound, nothing else.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private static bool TryStartGame(string exePath, out string error)");

        Assert.Contains("UseShellExecute = true", body, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = Path.GetDirectoryName(exePath)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", body, StringComparison.Ordinal);
    }

    private static string AppSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", name));
    }

    /// <summary>The body of a method, from its signature to the brace that closes it.</summary>
    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "could not find " + signature);

        int open = source.IndexOf('{', at);
        Assert.True(open > 0);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(open, i - open);
                }
            }
        }

        Assert.Fail("the body of " + signature + " is not closed");
        return string.Empty;
    }
}
