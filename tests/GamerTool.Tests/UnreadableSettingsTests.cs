using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The settings file that exists but cannot be read.
/// <para>
/// The protection is right: a file the app never managed to read must not be
/// overwritten with defaults, because its contents may be the only copy of the
/// user's presets. What was wrong with it was that the protection never lifted.
/// The flag was raised once during Load and stayed raised, so every save for the
/// rest of the session was dropped - and because the window had already reported a
/// backup restore as done, the user was told their work was safe while it was
/// being thrown away on quit.
/// </para>
/// <para>
/// These pin the timing: still locked means still protected, and once the lock
/// goes the save has to go through, with the original file kept aside rather than
/// destroyed.
/// </para>
/// </summary>
public sealed class UnreadableSettingsTests : IDisposable
{
    private readonly string _folder;

    public UnreadableSettingsTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "GamerToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    /// <summary>
    /// Reads one field back out of the saved file.
    /// <para>
    /// Parsed rather than matched as text, because the writer pretty-prints with a
    /// space after each colon and a substring assertion on the exact spacing fails
    /// for reasons that have nothing to do with what is being tested here.
    /// </para>
    /// </summary>
    private int SavedBlueLightFilter()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));

        return document.RootElement.GetProperty("BlueLightFilter").GetInt32();
    }

    [Fact]
    public void A_save_is_still_refused_while_the_file_cannot_be_read()
    {
        File.WriteAllText(SettingsPath, "{\"Schema\":2,\"BlueLightFilter\":7}");

        ProfileManager profiles = new(_folder);

        // Held the way an antivirus scan or a sync client holds it: nothing else may
        // open the file, not even for reading.
        using (FileStream held = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            profiles.Load();
            profiles.Save(new AppSettings { BlueLightFilter = 1 });
        }

        // Read after the lock is released, because the handle holding it denies
        // everything, and the content is the evidence either way: untouched it still
        // says 7, overwritten it would say 1.
        Assert.Equal(7, SavedBlueLightFilter());
    }

    [Fact]
    public void A_save_goes_through_once_the_lock_clears()
    {
        File.WriteAllText(SettingsPath, "{\"Schema\":2,\"BlueLightFilter\":7}");

        ProfileManager profiles = new(_folder);

        using (FileStream held = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            profiles.Load();
            profiles.Save(new AppSettings { BlueLightFilter = 1 });
        }

        Assert.Equal(7, SavedBlueLightFilter());

        // The lock is gone, which is the ordinary case: whatever held the file has
        // finished with it. The session's work must now be saveable, or a single
        // unlucky moment at launch costs the user everything they did afterwards.
        profiles.Save(new AppSettings { BlueLightFilter = 3 });

        Assert.Equal(3, SavedBlueLightFilter());
    }

    [Fact]
    public void The_file_it_could_not_read_is_kept_rather_than_destroyed()
    {
        File.WriteAllText(SettingsPath, "{\"Schema\":2,\"BlueLightFilter\":7}");

        ProfileManager profiles = new(_folder);

        using (FileStream held = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            profiles.Load();
        }

        profiles.Save(new AppSettings { BlueLightFilter = 3 });

        // The original content still exists somewhere under a stamped name. This is
        // the whole reason the save is allowed to proceed at all.
        string[] kept = Directory.GetFiles(_folder, "settings.json.corrupt.*.json");

        Assert.NotEmpty(kept);
        using JsonDocument keptDocument = JsonDocument.Parse(File.ReadAllText(kept[0]));
        Assert.Equal(7, keptDocument.RootElement.GetProperty("BlueLightFilter").GetInt32());
    }
}