using System;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Who gives the DDC bus back, and when.
/// <para>
/// A transaction that has not answered inside the timeout has left a worker still
/// inside the I2C call, still holding the gate, and holding it for good if the
/// monitor has wedged. Both callers released the claim in a <c>finally</c> that
/// ran on that path too, so the claim came back while the worker was still inside
/// the lock - and the next call entered, started a second worker, blocked on the
/// same lock, and timed out the same way. One pool thread consumed per attempt
/// for as long as the window stayed open, on the one feature that talks to
/// hardware over I2C.
/// </para>
/// <para>
/// The field's own comment promised the damage was capped at a single thread.
/// The code did the opposite. These pin the rule that makes the promise true:
/// an abandoned transaction keeps the claim and gives it back only when it is
/// genuinely finished.
/// </para>
/// <para>
/// Nothing here touches a real monitor - the rule is entirely about the claim,
/// which is a plain interlocked flag.
/// </para>
/// </summary>
[Collection("BusClaim")]
public class BusClaimTests : IDisposable
{
    public void Dispose()
    {
        // The claim is static and process wide, so each test hands it back
        // whatever it found rather than leaving the next one to trip over it.
        HardwareBrightness.LeaveBusForTest();
    }

    [Fact]
    public void The_bus_is_free_before_anything_has_claimed_it()
    {
        Assert.False(HardwareBrightness.IsBusClaimed);
        Assert.True(HardwareBrightness.TryEnterBusForTest());
    }

    [Fact]
    public void Only_one_call_can_be_on_the_bus()
    {
        Assert.True(HardwareBrightness.TryEnterBusForTest());

        // This is the answer a second monitor in a probe gets while the first is
        // still being asked, and it is why a busy row stays live rather than
        // greying itself out.
        Assert.False(HardwareBrightness.TryEnterBusForTest());

        HardwareBrightness.LeaveBusForTest();
        Assert.True(HardwareBrightness.TryEnterBusForTest());
    }

    [Fact]
    public void An_abandoned_transaction_keeps_the_claim_until_it_really_finishes()
    {
        TaskCompletionSource<bool> wedged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(HardwareBrightness.TryEnterBusForTest());

        // The timeout fires, the caller stops waiting, and hands the claim to the
        // worker. The worker is still blocked, so the claim must still be held.
        HardwareBrightness.HandBusToAbandonedWorkerForTest(wedged.Task);

        Assert.True(HardwareBrightness.IsBusClaimed);
        Assert.False(HardwareBrightness.TryEnterBusForTest());

        // The monitor answers at last. Only now is the bus free again.
        wedged.SetResult(true);
        SpinWait.SpinUntil(() => !HardwareBrightness.IsBusClaimed, TimeSpan.FromSeconds(5));

        Assert.False(HardwareBrightness.IsBusClaimed);
        Assert.True(HardwareBrightness.TryEnterBusForTest());
    }

    [Fact]
    public void A_monitor_that_never_answers_never_gives_the_bus_back()
    {
        // The whole point of the fix. A worker that is never completed holds the
        // claim for the life of the process, so every later call is told the bus
        // is busy and no further threads are spent finding that out again.
        TaskCompletionSource<bool> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(HardwareBrightness.TryEnterBusForTest());
        HardwareBrightness.HandBusToAbandonedWorkerForTest(never.Task);

        for (int attempt = 0; attempt < 50; attempt++)
        {
            Assert.False(HardwareBrightness.TryEnterBusForTest());
        }

        Assert.True(HardwareBrightness.IsBusClaimed);
    }

    [Fact]
    public void A_finished_worker_gives_the_bus_back_immediately()
    {
        Assert.True(HardwareBrightness.TryEnterBusForTest());

        // The ordinary case: the answer arrived, so there is nothing to wait for
        // and the claim comes straight back through the continuation.
        HardwareBrightness.HandBusToAbandonedWorkerForTest(Task.CompletedTask);

        SpinWait.SpinUntil(() => !HardwareBrightness.IsBusClaimed, TimeSpan.FromSeconds(5));
        Assert.False(HardwareBrightness.IsBusClaimed);
        Assert.True(HardwareBrightness.TryEnterBusForTest());
    }

    [Fact]
    public void A_worker_that_throws_still_gives_the_bus_back()
    {
        Assert.True(HardwareBrightness.TryEnterBusForTest());

        // The continuation runs whatever the worker's outcome, so a transaction
        // that failed rather than timed out cannot strand the bus permanently.
        Task faulted = Task.Run(() => throw new InvalidOperationException("simulated"));
        HardwareBrightness.HandBusToAbandonedWorkerForTest(faulted);

        SpinWait.SpinUntil(() => !HardwareBrightness.IsBusClaimed, TimeSpan.FromSeconds(5));
        Assert.False(HardwareBrightness.IsBusClaimed);
    }
}
