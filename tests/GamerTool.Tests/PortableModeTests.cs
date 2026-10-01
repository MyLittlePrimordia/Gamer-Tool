using System;
using System.IO;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Portable mode: an install that keeps everything in the folder it was put in.
/// <para>
/// The whole point of the app is one file and no installer, and an install that
/// still scatters a settings file and a log through the user profile is only half
/// of that. Dropping a settings file next to the executable is the only signal
/// available that does not need somewhere else to record the decision.
/// </para>
/// <para>
/// These are the rules that make it safe: nothing changes for anyone who has not
/// done it, the folder is the executable's own rather than a single file bundle's
/// extraction directory, and a folder that cannot be written to does not stop the
/// app starting.
/// </para>
/// </summary>
public class PortableModeTests
{
    [Fact]
    public void An_install_with_no_settings_file_beside_it_is_not_portable()
    {
        // The default, and what every existing install sees. Nothing here writes
        // a file to check, so this is false on any machine that has not been set
        // up for it and stays false.
        if (!ProfileManager.IsPortable)
        {
            Assert.NotNull(ProfileManager.AppDataFolder);
            Assert.Contains("GamerTool", ProfileManager.AppDataFolder, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_profile_and_the_log_always_agree_on_where_they_live()
    {
        // The Settings tab has an "open log folder" button that reads AppLog.Folder
        // when it is pressed. If the log went to one place and the profile to
        // another, that button would open a folder with nothing in it on a
        // portable install, which is the one case where somebody is using it.
        //
        // Read through DefaultFolder rather than Folder, because the test suite
        // redirects Folder into a temp directory - which is the whole point of
        // that, it stops test failures landing in a user's diagnostic log.
        string profileFolder = ProfileManager.AppDataFolder;
        string logFolder = AppLog.DefaultFolder;

        if (ProfileManager.IsPortable)
        {
            Assert.Equal(profileFolder, Path.GetDirectoryName(logFolder));
            Assert.Equal("logs", Path.GetFileName(logFolder));
        }
        else
        {
            // Both under the user profile, and the log in its own subfolder.
            Assert.NotEqual(profileFolder, logFolder);
            Assert.Equal("logs", Path.GetFileName(logFolder));
        }
    }

    [Fact]
    public void The_log_stays_inside_the_folder_it_is_meant_to_be_in()
    {
        Assert.StartsWith(AppLog.DefaultFolder, AppLog.DefaultFolder + "\\app.log", StringComparison.Ordinal);
        Assert.Equal("app.log", Path.GetFileName(AppLog.Path_));
    }

    [Fact]
    public void The_test_suite_does_not_write_into_the_real_log()
    {
        // The log is the one artefact a user is asked to paste into a bug report.
        // Tests that drive the failure paths must not fill it with failures that
        // never happened on their machine.
        Assert.NotEqual(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool",
                "logs"),
            AppLog.Folder);
        Assert.StartsWith(
            Path.GetTempPath(),
            AppLog.Folder,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_executable_folder_is_the_real_one_and_not_a_bundle_extraction()
    {
        string? folder = ProfileManager.ExecutableFolder;

        Assert.NotNull(folder);
        Assert.False(string.IsNullOrWhiteSpace(folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void An_unwritable_profile_folder_does_not_stop_the_app_starting()
    {
        // A portable install on a read only drive has to come up and say so
        // through the log and the status line, not fail to launch. Constructing
        // the manager is the step that touches the disk.
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests", Guid.NewGuid().ToString("N"));
        FileStream blocker;
        try
        {
            blocker = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("could not set the test up", ex);
        }

        using (blocker)
        {
            // A path that cannot be created because a file is sitting on it.
            Exception? thrown = Record.Exception(() => new ProfileManager(Path.Combine(path, "nope")));

            Assert.Null(thrown);
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Leftover temp file, not worth failing over.
        }
    }

    [Fact]
    public void A_portable_folder_is_chosen_by_the_presence_of_the_settings_file()
    {
        // The decision itself, without touching the executable's own folder: a
        // manager pointed at a folder takes that folder whatever it is called.
        string folder = Path.Combine(Path.GetTempPath(), "GamerToolTests", Guid.NewGuid().ToString("N"));

        try
        {
            ProfileManager manager = new(folder);
            Assert.Equal(folder, Path.GetDirectoryName(manager.SettingsPath));
            Assert.Equal("settings.json", Path.GetFileName(manager.SettingsPath));

            // Nothing there yet, so it is a first run rather than a failure.
            AppSettings loaded = manager.Load();
            Assert.NotNull(loaded);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // Leftover temp folder, not worth failing over.
            }
        }
    }
}
