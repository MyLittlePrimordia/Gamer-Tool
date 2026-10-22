using System;
using System.Collections.Generic;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// Which screen the Display tab's Apply changes.
/// <para>
/// The saved value is a device name, and a device name is the one thing about a
/// monitor that Windows is happy to hand to different hardware after a cable move.
/// So the rule is not "use what was saved" but "use what was saved if that screen
/// is attached now, otherwise all of them" - a scope that has gone away must not
/// make Apply fail, and it must not silently pick a screen the user did not mean.
/// </para>
/// <para>
/// Pure, so the rule can be tested without a desktop: the window supplies the
/// attached list and the answer is a string.
/// </para>
/// </summary>
public static class DisplayScope
{
    /// <summary>
    /// The device to apply to, or empty when Apply should change every screen.
    /// </summary>
    public static string Resolve(string? saved, IEnumerable<string> attached)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return string.Empty;
        }

        // The attached spelling is returned rather than the saved one, because it
        // is about to be handed to CreateDC. A case-insensitive match is enough to
        // recognise the screen; it is not a promise that the API accepts the other
        // spelling of it.
        foreach (string device in attached)
        {
            if (string.Equals(device, saved, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return string.Empty;
    }
}
