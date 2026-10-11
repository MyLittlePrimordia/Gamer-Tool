using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The app-library scan's sequencing: one pass at a time, and another afterwards
/// for anything that asked while the first was running.
/// <para>
/// This is the rule that could not be tested while it lived in the window. A scan
/// of the real machine reads every Steam library and every uninstall registry key,
/// so there is no seam to drive it through, and the coalescing was correct only in
/// the sense that somebody had read it carefully. It had a lost-request window that
/// reading it carefully did not catch.
/// </para>
/// <para>
/// Every test here holds the fake scan open on a gate, so the interleaving is
/// decided by the test rather than by the scheduler. That is the whole reason the
/// coordinator takes a delegate: no engine, no interface, no window.
/// </para>
/// </summary>
public class ScanCoordinatorTests
{
    /// <summary>
    /// A scan the test drives: it counts its passes, and a pass does not finish
    /// until the test says so.
    /// </summary>
    /// <remarks>
    /// The gate is read per pass rather than captured once. A single gate shared by
    /// every pass looks equivalent and is not: once it has been released, a
    /// continuation attached to it fires immediately for every later pass too, so a
    /// test asking for two passes counts three and blames the coordinator. That cost
    /// one debugging round and is the reason this is written the long way.
    /// </remarks>
    private sealed class GatedScan
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? _gate;

        public int Passes;

        /// <summary>Completes once a pass has actually started.</summary>
        public Task Entered => _entered.Task;

        public Task Run()
        {
            Interlocked.Increment(ref Passes);
            _entered.TrySetResult();

            TaskCompletionSource gate;

            lock (this)
            {
                gate = _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return gate.Task;
        }

        /// <summary>
        /// Lets every pass through from now on.
        /// </summary>
        /// <remarks>
        /// Synchronous on purpose. Returning a task would make every call site
        /// remember to await it, and the one that forgot would release the gate after
        /// asserting on the count.
        /// </remarks>
        public void Release()
        {
            lock (this)
            {
                _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gate.TrySetResult();
            }
        }

        /// <summary>Waits for the pass count to reach <paramref name="count"/>, or gives up.</summary>
        public async Task<bool> WaitForPassesAsync(int count)
        {
            for (int i = 0; i < 200; i++)
            {
                if (Volatile.Read(ref Passes) >= count)
                {
                    return true;
                }

                await Task.Delay(10);
            }

            return Volatile.Read(ref Passes) >= count;
        }
    }

