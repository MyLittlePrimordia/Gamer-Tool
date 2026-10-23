using System;
using System.IO;
using System.Linq;

namespace GamerTool.Tests;

/// <summary>
/// Reads the slot board's source for the tests that assert on it.
/// <para>
/// The board used to be one file and around a dozen test files read it by path.
/// It is now ten partials - board (itself three: the list actions, the rows and
/// the pickers), keys, play (itself three: the flow, the launch and the undo
/// record), watch, routing and apps - and the tests care about the text, not the
/// file it happens to sit in. Reading every
/// <c>MainWindow.Slots*.cs</c> in a stable order keeps those assertions working
/// and keeps them working after the next time a member moves between them, which
/// is the whole point of not pinning a path.
/// </para>
/// <para>
/// Deliberately still one string: these tests look up a member by its declaration
/// and read forward from there, so the concatenation has to keep each member
/// intact. It does - the files are joined at their boundaries, not merged.
/// </para>
/// </summary>
internal static class SlotsSource
{
    /// <summary>The single file the board used to be, still accepted as a request.</summary>
    public const string FormerFileName = "MainWindow.Slots.cs";

    /// <summary>True when <paramref name="path"/> names the old single slots file.</summary>
    public static bool IsSlotsFile(string path) =>
        string.Equals(Path.GetFileName(path), FormerFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every slots partial in <paramref name="folder"/>, joined in file-name order.</summary>
    public static string ReadFolder(string folder)
    {
        string[] files = Directory.GetFiles(folder, "MainWindow.Slots*.cs");
        Array.Sort(files, StringComparer.Ordinal);

        return string.Join("\n", files.Select(File.ReadAllText));
    }

    /// <summary>
    /// The partials beside <paramref name="anySlotsFile"/>. For callers that have
    /// already resolved one path and would otherwise have to work out its folder.
    /// </summary>
    public static string ReadNear(string anySlotsFile) =>
        ReadFolder(Path.GetDirectoryName(Path.GetFullPath(anySlotsFile))!);
}
