using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The "your GPU driver is in the way" hint.
/// <para>
/// The gate is the part that matters. Shown for every unsupported monitor, the
/// hint would tell a great many people with nothing wrong with their setup to go
/// and change a graphics setting, and a hint that cries wolf is worse than no
/// hint at all. It is offered for one failure only: the driver handed back a
/// physical monitor with no DDC/CI handle, which is the single case a driver
/// setting is known to cause.
/// </para>
/// </summary>
public class DdcDriverHintTests
{
    private const string Device = @"\\.\DISPLAY1";

    private static MonitorProbe Probe(BusOutcome outcome, string? blocked = null) => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Outcome = outcome,
        BlockedReason = blocked
    };

    [Fact]
    public void A_missing_ddc_handle_is_offered_the_hint()
    {
        // The one case. This is what an AMD card with HDCP Support on reports.
        Assert.True(DdcDriverHint.ShouldOffer(Probe(BusOutcome.NoDdcPathway)));
    }

    [Fact]
    public void A_working_display_is_never_offered_it()
    {
        Assert.False(DdcDriverHint.ShouldOffer(Probe(BusOutcome.Ok)));
    }

    [Fact]
    public void A_display_that_declined_the_code_is_not_offered_it()
    {
        // The monitor answered and said no. A driver setting is not the reason.
        Assert.False(DdcDriverHint.ShouldOffer(Probe(BusOutcome.Refused)));
    }

    [Fact]
    public void A_display_with_no_edid_to_check_is_not_offered_it()
    {
        Assert.False(DdcDriverHint.ShouldOffer(Probe(BusOutcome.Failed, "no EDID to check")));
    }

    [Fact]
    public void An_excluded_display_is_not_offered_it()
    {
        // It has its own advice elsewhere, and offering a second, different
        // explanation for the same greyed out row is how a row stops making sense.
        Assert.False(DdcDriverHint.ShouldOffer(Probe(BusOutcome.Failed, "on your exclusion list")));
    }

    [Fact]
    public void A_busy_probe_is_not_offered_it()
    {
        // Nothing was asked of the monitor, so nothing can be concluded.
        Assert.False(DdcDriverHint.ShouldOffer(Probe(BusOutcome.Busy)));
    }

    [Fact]
    public void A_missing_probe_is_not_offered_it()
    {
        Assert.False(DdcDriverHint.ShouldOffer(null!));
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", GpuVendor.Amd)]
    [InlineData("AMD Radeon 780M Graphics", GpuVendor.Amd)]
    [InlineData("ATI Radeon HD 5770", GpuVendor.Amd)]
    [InlineData("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia)]
    [InlineData("Quadro P2000", GpuVendor.Nvidia)]
    [InlineData("Intel(R) UHD Graphics 630", GpuVendor.Intel)]
    [InlineData("Intel Arc A770", GpuVendor.Intel)]
    public void The_vendor_is_read_out_of_the_adapter_name(string name, GpuVendor expected)
    {
        Assert.Equal(expected, DdcDriverHint.VendorFromName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Display Adapter")]
    public void An_unrecognised_adapter_is_unknown_rather_than_guessed(string? name)
    {
        // Guessing here would put the wrong vendor's click paths in front of
        // someone, which is worse than saying nothing.
        Assert.Equal(GpuVendor.Unknown, DdcDriverHint.VendorFromName(name));
    }

    [Fact]
    public void Amd_gets_the_steps_that_have_actually_been_confirmed()
    {
        IReadOnlyList<string> steps = DdcDriverHint.Steps(GpuVendor.Amd);
        string joined = string.Join(" ", steps);

        Assert.Contains("AMD SOFTWARE", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HDCP SUPPORT", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_last_step_is_a_pc_restart_because_the_driver_demands_one()
    {
        // Adrenalin states it under the toggle: "HDCP changes will take effect
        // after the next system restart". Telling someone to reopen Gamer Tool
        // instead would have them do the work, see nothing change, and conclude
        // the fix does not work.
        foreach (GpuVendor vendor in new[] { GpuVendor.Amd, GpuVendor.Nvidia, GpuVendor.Intel, GpuVendor.Unknown })
        {
            IReadOnlyList<string> steps = DdcDriverHint.Steps(vendor);

            Assert.Contains("RESTART YOUR PC", steps[steps.Count - 1], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RESTART GAMER TOOL", string.Join(" ", steps), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_vendor_gets_spelled_out_click_paths_it_has_not_been_given()
    {
        // The AMD sequence is the only one confirmed on real hardware. Inventing
        // the equivalent for another vendor would be presenting a guess as an
        // instruction, so everyone else is pointed at the idea instead.
        string joined = string.Join(" ", DdcDriverHint.Steps(GpuVendor.Nvidia));

        Assert.DoesNotContain("AMD", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONTROL PANEL", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_warning_names_what_the_advice_costs()
    {
        // The hint is not free advice, and saying so is the difference between a
        // useful prompt and a trap.
        string warning = DdcDriverHint.Warning;

        Assert.Contains("HDCP", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DRM", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_amd_title_states_the_cause_because_it_is_a_confirmed_one()
    {
        Assert.Contains("AMD", DdcDriverHint.Title(GpuVendor.Amd), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_title_hedges_rather_than_accusing()
    {
        // The app knows the driver gave no handle. It does not know why, and for
        // anyone but AMD it is guessing to say otherwise.
        string title = DdcDriverHint.Title(GpuVendor.Unknown);

        Assert.Contains("MAY BE", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_copy_is_sentence_case_and_not_shouting()
    {
        // The rest of this app's dialogs are set in capitals because they are one
        // line of labels. This is a paragraph of instructions that someone has to
        // read carefully and type into another program, and set that way it stops
        // looking like anything to follow. Pinned here because it is invisible in
        // a diff of the words and only shows up on screen.
        foreach (GpuVendor vendor in new[] { GpuVendor.Amd, GpuVendor.Nvidia, GpuVendor.Intel, GpuVendor.Unknown })
        {
            HintContent hint = DdcDriverHint.For(vendor);

            foreach (string text in new[] { hint.Title, hint.Warning }.Concat(hint.Steps))
            {
                Assert.True(
                    text.Any(char.IsLower),
                    "expected sentence case, got: " + text);
            }
        }
    }

    [Fact]
    public void The_content_carries_everything_the_modal_needs()
    {
        HintContent hint = DdcDriverHint.For(GpuVendor.Amd);

        Assert.False(string.IsNullOrWhiteSpace(hint.Title));
        Assert.NotEmpty(hint.Steps);
        Assert.False(string.IsNullOrWhiteSpace(hint.Warning));
    }
}
