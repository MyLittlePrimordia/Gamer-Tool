using System.Threading;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The loopback teardown must not hold the ring lock while it disposes.
/// <para>
/// NAudio's WasapiCapture.Dispose stops recording and then joins its capture
/// thread, and that thread raises DataAvailable into the feed's callback, which
/// takes the ring lock. Closing under that lock is therefore a deadlock with no
/// symptom at all: the UI thread waits on a thread that is waiting on a lock the
/// UI thread is holding. No exception, no log line, no recovery, because the
/// dispatcher simply never comes back. It was reachable from every tab change,
/// every minimise and every hide-to-tray, because all of those stop the capture.
/// </para>
/// <para>
/// The hang cannot be reproduced honestly in a unit test, which needs a live
/// WASAPI capture deliberately suspended mid-callback, and a test that depends on
/// real audio hardware either passes vacuously on CI or takes the whole suite
/// down with it. So what is asserted here is the invariant the fix rests on
/// instead: at the moment the dispose would happen, this thread is not inside the
/// ring lock. That is deterministic, needs no sound card, and fails immediately
/// if the dispose is ever moved back inside the lock.
/// </para>
/// </summary>
public class LoopbackCloseTests
{
    /// <summary>
    /// A close with nothing open still has to run the probe, because "there was no
    /// capture" is not the case being protected - the ordering is. The old code
    /// returned early on an empty teardown too, so a probe placed after that
    /// return would never fire and the test would pass for the wrong reason.
    /// </summary>
    [Fact]
    public void Close_reaches_the_dispose_without_holding_the_ring_lock()
    {
        var feed = new LoopbackSampleFeed();
        bool called = false;
        bool heldDuringDispose = true;

        feed.DisposeProbe = () =>
        {
            called = true;
            heldDuringDispose = feed.RingLockHeldByThisThread;
        };

        feed.Stop();

        Assert.True(called, "the close never reached the point where a capture would be disposed");
        Assert.False(
            heldDuringDispose,
            "the capture is being disposed while the ring lock is held, which deadlocks against the capture thread's own callback");
    }

    /// <summary>
    /// The same check on the path that matters most: closing a feed that has a
    /// capture attached. The probe runs before the null check on purpose, so this
    /// asserts the ordering rather than the presence of a capture - a test that
    /// needed a real one would skip itself on CI and prove nothing anywhere.
    /// </summary>
    [Fact]
    public async Task Closing_repeatedly_from_two_threads_never_blocks()
    {
        var feed = new LoopbackSampleFeed();
        bool overlapped = false;
        object probeGate = new();

        // Two closers, because the second one arriving while the first is
        // between "take the capture" and "dispose it" is the case the nulling
        // under the lock exists for. If ownership were not taken under the lock,
        // both would try to dispose the same capture.
        feed.DisposeProbe = () =>
        {
            lock (probeGate)
            {
                if (feed.RingLockHeldByThisThread)
                {
                    overlapped = true;
                }
            }
        };

        Task a = Task.Run(() => feed.Stop());
        Task b = Task.Run(() => feed.Stop());

        // Bounded, so a reintroduced deadlock fails the test rather than hanging
        // the run until the CI job is killed.
        Task both = Task.WhenAll(a, b);
        Task finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(both, finished);
        await both;

        Assert.False(overlapped, "a close disposed a capture while holding the ring lock");
        Assert.False(feed.IsLive, "a closed feed is still reporting a live capture");
    }

    /// <summary>
    /// Stopping a feed that was never started has to be a no-op rather than a
    /// throw. Every window state change calls it, including the ones that arrive
    /// before the spectrum has ever been shown.
    /// </summary>
    [Fact]
    public void Stopping_a_feed_that_never_started_is_harmless()
    {
        var feed = new LoopbackSampleFeed();

        feed.Stop();
        feed.Stop();

        Assert.False(feed.IsLive);
        Assert.Equal(0, feed.Read(new float[512]));
    }
}