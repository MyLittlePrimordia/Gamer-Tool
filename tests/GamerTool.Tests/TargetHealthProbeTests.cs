using System;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Whether the driver's account of a display target holds together.
/// <para>
/// This is the check that would have caught the original fault. The missing
/// DDC/CI handle was read for a long time as a driver withholding access to a
/// display it had set up, and it was not: the same probe was reporting a mode
/// index of 10 out of 2 on every single pass, in a field nobody was reading. An
/// out of range index needs no theory about content protection to mean anything,
/// which is why it is the first thing tested here.
/// </para>
/// </summary>
public class TargetHealthProbeTests
{
    /// <summary>
    /// A display the driver has set up properly. Every field the driver is expected
    /// to fill in, filled in.
    /// </summary>
    private static TargetDescription Healthy() => new()
    {
        Available = true,
        VideoOutputTechnology = 10,
        RefreshNumerator = 240,
        RefreshDenominator = 1,
        TargetModeInfoIdx = 1,
        ModeCount = 2,
        SourceModeInfoOutOfRange = false,
        TargetNameRefused = false,
        TargetInfoRefused = false,
        AdvancedColorRefused = false,
        SourceNameOk = true
    };

    /// <summary>
    /// The real machine, exactly as measured. A DisplayPort monitor at 1440p240
    /// reporting itself as VGA at 1Hz, naming mode 10 out of 2, refusing every
    /// target query while answering for the source.
    /// </summary>
    private static TargetDescription Stub() => new()
    {
        Available = true,
        VideoOutputTechnology = 1,
        RefreshNumerator = 1,
        RefreshDenominator = 1,
        TargetModeInfoIdx = 10,
        ModeCount = 2,
        SourceModeInfoOutOfRange = false,
        TargetNameRefused = true,
        TargetInfoRefused = true,
        AdvancedColorRefused = true,
        SourceNameOk = true
    };

    [Fact]
    public void A_fully_described_target_is_complete()
    {
        Assert.Equal(TargetHealth.Complete, TargetHealthProbe.Classify(Healthy()).Health);
    }

    [Fact]
    public void An_out_of_range_mode_index_is_a_stub()
    {
        // The finding. Everything else about this display is plausible, and the
        // index alone is enough: the compositor allocated two mode slots and the
        // driver named the tenth.
        Assert.Equal(TargetHealth.Stub, TargetHealthProbe.Classify(Stub()).Health);
    }

