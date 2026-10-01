using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GamerTool.Services;

/// <summary>
/// Runs one thing at a time, and runs the newest one rather than all of them.
/// <para>
/// The shape is the one an audio engine forces on you. It is a single process
/// with one command line at a time, so two of its operations overlapping does
/// not mean they both happen - it means they interleave and the engine is left
/// holding some mixture of the two that neither caller asked for.
/// </para>
/// <para>
/// "Newest wins" rather than "everything in order" is deliberate, and it is the
/// part that is easy to get wrong. A queue that ran every request in sequence
/// would be correct and useless: a user dragging through six presets would push
/// all six, ending on the one they stopped on only by luck of the timing, and a
/// user who hit the panic key and then a slot key would be taken back to
/// silence after asking for a tune. Dropping the superseded requests is what
/// makes both of those do the obvious thing.
/// </para>
/// <para>
/// The guarantee is about the <em>pending</em> slot, and it is worth being
/// exact about. A request that has already started is not superseded - it is run
/// to the end, because abandoning one halfway through its work is the interleave
/// this exists to prevent. So a burst of requests settles as "the one that was
/// in flight" and then "the newest of the rest". The first request always
/// starts, since there is nothing to coalesce it into.
/// </para>
/// <para>
/// A reset being dropped by that rule is not a loss. Somebody who presses panic
/// twice while the first is still running gets two resets, about as long apart
/// as the first one took. Slower than ideal and harmless: a reset is idempotent,
/// and what used to be wrong was not the count but that two of them overlapped
/// and the older one's writes landed last.
/// </para>
/// <para>
/// Generic over the request and taking a run delegate, rather than typed to one
/// engine. That is all the seam there is: the ordering is the part worth
/// testing, and testing it needs no engine, no interface and no window.
/// </para>
/// </summary>
public sealed class LatestWinsQueue<T>
{
    private readonly Func<T, Task> _run;
    private readonly Action<Exception>? _onError;

    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _waiters = new();

    private T? _pending;
    private bool _hasPending;
    private bool _busy;

    /// <param name="run">Carries out one request. Anything it throws is logged and swallowed, so one bad request cannot wedge the queue for every request after it.</param>
    /// <param name="onError">Where a failure goes. Optional, so a test does not have to supply one.</param>
    public LatestWinsQueue(Func<T, Task> run, Action<Exception>? onError = null)
    {
        _run = run;
        _onError = onError;
    }

    /// <summary>Whether a request is in flight.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _busy;
            }
        }
    }

    /// <summary>
    /// Asks for a request to be run, replacing any that has not started yet.
    /// <para>
    /// Returns immediately. Use <see cref="WhenIdle"/> if the caller needs to
    /// know the engine has finished.
    /// </para>
    /// </summary>
    public void Enqueue(T request)
    {
        bool own;

        lock (_gate)
        {
            _pending = request;
            _hasPending = true;
            own = !_busy;
            _busy = true;
        }

        // Started outside the lock. The pump is async and resumes wherever the
        // await says, so letting it start inside would mean the thread that
        // called Enqueue decides which thread runs the engine.
        if (own)
        {
            _ = PumpAsync();
        }
    }

    /// <summary>
    /// Completes when nothing is left to run.
    /// <para>
    /// Already complete when the queue is idle, which is the common case and the
    /// reason this is cheap to call from a click handler: a request nobody is
    /// waiting on pays for no completion source at all.
    /// </para>
    /// <para>
    /// A single boundary on a machine that was already busy has a small window
    /// where the queue empties between the caller's request being recorded and
    /// this read, and the wait returns immediately rather than after the
    /// request. Every caller here is a confirmation message rather than a
    /// correctness dependency, and releasing early leaves the work still
    /// happening - the alternative is a lock held across an await.
    /// </para>
    /// </summary>
    public Task WhenIdle()
    {
        lock (_gate)
        {
            if (!_busy)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(drained);
            return drained.Task;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (TryTakePending(out T request))
            {
                try
                {
                    await _run(request);
                }
                catch (Exception ex)
                {
                    // Swallowed on purpose. A request that throws is one that
                    // failed, and letting it escape would tear down the pump and
                    // leave every later request sitting in the pending slot with
                    // nobody left to pick them up.
                    _onError?.Invoke(ex);
                }
            }
        }
        finally
        {
            bool restart;
            List<TaskCompletionSource> waiting;

            lock (_gate)
            {
                _busy = false;

                // A request that landed between the last empty check and the
                // flag coming down would otherwise sit with nobody to pick it
                // up, so ownership is handed straight over rather than dropped.
                restart = _hasPending;
                if (restart)
                {
                    _busy = true;
                }

                waiting = new List<TaskCompletionSource>(_waiters);
                _waiters.Clear();
            }

            if (restart)
            {
                _ = PumpAsync();
            }
            else
            {
                // Only once the re-arm check has come back empty, so a late
                // request is run rather than announced as finished.
                foreach (TaskCompletionSource waiter in waiting)
                {
                    waiter.TrySetResult();
                }
            }
        }
    }

    private bool TryTakePending(out T request)
    {
        lock (_gate)
        {
            if (!_hasPending)
            {
                request = default!;
                return false;
            }

            request = _pending!;
            _pending = default;
            _hasPending = false;
            return true;
        }
    }
}
