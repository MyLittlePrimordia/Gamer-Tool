using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Tidying up after itself in FxSound's preset folder.
/// <para>
/// The equaliser file has to be written there - it is the only documented route
/// for getting a band curve into the engine - but the app was leaving it behind
/// on exit. That makes Gamer Tool an app that puts a file in another program's
/// data directory and never takes it away, in a tool whose entire claim is that
/// it leaves nothing behind but one settings file.
/// </para>
/// <para>
/// These tests are as much about what the cleanup must NOT do as about what it
/// does. A presets folder belongs to the user, and a delete that is one path
/// wrong in either direction is either litter left behind or somebody's
/// equaliser gone.
/// </para>
/// </summary>
public class FxPresetCleanupTests
{
    private static string MakeFolder()
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "GamerToolFacTests-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Gone(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<double> Frequencies(int bands) =>
        Enumerable.Range(0, bands)
            .Select(i => AudioPreset.BandFrequency(bands, i))
            .ToList();

    [Fact]
    public void The_written_preset_is_removed_again()
    {
        string folder = MakeFolder();

        try
        {
            string? written = FxPresetFile.Write(AudioPreset.Defaults.First(p => p.Id == "footstep"), Frequencies(10), true, folder);
            Assert.NotNull(written);
            Assert.True(File.Exists(written!));

            FxPresetFile.RemoveWrittenPreset(folder);

            Assert.False(File.Exists(written!), "the preset the app wrote is still sitting in the folder");
        }
        finally
        {
            Gone(folder);
        }
    }

    [Fact]
    public void Somebody_elses_presets_are_left_alone()
    {
        // The whole point of naming the file rather than clearing the folder. An
        // FxSound user's own presets live in exactly this directory, and the one
        // file this app has any business touching is the one it wrote.
        string folder = MakeFolder();

        try
        {
            string? mine = FxPresetFile.Write(AudioPreset.Defaults.First(p => p.Id == "footstep"), Frequencies(10), true, folder);
            Assert.NotNull(mine);

            string[] theirs =
            {
                Path.Combine(folder, "Warm Master.fac"),
                Path.Combine(folder, "Bass Boost.fac"),
                Path.Combine(folder, FxPresetFile.PresetName + ".fxp")
            };

            foreach (string path in theirs)
            {
                File.WriteAllText(path, "not ours");
            }

            FxPresetFile.RemoveWrittenPreset(folder);

            foreach (string path in theirs)
            {
                Assert.True(File.Exists(path), "cleanup deleted " + Path.GetFileName(path) + ", which it did not write");
            }
        }
        finally
        {
            Gone(folder);
        }
    }

    [Fact]
    public void The_folder_itself_survives()
    {
        // Deleting the directory to be thorough would take every preset the user
        // has, and FxSound itself expects the folder to be there.
        string folder = MakeFolder();

        try
        {
            _ = FxPresetFile.Write(AudioPreset.Defaults.First(p => p.Id == "footstep"), Frequencies(10), true, folder);
            FxPresetFile.RemoveWrittenPreset(folder);

            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            Gone(folder);
        }
    }

    [Fact]
    public void Removing_a_preset_that_was_never_written_is_harmless()
    {
        // The exit path runs whether or not anything was ever applied, including
        // on a machine with no FxSound at all and therefore no folder. It must not
        // be able to fail a shutdown.
        string folder = MakeFolder();

        try
        {
            FxPresetFile.RemoveWrittenPreset(folder);
            FxPresetFile.RemoveWrittenPreset(folder);
        }
        finally
        {
            Gone(folder);
        }
    }

    [Fact]
    public void Removing_from_a_folder_that_does_not_exist_is_harmless()
    {
        FxPresetFile.RemoveWrittenPreset(
            Path.Combine(Path.GetTempPath(), "GamerToolFacTests-" + Guid.NewGuid().ToString("N")));
    }
}
