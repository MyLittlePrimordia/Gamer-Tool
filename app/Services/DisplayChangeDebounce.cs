namespace GamerTool.Services;

/// <summary>
/// One pending rescan, however many display-change messages asked for it.
/// <para>
/// Docking or undocking a laptop produces several WM_DISPLAYCHANGE messages over
/// a couple of hundred milliseconds - one per mode change, one for the new
/// topology, one for the EDID handshake. Each rescan is a full EnumDisplayDevices
/// walk plus a registry lookup per monitor for the label, so running one per
/// message is the difference between a dock being handled and the UI stuttering
/// while it is handled.
/// </para>
/// <para>
/// The wait is not only about collapsing the burst. Windows is not finished when
/// the first message arrives, so an immediate rescan can enumerate a topology that
/// is still being negotiated and miss the screen that appears a moment later.
/// </para>
/// </summary>
public sealed class DisplayChangeDebounce
{
    /// <summary>Set once a message has asked for a rescan, cleared when it is done.</summary>
    public bool IsDue { get; private set; }

    /// <summary>
    /// Stops the debounce from acting. Set while the window is closing, because
    /// unplugging a display on the way out - or a dock closing as the machine
    /// suspends - produces messages right through teardown, and a rescan then would
    /// push a gamma ramp while the exit path is trying to take that same ramp off.
    /// </summary>
    public bool Ignore { get; set; }

    /// <summary>A display change happened. Collapses into whatever is already due.</summary>
    public void Notify()
    {
        if (!Ignore)
        {
            IsDue = true;
        }
    }

    /// <summary>
    /// Records that the rescan has happened. Called by the timer once the quiet
    /// period has passed, and never called twice for one burst - the flag is what
    /// makes that true rather than the caller's care.
    /// </summary>
    public void Consume() => IsDue = false;
}