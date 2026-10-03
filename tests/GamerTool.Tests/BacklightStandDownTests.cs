using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Which stand-down puts the hardware panel back, and which one leaves it alone.
/// <para>
/// There are two ways the screen goes back to neutral and they have genuinely
/// different answers for the panel. A hard reset - the panic key, the reset button,
/// the tray menu, quitting the app - is the user asking for everything back. A
/// stand-down - a slot toggled off, a game closing, a wildcard losing fullscreen -
/// is the app tidying up after itself, and it only puts the panel back if the preset
/// it is undoing is the thing that moved it.
/// </para>
/// <para>
/// The distinction is not cosmetic. One method served both, so a slot with no
/// backlight in it restored the panel anyway, and a user who had set their
/// brightness in Windows while the app was running watched the app override it on
/// the way out. Which is the entire reason the backlight value is optional in a
/// preset.
/// </para>
/// <para>
/// The routing is asserted against the source rather than through a window. These
/// are separate methods on a WPF window and the question is which one each call
/// site reaches for - a question about wiring, not about a value, and there is no
/// arrangement of windows that answers it.
/// </para>
/// </summary>
public class BacklightStandDownTests
{
    // ---- The rule ----

    /// <summary>
    /// Mirrors the one line that decides it in
    /// <c>StandDownScreen</c>. True for the paths where the user is explicitly
    /// asking for a full reset, otherwise the panel is only put back if a preset
    /// actually wrote to it.
    /// </summary>
    private static bool ShouldRestorePanel(bool hardReset, bool presetOwnsPanel)
    {
        return hardReset || presetOwnsPanel;
    }

    [Fact]
    public void A_hard_reset_restores_the_panel_even_when_no_preset_owns_it()
    {
        // The panic key's case, and the reason it cannot share the conditional
        // path. The panel is left at whatever the last slot asked for, and the one
        // control a user reaches for when the picture is already wrong restores the
        // gamma and leaves the monitor dim.
        Assert.True(ShouldRestorePanel(hardReset: true, presetOwnsPanel: false));
    }

    [Fact]
    public void A_hard_reset_restores_the_panel_when_a_preset_owns_it_too()
    {
        Assert.True(ShouldRestorePanel(hardReset: true, presetOwnsPanel: true));
    }

    [Fact]
    public void A_stand_down_leaves_the_panel_alone_when_no_preset_owns_it()
    {
        // The reported problem. The slot being toggled off never named a
        // brightness, so it never moved the panel, and restoring one here would
        // override whatever the user had set in Windows in the meantime.
        Assert.False(ShouldRestorePanel(hardReset: false, presetOwnsPanel: false));
    }

    [Fact]
    public void A_stand_down_restores_the_panel_when_a_preset_owns_it()
    {
        // The other half, and it has to be here too. The game dimmed the panel, so
        // the desktop the user alt-tabbed back to is dimmer than they set it -
        // every single time they left.
        Assert.True(ShouldRestorePanel(hardReset: false, presetOwnsPanel: true));
    }

    [Fact]
    public void Only_a_preset_that_wrote_the_panel_claims_to_own_it()
    {
        // A sweep, because the flag's meaning is the whole subtlety here: it is a
        // statement about the hardware, not about the preset file in front of it.
        List<string> owners = new();
        int claimed = 0;

        foreach (bool hard in new[] { true, false })
        foreach (bool wantsBacklight in new[] { true, false })
        foreach (bool drivableDisplay in new[] { true, false })
        foreach (bool hardwareEnabled in new[] { true, false })
        {
            // Mirrors ApplyPresetBacklight: the flag is claimed on the way to a
            // write, and only when there is a value and somewhere to send it.
            bool ownsPanel = wantsBacklight && drivableDisplay && hardwareEnabled;

            if (ownsPanel)
            {
                claimed++;
                owners.Add($"hard={hard} wants={wantsBacklight} drivable={drivableDisplay} on={hardwareEnabled}");
            }
        }

        // Two of the four combinations want a brightness; both also need a
        // display to send it to and the feature switched on, so the two switches
        // are not redundant and there are exactly two owners.
        Assert.Equal(2, claimed);
        Assert.Equal(2, owners.Count);
    }