    [Fact]
    public void The_out_of_range_index_is_named_with_both_numbers()
    {
        // Both, because "the index is wrong" is only actionable when you can see
        // what it claimed and what existed.
        TargetHealthReading reading = TargetHealthProbe.Classify(Stub());

        Assert.Contains(reading.Reasons, r => r.Contains("10", StringComparison.Ordinal)
            && r.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_equal_to_the_count_is_also_out_of_range()
    {
        // Off by one is the classic way to get this wrong, and it is a stub either
        // way: valid indices are zero to count-1.
        TargetDescription d = Healthy() with { TargetModeInfoIdx = 2, ModeCount = 2 };

        Assert.Equal(TargetHealth.Stub, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void An_index_one_past_the_last_is_fine()
    {
        // The other side of the boundary, so the test above is pinning the
        // comparison and not just "a big number".
        TargetDescription d = Healthy() with { TargetModeInfoIdx = 1, ModeCount = 2 };

        Assert.Equal(TargetHealth.Complete, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void A_stub_outranks_the_refusals()
    {
        // The ordering that matters. A driver that names a mode which does not
        // exist has already said the target is not set up, so whether it would also
        // have withheld the DDC handle is a side question rather than the
        // explanation. Reporting Withheld here is what sent the original diagnosis
        // after a content protection setting.
        TargetHealthReading reading = TargetHealthProbe.Classify(Stub());

        Assert.NotEqual(TargetHealth.Withheld, reading.Health);
        Assert.Contains(reading.Reasons, r => r.Contains("mode index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_out_of_range_source_index_is_a_stub_too()
    {
        TargetDescription d = Healthy() with { SourceModeInfoOutOfRange = true };

        Assert.Equal(TargetHealth.Stub, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void A_coherent_target_that_is_only_refused_is_withheld()
    {
        // The case the HDCP theory actually predicted, and the one that would still
        // justify suggesting a content protection setting. Every number is sane and
        // the driver still will not describe the target.
        TargetDescription d = Healthy() with
        {
            TargetNameRefused = true,
            TargetInfoRefused = true,
            AdvancedColorRefused = true
        };

        Assert.Equal(TargetHealth.Withheld, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void A_refusal_with_the_source_also_refused_is_not_called_selective()
    {
        // Both failing means the whole topology query is unavailable, which is a
        // different problem from a driver declining about one specific target, and
        // saying "selective" when it was not would be a claim it has not earned.
        TargetDescription d = Healthy() with
        {
            TargetNameRefused = true,
            TargetInfoRefused = true,
            AdvancedColorRefused = true,
            SourceNameOk = false
        };

        TargetHealthReading reading = TargetHealthProbe.Classify(d);

        Assert.Equal(TargetHealth.Withheld, reading.Health);
        Assert.Contains(reading.Reasons, r => r.Contains("source query too", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_placeholder_refresh_rate_is_reported_when_it_is_the_only_finding()
    {
        // Deliberately not asserted against the full stub, which also has an
        // out of range index. When the index already proves the target is not set
        // up, the refresh rate is saying the same thing twice and is dropped rather
        // than padding the reason list. Here it is the only thing wrong.
        TargetDescription d = Healthy() with { RefreshNumerator = 1, RefreshDenominator = 1 };

        Assert.Contains(
            TargetHealthProbe.Classify(d).Reasons,
            r => r.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_one_hertz_refresh_is_not_reported_when_the_index_already_proved_it()
    {
        // Redundant, and a reason string full of the same point twice is a reason
        // string nobody reads to the end.
        TargetDescription d = Healthy() with
        {
            TargetModeInfoIdx = 10,
            RefreshNumerator = 1,
            RefreshDenominator = 1
        };

        TargetHealthReading reading = TargetHealthProbe.Classify(d);

        Assert.DoesNotContain(reading.Reasons, r => r.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_one_hertz_refresh_alone_is_still_a_stub()
    {
        // A real mode cannot be 1Hz, so on its own it is the same class of finding.
        TargetDescription d = Healthy() with { RefreshNumerator = 1, RefreshDenominator = 1 };

        Assert.Equal(TargetHealth.Stub, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void A_refused_query_on_its_own_does_not_make_a_stub()
    {
        // One query failing is not a contradiction. The driver is allowed to decline
        // an individual request, and calling that a stub would cry wolf on a display
        // that is working.
        TargetDescription d = Healthy() with { AdvancedColorRefused = true };

        Assert.Equal(TargetHealth.Complete, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void An_unavailable_target_is_unknown_rather_than_a_stub()
    {
        // Nothing was asked, so nothing can be concluded. Reporting a fault here
        // would put a verdict on a display that was simply not being used.
        TargetDescription d = Healthy() with { Available = false };

        Assert.Equal(TargetHealth.Unknown, TargetHealthProbe.Classify(d).Health);
    }

    [Fact]
    public void A_complete_target_is_never_given_reasons()
    {
        Assert.Empty(TargetHealthProbe.Classify(Healthy()).Reasons);
    }

    [Fact]
    public void A_stub_always_carries_at_least_one_reason()
    {
        // A verdict with nothing behind it is the thing this whole type exists to
        // avoid, so the invariant is asserted rather than assumed.
        foreach (TargetDescription d in new[] { Stub(), Healthy() with { SourceModeInfoOutOfRange = true } })
        {
            Assert.NotEmpty(TargetHealthProbe.Classify(d).Reasons);
        }
    }

    [Fact]
    public void The_summary_names_the_health_and_the_reason()
    {
        string summary = TargetHealthProbe.Classify(Stub()).Summary;

        Assert.Contains("Stub", summary, StringComparison.Ordinal);
        Assert.Contains("mode index", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_complete_summary_says_so_without_an_explanation()
    {
        Assert.Equal("driver described the target fully", TargetHealthProbe.Classify(Healthy()).Summary);
    }

    [Theory]
    [InlineData(TargetHealth.Complete, true)]
    [InlineData(TargetHealth.Withheld, true)]
    [InlineData(TargetHealth.Stub, false)]
    [InlineData(TargetHealth.Unknown, false)]
    public void The_hdcp_hint_is_only_offered_where_it_could_work(TargetHealth health, bool expected)
    {
        // The user-facing consequence. No content protection setting brings up a
        // target that was never initialised, so on a stub the advice is several
        // clicks and a restart that change nothing.
        TargetHealthReading reading = new(health, Array.Empty<string>());

        Assert.Equal(expected, TargetHealthProbe.HdcpHintIsPlausible(reading));
    }

    [Fact]
    public void The_real_measurement_does_not_get_the_hdcp_hint()
    {
        // Spelled out as a test rather than left to the theory tests, because this
        // is the exact input that produced the wrong advice in the first place.
        Assert.False(TargetHealthProbe.HdcpHintIsPlausible(TargetHealthProbe.Classify(Stub())));
    }
}
