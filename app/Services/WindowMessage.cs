using System;

namespace GamerTool.Services;

/// <summary>
/// The window messages that mean the display topology moved, and nothing else.
/// <para>
/// Split out as plain constants and one predicate so the decision can be tested
/// without a display change actually happening. The hook is on the window's own
/// handle, so it sees every message the window receives; anything not named here
/// is not about displays.
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
}