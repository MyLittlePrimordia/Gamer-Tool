using System;

namespace GamerTool.Services;

/// <summary>
/// The rule behind the OLED guard, and the reading of how long a crosshair has been on
/// the screen.
/// <para>
/// Burn-in is cumulative: an OLED pixel ages with the current pushed through it, and a
/// small bright shape held in the same place for months is exactly the pattern that
/// leaves a mark. Nothing can undo that once it is there, and no monitor's own
/// protection is documented to notice a twenty pixel crosshair, so the only lever with
/// a real effect is spending less time lit. This is where that is decided.
/// </para>
/// <para>
/// Pure, so the whole rule can be checked without a desktop, a keyboard or a clock: the
/// tab asks it with a few facts and does what it says.
/// </para>
/// </summary>
public static class OledGuard
{
    /// <summary>
    /// Minutes without keyboard or mouse input before the crosshair steps aside.
    /// <para>
    /// Fifteen: long enough that nobody actually at the machine ever sees it, short
    /// enough that an afternoon away is an afternoon of an unlit panel. It is not a
    /// safety threshold - no source establishes one for an element this size - it is a
    /// guess at when "away" has stopped being a pause.
    /// </para>
    /// </summary>
    public const int IdleMinutes = 15;

    /// <summary>
    /// Input this recent means the user is back. A few seconds, because the guard polls
    /// every second while it is resting and the crosshair should be there by the time
    /// the hand reaches the mouse.
    /// </summary>
    public static readonly TimeSpan BackSeconds = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the crosshair should be taken off the screen: the guard is on, something
    /// is up, the user has been away for the whole idle period, and no game is in front.
    /// <para>
    /// The fullscreen exemption is for controllers. <c>GetLastInputInfo</c> sees a
    /// keyboard and a mouse and nothing else, so a player on a pad is "idle" from the
    /// first minute of a match - and a crosshair that vanishes mid-round is a worse
    /// failure than the wear it was avoiding.
    /// </para>
    /// </summary>
    public static bool ShouldRest(bool enabled, bool visible, bool gameFullscreen, TimeSpan idle)
    {
        if (!enabled || !visible || gameFullscreen)
        {
            return false;
        }

        return idle >= TimeSpan.FromMinutes(IdleMinutes);
    }

    /// <summary>Whether a crosshair the guard took away should come back.</summary>
    public static bool ShouldWake(bool rested, TimeSpan idle)
    {
        return rested && idle < BackSeconds;
    }

    /// <summary>
    /// How long a crosshair has been up, as the tab reads it: minutes under an hour,
    /// hours and minutes over it. Rounded down, because this is a total that only ever
    /// needs to be roughly right, and a number that ticks up every second draws the eye
    /// to a clock nobody asked to watch.
    /// </summary>
    public static string OnScreenFor(DateTime? since, DateTime now)
    {
        if (since is null)
        {
            return "0m";
        }

        TimeSpan elapsed = now - since.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return "<1m";
        }

        if (elapsed.TotalHours < 1)
        {
            return ((int)elapsed.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
        }

        int hours = (int)elapsed.TotalHours;
        int minutes = elapsed.Minutes;
        return hours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "h "
            + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
    }
}
