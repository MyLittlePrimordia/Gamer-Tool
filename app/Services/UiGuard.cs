using System;

namespace GamerTool.Services;

/// <summary>
/// The one place an event handler's failure is caught, so that no handler has to
/// remember to.
/// <para>
/// WPF event handlers are <c>async void</c> by signature, and that is not
/// negotiable: the signature is what the XAML and the <c>+=</c> wiring require.
/// The consequence is that an exception thrown after an await in one of them does
/// not surface at the await, it propagates out of the void method and lands on
/// <c>Application.DispatcherUnhandledException</c>. That handler logs and raises a
/// modal "SOMETHING WENT WRONG" box, which is right for a fault nobody was doing
/// anything about and wrong for a hotkey fired in the middle of a game: the
/// dialog appears on top of the game, raised by the feature that is supposed to
/// work silently, and the user cannot dismiss it without leaving the game.
/// </para>
/// <para>
/// So a handler stays <c>async void</c> and does nothing but delegate here. The
/// failure is still recorded - swallowing it silently would be its own lie, and
/// the log plus one line of on-screen feedback is the answer this app gives
/// everywhere else - but it never becomes a modal the user has to deal with.
/// </para>
/// </summary>
internal static class UiGuard
{
    /// <summary>
    /// Runs <paramref name="body"/>, reporting anything it throws through
    /// <paramref name="onFault"/> and returning normally.
    /// </summary>
    /// <param name="tag">Short name of the handler, used in the log line.</param>
    /// <param name="body">The handler's real work, moved into a Task-returning method.</param>
    /// <param name="onFault">
    /// Feedback for the user, normally a status flash. Not a dialog: see above.
    /// </param>
    public static async Task RunAsync(string tag, Func<Task> body, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onFault);

        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TraceLog.Write(tag, ex);

            try
            {
                onFault(ex);
            }
            catch (Exception feedbackEx)
            {
                // Reporting the failure must not itself throw: this runs on the
                // dispatcher during an already-failed handler, and a second fault
                // here would go straight back out to the unhandled path.
                TraceLog.Write(tag + " feedback", feedbackEx);
            }
        }
    }
}