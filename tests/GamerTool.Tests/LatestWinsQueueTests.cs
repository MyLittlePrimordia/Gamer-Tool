using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The audio command queue's ordering guarantees, with a stand-in for the engine.
/// <para>
/// This is the fix for the bug where a sound reset ran outside the queue. The
/// reset is three engine calls with about half a second of waits between them,
/// and the writes it made afterwards - the active preset id, the profile on
/// disk, the panel - landed after whatever had been applied in the meantime. The
/// app came back remembering Flat while the engine was playing something else,
/// and the next launch loaded the wrong preset.
/// </para>
/// <para>
/// The defect could not be tested at all before, because the queue lived inside
/// the window and a WPF window cannot be brought up in a test runner. It is
/// these properties - never two at once, newest wins, nothing dropped that was
/// not superseded, and a waiter released only when there is genuinely nothing
/// left - that make it safe, so they are what is pinned here.
/// </para>
/// <para>
/// The stand-in is a request that is just a name, and a runner that appends to a
/// list and can be made to wait. No engine, no interface, no window.
/// </para>
/// </summary>
public class LatestWinsQueueTests
{
    /// <summary>
    /// Records the order things ran in, and can be told to hold one open so a
    /// request is reliably still in flight when the next one is enqueued.
    /// </summary>
    private sealed class Recorder
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Ran { get; } = new();

        public bool Blocked { get; set; }

        /// <summary>Set when the run delegate is called, so a test can enqueue during it.</summary>
        public Action? OnRun { get; set; }

        public async Task Run(string request)
        {
            lock (Ran)
            {
                Ran.Add(request);
            }

            OnRun?.Invoke();

            if (Blocked)
            {
                await _gate.Task;
            }

            // A yield, so two requests that were allowed to overlap would
            // interleave rather than happening to land in order anyway.
            await Task.Yield();
        }

        public void Release() => _gate.TrySetResult();

