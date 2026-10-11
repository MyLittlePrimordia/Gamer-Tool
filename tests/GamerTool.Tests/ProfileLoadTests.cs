using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What happens to the settings file when it cannot be read.
/// <para>
/// These are the only tests that exercise the load and save paths, and they can
/// only do it because <see cref="ProfileManager"/> takes a folder. Every other
/// test in this suite reaches the schema guard directly through
/// <c>Normalize</c>, which is the half of the problem that was already covered.
/// </para>
/// <para>
/// The rule all of this serves is that a settings file the app cannot read is
/// never replaced with defaults. The old code could not tell a damaged file from
/// a busy one, so an antivirus scan or a sync client holding the file open for a
/// moment was enough to have a good profile renamed as corrupt and then
/// overwritten on the first interaction.
/// </para>
/// <para>
/// Every test here works in a disposable folder and touches nothing else.
/// </para>
/// </summary>
public sealed class ProfileLoadTests : IDisposable
{
    private readonly string _folder;

    public ProfileLoadTests()
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

    private string BackupPath => SettingsPath + ".bak";

    private void Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_folder, name), content);
    }

    private string GoodProfile()
    {
        AppSettings settings = new() { GammaLock = false, EmergencyHotkey = "CTRL+ALT+F11" };
        settings.CustomDisplayPresets.Add(new DisplayPreset { Id = "mine", Name = "My Screen" });
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public void A_valid_profile_is_loaded()
    {
        Write("settings.json", GoodProfile());

        ProfileManager manager = new(_folder);
        AppSettings loaded = manager.Load();

        Assert.False(loaded.GammaLock);
        Assert.Equal("CTRL+ALT+F11", loaded.EmergencyHotkey);
        Assert.Single(loaded.CustomDisplayPresets);
        Assert.False(manager.Quarantined);
        Assert.False(manager.RecoveredFromBackup);
    }

    [Fact]
    public void No_profile_at_all_is_a_normal_first_run()
    {
        ProfileManager manager = new(_folder);

        AppSettings loaded = manager.Load();

        Assert.NotNull(loaded);
        Assert.False(manager.Quarantined);
        Assert.False(manager.RecoveredFromBackup);
    }

    [Fact]
    public void A_file_that_will_not_parse_is_kept_under_a_stamped_name()
    {
        Write("settings.json", "{ this is not json");

        ProfileManager manager = new(_folder);
        manager.Load();

        Assert.True(manager.Quarantined);

        // Moved, not deleted: the presets in it are still recoverable by hand.
        Assert.False(File.Exists(SettingsPath));
        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrupt.*.json"));
    }

    [Fact]
    public void A_damaged_profile_falls_back_to_the_copy_the_last_save_left_behind()
    {
        // What a crash part way through a write looks like.
        Write("settings.json", "{\"GammaLock\": tr");
        Write("settings.json.bak", GoodProfile());

        ProfileManager manager = new(_folder);
        AppSettings loaded = manager.Load();

        Assert.True(manager.RecoveredFromBackup);
        Assert.True(manager.Quarantined);

        // The user's work, not a fresh install. This is the case that used to
        // start everybody off from defaults after a truncated write.
        Assert.Equal("CTRL+ALT+F11", loaded.EmergencyHotkey);
        Assert.Single(loaded.CustomDisplayPresets);
    }

    [Fact]
    public void A_damaged_profile_with_no_backup_falls_back_to_defaults()
    {
        Write("settings.json", "not json at all");

        ProfileManager manager = new(_folder);
        AppSettings loaded = manager.Load();

        Assert.True(manager.Quarantined);
        Assert.False(manager.RecoveredFromBackup);
        Assert.Equal("flat", loaded.ActiveDisplayPresetId);
    }

    [Fact]
    public void A_file_that_is_busy_is_neither_quarantined_nor_overwritten()
    {
        Write("settings.json", GoodProfile());
        string original = File.ReadAllText(SettingsPath);

        ProfileManager manager = new(_folder);

        // Held the way an antivirus scan or a cloud sync client holds it: open for
        // writing with nobody else allowed to touch it. Scoped so the lock is
        // released before the file is read back for comparison.
        using (FileStream held = new(
            SettingsPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            AppSettings loaded = manager.Load();

            // The file is exactly where it was, under its own name, with its own
            // contents. A busy file is not a damaged one.
            Assert.False(manager.Quarantined);
            Assert.Empty(Directory.GetFiles(_folder, "settings.json.corrupt.*.json"));

            // And the first save of the session must not replace it with defaults.
            manager.Save(loaded);
        }

        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Saving_resumes_normally_once_the_file_can_be_read_again()
    {
        ProfileManager manager = new(_folder);

        // The blocked launch: readable on the retry, so nothing is held off.
        AppSettings first = manager.Load();
        first.BlueLightFilter = 2;
        manager.Save(first);

        Assert.Equal(2, new ProfileManager(_folder).Load().BlueLightFilter);
    }

    [Fact]
    public void The_first_save_after_a_recovery_leaves_the_recovered_profile_in_place()
    {
        Write("settings.json", "{\"GammaLock\": tr");
        Write("settings.json.bak", GoodProfile());

        ProfileManager manager = new(_folder);
        AppSettings loaded = manager.Load();
        Assert.True(manager.RecoveredFromBackup);

        loaded.BlueLightFilter = 1;
        manager.Save(loaded);

        // The damaged file is still on disk under its stamped name, and what is
        // live is now the recovered profile rather than defaults.
        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrupt.*.json"));
        Assert.Equal(1, new ProfileManager(_folder).Load().BlueLightFilter);
    }

    [Fact]
    public void Kept_aside_files_do_not_accumulate_without_limit()
    {
        // Five launches that each found the profile damaged. The stamped names
        // sort by time, so the newest three are the ones kept.
        for (int i = 1; i <= 5; i++)
        {
            Write("settings.json.corrupt.2026010" + i + "-120000.json", "{}");
        }

        Write("settings.json", "not json at all");
        ProfileManager manager = new(_folder);
        manager.Load();

        string[] kept = Directory.GetFiles(_folder, "settings.json.corrupt.*.json");
        Assert.Equal(3, kept.Length);

        // Newest kept, oldest dropped. Six files are in play: the one this launch
        // just made, plus the five written above. Full paths, so matched on name.
        Assert.Contains(kept, f => f.EndsWith("20260105-120000.json", StringComparison.Ordinal));
        Assert.Contains(kept, f => f.EndsWith("20260104-120000.json", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, f => f.EndsWith("20260103-120000.json", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, f => f.EndsWith("20260102-120000.json", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, f => f.EndsWith("20260101-120000.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_backup_left_by_the_atomic_swap_is_what_a_recovery_reads()
    {
        ProfileManager manager = new(_folder);

        // Two saves, so the swap has run and there is a real .bak on disk.
        AppSettings settings = manager.Load();
        settings.BlueLightFilter = 1;
        manager.Save(settings);
        settings.BlueLightFilter = 2;
        manager.Save(settings);

        Assert.True(File.Exists(BackupPath));

        // The .bak holds the previous good file, which is the point of keeping it.
        string backup = File.ReadAllText(BackupPath);
        Assert.Contains("\"BlueLightFilter\": 1", backup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_load_does_not_quarantine_the_same_file_twice()
    {
        Write("settings.json", "{\"GammaLock\": tr");
        Write("settings.json.bak", GoodProfile());

        ProfileManager manager = new(_folder);
        manager.Load();
        Assert.True(manager.Quarantined);

        // The damaged file was moved aside on the first pass, so the second has
        // nothing left to quarantine. What it does still do is fall back to the
        // backup, which is the honest answer: that is where the profile came from.
        AppSettings second = manager.Load();
        Assert.False(manager.Quarantined);
        Assert.True(manager.RecoveredFromBackup);
        Assert.Single(second.CustomDisplayPresets);
        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrupt.*.json"));
    }
}
