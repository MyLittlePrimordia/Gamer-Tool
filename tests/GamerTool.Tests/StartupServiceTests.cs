using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The registry round trip for "Start with Windows".
/// <para>
/// This file had no tests at all, and shipped a reader that could not recognise a
/// single value the writer had ever produced, so the switch read off forever while
/// the app autostarted. These pin the parse against the exact shapes
/// <see cref="StartupService.SetEnabled"/> writes, plus the ones a different tool or
/// a hand edit could leave behind.
/// </para>
/// </summary>
public class StartupServiceTests
{
    [Theory]
    // The app's own install folder contains a space, which is the normal case and
    // was the one the old split-on-first-space reader could not survive.
    [InlineData(@"""C:\Users\User\AppData\Local\Gamer Tool\GamerTool.exe"" --tray", @"C:\Users\User\AppData\Local\Gamer Tool\GamerTool.exe")]
    [InlineData(@"""C:\Program Files\Gamer Tool\GamerTool.exe"" --tray", @"C:\Program Files\Gamer Tool\GamerTool.exe")]
    [InlineData(@"""C:\GamerTool\GamerTool.exe"" --tray", @"C:\GamerTool\GamerTool.exe")]
    [InlineData(@"""C:\GamerTool\GamerTool.exe""", @"C:\GamerTool\GamerTool.exe")]
    // No arguments at all, and stray whitespace around the value.
    [InlineData("  \"C:\\Gamer Tool\\GamerTool.exe\" --tray  ", @"C:\Gamer Tool\GamerTool.exe")]
    // An unquoted entry written by something else: the path is what precedes the args.
    [InlineData(@"C:\GamerTool\GamerTool.exe --tray", @"C:\GamerTool\GamerTool.exe")]
    [InlineData(@"C:\GamerTool\GamerTool.exe", @"C:\GamerTool\GamerTool.exe")]
    public void The_path_is_recovered_from_the_value_the_writer_produces(string stored, string expected)
    {
        Assert.Equal(expected, StartupService.ReadConfiguredPath(stored));
    }

    [Fact]
    public void A_quoted_path_keeps_every_space_inside_it()
    {
        // The regression in one line. Splitting on the first space turned this into
        // "C:\Users\User\AppData\Local\Gamer", whose leaf is "Gamer" and so never
        // matched "GamerTool.exe".
        string stored = "\"C:\\Users\\User\\AppData\\Local\\Gamer Tool\\GamerTool.exe\" --tray";

        string path = StartupService.ReadConfiguredPath(stored);

        Assert.Equal("GamerTool.exe", Path.GetFileName(path));
    }

    [Fact]
    public void The_closing_quote_is_not_left_attached_to_the_leaf()
    {
        // The other half. Trim('"') could only strip the leading quote, because the
        // value ends in the y of "--tray", leaving GamerTool.exe" behind.
        string stored = "\"C:\\GamerTool\\GamerTool.exe\" --tray";

        string path = StartupService.ReadConfiguredPath(stored);

        Assert.DoesNotContain("\"", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void A_value_with_no_usable_path_yields_nothing(string stored)
    {
        Assert.True(StartupService.ReadConfiguredPath(stored).Length == 0);
    }
}