        public string[] Order()
        {
            lock (Ran)
            {
                return Ran.ToArray();
            }
        }
    }

    private static async Task WaitIdleAsync(LatestWinsQueue<string> queue) =>
        await queue.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task A_request_alone_runs()
    {
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("only");
        await WaitIdleAsync(queue);

        Assert.Equal(new[] { "only" }, log.Order());
    }

    [Fact]
    public async Task Two_requests_never_run_at_the_same_time()
    {
        // The whole point. The engine is one process with one command line at a
        // time, so two operations overlapping does not mean both happen - it
        // means they interleave and the engine is left holding a mixture of the
        // two that neither caller asked for.
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(log.Run);

        int concurrent = 0;
        int peak = 0;
        var gate = new object();

        var measured = new LatestWinsQueue<string>(async request =>
        {
            int now = Interlocked.Increment(ref concurrent);
            lock (gate)
            {
                peak = Math.Max(peak, now);
            }

            await log.Run(request);
            await Task.Delay(5);

            Interlocked.Decrement(ref concurrent);
        });

        for (int i = 0; i < 12; i++)
        {
            measured.Enqueue("r" + i);
            await Task.Delay(1);
        }

        await measured.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task A_request_that_arrives_mid_flight_replaces_the_one_waiting()
    {
        // The rule that makes the queue usable rather than merely correct. A user
        // dragging through presets wants the one they stopped on, not all six
        // they passed.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("first");
        await WaitUntilAsync(() => log.Order().Length == 1);

        queue.Enqueue("second");
        queue.Enqueue("third");
        queue.Enqueue("fourth");

        log.Release();
        await WaitIdleAsync(queue);

        // "first" had already started, so it finishes. The three that were
        // waiting collapse into the one the user actually stopped on.
        Assert.Equal(new[] { "first", "fourth" }, log.Order());
    }

    [Fact]
    public async Task Requests_arriving_while_one_is_in_flight_collapse_onto_the_last_of_them()
    {
        // Worth pinning because the guarantee is narrower than it first looks.
        //
        // "Newest wins" coalesces the *pending* slot. It does not coalesce a
        // request that has already started, because a request the engine is
        // halfway through cannot be un-started - abandoning it mid sequence is
        // the interleave this whole thing exists to prevent. So a burst settles
        // as "the one that was in flight" then "the newest of the rest".
        //
        // The window is the requests that arrive while one is genuinely running,
        // which is why this blocks the first one rather than relying on timing.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("first");
        await WaitUntilAsync(() => log.Order().Length == 1);

        queue.Enqueue("second");
        queue.Enqueue("third");
        queue.Enqueue("fourth");

        log.Release();
        await WaitIdleAsync(queue);

        Assert.Equal(new[] { "first", "fourth" }, log.Order());
    }

    [Fact]
    public async Task The_first_request_starts_immediately()
    {
        // The other half of the rule above, and the reason a burst of presses
        // produces two runs rather than one: the pump is started by the enqueue
        // and runs as far as its first await, so by the time the first Enqueue
        // has returned that request is already taken.
        //
        // That is the right way round. A queue that dropped the first request in
        // favour of something that had not been asked for yet would be deciding
        // a preset was superseded before it had been superseded, and a push that
        // never happened is a worse failure than a redundant one.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("only");

        // No await before this: the request is already recorded.
        Assert.Equal(new[] { "only" }, log.Order());

        log.Release();
        await WaitIdleAsync(queue);
    }

    [Fact]
    public async Task A_burst_of_resets_never_leaves_a_stale_one_running_last()
    {
        // Which of the two survivors matters more than how many there are. Panic
        // three times quickly: the runs may be two, but the second of them is
        // the newest request, so the state the app ends up in is the one the user
        // asked for last. The bug this replaced left the *older* reset's writes
        // landing after the newer tune, which is the opposite.
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("reset");
        queue.Enqueue("gaming");

        await WaitIdleAsync(queue);

        string[] order = log.Order();
        Assert.Equal("gaming", order[^1]);
    }

    [Fact]
    public async Task A_reset_already_running_is_finished_rather_than_abandoned()
    {
        // The honest limit of the rule, and worth pinning because it is easy to
        // assume the other way round. Only a request that has not started can be
        // replaced; one already halfway through its engine calls is left to
        // finish, because abandoning it mid sequence is the interleave this
        // whole thing exists to prevent.
        //
        // So panic twice quickly does run two resets, about a second apart and
        // strictly in order. That is slower than ideal and harmless: a reset is
        // idempotent, and what used to be wrong was not the count but the fact
        // that the two overlapped and the older one's writes landed last.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("reset");
        await WaitUntilAsync(() => log.Order().Length == 1);

        queue.Enqueue("reset");
        queue.Enqueue("reset");

        log.Release();
        await WaitIdleAsync(queue);

        // The one in flight ran, and the two waiting collapsed to one.
        Assert.Equal(new[] { "reset", "reset" }, log.Order());
    }

    [Fact]
    public async Task A_reset_and_a_slot_key_never_overlap()
    {
        // The shape the bug actually needed, asserted directly: the engine sees
        // the reset's sequence through to the end before the tune's begins.
        Recorder log = new();
        var inside = 0;
        int peak = 0;

        var queue = new LatestWinsQueue<string>(async request =>
        {
            int now = Interlocked.Increment(ref inside);
            peak = Math.Max(peak, now);

            await log.Run(request);
            await Task.Delay(5);

            Interlocked.Decrement(ref inside);
        });

        queue.Enqueue("reset");
        queue.Enqueue("gaming");
        await WaitIdleAsync(queue);

        Assert.Equal(1, peak);
        Assert.Equal(new[] { "reset", "gaming" }, log.Order());
    }

    [Fact]
    public async Task A_slot_key_after_the_panic_key_wins()
    {
        // The exact sequence the bug needed. Panic resets, a slot key a moment
        // later asks for a tune, and a queue that ran its requests in order would
        // take the user back to silence after they had asked for a preset.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("reset");
        await WaitUntilAsync(() => log.Order().Length == 1);

        queue.Enqueue("gaming");

        log.Release();
        await WaitIdleAsync(queue);

        Assert.Equal(new[] { "reset", "gaming" }, log.Order());
        Assert.Equal("gaming", log.Order()[^1]);
    }

    [Fact]
    public async Task A_request_arriving_as_the_queue_stops_is_still_run()
    {
        // The narrowest race in the thing: a request that lands between the
        // pump deciding it has nothing left and the pump marking itself idle.
        // Missed, it sits in the pending slot with nobody to pick it up, and the
        // request is simply never made.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Recorder log = new();
            var queue = new LatestWinsQueue<string>(log.Run);

            // Enqueue from inside the run delegate, which is the moment the
            // pump is between "took the last one" and "found nothing pending".
            bool injected = false;
            log.OnRun = () =>
            {
                if (!injected)
                {
                    injected = true;
                    queue.Enqueue("late");
                }
            };

            queue.Enqueue("first");
            await WaitIdleAsync(queue);

            // Give a dropped request a chance to show up as a hang rather than a
            // miss, by waiting for the queue to settle twice.
            await Task.Delay(2);
            await WaitIdleAsync(queue);

            Assert.Equal(new[] { "first", "late" }, log.Order());
        }
    }

    [Fact]
    public async Task A_waiter_is_released_only_when_there_is_nothing_left()
    {
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("slow");

        Task first = queue.WhenIdle();
        queue.Enqueue("second");

        log.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        // Both ran before the waiter was let go. Releasing early would be a
        // confirmation message appearing before the thing it confirms.
        Assert.Equal(new[] { "slow", "second" }, log.Order());
        await WaitIdleAsync(queue);
    }

    [Fact]
    public async Task Waiting_on_an_idle_queue_is_free_and_immediate()
    {
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(log.Run);

        Task idle = queue.WhenIdle();

        Assert.True(idle.IsCompleted);
        await idle;
    }

    [Fact]
    public async Task Two_waiters_are_both_released()
    {
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("slow");

        Task a = queue.WhenIdle();
        Task b = queue.WhenIdle();

        log.Release();

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.IsCompleted && b.IsCompleted);
    }

    [Fact]
    public async Task A_request_that_throws_does_not_wedge_the_queue()
    {
        // Otherwise one bad request leaves every later request sitting in the
        // pending slot with nobody left to pick them up, which is a far worse
        // failure than the one that caused it.
        Recorder log = new();
        List<Exception> errors = new();

        var queue = new LatestWinsQueue<string>(
            async request =>
            {
                await log.Run(request);
                if (request == "bad")
                {
                    throw new InvalidOperationException("engine said no");
                }
            },
            errors.Add);

        queue.Enqueue("bad");
        queue.Enqueue("good");
        await WaitIdleAsync(queue);

        Assert.Equal(new[] { "bad", "good" }, log.Order());
        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(errors[0]);
    }

    [Fact]
    public async Task A_request_that_throws_synchronously_still_does_not_wedge_it()
    {
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(
            request => throw new InvalidOperationException("threw before returning a task"));

        queue.Enqueue("bad");
        await queue.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

        // The point is that WhenIdle completed at all: the queue came back down
        // rather than staying busy forever.
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task The_queue_reports_when_it_is_busy()
    {
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        Assert.False(queue.IsBusy);

        queue.Enqueue("slow");
        await WaitUntilAsync(() => log.Order().Length == 1);
        Assert.True(queue.IsBusy);

        log.Release();
        await WaitIdleAsync(queue);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task A_burst_collapses_to_the_first_and_the_last()
    {
        // The realistic shape: a user mashing a key, or six slots firing as
        // games start. The engine sees two command sequences, not twenty.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("burst0");
        await WaitUntilAsync(() => log.Order().Length == 1);

        for (int i = 1; i < 40; i++)
        {
            queue.Enqueue("burst" + i);
        }

        log.Release();
        await WaitIdleAsync(queue);

        Assert.Equal(new[] { "burst0", "burst39" }, log.Order());
    }

    [Fact]
    public async Task Nothing_is_dropped_when_nothing_overlaps()
    {
        // The other half of the coalescing rule. Dropping is only correct
        // because a superseded request is genuinely redundant, and that is only
        // true when something ran in between to supersede it.
        Recorder log = new();
        var queue = new LatestWinsQueue<string>(log.Run);

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue("r" + i);
            await WaitIdleAsync(queue);
        }

        Assert.Equal(new[] { "r0", "r1", "r2", "r3", "r4" }, log.Order());
    }

    [Fact]
    public async Task Enqueueing_from_several_threads_does_not_lose_the_newest()
    {
        // The window only ever enqueues from the dispatcher, but a queue that
        // silently drops a request is not a thing worth having, and the test
        // costs one loop.
        Recorder log = new() { Blocked = true };
        var queue = new LatestWinsQueue<string>(log.Run);

        queue.Enqueue("start");
        await WaitUntilAsync(() => log.Order().Length == 1);

        Parallel.For(0, 64, i => queue.Enqueue("t" + i));
        log.Release();
        await WaitIdleAsync(queue);

        string[] order = log.Order();
        Assert.Equal("start", order[0]);

        // Whatever won, the queue must have settled on one of them and be idle.
        Assert.False(queue.IsBusy);
        Assert.Contains(order[^1], Enumerable.Range(0, 64).Select(i => "t" + i));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        Stopwatch watch = Stopwatch.StartNew();

        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("the condition was not met within " + timeoutMs + " ms");
            }

            await Task.Delay(1);
        }
    }
}
