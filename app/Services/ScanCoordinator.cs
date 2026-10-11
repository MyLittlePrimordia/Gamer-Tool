using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GamerTool.Services;

/// <summary>
/// Runs one scan at a time, and runs another afterwards if one was asked for while
/// the first was in flight.
/// </summary>
/// <remarks>
/// Out here rather than inside the window for one reason: this is the whole of the
/// app-list scan's state and all of its concurrency, and it is the part that cannot
/// be checked by reading it. The scan walks the real Steam libraries and the real
/// uninstall registry, so while this lived in <c>MainWindow.Slots.cs</c> the only
/// evidence that the coalescing worked was that somebody had thought about it
/// carefully.
/// <para>
/// The rules, all of which are asserted directly in <c>ScanCoordinatorTests</c>:
/// </para>
/// <para>
/// <b>One at a time.</b> A second caller while a scan is in flight does not start
/// a second scan. The scan reads the registry and every Steam library, so two
/// overlapping runs finish with whichever ended last overwriting the other's
/// result - possibly with a list built from a half-read machine.
/// </para>
/// <para>
/// <b>A request during a scan is a request for another pass.</b> Not for a dropped
/// one. The scan button is the caller that matters: it deliberately bypasses the
/// cache, so serving it the previous list is the failure where a button reports a
/// fresh count having done nothing.
/// </para>
/// <para>
/// <b>Nothing is lost on the way out.</b> The state is read and written only under
/// one lock, ownership is handed over rather than dropped, and the handover is
/// conditional. The version this replaced released a CAS flag outside any lock and
/// cleared a pending flag in a separate statement, which left a few instructions in
/// which a request could be recorded and then wiped by the release itself.
/// </para>
/// <para>
/// Deliberately not built on <see cref="LatestWinsQueue{T}"/>, which is the
/// coalescing primitive this codebase already has and the obvious thing to reach
/// for. It drops a superseded pending request, and what a force caller wants is a
/// fresh look <em>after</em> the pass in flight rather than instead of it. What is
/// being coalesced has a different shape, so the primitive is different.
/// </para>
/// </remarks>
public sealed class ScanCoordinator
{
    private readonly Func<Task> _scan;
    private readonly Action<Exception>? _onError;

    /// <summary>Guards every field below it.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Whether a pass is running. Set when a caller claims the coordinator, cleared
    /// when the loop that owns it finishes.
    /// </summary>
    private bool _running;

    /// <summary>
    /// Whether somebody has asked for a pass that the one in flight will not serve.
    /// <para>
    /// A second flag rather than one shared with <see cref="_running"/>, and that
    /// separation is the whole correctness of the loop. A single flag conflates two
    /// different questions - "is a pass running" and "is another pass wanted" - and
    /// the claim itself sets the first one, so a loop reading that flag after its
    /// own first pass always sees a request that nobody made and runs one pass too
    /// many. Every single caller, every time.
    /// </para>
    /// <para>
    /// This is the defect the coordinator was extracted to find, and the reason it
    /// is written down at this length: the one-flag version reads perfectly well.
    /// </para>
    /// </summary>
    private bool _wanted;

    /// <summary>
    /// The callers waiting for the pass they joined to finish.
    /// <para>
    /// A list rather than a single waiter, because one busy scan can be joined by
    /// more than one caller - the dropdown, a restore and a state change can all
    /// land inside the same pass. Releasing one and dropping the rest would leave
    /// those callers' tasks incomplete for the life of the process, and which one
    /// got dropped would come down to the scheduler.
    /// </para>
    /// <para>
    /// There is deliberately no "is it idle" flag beside <see cref="_wanted"/>. One
    /// was written first, on the reasoning that a caller has to decide whether to
    /// wait before it can add itself to this list, and a lost wakeup lives in that
    /// gap. It turned out to be redundant: the decision and the registration below
    /// are both inside one <c>lock</c>, so there is no gap between them to be
    /// pre-empted in, and a second field meaning "roughly the opposite of
    /// <see cref="_wanted"/>" is one more thing that can disagree with it. The
    /// question that actually needed answering was which of two owners clears the
    /// flag, and that is answered by the <c>completed</c> argument to
    /// <see cref="Finish"/>.
    /// </para>
    /// </summary>
    private readonly List<TaskCompletionSource> _idleWaiters = new();

    /// <param name="scan">Runs one pass. What it does with the result is the caller's business; this only sequences it.</param>
    /// <param name="onError">Where <see cref="RequestQuietlyAsync"/> sends a failure. Optional, so a test does not have to supply one.</param>
    public ScanCoordinator(Func<Task> scan, Action<Exception>? onError = null)
    {
        _scan = scan;
        _onError = onError;
    }

