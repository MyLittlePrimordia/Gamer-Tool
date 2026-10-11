using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The engine path and the shapes that reach it from outside the running app.
/// <para>
/// Both are the same concern from two directions. The engine path is executed, so
/// a profile naming something that is not the engine must not survive the trip.
/// The shapes are deserialized from a settings file or a backup, and a null where
/// the model promises a string is not caught by a null check on the list that
/// holds it.
/// </para>
/// </summary>
public class UntrustedProfileShapeTests
{
    private static AppSettings FromJson(string json)
    {
        return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions())
            ?? throw new InvalidOperationException("test json did not deserialize");
    }

    // ---------------- the engine path ----------------

    [Theory]
    [InlineData(@"C:\Program Files\FxSound LLC\FxSound\FxSound.exe")]
    [InlineData(@"D:\Apps\FxSound\FxSound.exe")]
    [InlineData(@"C:\Program Files (x86)\FxSound LLC\FxSound\FxSound.exe")]
    [InlineData(@"\\server\share\FxSound.exe")]
    public void A_real_install_folder_is_left_alone(string path)
    {
        // FxSound installs anywhere. Pinning the folder would break every custom
        // install to close a hole that checking the file name already closes.
        Assert.True(AudioService.IsPlausibleFxSoundPath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\victim\Downloads\payload.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("FxSound.exe")]
    [InlineData(@"..\FxSound.exe")]
    [InlineData(@"C:\FxSound.exe\..\other.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_the_engine_is_refused(string? path)
    {
        Assert.False(AudioService.IsPlausibleFxSoundPath(path));
    }

    [Fact]
    public void The_service_will_not_take_a_path_that_names_something_else()
    {
        AudioService audio = new();

        audio.ExePath = @"C:\Users\victim\Downloads\payload.exe";

        // The setter is the only way in, so this is where a hostile profile would
        // have to be stopped. Fails closed to the default rather than to nothing.
        Assert.Equal(AudioService.DefaultFxSoundPath, audio.ExePath);
    }

    [Fact]
    public void The_service_accepts_a_custom_install_folder()
    {
        AudioService audio = new();

        audio.ExePath = @"D:\Apps\FxSound\FxSound.exe";

        Assert.Equal(@"D:\Apps\FxSound\FxSound.exe", audio.ExePath);
    }

    [Fact]
    public void A_profile_cannot_carry_a_path_past_the_schema_guard()
    {
        AppSettings settings = new() { FxSoundPath = @"C:\Users\victim\Downloads\payload.exe" };

        AppSettings normalized = ProfileManager.Normalize(settings);

        Assert.Equal(AudioService.DefaultFxSoundPath, normalized.FxSoundPath);
    }

    [Fact]
    public void A_profile_cannot_carry_a_relative_path_past_the_schema_guard()
    {
        AppSettings settings = new() { FxSoundPath = "FxSound.exe" };

        Assert.Equal(AudioService.DefaultFxSoundPath, ProfileManager.Normalize(settings).FxSoundPath);
    }

    [Fact]
    public void A_custom_install_folder_survives_normalization()
    {
        AppSettings settings = new() { FxSoundPath = @"D:\Apps\FxSound\FxSound.exe" };

        Assert.Equal(@"D:\Apps\FxSound\FxSound.exe", ProfileManager.Normalize(settings).FxSoundPath);
    }

    [Fact]
    public void A_backup_carrying_another_machines_path_is_pointed_at_this_one()
    {
        var backups = new BackupService();
        AppSettings imported = new() { FxSoundPath = @"C:\Users\victim\Downloads\payload.exe" };

        RestoreReport report = new();
        backups.Repair(
            imported,
            new List<string>(),
            new List<string>(),
            new List<AppCandidate>(),
            AudioService.DefaultFxSoundPath,
            report);

        Assert.Equal(AudioService.DefaultFxSoundPath, imported.FxSoundPath);
        Assert.True(report.FxSoundPathReset);
    }

    [Fact]
    public void A_backup_carrying_a_real_path_from_elsewhere_is_left_to_the_schema_guard()
    {
        var backups = new BackupService();

        // Another machine, custom install, and genuinely the engine. Kept: the
        // schema guard allows it and nothing here is a reason to overwrite it.
        AppSettings imported = new() { FxSoundPath = @"E:\Games\FxSound\FxSound.exe" };
        RestoreReport report = new();
        backups.Repair(imported, new List<string>(), new List<string>(), new List<AppCandidate>(),
            AudioService.DefaultFxSoundPath, report);

        Assert.Equal(@"E:\Games\FxSound\FxSound.exe", imported.FxSoundPath);
        Assert.False(report.FxSoundPathReset);
    }

    // ---------------- null elements and null strings ----------------

    [Fact]
    public void A_null_slot_is_dropped_rather_than_dereferenced()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2, \"Slots\": [ null, { \"Id\": \"slot_x\", \"Name\": \"Kept\" } ] }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        // Repair reads slot.MonitorDevice.Length on every entry, so a null in the
        // list was a crash on the restore path rather than a bad profile.
        HotkeySlot kept = Assert.Single(normalized.Slots);
        Assert.Equal("Kept", kept.Name);
    }

    [Fact]
    public void A_null_string_inside_a_slot_does_not_reach_a_length_check()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2, \"Slots\": [ { \"Id\": null, \"Name\": null, \"Hotkey\": null, \"MonitorDevice\": null } ] }");

        AppSettings normalized = ProfileManager.Normalize(settings);
        HotkeySlot slot = Assert.Single(normalized.Slots);

        // Repair reads .Length on every one of these, so none may still be null.
        // What they hold afterwards is the migration's business: a blank id and a
        // blank name are given real values there, which is better than empty and
        // is the existing behaviour for a hand-edited file.
        Assert.NotNull(slot.Id);
        Assert.NotEmpty(slot.Id);
        Assert.NotNull(slot.Name);
        Assert.NotEmpty(slot.Name);
        Assert.Equal(string.Empty, slot.Hotkey);
        Assert.Equal(string.Empty, slot.MonitorDevice);

        // The point of the exercise: the repair pass runs over this without
        // throwing, which it did not before.
        var backups = new BackupService();
        RestoreReport report = new();
        backups.Repair(normalized, new List<string>(), new List<string>(), new List<AppCandidate>(),
            AudioService.DefaultFxSoundPath, report);
    }

    /// <summary>
    /// The gap the null-combo test above left open.
    /// <para>
    /// That test carries a null combo id but no Slots key, so the migration
    /// short-circuits on an empty slot list and never reaches the read. Add one
    /// slot and the same file used to throw: the migration reads combo.Id on every
    /// entry to decide whether it is already a slot, while the guard that puts a
    /// null id back to empty sat 45 lines further down - after the call. The
    /// exception escaped Normalize, escaped Load, escaped the window constructor
    /// and landed on the "could not start" box. The file had parsed perfectly well,
    /// so the quarantine path never ran and the profile stayed exactly where it
    /// was: not one bad launch, but every launch until it was edited by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void A_null_combo_id_alongside_a_real_slot_does_not_throw()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2,"
            + " \"Slots\": [ { \"Id\": \"s1\", \"Name\": \"Keep\" } ],"
            + " \"CustomCombos\": [ { \"Id\": null, \"Name\": \"legacy\", \"Hotkey\": null } ] }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        // The original slot survives, and the combo became a slot rather than being
        // dropped, which is what the migration does with a real combo.
        Assert.Contains(normalized.Slots, s => s.Id == "s1");
        Assert.All(normalized.Slots, s => Assert.NotNull(s.Id));
        Assert.All(normalized.CustomCombos, c => Assert.NotNull(c.Id));
    }

    /// <summary>
    /// The same shape with every string on every entry null, which is what a
    /// truncated or hand-written file tends to look like rather than one targeted
    /// field.
    /// </summary>
    [Fact]
    public void A_slot_list_with_nulls_throughout_it_survives_normalization()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2,"
            + " \"Slots\": [ { \"Id\": \"s1\", \"Name\": \"Keep\" } ],"
            + " \"CustomCombos\": [ { \"Id\": null, \"Name\": null, \"Tag\": null,"
            + "   \"Hotkey\": null, \"DisplayPresetId\": null, \"AudioPresetId\": null } ],"
            + " \"AppProfiles\": [ { \"Id\": null, \"Name\": null, \"ExePath\": null } ] }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        Assert.NotEmpty(normalized.Slots);
        Assert.All(normalized.Slots, s =>
        {
            Assert.NotNull(s.Id);
            Assert.NotNull(s.Name);
            Assert.NotNull(s.Hotkey);
            Assert.NotNull(s.MonitorDevice);
        });
    }

    [Fact]
    public void A_null_entry_in_every_list_is_dropped()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2,"
            + " \"CustomDisplayPresets\": [ null, { \"Id\": \"d1\", \"Name\": \"Screen\" } ],"
            + " \"CustomAudioPresets\": [ null, { \"Id\": \"a1\", \"Name\": \"Sound\" } ],"
            + " \"CustomCombos\": [ null, { \"Id\": \"c1\", \"Name\": \"Combo\" } ],"
            + " \"AppProfiles\": [ null, { \"Id\": \"p1\", \"Name\": \"Game\", \"ExePath\": \"C:\\\\g\\\\g.exe\" } ],"
            + " \"Slots\": [ null, { \"Id\": \"s1\", \"Name\": \"Slot\" } ] }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        // Each list kept the one real entry and lost the null. The slot count is
        // more than one because the migration deliberately turns a combo and an
        // app profile into slots of their own, so membership is what matters.
        Assert.Contains(normalized.CustomDisplayPresets, p => p.Name == "Screen");
        Assert.Contains(normalized.CustomAudioPresets, p => p.Name == "Sound");
        Assert.Contains(normalized.CustomCombos, p => p.Name == "Combo");
        Assert.Contains(normalized.AppProfiles, p => p.Name == "Game");
        Assert.Contains(normalized.Slots, s => s.Name == "Slot");

        Assert.DoesNotContain(null!, normalized.Slots);
    }

    [Fact]
    public void A_null_string_inside_a_profile_or_preset_does_not_reach_a_length_check()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2,"
            + " \"AppProfiles\": [ { \"Id\": null, \"Name\": null, \"ExePath\": null,"
            + "   \"ProcessName\": null, \"DisplayPresetId\": null, \"AudioPresetId\": null, \"Source\": null } ],"
            + " \"CustomDisplayPresets\": [ { \"Id\": null, \"Name\": null, \"Tag\": null } ],"
            + " \"CustomAudioPresets\": [ { \"Id\": null, \"Name\": null, \"Tag\": null } ],"
            + " \"CustomCombos\": [ { \"Id\": null, \"Name\": null, \"Hotkey\": null } ],"
            + " \"OutputDeviceId\": null, \"OutputDeviceName\": null,"
            + " \"ActiveDisplayPresetId\": null, \"ActiveAudioPresetId\": null,"
            + " \"EmergencyHotkey\": null }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        AppProfile profile = Assert.Single(normalized.AppProfiles);
        Assert.Equal(string.Empty, profile.ExePath);
        Assert.Equal(string.Empty, profile.ProcessName);

        Assert.Equal(string.Empty, Assert.Single(normalized.CustomDisplayPresets).Name);
        Assert.Equal(string.Empty, Assert.Single(normalized.CustomAudioPresets).Name);
        Assert.Equal(string.Empty, Assert.Single(normalized.CustomCombos).Hotkey);

        Assert.Equal(string.Empty, normalized.OutputDeviceId);
        Assert.Equal(string.Empty, normalized.ActiveDisplayPresetId);
        Assert.Equal(string.Empty, normalized.EmergencyHotkey);
    }

    [Fact]
    public void A_null_backing_collection_is_replaced_rather_than_skipped()
    {
        AppSettings settings = FromJson(
            "{ \"Schema\": 2, \"ExcludedDdcMonitors\": null, \"OriginalHardwareBrightness\": null }");

        AppSettings normalized = ProfileManager.Normalize(settings);

        Assert.NotNull(normalized.ExcludedDdcMonitors);
        Assert.NotNull(normalized.OriginalHardwareBrightness);
        normalized.OriginalHardwareBrightness["DISPLAY1"] = 50u;
    }

    [Fact]
    public void A_blank_excluded_display_is_dropped_rather_than_breaking_the_load()
    {
        // Removing an entry from a list while walking it throws, because the
        // remove bumps the version the enumerator checks on every step. It did
        // throw here, and the failure was unreachable to any recovery: the file
        // had already parsed by then, so it never got quarantined, so the
        // exception escaped Load and the window constructor and the app
        // refused to start - with the same file still in place to do it again.
        //
        // Every position is exercised, because it throws even when the offending
        // entry is the last one and there is nothing left to walk.
        List<string>[] cases =
        {
            new List<string> { string.Empty },
            new List<string> { "   " },
            new List<string> { @"\\.\DISPLAY1", string.Empty },
            new List<string> { string.Empty, @"\\.\DISPLAY1", string.Empty },
            new List<string> { @"\\.\DISPLAY1", "   " },
            new List<string> { string.Empty, string.Empty },
        };

        var options = new JsonSerializerOptions();

        foreach (List<string> excluded in cases)
        {
            // Round tripped through JSON rather than hand written, so this is
            // exactly what a file on disk produces.
            string json = JsonSerializer.Serialize(
                new { Schema = 2, ExcludedDdcMonitors = excluded },
                options);

            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, options)!;
            AppSettings normalized = ProfileManager.Normalize(settings);

            Assert.DoesNotContain(normalized.ExcludedDdcMonitors, string.IsNullOrWhiteSpace);
        }
    }

    [Fact]
    public void A_blank_excluded_display_does_not_stop_the_app_starting()
    {
        // The same thing through the real load path, because that is where the
        // unrecoverable version lived.
        string folder = Path.Combine(Path.GetTempPath(), "GamerToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            File.WriteAllText(
                Path.Combine(folder, "settings.json"),
                "{ \"Schema\": 2, \"ExcludedDdcMonitors\": [\"\"], \"GammaLock\": false }");

            ProfileManager manager = new(folder);
            AppSettings loaded = manager.Load();

            Assert.False(loaded.GammaLock);
            Assert.Empty(loaded.ExcludedDdcMonitors);

            // And the profile is still writable afterwards, so a real session
            // could carry on rather than being stuck.
            loaded.BlueLightFilter = 1;
            manager.Save(loaded);
            Assert.Equal(1, new ProfileManager(folder).Load().BlueLightFilter);
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

    [Fact]
    public void A_profile_with_nothing_wrong_is_not_changed()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "Main", Hotkey = "CTRL+F1", MonitorDevice = "DISPLAY1" });
        settings.AppProfiles.Add(new AppProfile { Id = "p1", Name = "Game", ExePath = @"C:\g\g.exe" });

        AppSettings normalized = ProfileManager.Normalize(settings);

        HotkeySlot? slot = normalized.Slots.Find(s => s.Hotkey == "CTRL+F1");
        Assert.NotNull(slot);
        Assert.Equal("DISPLAY1", slot!.MonitorDevice);
        Assert.Equal(@"C:\g\g.exe", Assert.Single(normalized.AppProfiles).ExePath);
        Assert.Equal("Main", slot.Name);
    }
}
