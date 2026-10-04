using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What an update check is able to report.
/// <para>
/// This exists because of a bug the tests did not catch and the compiler could
/// not either. The check returned null for six different outcomes, the window
/// treated null as "the check failed", and a machine with no winget - a supported
/// and deliberate configuration - was told its update check had failed. The
/// banner was making a claim about a query that had never been run.
/// </para>
/// <para>
/// Two properties are worth pinning. The outcome is never absent, because there
/// is no longer a null to mean "several things went differently". And the update
/// flag is derived from the outcome rather than set beside it, so a result cannot
/// claim an update while naming no version to update to.
/// </para>
/// </summary>
public class FxUpdateResultTests
{
    [Fact]
    public void An_update_is_only_claimed_when_the_outcome_says_there_is_one()
    {
        foreach (FxUpdateOutcome outcome in System.Enum.GetValues<FxUpdateOutcome>())
        {
            FxUpdateResult result = new() { Outcome = outcome };

            Assert.Equal(outcome == FxUpdateOutcome.NewerAvailable, result.UpdateAvailable);
        }
    }

    [Fact]
    public void No_checker_is_not_the_same_answer_as_a_check_that_failed()
    {
        // The distinction the bug collapsed. One says the app never asked, because
        // it decided not to guess; the other says it asked and got nothing back.
        // Only the second is a problem worth a line in a support log, and only the
        // second should ever invite the user to try again.
        Assert.NotEqual(FxUpdateOutcome.NoChecker, FxUpdateOutcome.Failed);

        Assert.False(new FxUpdateResult { Outcome = FxUpdateOutcome.NoChecker }.UpdateAvailable);
        Assert.False(new FxUpdateResult { Outcome = FxUpdateOutcome.Failed }.UpdateAvailable);
    }

    [Fact]
    public void The_update_flag_cannot_disagree_with_the_versions()
    {
        // It used to be a settable field next to two version strings, so
        // { UpdateAvailable = true, AvailableVersion = "" } was constructible and
        // meant nothing - the banner would have offered "Update to " with nothing
        // after it.
        FxUpdateResult newer = new()
        {
            Outcome = FxUpdateOutcome.NewerAvailable,
            InstalledVersion = "1.0.0.0",
            AvailableVersion = "1.2.0.0",
        };

        Assert.True(newer.UpdateAvailable);
        Assert.Equal("1.2.0.0", newer.AvailableVersion);

        // Current is not an update, whatever the versions are.
        Assert.False(new FxUpdateResult
        {
            Outcome = FxUpdateOutcome.UpToDate,
            InstalledVersion = "1.2.0.0",
            AvailableVersion = "1.2.0.0",
        }.UpdateAvailable);
    }

    [Fact]
    public void A_result_with_no_outcome_set_is_a_failure_rather_than_a_pass()
    {
        // The safe direction for an unset value. A caller that forgets to set it
        // must not end up reporting "up to date".
        FxUpdateResult unset = new();

        Assert.Equal(FxUpdateOutcome.Failed, unset.Outcome);
        Assert.False(unset.UpdateAvailable);
    }
}