using System;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// The extra words a slot's Play hands to the game.
/// <para>
/// Nothing here is a command line for a shell: the arguments are set on a
/// <see cref="System.Diagnostics.ProcessStartInfo"/> alongside the executable the
/// slot already names, so they arrive as one string the program parses for itself.
/// What this class does is refuse the shapes a text box invites - a pasted line
/// break, a trailing tab, a paragraph - because those reach the program as
/// argument separators or as part of a value, and the user sees only a game that
/// started without the flag they typed.
/// </para>
/// </summary>
public static class LaunchArguments
{
    /// <summary>
    /// Long enough for the flags a game launcher actually takes, short enough that
    /// the field cannot be used as a place to keep a file.
    /// </summary>
    public const int Limit = 512;

    /// <summary>
    /// The arguments as they will be passed: one line, trimmed, and no longer than
    /// <see cref="Limit"/> characters.
    /// </summary>
    public static string Normalise(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        StringBuilder clean = new(raw.Length);

        foreach (char c in raw)
        {
            // A control character cannot survive the trip to another process
            // unchanged: a newline or a tab is whitespace there, and the rest are
            // dropped by the shell. Made into a space, which is what the user meant
            // by pressing it.
            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        string text = clean.ToString().Trim();

        return text.Length <= Limit ? text : text[..Limit].TrimEnd();
    }
}
