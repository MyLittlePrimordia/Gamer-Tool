using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GamerTool.Services;

/// <summary>
/// The desktop shortcut that starts one slot's game with its profile.
/// <para>
/// A <c>.lnk</c> rather than a command file: a console window flashing up before
/// a game is exactly the kind of friction this feature exists to remove, and the
/// shortcut is also what lets the icon and the name be the game's rather than a
/// script's. The COM object is reached by its registered ProgID, so there is no
/// extra package and no new deployment requirement; every supported Windows
/// install has it.
/// </para>
/// <para>
/// A failure is reported rather than thrown. Some locked-down machines refuse
/// COM object creation from an ordinary process, and the right answer there is
/// "the shortcut was not made", not an app fault dialog over a feature the user
/// can simply skip.
/// </para>
/// </summary>
public static class PlayShortcut
{
    /// <summary>
    /// A safe file name for the shortcut, before the .lnk extension.
    /// <para>
    /// Slot names are free text, including characters Windows refuses in a file
    /// name. Those become a dash rather than disappearing, so two slots with
    /// names that differ only by a forbidden character do not collapse onto the
    /// same file.
    /// </para>
    /// </summary>
    public static string FileNameFor(string slotName)
    {
        string name = string.IsNullOrWhiteSpace(slotName) ? "Play slot" : slotName.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char bad in invalid)
        {
            name = name.Replace(bad, '-');
        }

        // A trailing dot or space is legal to type and illegal to end a file name
        // with on Windows. Trim after the substitution, not before it.
        name = name.Trim().TrimEnd('.', ' ');

        if (name.Length == 0)
        {
            name = "Play slot";
        }

        if (name.Length > 80)
        {
            name = name[..80].TrimEnd('.', ' ');
        }

        return name.Length == 0 ? "Play slot" : name;
    }

    public static string PathFor(string folder, string slotName) =>
        Path.Combine(folder, FileNameFor(slotName) + ".lnk");

    /// <summary>
    /// Writes the shortcut, replacing one with the same name.
    /// </summary>
    public static bool TryCreate(
        string folder,
        string slotName,
        string slotId,
        out string shortcutPath,
        out string error)
    {
        shortcutPath = PathFor(folder, slotName);
        error = string.Empty;

        if (!PlayRequest.TrySlotId(slotId, out string id))
        {
            error = "This slot has no usable id.";
            return false;
        }

        string? app = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(app) || !File.Exists(app))
        {
            app = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(app) || !File.Exists(app))
        {
            error = "Could not find the Gamer Tool executable.";
            return false;
        }

        object? shell = null;
        object? shortcut = null;

        try
        {
            Directory.CreateDirectory(folder);

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                error = "Windows did not provide the shortcut service.";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                error = "Windows did not provide the shortcut service.";
                return false;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shell,
                new object[] { shortcutPath });

            if (shortcut is null)
            {
                error = "The shortcut could not be created.";
                return false;
            }

            Type linkType = shortcut.GetType();

            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { app });
            linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { PlayRequest.ShortcutArguments(id) });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(app) ?? string.Empty });
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { app + ",0" });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Play " + slotName + " with its Gamer Tool profile" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // The process is about to let go of it anyway.
            }
        }
    }
}
