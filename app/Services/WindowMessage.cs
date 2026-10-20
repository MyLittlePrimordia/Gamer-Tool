using System;
using System.Runtime.InteropServices;
using System.Threading;

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

    /// <summary>
    /// The window message that carries a play request. <c>WM_COPYDATA</c> is the
    /// documented way to send a block of bytes to another process in this
    /// session, and unlike the registered broadcast above it needs the target's
    /// handle - which is the trade: a broadcast cannot carry the slot id, and a
    /// play request is useless without it.
    /// </summary>
    public const int WmCopyData = 0x004A;

    private const long PlayCopyDataId = 0x47504C59;

    private const uint SmtoBlock = 0x0001;

    private const uint SmtoAbortIfHung = 0x0002;

    private const int PlayDeliveryAttempts = 20;

    private const int PlayDeliveryDelayMilliseconds = 50;

    private const uint PlayDeliveryTimeoutMilliseconds = 2000;

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

    /// <summary>
    /// Asks the running copy to apply one slot and start its game.
    /// <para>
    /// The window is found by title rather than by process id because this
    /// process has no handle to the other one. The title is a constant in the
    /// page and the app is single-instance, so there is exactly one candidate.
    /// It retries briefly for the case the mutex already describes: a replacement
    /// process can be starting while the old one is still tearing down, and the
    /// window may not exist for a moment after the claim is released.
    /// </para>
    /// <para>
    /// False means the request was not delivered. The caller falls back to
    /// showing the window, so a shortcut never becomes a click that does
    /// nothing at all.
    /// </para>
    /// </summary>
    public static bool BroadcastPlay(string slotId)
    {
        if (!PlayRequest.TrySlotId(slotId, out string id))
        {
            return false;
        }

        IntPtr window = IntPtr.Zero;
        for (int attempt = 0; attempt < PlayDeliveryAttempts && window == IntPtr.Zero; attempt++)
        {
            window = FindWindow(null, "Gamer Tool");
            if (window == IntPtr.Zero && attempt + 1 < PlayDeliveryAttempts)
            {
                Thread.Sleep(PlayDeliveryDelayMilliseconds);
            }
        }

        if (window == IntPtr.Zero)
        {
            return false;
        }

        string payload = PlayRequest.Payload(id);
        IntPtr text = Marshal.StringToHGlobalUni(payload);
        IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());

        try
        {
            CopyDataStruct copy = new()
            {
                dwData = new IntPtr(PlayCopyDataId),
                cbData = (payload.Length + 1) * sizeof(char),
                lpData = text
            };

            Marshal.StructureToPtr(copy, data, fDeleteOld: false);

            IntPtr sent = SendMessageTimeout(
                window,
                WmCopyData,
                IntPtr.Zero,
                data,
                SmtoBlock | SmtoAbortIfHung,
                PlayDeliveryTimeoutMilliseconds,
                out _);

            return sent != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>
    /// Reads a play request out of a received <c>WM_COPYDATA</c>.
    /// <para>
    /// The id and the size are checked before the pointer is touched. A window
    /// receives this message from any program in the session, so the caller must
    /// be able to reject a sender that is not this app without first trusting
    /// its idea of how long a string is.
    /// </para>
    /// </summary>
    public static bool TryReadPlayCopyData(IntPtr lParam, out string slotId)
    {
        slotId = string.Empty;

        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        CopyDataStruct copy = Marshal.PtrToStructure<CopyDataStruct>(lParam);

        if (copy.dwData.ToInt64() != PlayCopyDataId
            || copy.lpData == IntPtr.Zero
            || copy.cbData < sizeof(char)
            || copy.cbData > 8192
            || copy.cbData % sizeof(char) != 0)
        {
            return false;
        }

        string? payload = Marshal.PtrToStringUni(copy.lpData, copy.cbData / sizeof(char));
        return PlayRequest.TryReadPayload(payload, out slotId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;

        public int cbData;

        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}