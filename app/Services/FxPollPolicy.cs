using System;

namespace GamerTool.Services;

/// <summary>
/// How often the engine's state is re-read in the background, as a function of
/// whether anyone can see the answer.
/// <para>
/// Every read starts a FxSound.exe --status process. That is worth doing every
/// second and a half while the window is up, because the status line on the
/// Settings tab describes another program and has to stay true while it is being
/// looked at. It is not worth doing at that rate for as long as the app sits in
/// the tray, which is where it spends the whole of a game: about forty process
/// launches a minute, for a status line nobody can see.
/// </para>
/// <para>
/// Hidden is slower, not stopped. The settings-file watch still raises a read the
/// moment FxSound's own window changes something, and the slow tick is the net
/// under it for a change the watch missed, such as the engine losing the curve
/// this app loaded. Every read that answers something the user just did, an apply,
/// a restore, an install, is separate and unaffected.
/// </para>
/// </summary>
internal static class FxPollPolicy
{
    /// <summary>Window on screen: the status line is being read.</summary>
    public static readonly TimeSpan Visible = TimeSpan.FromMilliseconds(1500);

    /// <summary>Hidden to the tray or minimised: nobody is looking.</summary>
    public static readonly TimeSpan Hidden = TimeSpan.FromSeconds(20);

    public static TimeSpan IntervalFor(bool onScreen) => onScreen ? Visible : Hidden;
}
