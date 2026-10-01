using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The privacy boundary of the diagnostic log.
/// <para>
/// The Settings tab has a button whose whole job is to put this on the clipboard
/// for a user to paste into a public bug report. That makes the redaction here the
/// difference between a support log and a disclosure of where somebody keeps their
/// games and what their account is called, so it is worth pinning rather than
/// trusting.
/// </para>
/// <para>
/// It is also the one part of the logging path that is pure enough to test
/// properly: everything else writes files and reads the audio engine.
/// </para>
/// </summary>
public class AppLogSanitiseTests
{
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string UserName => Environment.UserName;

    /// <summary>
    /// The redacted form of a user path segment.
    /// <para>
    /// Matched as a whole segment rather than as the bare name, because the
    /// marker itself contains the word: the output reads <c>C:\Users\&lt;user&gt;</c>,
    /// so a machine whose account happens to be called "User" would fail any
    /// assertion looking for the bare string. That is a property of the marker,
    /// not a leak.
    /// </para>
    /// </summary>
    private static string UserSegment => @"\Users\" + UserName;

    [Fact]
    public void The_profile_path_is_replaced()
    {
        string text = "could not read " + Path.Combine(Profile, "AppData", "Roaming", "GamerTool", "settings.json");

        string result = AppLog.Sanitise(text);

        Assert.DoesNotContain(Profile, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_replacement_is_case_insensitive()
    {
        // Windows paths arrive from the registry and from FxSound with whatever
        // casing they happened to be written with.
        string text = "at " + Profile.ToUpperInvariant() + "\\app.log";

        Assert.DoesNotContain(Profile, AppLog.Sanitise(text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_device_path_form_is_replaced()
    {
        // The display APIs hand back \\?\C:\... rather than C:\..., and the
        // profile replace alone does not catch it - the prefix sits between the
        // slash and the drive.
        string text = @"\\?\" + Path.Combine(Profile, "AppData") + @"\thing";

        string result = AppLog.Sanitise(text);

        Assert.DoesNotContain(UserSegment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_bare_user_name_is_replaced_inside_a_path()
    {
        string text = @"not here: C:\Users\" + UserName + @"\stuff";

        string result = AppLog.Sanitise(text);

        Assert.DoesNotContain(UserSegment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_is_changed_when_there_is_nothing_to_redact()
    {
        const string Text = "backlight probe ended Refused: I2C_ERROR_RECEIVING_DATA";

        Assert.Equal(Text, AppLog.Sanitise(Text));
    }

    [Fact]
    public void Empty_and_null_are_handled_rather_than_thrown_at()
    {
        Assert.Equal(string.Empty, AppLog.Sanitise(string.Empty));
        Assert.Equal(string.Empty, AppLog.Sanitise(null!));
    }

    [Fact]
    public void Every_occurrence_is_replaced_not_only_the_first()
    {
        // Replace, not a single substitution: a line that names the profile twice
        // would otherwise leave the second one behind, which is the half that
        // actually ends up in the pasted log.
        string text = Path.Combine(Profile, "a") + " then " + Path.Combine(Profile, "b");

        string result = AppLog.Sanitise(text);

        Assert.Equal(2, CountOccurrences(result, "<user>"));
        Assert.DoesNotContain(Profile, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_whole_log_body_can_be_redacted_without_throwing()
    {
        // What the button actually does is hand the entire log file through here,
        // so this is the shape that matters rather than a single path.
        string body = string.Join(
            Environment.NewLine,
            "2026-01-01  INFO   start",
            "  profile was " + Profile,
            @"  device \\?\" + Profile + @"\DISPLAY1",
            "  nothing to redact here",
            "  exception at " + Path.Combine(Profile, "app", "GamerTool.dll"));

        string result = AppLog.Sanitise(body);

        Assert.DoesNotContain(Profile, result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserSegment, result, StringComparison.OrdinalIgnoreCase);

        // And the useful part survives, because a redacted log that says nothing
        // is no use to anybody.
        Assert.Contains("nothing to redact here", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_shaped_string_never_carries_the_user_name_through()
    {
        // The property that matters, stated as a property rather than as a list of
        // examples: after redaction, no fragment that identifies this machine may
        // survive. Written as a loop over the shapes that actually turn up so a
        // new one is a new entry rather than a new worry.
        string[] shapes =
        {
            Path.Combine(Profile, "a"),
            @"\\?\" + Profile + @"\b",
            @"C:\Users\" + UserName + @"\c",
            Profile.ToUpperInvariant() + @"\d",
            @"\\.\PIPE\" + UserName,
        };

        foreach (string shape in shapes)
        {
            string result = AppLog.Sanitise("path: " + shape);
            Assert.DoesNotContain(UserSegment, result, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_one_character_user_name_is_still_redacted()
    {
        // The reason this is plain string replacement rather than a pattern: a
        // user name that is a single letter, or that ends in a backslash, made the
        // regular expression illegal, and that was a crash on the very first line
        // of startup - on the path that every log write goes through.
        //
        // Asserted through the overload that takes the name, because no machine
        // running these tests has a one character account and the behaviour that
        // matters is exactly that one.
        //
        // The profile is deliberately somewhere the first replacement cannot see,
        // because otherwise it would redact the name on its own and the test would
        // pass with the second one switched off entirely - which is what happened
        // the first time this was written. It has to be the case the guard
        // actually decided: the name appears in a path, and nothing else in the
        // function would have removed it.
        const string Profile = @"D:\Profiles\placeholder";

        foreach (string name in new[] { "a", "ab", "x", "1", "User\\", "a.b" })
        {
            string userPath = @"C:\Users\" + name;

            foreach (string shape in new[]
            {
                userPath + @"\file",
                @"\\?\" + userPath + @"\file",
            })
            {
                string result = AppLog.Sanitise(shape, Profile, name);

                Assert.DoesNotContain(userPath + @"\", result, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("<user>", result, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_second_replacement_is_what_covers_what_the_first_cannot()
    {
        // Said separately, because it is the load-bearing part of the above: a
        // profile on another drive, or a machine that keeps its profile somewhere
        // unusual, leaves the first replacement with nothing to match. The name
        // then survives unless the second one runs, and it used to be skipped
        // outright for short names.
        string result = AppLog.Sanitise(
            @"display \\?\C:\Users\ab\DISPLAY1",
            @"D:\Profiles\placeholder",
            "ab");

        Assert.Equal(@"display \\?\C:\Users\<user>\DISPLAY1", result);
    }

    [Fact]
    public void An_awkward_user_name_does_not_throw()
    {
        // The original reason for avoiding a pattern. Whatever the name is, the
        // call has to return rather than fault, because it is on the first log
        // write of the process.
        foreach (string name in new[] { "a", "ab", "User\\", "*", "[x]", "(y)", "+", "?", "1", "  " })
        {
            string result = AppLog.Sanitise("path @" + name, @"C:\Users\someone", name);

            Assert.NotNull(result);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
