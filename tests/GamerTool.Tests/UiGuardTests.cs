using System;
using System.IO;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Why these exist: an <c>async void</c> event handler has nowhere to throw to.
/// WPF requires the void signature, so an exception after an await propagates out
/// of the handler and lands on Application.DispatcherUnhandledException, which
/// shows a modal. For the hotkey that is a dialog over the game, raised by the
/// feature that is supposed to work quietly.
/// <para>
/// These test the helper the handlers now delegate to, which is the only place
/// the catch happens. The assertion that matters is the first one: the fault is
/// caught by the guard, not handed to the dispatcher.
/// </para>
/// </summary>
public class UiGuardTests
{
    [Fact]
    public async Task AThrowingHandlerDoesNotEscape()
    {
        bool reported = false;

        // The real defect was an exception leaving the handler. Reaching the line
        // after this call at all is the assertion; xunit fails the test on an
        // unhandled exception too, but this states it.
        await UiGuard.RunAsync("TEST", () => throw new InvalidOperationException("boom"), _ => reported = true);

        Assert.True(reported);
    }

    [Fact]
    public async Task AThrowingHandlerIsReportedWithItsTagAndTheRealFault()
    {
        Exception? seen = null;

        await UiGuard.RunAsync(
            "HOTKEY slot:abc",
            () => Task.FromException(new InvalidOperationException("boom")),
            ex => seen = ex);

        Assert.NotNull(seen);
        Assert.IsType<InvalidOperationException>(seen);
        Assert.Equal("boom", seen.Message);
    }

    [Fact]
    public async Task TheRealExceptionIsNotSwallowedSilently()
    {
        // UiGuard must still write to TraceLog. A catch that only flashes the UI
        // loses the one thing that makes a bug fixable.
        string tag = "UIGUARD " + Guid.NewGuid().ToString("N")[..8];

        await UiGuard.RunAsync(tag, () => Task.FromException(new InvalidOperationException("boom")), _ => { });

        // AppLog appends synchronously and closes the handle, so the line is
        // already on disk. The tag is unique per run so the tests writing to this
        // shared log at the same time cannot produce a false pass.
        string log = File.Exists(AppLog.Path_) ? File.ReadAllText(AppLog.Path_) : string.Empty;

        Assert.Contains(tag, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkingHandlerRunsAndReportsNothing()
    {
        bool ran = false;
        bool reported = false;

        await UiGuard.RunAsync("TEST", () => { ran = true; return Task.CompletedTask; }, _ => reported = true);

        Assert.True(ran);
        Assert.False(reported);
    }

    [Fact]
    public async Task AHandlerThatThrowsAfterAnAwaitIsStillCaught()
    {
        bool reported = false;

        // The shape the audit flagged: nothing throws until past a yield point,
        // which is why a try/catch around the synchronous part of a handler misses
        // it entirely.
        await UiGuard.RunAsync("TEST", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("after the await");
        }, _ => reported = true);

        Assert.True(reported);
    }

    [Fact]
    public async Task FeedbackThatItselfThrowsDoesNotEscape()
    {
        // Reporting the failure must not become a second fault, which would go
        // straight back out to the unhandled path that this exists to avoid.
        await UiGuard.RunAsync("TEST", () => Task.FromException(new InvalidOperationException("boom")),
            _ => throw new InvalidOperationException("feedback failed too"));
    }
}