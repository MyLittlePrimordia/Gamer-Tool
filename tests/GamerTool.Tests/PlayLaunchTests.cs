using System;
using System.Collections.Generic;
using System.IO;
using GamerTool.Models;
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

        // A direct start gets the direct timeout and a launcher gets the long one,
        // so which one was used has to reach the watchdog rather than be assumed.
        Assert.Contains("via == LaunchVia.None ? PlayLaunchKind.Direct : PlayLaunchKind.Launcher", body, StringComparison.Ordinal);

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
        // breaks the games that load files relative to their own folder. The one
        // string that may be passed as arguments is the slot's own, normalised by
        // its owner before it gets here - nothing on this path builds a command
        // line out of a profile of its own.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private static bool TryStartGame(string exePath, string arguments, out string error)");

        Assert.Contains("UseShellExecute = true", body, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = Path.GetDirectoryName(exePath)", body, StringComparison.Ordinal);
        Assert.Contains("Arguments = arguments", body, StringComparison.Ordinal);

        // The launcher path still takes none: it starts a URI, and the launcher owns
        // the command line of the game it starts.
        Assert.DoesNotContain(
            "Arguments",
            Body(slots, "private static bool TryStartLauncher(string uri, out string error)"),
            StringComparison.Ordinal);
    }

    // ---------- the untrusted path ----------

    [Fact]
    public void A_path_this_pc_never_chose_has_to_be_confirmed()
    {
        HotkeySlot slot = new() { Id = "1", Name = "Game", AppExePath = @"C:\Games\Game\game.exe" };

        Assert.False(slot.LaunchConfirmed);
        Assert.True(slot.NeedsLaunchConfirmation);

        slot.LaunchConfirmed = true;

        Assert.False(slot.NeedsLaunchConfirmation);
    }

    [Fact]
    public void The_targets_play_refuses_to_start_never_ask()
    {
        // Play only launches an exact executable. A folder, the wildcard and the
        // app's own window are refused before a launch is attempted, so asking
        // about a path there would be asking about nothing.
        Assert.False(new HotkeySlot { Id = "1", IsAnyGameTarget = true }.NeedsLaunchConfirmation);
        Assert.False(new HotkeySlot { Id = "1", IsSelfTarget = true }.NeedsLaunchConfirmation);
        Assert.False(new HotkeySlot { Id = "1", TargetFolder = @"C:\Games\Somewhere" }.NeedsLaunchConfirmation);
        Assert.False(new HotkeySlot { Id = "1" }.NeedsLaunchConfirmation);
    }

    [Fact]
    public void An_import_drops_the_confirmation_even_for_a_path_that_is_here()
    {
        // The path survives if it exists - it may be the same installation - but the
        // fact that somebody on this PC approved it does not. That is the whole
        // difference between a profile and a file somebody sent.
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(path, string.Empty);

        try
        {
            AppSettings settings = new();
            settings.Slots.Add(new HotkeySlot
            {
                Id = "1",
                Name = "Game",
                AppExePath = path,
                AppName = "game",
                LaunchConfirmed = true
            });

            new BackupService().Repair(
                settings,
                Array.Empty<MonitorChoice>(),
                Array.Empty<string>(),
                Array.Empty<AppCandidate>(),
                null,
                Array.Empty<string>(),
                new RestoreReport());

            HotkeySlot slot = Assert.Single(settings.Slots);
            Assert.Equal(path, slot.AppExePath);
            Assert.False(slot.LaunchConfirmed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Play_asks_before_it_applies_anything()
    {
        // Asked before the apply, not after it: a slot applied for a launch the user
        // then cancels would have to be reverted for no reason. And the answer is
        // saved before the launch, so a launch that fails does not ask twice.
        string slots = AppSource("MainWindow.Slots.cs");
        string body = Body(slots, "private async Task PlaySlotByIdAsync(string slotId)");

        int ask = body.IndexOf("slot.NeedsLaunchConfirmation", StringComparison.Ordinal);
        int flight = body.IndexOf("_playInFlight = true;", StringComparison.Ordinal);

        Assert.True(ask >= 0, "Play no longer asks about an unconfirmed path");
        Assert.True(flight > ask, "the confirmation runs after the launch has already been claimed");

        string confirm = Body(slots, "private void ShowLaunchConfirm(HotkeySlot slot, string target)");

        Assert.Contains("Text = target", confirm, StringComparison.Ordinal);
        Assert.Contains("slot.LaunchConfirmed = true;", confirm, StringComparison.Ordinal);
        Assert.Contains("_ = PlaySlotByIdAsync(slot.Id);", confirm, StringComparison.Ordinal);
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
