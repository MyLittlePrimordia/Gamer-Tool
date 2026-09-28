using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Every resource key a XAML file names is actually defined.
/// <para>
/// This exists because the app shipped a build that compiled clean, passed every
/// test in this project, and then refused to start with "Provide value on
/// 'System.Windows.StaticResourceExtension' threw an exception". A style had been
/// deleted from the theme in one change and referenced by name in a later one, and
/// nothing noticed for three edits and a full publish.
/// <para>
/// A missing StaticResource is a runtime failure by design: the XAML compiler has
/// no way to know which dictionary a key will be in, because resources resolve
/// against whatever merged dictionaries are in scope at load time. So neither the
/// build nor the test project can see it, and the only thing that catches it is
/// somebody running the app. Which is exactly what happened.
/// </para>
/// </summary>
public class XamlResourceTests
{
    /// <summary>
    /// Walks up from the test assembly to the repository root, so the test does not
    /// care what directory the runner was started in.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return dir!.FullName;
    }

    private static IReadOnlyList<string> XamlFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "app"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\") && !p.Contains(@"\bin\"))
            .ToArray();

    /// <summary>Keys declared with x:Key, across every resource dictionary.</summary>
    private static IReadOnlySet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in XamlFiles())
        {
            string text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, "x:Key=\"([A-Za-z_][A-Za-z0-9_.]*)\""))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
    }

    /// <summary>
    /// Keys referenced as {StaticResource Name} or {DynamicResource Name}.
    /// <para>
    /// Skips anything inside an XML comment, because the prose in this file's
    /// comments names StaticResource often enough to be picked up otherwise, and a
    /// resource key called "is" is not something a theme file declares.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(string Key, string File, int Line)> ReferencedKeys()
    {
        var found = new List<(string, string, int)>();
        var comment = new Regex(@"<!--.*?-->", RegexOptions.Singleline);

        foreach (string file in XamlFiles())
        {
            string raw = File.ReadAllText(file);
            string text = comment.Replace(raw, string.Empty);

            int line = 1;

            foreach (string source in text.Split('\n'))
            {
                foreach (Match match in Regex.Matches(source, @"(?:Static|Dynamic)Resource\s+([A-Za-z_][A-Za-z0-9_.]*)"))
                {
                    found.Add((match.Groups[1].Value, Path.GetFileName(file), line));
                }

                line++;
            }
        }

        return found;
    }

    [Fact]
    public void Every_resource_key_a_xaml_file_names_is_defined_somewhere()
    {
        IReadOnlySet<string> defined = DefinedKeys();
        IReadOnlyList<(string Key, string File, int Line)> referenced = ReferencedKeys();

        Assert.NotEmpty(referenced);

        List<string> missing = referenced
            .Where(r => !defined.Contains(r.Key))
            .Select(r => r.Key + "  (used in " + r.File + " line " + r.Line + ")")
            .Distinct()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "XAML names resource keys that nothing declares. The app builds and the tests"
            + " pass, and then throws StaticResourceExtension at startup:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void The_theme_defines_a_usable_number_of_keys()
    {
        // Guards the test itself. If the walk stopped finding the theme the
        // previous test would pass by comparing an empty set against an empty list,
        // which is a test that cannot fail.
        IReadOnlySet<string> defined = DefinedKeys();

        Assert.True(defined.Count > 40, "expected a full theme, found " + defined.Count + " keys");
    }

    [Fact]
    public void The_info_hint_button_style_is_still_declared()
    {
        // Pinned by name because it was deleted and then referenced again, which is
        // the specific version of the bug above that actually shipped. One shared
        // style, one place to go wrong, and greppable.
        IReadOnlySet<string> defined = DefinedKeys();

        Assert.Contains("InfoHintButton", defined);
    }
}
