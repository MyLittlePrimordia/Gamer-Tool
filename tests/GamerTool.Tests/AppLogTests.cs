using System;
using System.Text;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What a failure looks like once it has been written to the log.
/// <para>
/// Written because of a real one. The app refused to start on every launch, and
/// the log said "Provide value on 'System.Windows.StaticResourceExtension' threw an
/// exception". That names a resource lookup, so the obvious next step is hunting
/// for a missing resource, and there was no missing resource: the cause two levels
/// further down was a null Binding inside a control template. The logger unwrapped
/// exactly one level, which is precisely the number of levels that says nothing.
/// </para>
/// <para>
/// These pin the chain, not the format. The wording can change freely; what must
/// not change is that the deepest cause appears.
/// </para>
/// </summary>
public class AppLogTests
{
    [Fact]
    public void A_single_exception_is_written_with_its_type_and_message()
    {
        string text = AppLog.Describe(new InvalidOperationException("the widget is upside down"));

        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("the widget is upside down", text, StringComparison.Ordinal);
    }


    [Fact]
    public void The_cause_at_the_bottom_of_the_chain_is_written_out()
    {
        // The shape that cost the afternoon: three wrappers, and the only one that
        // says anything is the last. An ArgumentNullException is used for the
        // bottom because the real one was, and because its message names the
        // parameter, which is the part worth having in a log.
        var bottom = new ArgumentNullException("binding");
        var middle = new InvalidOperationException("Failed to create object", bottom);
        var top = new Exception("Startup failed", middle);

        string text = AppLog.Describe(top);

        Assert.Contains("Startup failed", text, StringComparison.Ordinal);
        Assert.Contains("Failed to create object", text, StringComparison.Ordinal);
        Assert.Contains("ArgumentNullException", text, StringComparison.Ordinal);
        Assert.Contains("binding", text, StringComparison.Ordinal);
    }


    [Fact]
    public void The_cause_is_kept_however_deep_it_is()
    {
        // One level of unwrapping is what failed in the first place, so the depth
        // is pinned rather than left to whatever the loop happens to do.
        Exception deepest = new InvalidOperationException("the real reason");
        for (int i = 0; i < 6; i++)
        {
            deepest = new Exception("wrapper " + i, deepest);
        }

        string text = AppLog.Describe(deepest);

        Assert.Contains("wrapper 0", text, StringComparison.Ordinal);
        Assert.Contains("wrapper 5", text, StringComparison.Ordinal);
        Assert.Contains("the real reason", text, StringComparison.Ordinal);
    }


    [Fact]
    public void An_absurdly_long_chain_is_truncated_rather_than_filling_the_log()
    {
        // This runs in a crash handler on the way to a dialog, so it has to
        // terminate and it has to stay readable. A real cycle cannot be built
        // here: Exception.InnerException is not virtual, so there is no way to
        // make one point at itself, and the depth cap is the belt to that braces.
        Exception chain = new InvalidOperationException("bottom of a very long chain");
        for (int i = 0; i < 40; i++)
        {
            chain = new Exception("layer " + i, chain);
        }

        string text = AppLog.Describe(chain);

        Assert.Contains("truncated", text, StringComparison.OrdinalIgnoreCase);

        // The chain is written outermost first, so what survives a truncated walk
        // is the top of it. That is the right way round for a crash handler: the
        // outer frames are what say where the app was, and a pathological chain
        // is noise long before its tail is reached.
        Assert.Contains("layer 39", text, StringComparison.Ordinal);
        Assert.DoesNotContain("layer 0", text, StringComparison.Ordinal);
    }


    [Fact]
    public void A_null_exception_is_refused_rather_than_written_as_nothing()
    {
        // A crash handler that itself throws is worse than no log, and the guard
        // has to be explicit because every other parameter in this file is
        // nullable enabled.
        Assert.Throws<ArgumentNullException>(() => AppLog.Describe(null!));
    }


    [Fact]
    public void Every_level_gets_its_own_line()
    {
        // Multi-line messages are normal, so the chain cannot be told apart by line
        // count alone. Each level is separated by a newline after its own message
        // and stack trace.
        var text = AppLog.Describe(
            new Exception("outer", new Exception("inner")));

        Assert.Contains(Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("caused by", text, StringComparison.Ordinal);
    }
}
