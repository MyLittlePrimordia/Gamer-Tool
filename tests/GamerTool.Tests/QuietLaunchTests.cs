using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The launcher that starts the engine's status read without the busy cursor.
/// What can be checked from a test is that it still behaves like starting a
/// program and waiting for it: it runs it, reports the exit code, and says so when
/// nothing could be started so the caller can fall back. The cursor itself is a
/// property of the flag and has to be looked at on a screen.
/// </summary>
public class QuietLaunchTests
{
    private static string Cmd =>
        Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void A_program_that_succeeds_reports_exit_zero()
    {
        bool started = QuietLaunch.TryRun(Cmd, "/c exit 0", 10000, out bool exited, out int code);

        Assert.True(started);
        Assert.True(exited);
        Assert.Equal(0, code);
    }

    [Fact]
    public void The_exit_code_comes_back_unchanged()
    {
        bool started = QuietLaunch.TryRun(Cmd, "/c exit 7", 10000, out bool exited, out int code);

        Assert.True(started);
        Assert.True(exited);
        Assert.Equal(7, code);
    }

    [Fact]
    public void A_program_that_does_not_exist_is_reported_as_not_started()
    {
        string missing = Path.Combine(Path.GetTempPath(), "GamerToolNoSuchProgram-" + Guid.NewGuid().ToString("N") + ".exe");

        bool started = QuietLaunch.TryRun(missing, "--status", 1000, out bool exited, out _);

        // False means nothing ran, which is the caller's cue to start it the
        // ordinary way instead.
        Assert.False(started);
        Assert.False(exited);
    }

    [Fact]
    public void A_path_with_spaces_reaches_the_program_in_one_piece()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Gamer Tool Quiet " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "quiet cmd.exe");

        try
        {
            File.Copy(Cmd, copy);

            bool started = QuietLaunch.TryRun(copy, "/c exit 3", 10000, out bool exited, out int code);

            Assert.True(started);
            Assert.True(exited);
            Assert.Equal(3, code);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
