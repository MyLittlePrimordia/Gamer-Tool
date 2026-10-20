using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GamerTool.Services;

public enum SetupCheckState
{
    /// <summary>It works.</summary>
    Pass,

    /// <summary>It works, but something about it is worth knowing.</summary>
    Warn,

    /// <summary>It does not work, and the feature it belongs to will not either.</summary>
    Fail,

    /// <summary>There was nothing here to test, which is not a failure.</summary>
    Skip,
}

public sealed record SetupCheckItem(string Label, string Detail, SetupCheckState State)
{
    public string Mark => State switch
    {
        SetupCheckState.Pass => "[x]",
        SetupCheckState.Warn => "[!]",
        SetupCheckState.Fail => "[X]",
        _ => "[ ]",
    };
}

/// <summary>
/// One screen, as the setup check sees it: what to open, which panel it is, and
/// what HDR reports about it.
/// </summary>
public readonly record struct SetupScreen(string Device, string Key, string Name, bool HdrSupported, bool HdrActive);

/// <summary>
/// What the setup check found, in the order it was checked.
/// <para>
/// One object rather than four loose strings, because the same answer is shown in
/// a dialog, written to the log and reduced to a one line result. Three surfaces
/// describing one run is exactly how they end up disagreeing.
/// </para>
/// </summary>
public sealed class SetupCheckReport
{
    public SetupCheckReport(IReadOnlyList<SetupCheckItem> items)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public IReadOnlyList<SetupCheckItem> Items { get; }

    public int Failures => Items.Count(i => i.State == SetupCheckState.Fail);

    public bool HasFailure => Failures > 0;

    public int Checked => Items.Count(i => i.State != SetupCheckState.Skip);

    public string Summary
    {
        get
        {
            if (HasFailure)
            {
                return Failures == 1 ? "ONE CHECK FAILED" : Failures + " CHECKS FAILED";
            }

            return Checked == 0 ? "NOTHING TO CHECK" : "EVERYTHING CHECKED OUT";
        }
    }

    /// <summary>
    /// One line for the trace log, so a report pasted into a support thread and a
    /// report copied from the log are the same words.
    /// </summary>
    public string LogText =>
        string.Join(" | ", Items.Select(i => i.Label + " " + i.State.ToString().ToUpperInvariant() + ": " + i.Detail));

    public string BodyText =>
        string.Join(
            Environment.NewLine,
            Items.Select(i => i.Mark + " " + i.Label.PadRight(9) + i.Detail));
}

/// <summary>
/// Runs the checks a user cannot run by themselves: whether this screen still
/// takes a gamma ramp, whether HDR can switch and come back, and whether Windows
/// will move the default sound device and put it back.
/// <para>
/// Every step is a delegate, so the assembly of the report - which is the part
/// with the rules in it - can be tested without a compositor, a monitor or an
/// audio endpoint. The real calls live in <see cref="WindowsSetupProbe"/>.
/// </para>
/// <para>
/// A step that throws becomes a failed line rather than an exception that takes
/// the rest of the report with it. The whole value of this is the list of answers;
/// stopping at the first unexpected failure would hide the other three.
/// </para>
/// </summary>
public sealed class SetupCheckRunner
{
    private readonly Func<IReadOnlyList<SetupScreen>> _screens;
    private readonly Func<string, SetupCheckItem> _gamma;
    private readonly Func<SetupScreen, Task<SetupCheckItem>> _hdr;
    private readonly Func<IReadOnlyList<SetupCheckItem>> _routes;

    public SetupCheckRunner(
        Func<IReadOnlyList<SetupScreen>> screens,
        Func<string, SetupCheckItem> gamma,
        Func<SetupScreen, Task<SetupCheckItem>> hdr,
        Func<IReadOnlyList<SetupCheckItem>> routes)
    {
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _gamma = gamma ?? throw new ArgumentNullException(nameof(gamma));
        _hdr = hdr ?? throw new ArgumentNullException(nameof(hdr));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
    }

    public async Task<SetupCheckReport> RunAsync()
    {
        List<SetupCheckItem> items = new();

        IReadOnlyList<SetupScreen> screens;
        try
        {
            screens = _screens() ?? Array.Empty<SetupScreen>();
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETUP screens", ex);
            screens = Array.Empty<SetupScreen>();
        }

        if (screens.Count == 0)
        {
            // Everything below needs a screen, so this is the one failure that
            // short-circuits the rest: reporting "no screen" four times is noise.
            items.Add(new SetupCheckItem("SCREEN", "no screen was found", SetupCheckState.Fail));
            return new SetupCheckReport(items);
        }

        items.Add(new SetupCheckItem(
            "SCREEN",
            screens.Count == 1
                ? screens[0].Name
                : screens.Count + " screens: " + string.Join(", ", screens.Select(s => s.Name)),
            SetupCheckState.Pass));

        items.Add(RunGamma(screens[0]));

        SetupScreen? firstHdr = null;
        foreach (SetupScreen screen in screens)
        {
            if (screen.HdrSupported)
            {
                firstHdr = screen;
                break;
            }
        }

        items.Add(firstHdr is { } hdrScreen
            ? await RunHdr(hdrScreen)
            : new SetupCheckItem("HDR", "no screen here supports HDR", SetupCheckState.Skip));

        try
        {
            IReadOnlyList<SetupCheckItem> routes = _routes();
            if (routes is { Count: > 0 })
            {
                items.AddRange(routes);
            }
            else
            {
                items.Add(new SetupCheckItem("AUDIO", "nothing to test", SetupCheckState.Skip));
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETUP routes", ex);
            items.Add(new SetupCheckItem("AUDIO", "the check did not finish: " + ex.Message, SetupCheckState.Fail));
        }

        return new SetupCheckReport(items);
    }

    private SetupCheckItem RunGamma(SetupScreen screen)
    {
        try
        {
            return _gamma(screen.Device);
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETUP gamma", ex);
            return new SetupCheckItem("GAMMA", "the check did not finish: " + ex.Message, SetupCheckState.Fail);
        }
    }

    private async Task<SetupCheckItem> RunHdr(SetupScreen screen)
    {
        try
        {
            return await _hdr(screen);
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETUP hdr", ex);
            return new SetupCheckItem("HDR", "the check did not finish: " + ex.Message, SetupCheckState.Fail);
        }
    }
}
