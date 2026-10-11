using System;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>
/// The window messages that mean the display topology moved, and the one that means
/// a second launch wants the first one to come forward.
/// <para>
/// Split out as plain constants and predicates so the decisions can be tested
/// without a display change or a second process actually happening. The hook is on
/// the window's own handle, so it sees every message the window receives; anything
/// not named here is not about displays and not about us.
/// </para>
/// </summary>
public static class WindowMessage
{
    /// <summary>The display resolution or topology has changed.</summary>
    public const int WmDisplayChange = 0x007E;

    /// <summary>A device has been added, removed or changed.</summary>
    public const int WmDeviceChange = 0x0219;

    /// <summary>
    /// The wParam that accompanies <see cref="WmDeviceChange"/> when the set of
    /// display nodes itself has changed.
    /// <para>
    /// Device change arrives for everything plugged into the machine, several times
    /// a second while a mouse is moving, so acting on it unfiltered would re-scan
    /// the displays continuously. This is the one that means a display came or went.
    /// </para>
    /// </summary>
    public const int DbtDevnodesChanged = 0x0007;

    /// <summary>
    /// Whether a message is worth re-scanning for.
    /// <para>
    /// A WM_DISPLAYCHANGE carries no useful wParam, so anything counts for it. A
    /// WM_DEVICECHANGE only counts for the one notification above, because the
    /// others are mice, keyboards and removable drives.
    /// </para>
    /// </summary>
    public static bool Matters(int message, long wParam)
    {
        return message switch
        {
            WmDisplayChange => true,
            WmDeviceChange => wParam == DbtDevnodesChanged,
            _ => false,
        };
    }

    /// <summary>
    /// "Another copy was started - bring yourself to the front."
    /// <para>
    /// Registered rather than a constant this app picked. Two processes have to
    /// agree on this number without shipping it between them, and the only thing
    /// that guarantees that is asking Windows: <c>RegisterWindowMessage</c> hands
    /// back the same id for the same name for as long as the session lasts, and
    /// zero when the registration failed.
    /// </para>
    /// <para>
    /// Zero would be worth knowing about rather than broadcasting, because a
    /// message id of zero is <c>WM_NULL</c> - it reaches every window and means
    /// nothing to any of them. A failed registration would leave the running copy
    /// silently deaf and the feature quietly dead, which is exactly the sort of
    /// thing that has to be visible rather than inferred. So the id is resolved
    /// once, here, and <see cref="BroadcastShowMe"/> refuses to send a zero.
    /// </para>
    /// </summary>
    private static readonly uint ShowMeId = RegisterWindowMessage("GamerTool.ShowMe");

    /// <summary>Every top level window on the desktop. The one target that needs no handle.</summary>
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public static uint ShowMe => ShowMeId;

    /// <summary>
    /// Whether a received message is the second launch asking to be shown.
    /// <para>
    /// The one non-zero clause is load-bearing. If the registration ever failed,
    /// <see cref="ShowMe"/> is zero, and a bare comparison would then answer
    /// "yes" to <c>WM_NULL</c> - a message the window does receive, routinely. That
    /// would bring the app to the front on nothing at all, so a zero id matches
    /// nothing, including itself.
    /// </para>
    /// <para>
    /// Compared unsigned because the ids Windows hands out for registered messages
    /// live above <see cref="int.MaxValue"/>, and the hook receives them in the
    /// signed int that the Win32 signature uses.
    /// </para>
    /// </summary>
    public static bool IsShowMe(int message)
    {
        return ShowMeId != 0 && unchecked((uint)message) == ShowMeId;
    }

    /// <summary>
    /// Asks every window on the desktop to come forward.
    /// <para>
    /// Broadcast rather than addressed to a known handle, because the running
    /// copy's window is created and owned by a process this one has no handle to.
    /// That is safe: an id this app registered is meaningless to every other
    /// program, so each of them ignores a message it has never heard of. The one
    /// window that answers is this app's own.
    /// </para>
    /// <para>
    /// False when there is nothing to send, rather than reported as a success -
    /// the caller is deciding whether to explain itself to the user, and "I tried"
    /// is not that.
    /// </para>
    /// </summary>
    public static bool BroadcastShowMe()
    {
        if (ShowMeId == 0)
        {
            return false;
        }

        return PostMessage(HwndBroadcast, ShowMeId, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}