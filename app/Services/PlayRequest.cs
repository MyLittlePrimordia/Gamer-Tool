using System;

namespace GamerTool.Services;

/// <summary>
/// The command line and the local message that mean "load this slot and start
/// its game".
/// <para>
/// Both halves are strings and both are parsed here, away from the window, so
/// the rules can be tested without a desktop. The command line is what a
/// shortcut carries; the payload is what a second launch sends to the copy that
/// already owns the hotkeys, the ramp and the settings file.
/// </para>
/// </summary>
public static class PlayRequest
{
    /// <summary>The switch a shortcut passes, followed by the slot id.</summary>
    public const string Switch = "--play";

    /// <summary>
    /// A slot id can be carried in a window message, but not an unbounded one.
    /// The generated ids are ten characters; this leaves room for an imported
    /// profile without turning a local message into a buffer to fill.
    /// </summary>
    public const int MaxSlotIdLength = 96;

    private const string PayloadPrefix = "GamerTool.Play\n";

    /// <summary>
    /// Reads the slot id out of command line arguments.
    /// <para>
    /// Accepts both <c>--play slot_x</c> and <c>--play=slot_x</c>. The first is
    /// what a shortcut writes and what a person is likely to type; the second is
    /// the shape every other command line convention offers, and refusing it
    /// would be a pointless surprise.
    /// </para>
    /// </summary>
    public static bool TryParse(string[]? args, out string slotId)
    {
        slotId = string.Empty;

        if (args is null)
        {
            return false;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] ?? string.Empty;

            if (arg.Equals(Switch, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length && TrySlotId(args[i + 1], out slotId);
            }

            if (arg.StartsWith(Switch + "=", StringComparison.OrdinalIgnoreCase))
            {
                return TrySlotId(arg[(Switch.Length + 1)..], out slotId);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a string can travel from a shortcut, through a window message,
    /// and back into a slot lookup.
    /// </summary>
    public static bool TrySlotId(string? value, out string slotId)
    {
        slotId = (value ?? string.Empty).Trim();

        if (slotId.Length == 0 || slotId.Length > MaxSlotIdLength)
        {
            return false;
        }

        // The payload is line based. A line break in an id would turn one
        // requested slot into two, and a NUL would end the native string early.
        foreach (char c in slotId)
        {
            if (c is '\r' or '\n' or '\0')
            {
                slotId = string.Empty;
                return false;
            }
        }

        return true;
    }

    /// <summary>The bytes a second launch puts in a WM_COPYDATA message.</summary>
    public static string Payload(string slotId) => PayloadPrefix + slotId;

    /// <summary>Reads a payload back, rejecting anything this app did not write.</summary>
    public static bool TryReadPayload(string? payload, out string slotId)
    {
        slotId = string.Empty;

        if (payload is null || !payload.StartsWith(PayloadPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string value = payload[PayloadPrefix.Length..].TrimEnd('\0');
        return TrySlotId(value, out slotId);
    }

    /// <summary>
    /// The arguments a generated shortcut carries.
    /// <para>
    /// <c>--tray</c> keeps the window out of the way on a cold start. On a warm
    /// one the running copy already exists and the argument is ignored, which is
    /// exactly what the play request wants.
    /// </para>
    /// </summary>
    public static string ShortcutArguments(string slotId) =>
        "--tray " + Switch + " " + Quote(slotId);

    /// <summary>Quotes a slot id for a Windows command line, if it needs it.</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