    [Fact]
    public async Task OneCallerRunsOnePass()
    {
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> running = coordinator.RequestAsync();
        await scan.Entered;

        Assert.True(coordinator.IsRunning);
        Assert.Equal(1, Volatile.Read(ref scan.Passes));

        scan.Release();
        Assert.True(await running);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task ASecondCallerDoesNotStartASecondScan()
    {
        // The scan reads the registry and every Steam library. Two overlapping runs
        // finish with whichever ended last overwriting the other's result, possibly
        // with a list built from a half-read machine.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> first = coordinator.RequestAsync();
        await scan.Entered;

        Task<bool> second = coordinator.RequestAsync();

        // Long enough that a second pass would have started if it were going to.
        await Task.Delay(80);
        Assert.Equal(1, Volatile.Read(ref scan.Passes));

        scan.Release();
        await first;
        await second;
    }

    [Fact]
    public async Task ARequestDuringAScanIsServedByAnotherPassAfterwards()
    {
        // The scan button is the caller that matters: it deliberately bypasses the
        // cache, so being served the previous list is the failure where a button
        // reports a fresh count having done nothing.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> owner = coordinator.RequestAsync();
        await scan.Entered;

        _ = coordinator.RequestAsync();

        scan.Release();

        Assert.True(await owner);
        Assert.True(await scan.WaitForPassesAsync(2), "the request made during the scan was dropped");
    }

    [Fact]
    public async Task AJoiningCallerIsToldItDidNotRunThePass()
    {
        // The return value is the caller's only way to know whether the result it is
        // about to read was built for it. The scan button builds the board from the
        // list as soon as this returns, so a caller that joined one has to be able
        // to tell.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> owner = coordinator.RequestAsync();
        await scan.Entered;

        Task<bool> joiner = coordinator.RequestAsync();

        // The joiner must not have completed yet: its request is owed a pass, and
        // returning now would hand it the list from before it asked.
        Assert.False(joiner.IsCompleted);

        scan.Release();

        Assert.True(await owner);
        Assert.False(await joiner);
    }

    [Fact]
    public async Task EveryJoiningCallerIsReleasedRatherThanTheFirstOneOnly()
    {
        // A busy scan can be joined by more than one caller - the dropdown, a
        // restore and a state change can all land inside one pass. Releasing one and
        // dropping the rest leaves those callers waiting for the life of the
        // process, and which one got dropped comes down to the scheduler.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> owner = coordinator.RequestAsync();
        await scan.Entered;

        List<Task<bool>> joiners = new();
        for (int i = 0; i < 5; i++)
        {
            joiners.Add(coordinator.RequestAsync());
        }

        scan.Release();
        await owner;

        Task all = Task.WhenAll(joiners);
        Task finished = await Task.WhenAny(all, Task.Delay(3000));

        Assert.True(ReferenceEquals(finished, all), "a joining caller was never released");
    }

    [Fact]
    public async Task ThreeRequestsDuringOneScanCostOneExtraPassAndNoMore()
    {
        // The re-arm is a flag, not a counter: the point of the second pass is to
        // see the machine as it is now, and three callers asking for that want one
        // fresh look between them, not three.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        Task<bool> owner = coordinator.RequestAsync();
        await scan.Entered;

        _ = coordinator.RequestAsync();
        _ = coordinator.RequestAsync();
        _ = coordinator.RequestAsync();

        scan.Release();

        Assert.True(await owner);
        Assert.True(await scan.WaitForPassesAsync(2));

        // Give a third pass every chance to appear before saying it did not.
        await Task.Delay(120);
        Assert.Equal(2, Volatile.Read(ref scan.Passes));
    }

    [Fact]
    public async Task AFailedScanDoesNotWedgeTheCoordinator()
    {
        // A pass that throws leaves the flag standing and no loop coming back for
        // it. Without the unwind clearing it, the next caller would find ownership
        // taken and return for ever: one bad scan and the button is dead for the
        // life of the process.
        int calls = 0;
        ScanCoordinator coordinator = new(() =>
        {
            calls++;

            return calls == 1
                ? Task.FromException(new InvalidOperationException("the registry said no"))
                : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestAsync());

        Assert.False(coordinator.IsRunning);
        Assert.True(await coordinator.RequestAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AFailedScanStillReleasesTheCallersThatJoinedIt()
    {
        // Otherwise a joiner's task is incomplete for the life of the process, and
        // the caller holding it is the scan button's own handler.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fail = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ScanCoordinator coordinator = new(async () =>
        {
            entered.TrySetResult();
            await fail.Task;
            throw new InvalidOperationException("the scan failed");
        });

        Task<bool> owner = coordinator.RequestAsync();
        await entered.Task;

        Task<bool> joiner = coordinator.RequestAsync();

        fail.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner);

        Task finished = await Task.WhenAny(joiner, Task.Delay(3000));
        Assert.True(ReferenceEquals(finished, joiner) || joiner.IsCompleted,
            "the joining caller was left waiting after the scan failed");

        // And whatever it returns, it must not claim to have run a pass.
        Assert.False(await joiner);
    }

    [Fact]
    public async Task SequentialCallersEachRunTheirOwnPass()
    {
        // The single-flight rule must not turn into a cache. A caller arriving after
        // everything has settled is owed a pass of its own, because that is what the
        // scan button asks for.
        GatedScan scan = new();
        ScanCoordinator coordinator = new(scan.Run);

        scan.Release();

        Assert.True(await coordinator.RequestAsync());
        Assert.True(await coordinator.RequestAsync());
        Assert.True(await coordinator.RequestAsync());

        Assert.Equal(3, Volatile.Read(ref scan.Passes));
    }
}