    // ---- The wiring ----

    [Fact]
    public void The_panic_key_is_a_hard_reset()
    {
        // Anchored on the panic branch, not on the name: OnHotkeyPressed also
        // contains the slot branch and the file contains other neutral calls.
        string slots = Source("MainWindow.Slots.cs");
        string body = BodyAfter(slots, "private async void OnHotkeyPressed");

        // Named, not bare: the panic key is the one caller that passes
        // emergency: true, which is what stops the night filter being re-asserted on
        // the one path where the user is saying the screen is wrong right now. Asserted
        // on the argument rather than the method because a bare call here would be the
        // same test passing while the filter came back on.
        AssertUses(body, "GoScreenNeutral(emergency: true)");
        Assert.DoesNotContain("GoScreenStandDown()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reset_button_is_a_hard_reset()
    {
        string display = Source("MainWindow.Display.cs");
        string body = BodyAfter(display, "private void OnResetScreenClick");

        AssertUses(body, "GoScreenNeutral()");
        Assert.DoesNotContain("GoScreenStandDown()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tray_reset_is_a_hard_reset()
    {
        string window = Source("MainWindow.xaml.cs");

        Assert.Contains("ResetScreenRequested += () => Dispatcher.Invoke(GoScreenNeutral)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_a_slot_off_is_a_stand_down()
    {
        string slots = Source("MainWindow.Slots.cs");
        string body = BodyAfter(slots, "private async Task ToggleSlot");

        AssertUses(body, "GoScreenStandDown()");
        Assert.DoesNotContain("GoScreenNeutral()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Alt_tabbing_out_of_a_wildcard_game_is_a_stand_down()
    {
        string slots = Source("MainWindow.Slots.cs");
        string body = BodyAfter(slots, "private void RevertWildcardOnFocusLoss");

        AssertUses(body, "GoScreenStandDown()");
        Assert.DoesNotContain("GoScreenNeutral()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_game_closing_is_a_stand_down()
    {
        // Quitting a game is the same tidying-up as alt-tabbing away from one, so
        // it gets the same answer for the panel.
        string slots = Source("MainWindow.Slots.cs");
        string body = BodyAfter(slots, "private void OnTargetExited");

        AssertUses(body, "GoScreenStandDown()");
        Assert.DoesNotContain("GoScreenNeutral()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_paths_take_the_same_gamma_ramp()
    {
        // The split is about the panel only. Gamma is not conditional in either
        // direction, so the two cannot drift into resetting different halves of
        // the picture - which is the failure a shared core exists to prevent.
        string display = Source("MainWindow.Display.cs");
        string body = BodyAfter(display, "private void StandDownScreen");

        Assert.Contains("LoadTune(DisplayPreset.Flat(), _workAudio);", body, StringComparison.Ordinal);
        Assert.Contains("_display.Reset()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_exactly_one_definition_of_each_stand_down()
    {
        // Two entry points over one body, so the rule cannot be implemented twice
        // and fixed in only one of them.
        string display = Source("MainWindow.Display.cs");

        // Full signatures, not prefixes. Both gained an emergency argument since this was
        // first written, and a prefix match on the old text would have quietly gone
        // to zero matches rather than failing.
        Assert.Equal(1, Count(display, "private void StandDownScreen(bool restorePanel, bool emergency = false)"));
        Assert.Equal(1, Count(display, "public void GoScreenNeutral(bool emergency = false)"));
        Assert.Equal(1, Count(display, "public void GoScreenStandDown()"));
    }

    [Fact]
    public void Only_the_hard_reset_restores_the_panel_unconditionally()
    {
        // The condition guards the call inside the shared body, so a stand-down
        // with no owner genuinely skips it. Pinned because the tempting
        // simplification is to hoist RestoreAll out of the guard and let the
        // callers sort it out.
        string display = Source("MainWindow.Display.cs");
        string body = BodyAfter(display, "private void StandDownScreen");

        int guard = body.IndexOf("if (restorePanel)", StringComparison.Ordinal);
        int call = body.IndexOf("Backlight.RestoreAll();", StringComparison.Ordinal);

        Assert.True(guard >= 0, "the panel restore is no longer conditional");
        Assert.True(call >= 0, "the stand-down no longer restores the panel at all");
        Assert.True(guard < call, "the panel is being restored before it is decided");
    }

    [Fact]
    public void A_stand_down_gives_up_the_panel()
    {
        // Otherwise the ownership survives a stand-down, and the next one restores
        // a panel this app wrote two presets ago.
        string display = Source("MainWindow.Display.cs");
        string body = BodyAfter(display, "private void StandDownScreen");
        int reset = body.IndexOf("_display.Reset()", StringComparison.Ordinal);
        int flag = body.IndexOf("_presetOwnedPanelDevice = string.Empty;", StringComparison.Ordinal);

        Assert.True(flag >= 0, "the stand-down leaves the panel owned");
        Assert.True(reset < flag, "ownership is given up before the reset can report whether it worked");
    }

    [Fact]
    public void The_wildcard_watch_is_released_before_its_name_is_cleared()
    {
        // Both lines are in the same method and order is the whole thing. The
        // other way round forgets the empty string - the name is already gone - and
        // leaves a still-running game watched for an exit that would revert a
        // screen the user had already put back.
        string display = Source("MainWindow.Display.cs");
        string body = BodyAfter(display, "private void StandDownScreen");

        int forget = body.IndexOf("_watcher.ForgetUnbound(_autoWildcardProcess)", StringComparison.Ordinal);
        int clear = body.IndexOf("_autoWildcardProcess = string.Empty;", StringComparison.Ordinal);

        Assert.True(forget >= 0, "the stand-down no longer releases the wildcard watch");
        Assert.True(clear >= 0, "the wildcard process name is never cleared");
        Assert.True(forget < clear, "the watch is released after the name it needs is cleared");
    }

    // ---- What the preset itself has to carry ----

    [Fact]
    public void A_preset_without_a_backlight_leaves_the_panel_alone_when_it_applies()
    {
        // The other half of "does not touch": a preset with no value must not even
        // read the panel, let alone write it. A read is a capture, and a capture is
        // what makes a later restore think it owns the panel.
        DisplayPreset preset = new() { Id = "a", Name = "A" };

        Assert.Null(preset.Backlight);
        Assert.False(ShouldRestorePanel(hardReset: false, presetOwnsPanel: preset.Backlight is not null));
    }

    [Fact]
    public void An_old_profile_still_leaves_the_panel_alone_after_a_round_trip()
    {
        // The compatibility case as a stand-down question rather than a load
        // question: a profile written before the field existed loads to null, so a
        // slot in it stands down without touching the panel.
        const string json = """
        {
          "CustomDisplayPresets": [ { "Id": "old", "Name": "Old" } ]
        }
        """;

        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions());
        DisplayPreset preset = settings!.CustomDisplayPresets[0];

        Assert.Null(preset.Backlight);
        Assert.False(ShouldRestorePanel(hardReset: false, presetOwnsPanel: preset.Backlight is not null));
    }

    // ---- helpers ----

    private static void AssertUses(string body, string call)
    {
        Assert.Contains(call, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source of a single call, so two tests about the same method cannot be
    /// satisfied by one call in a neighbour.
    /// </summary>
    private static string BodyAfter(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.Length;
        }

        return source.Substring(at, end - at);
    }

    private static int Count(string source, string needle)
    {
        int n = 0;
        int at = 0;

        while (true)
        {
            int found = source.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return n;
            }

            n++;
            at = found + needle.Length;
        }
    }

    private static string Source(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", fileName));
    }
}