    /// <summary>Whether a pass is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Asks for a scan, waits for the pass that serves this request, and reports
    /// whether this caller was the one that ran it.
    /// </summary>
    /// <remarks>
    /// The return value is not decoration. A caller that ran the pass is reading a
    /// list it built; a caller that joined one is reading a list somebody else
    /// produced, and has to know that, because the pass may have started before the
    /// request.
    /// <para>
    /// Both kinds of caller wait, and the waiting is half of what was wrong before.
    /// The version inside the window had a joining caller record its request and
    /// return immediately, so the scan button's handler - which awaits this and then
    /// builds the board from the list - could read a list that was still being
    /// rebuilt underneath it. It looked right only because a second click is hard to
    /// land inside a scan that the first click disabled the button for.
    /// </para>
    /// </remarks>
    public async Task<bool> RequestAsync()
    {
        bool owns;
        TaskCompletionSource? join = null;

        lock (_gate)
        {
            // Both flags mean "busy" to a caller deciding whether to join: a pass
            // running, or a pass owed that nobody is running yet.
            owns = !_running && !_wanted;
            _running = true;

            if (!owns)
            {
                // A pass is under way or owed. This request is for one that comes
                // after it, so the flag is set and the owner's loop picks it up.
                //
                // Registered inside the lock, so a loop that drains the waiters
                // between this caller reading the flags and this caller adding
                // itself cannot miss it - there is no gap to be pre-empted in.
                _wanted = true;
                join = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _idleWaiters.Add(join);
            }
        }

        if (join is not null)
        {
            await join.Task;
            return false;
        }

        return await RunAsync();
    }

    /// <summary>
    /// The owning caller's pass: one scan, then another for as long as somebody
    /// asked while the last one was running.
    /// </summary>
    private async Task<bool> RunAsync()
    {
        // Starts false, which is the answer the exception path needs: a pass that
        // threw did not serve anyone and still leaves the flag standing.
        bool served = false;

        try
        {
            // One pass, then another for each request that arrived while one was
            // running. The loop reads <see cref="_wanted"/>, which is set only by a
            // caller that joined - never by the claim - so the first pass does not
            // mistake its own claim for a request.
            while (true)
            {
                await _scan();

                lock (_gate)
                {
                    if (!_wanted)
                    {
                        // Nobody asked while that pass ran, so it answered everyone
                        // waiting and there is nothing left to do.
                        served = true;
                        break;
                    }

                    // Somebody asked. Cleared so the pass about to run is the one
                    // that serves them, and the loop goes round for it.
                    _wanted = false;
                }
            }
        }
        finally
        {
            Finish(completed: served);
        }

        return served;
    }

    /// <summary>
    /// Ends the pass, and lets the joined callers go if there is nothing left to run.
    /// </summary>
    /// <param name="completed">
    /// True for a loop that ran to its own end. False for a pass that threw, which
    /// is the only case that has a request of its own to clear.
    /// </param>
    /// <remarks>
    /// Clearing <see cref="_running"/> unconditionally is right, and it is worth
    /// saying why because an earlier version of this method did not.
    /// <para>
    /// The loop decides it is finished by finding <see cref="_wanted"/> clear, and
    /// it holds the lock while it does. So when the loop has exited, no later caller
    /// can have claimed - a claim needs the lock, and any claim taken before the
    /// final check would have set <see cref="_wanted"/> and kept the loop going. The
    /// pass therefore always still owns the coordinator by the time this runs, and
    /// clearing it cannot wipe a newer owner's flag. Guarding this against a
    /// handover that cannot happen was the shape that made the loop wrong twice.
    /// </para>
    /// <para>
    /// A pass that threw is the other case and does have a request of its own to
    /// clear: it jumped out with <see cref="_wanted"/> possibly set and no loop
    /// coming back for it, so without this the request would be served by nobody.
    /// </para>
    /// </remarks>
    private void Finish(bool completed)
    {
        TaskCompletionSource[] waiting;

        lock (_gate)
        {
            if (!completed)
            {
                _wanted = false;
            }

            _running = false;

            if (_wanted)
            {
                // A request is owed and a new loop has taken ownership of it. That
                // loop releases the waiters when it is done, so this one must not.
                return;
            }

            waiting = _idleWaiters.ToArray();
            _idleWaiters.Clear();
        }

        foreach (TaskCompletionSource waiter in waiting)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// <see cref="RequestAsync"/> with the failure swallowed and reported.
    /// </summary>
    /// <remarks>
    /// For callers that are not waiting on the result and must not be handed an
    /// exception to handle - the dropdown opening, a restore, a window state change.
    /// The scan is a convenience for them; a scan that failed means an empty picker,
    /// which is a state they already have to render.
    /// </remarks>
    public async Task RequestQuietlyAsync()
    {
        try
        {
            await RequestAsync();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }
}
