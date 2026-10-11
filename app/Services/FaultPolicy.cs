using System;

namespace GamerTool.Services;

/// <summary>
/// Whether a fault is worth putting a dialog in front of the user.
/// <para>
/// The decision is here, and not inline in the two handlers in App.xaml.cs, because
/// it is a judgement with a wrong answer on both sides and it is the kind of
/// judgement that gets quietly inlined. Too eager and a real fault is swallowed
/// into a log nobody reads; too reluctant and the app shows a modal on its way out,
/// which is a thing that blocks a quit.
/// </para>
/// <para>
/// The case that forced it: quitting the app would sometimes raise a
/// <see cref="DllNotFoundException"/> with a stack of nothing but native frames,
/// from a module uninitializer running during AppDomain unload. The app was fully
/// finished, the process was already dying, and the user was shown
/// "SOMETHING WENT WRONG: Dll was not found." - which reads as a crash caused by
/// pressing Quit, and is the exact thing a game utility must not do on the way out.
/// </para>
/// </summary>
public static class FaultPolicy
{
    /// <summary>
    /// True when the fault should be shown as a dialog rather than only logged.
    /// </summary>
    /// <param name="shuttingDown">The app has started going away.</param>
    /// <param name="isTerminating">
    /// The exception is taking the process down with it. A modal raised while the
    /// runtime is terminating is how a failing app becomes a hung one, so this is
    /// refused on its own account and not only because of the shutdown flag.
    /// </param>
    public static bool ShouldShowDialog(bool shuttingDown, bool isTerminating)
    {
        // Either one is enough. They overlap for a clean quit, and a terminating
        // fault during normal running still does not get a dialog, because the
        // process is going regardless of what is said in the last moment it is
        // standing up.
        return !shuttingDown && !isTerminating;
    }
}
