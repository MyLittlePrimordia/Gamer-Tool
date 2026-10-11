using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// The target a slot should be pointed at, from the game that was last in front.
/// </summary>
/// <param name="Name">What to call the slot.</param>
/// <param name="ExePath">The executable to bind it to.</param>
/// <param name="ProcessName">The process name, which is what the watcher matches on.</param>
public readonly record struct CurrentGameTarget(string Name, string ExePath, string ProcessName);

/// <summary>
/// Binding a slot to whatever the user was just playing.
/// </summary>
/// <remarks>
/// The problem this solves has a shape that makes it look impossible: the control
/// that adds the game you are playing is a control you have to click, and clicking
/// it takes the game out of the foreground. So the answer has to have been noticed
/// before the click, not asked for at the moment of it.
/// <para>
/// That half already exists and has done for a while - <see cref="ProcessWatcherService"/>
/// remembers the last window that was neither this app nor the shell, with a
/// freshness window, and reports it through <c>LastForeign</c>. What was missing was
/// the other half: nothing in the app ever read it. The remembered window, the
/// naming rule and their tests were all written, and no control was ever added, so
/// the feature existed everywhere except in front of the user.
/// </para>
/// <para>
/// The logic is out here rather than in the window for the same reason the watcher's
/// is: a decision that can be handed a window and a slot list is a decision a test
/// can make, and the window is left with the part that genuinely needs one - which
/// control to update and what to say afterwards.
/// </para>
/// </remarks>
public static class AddCurrentGame
{
    /// <summary>
    /// The target to offer, or null when there is nothing worth offering.
    /// </summary>
    /// <param name="remembered">The last window that was not this app or the shell.</param>
    /// <param name="rememberedAt">When it was seen.</param>
    /// <param name="now">The current time, passed in so the staleness rule is testable.</param>
    /// <remarks>
    /// Refuses on three conditions, and each is a wrong answer the user would
    /// otherwise be offered:
    /// <list type="bullet">
    /// <item>
    /// Nothing remembered. The app has not seen a game since it started, which is
    /// the ordinary state on a machine where the user has only opened this window.
    /// </item>
    /// <item>
    /// A memory that is too old. The button is still on screen long after the user
    /// has walked away, and an answer from an hour ago is a wrong answer with a
    /// confident look about it. See <see cref="ProcessWatcherService.RecentForeignWindow"/>.
    /// </item>
    /// <item>
    /// A remembered window with no usable executable path. Without a path there is
    /// nothing to bind a slot to, and a slot bound to an empty path would match
    /// nothing and look broken rather than look unset.
    /// </item>
    /// </list>
    /// </remarks>
    public static CurrentGameTarget? From(
        WatchedWindow? remembered,
        System.DateTime rememberedAt,
        System.DateTime now)
    {
        if (remembered is null)
        {
            return null;
        }

        if (now - rememberedAt >= ProcessWatcherService.RecentForeignWindow)
        {
            return null;
        }

        string path = remembered.ExePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string process = remembered.ProcessName ?? string.Empty;

        // The name comes from the window title where that is the better answer and
        // from the process name otherwise, which is the rule NameForCurrentGame
        // already implements and this deliberately reuses rather than restates.
        string name = SlotService.NameForCurrentGame(remembered);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CurrentGameTarget(name, path, process);
    }

    /// <summary>
    /// Whether a slot already points at this executable.
    /// </summary>
    /// <remarks>
    /// The window uses this to decide between adding a slot and moving an existing
    /// one. Adding a second slot for a program that already has one is how a user
    /// ends up with two rows for one game, one of which they have forgotten about,
    /// and no way to tell which of them actually fires.
    /// </remarks>
    public static HotkeySlot? SlotFor(System.Collections.Generic.IReadOnlyList<HotkeySlot> slots, string exePath)
    {
        if (slots is null || string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        foreach (HotkeySlot slot in slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.AppExePath)
                && string.Equals(slot.AppExePath, exePath, System.StringComparison.OrdinalIgnoreCase))
            {
                return slot;
            }
        }

        return null;
    }
}
