using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Restoring a backup must never be able to quietly replace a good profile with
/// an empty one, and must never hand a display or the equaliser a value the UI
/// itself would refuse.
/// </summary>
public class BackupAndNormalizeTests
{
    /// <summary>Mirrors the app's own serialiser settings, so a test file and a
    /// real backup file are shaped identically.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string WriteTemp(string name, string json)
    {
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string SettingsJson()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset { Id = "mine", Name = "Mine" });
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", AudioPresetId = "footstep", Hotkey = "CTRL+1" });
        return JsonSerializer.Serialize(new BackupFile
        {
            Format = BackupService.FormatTag,
            Version = BackupService.FormatVersion,
            Settings = settings
        }, Options);
    }

    [Fact]
    public void A_real_backup_round_trips()
    {
        string path = WriteTemp("good.json", SettingsJson());

        try
        {
            AppSettings? loaded = new BackupService().Import(path, out RestoreReport report, out string error);

            Assert.NotNull(loaded);
            Assert.Equal(string.Empty, error);
            Assert.Single(loaded!.CustomAudioPresets);
            Assert.Single(loaded.Slots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_bare_settings_file_is_accepted_because_it_is_a_real_profile()
    {
        // Deliberately accepted, and deliberately harmless. A settings.json is a
        // genuine profile written by this app on this machine, so restoring it
        // cannot lose anything. What must never happen is an unrelated file
        // being taken for one, which is what CarriesProfileMarkers rules out.
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", AudioPresetId = "footstep" });
        string path = WriteTemp("settings.json", JsonSerializer.Serialize(settings, Options));

        try
        {
            AppSettings? loaded = new BackupService().Import(path, out _, out string error);

            Assert.NotNull(loaded);
            Assert.Equal(string.Empty, error);
            Assert.Single(loaded!.Slots);
            Assert.Equal("footstep", loaded.Slots[0].AudioPresetId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_empty_json_object_is_not_accepted()
    {
        // The case that actually did the damage: a valid JSON object with nothing
        // this app recognises in it deserialises into a blank profile, and a
        // blank profile looks exactly like a legitimate empty backup.
        string path = WriteTemp("empty.json", "{}");

        try
        {
            Assert.Null(new BackupService().Import(path, out _, out string error));
            Assert.Equal("NOT A GAMER TOOL BACKUP", error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unrelated_json_file_with_its_own_members_is_not_accepted()
    {
        // Includes members that collide with nothing this app writes, so shape
        // alone cannot tell them apart.
        string path = WriteTemp("other.json", "{\"Format\":\"SomethingElse\",\"Version\":1,\"Settings\":{\"Slots\":[]},\"hello\":\"world\"}");

        try
        {
            Assert.Null(new BackupService().Import(path, out _, out string error));
            Assert.Equal("NOT A GAMER TOOL BACKUP", error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_future_backup_version_is_not_accepted()
    {
        string path = WriteTemp("future.json", "{\"Format\":\"GamerTool.Backup\",\"Version\":99,\"Settings\":{\"Slots\":[]}}");

        try
        {
            Assert.Null(new BackupService().Import(path, out _, out string error));
            Assert.Equal("NOT A GAMER TOOL BACKUP", error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_reports_not_found()
    {
        AppSettings? loaded = new BackupService().Import(
            Path.Combine(Path.GetTempPath(), "GamerToolTests-does-not-exist.json"), out _, out string error);

        Assert.Null(loaded);
        Assert.Equal("FILE NOT FOUND", error);
    }

    [Fact]
    public void Malformed_json_is_rejected_rather_than_thrown()
    {
        string path = WriteTemp("bad.json", "{ this is not json");

        try
        {
            Assert.Null(new BackupService().Import(path, out _, out string error));
            Assert.Equal("NOT A GAMER TOOL BACKUP", error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Repair_drops_a_slot_whose_presets_are_not_in_the_file()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "s1",
            Name = "One",
            DisplayPresetId = "no_such_screen",
            AudioPresetId = "no_such_sound"
        });

        RestoreReport report = new();
        new BackupService().Repair(settings, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<AppCandidate>(), null, report);

        HotkeySlot slot = Assert.Single(settings.Slots);
        Assert.Null(slot.DisplayPresetId);
        Assert.Null(slot.AudioPresetId);
        Assert.Equal(1, report.SlotsLostScreen);
        Assert.Equal(1, report.SlotsLostSound);
    }

    [Fact]
    public void Repair_resets_a_monitor_this_pc_does_not_have()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot { Id = "s1", Name = "One", AudioPresetId = "footstep", MonitorDevice = @"\\.\DISPLAY7" });

        RestoreReport report = new();
        new BackupService().Repair(settings, new[] { @"\\.\DISPLAY1" }, Array.Empty<string>(), Array.Empty<AppCandidate>(), null, report);

        Assert.Equal(string.Empty, Assert.Single(settings.Slots).MonitorDevice);
        Assert.Equal(1, report.ScreensReset);
    }

    [Fact]
    public void Repair_clears_a_duplicate_key_and_says_so()
    {
        AppSettings settings = new();
        settings.Slots.Add(new HotkeySlot { Id = "a", Name = "A", AudioPresetId = "footstep", Hotkey = "CTRL+1" });
        settings.Slots.Add(new HotkeySlot { Id = "b", Name = "B", AudioPresetId = "royale", Hotkey = "ctrl+1" });

        RestoreReport report = new();
        new BackupService().Repair(settings, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<AppCandidate>(), null, report);

        Assert.Equal(1, report.KeysCleared);
        Assert.Single(settings.Slots, s => !string.IsNullOrWhiteSpace(s.Hotkey));
    }

    // ---------- the schema guard ----------

    [Fact]
    public void Normalize_clamps_a_gamma_that_would_wash_the_screen_white()
    {
        AppSettings settings = new();
        settings.CustomDisplayPresets.Add(new DisplayPreset { Id = "bad", Name = "Bad", Gamma = 1000 });

        DisplayPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomDisplayPresets);

        // A gamma of 1000 makes the whole tone curve collapse to white, which the
        // slider itself can never produce because its Maximum is 3.
        Assert.InRange(preset.Gamma, 1.0, 3.0);
    }

    [Fact]
    public void Normalize_clamps_display_values_outside_the_slider_ranges()
    {
        AppSettings settings = new();
        settings.CustomDisplayPresets.Add(new DisplayPreset
        {
            Id = "bad",
            Name = "Bad",
            Gamma = 0.01,
            ShadowBoost = 5000,
            Brightness = -9999,
            Contrast = 9999,
            RedGain = 40.0,
            GreenGain = -3.0,
            BlueGain = 0.0
        });

        DisplayPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomDisplayPresets);

        Assert.InRange(preset.Gamma, 1.0, 3.0);
        Assert.InRange(preset.ShadowBoost, 0.0, 100.0);
        Assert.InRange(preset.Brightness, -50.0, 50.0);
        Assert.InRange(preset.Contrast, -50.0, 50.0);
        Assert.InRange(preset.RedGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
        Assert.InRange(preset.GreenGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
        Assert.InRange(preset.BlueGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
    }

    [Fact]
    public void Normalize_clamps_band_gains_to_what_the_engine_accepts()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "bad",
            Name = "Bad",
            NumBands = 10,
            Bands = new[] { 900.0, -900.0, 3.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.All(preset.Bands, band => Assert.InRange(band, AudioPreset.GainMin, AudioPreset.GainMax));
    }

    [Fact]
    public void Normalize_clamps_effect_and_engine_values()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "bad",
            Name = "Bad",
            NumBands = 10,
            Bands = new double[10],
            Clarity = 900.0,
            Ambience = -50.0,
            Surround = 900.0,
            DynamicBoost = 900.0,
            BassBoost = 900.0,
            MasterGain = 900.0,
            VolumeLeveling = 900.0,
            FilterQ = 900.0,
            Balance = 900.0
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.InRange(preset.Clarity, AudioPreset.EffectMin, AudioPreset.EffectMax);
        Assert.InRange(preset.Ambience, AudioPreset.EffectMin, AudioPreset.EffectMax);
        Assert.InRange(preset.Surround, AudioPreset.EffectMin, AudioPreset.EffectMax);
        Assert.InRange(preset.DynamicBoost, AudioPreset.EffectMin, AudioPreset.EffectMax);
        Assert.InRange(preset.BassBoost, AudioPreset.EffectMin, AudioPreset.EffectMax);
        Assert.InRange(preset.MasterGain, AudioPreset.MasterGainMin, AudioPreset.MasterGainMax);
        Assert.InRange(preset.VolumeLeveling, AudioPreset.LevelingMin, AudioPreset.LevelingMax);
        Assert.InRange(preset.FilterQ, AudioPreset.FilterQMin, AudioPreset.FilterQMax);
        Assert.InRange(preset.Balance, -20.0, 20.0);
    }

    [Fact]
    public void Normalize_pulls_a_stored_frequency_back_inside_its_band_window()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "bad",
            Name = "Bad",
            NumBands = 10,
            Bands = new double[10],
            Frequencies = new[] { 1.0, 99999.0, 0.0, -5.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        // 10 bands is a count with dials, so each stored centre is pulled into
        // its own window rather than discarded.
        AudioPreset.BandWindow[] dialWindows = AudioPreset.BandWindows[10];
        for (int i = 0; i < dialWindows.Length; i++)
        {
            Assert.InRange(preset.Frequencies[i], dialWindows[i].Min, dialWindows[i].Max);
        }

        // Which is to say: the near edge, the far edge, and nothing invented in
        // between. Band 1 was given 1 Hz, which is under its window, so it lands
        // on the minimum; band 2 was given 99999 Hz, so it lands on the maximum.
        Assert.Equal(dialWindows[0].Min, preset.Frequencies[0]);
        Assert.Equal(dialWindows[1].Max, preset.Frequencies[1]);
        Assert.Equal(dialWindows[3].Min, preset.Frequencies[3]);
        Assert.Equal(dialWindows[9].Min, preset.Frequencies[9]);
    }

    [Fact]
    public void Normalize_drops_stored_frequencies_for_a_band_count_with_no_dials()
    {
        // 15 bands has no dials, so a band has exactly one legal frequency and a
        // stored one is always wrong. Keeping it would put a value in the command
        // line that the engine ignores without a word.
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "bad",
            Name = "Bad",
            NumBands = 15,
            Bands = new double[15],
            Frequencies = Enumerable.Range(0, 15).Select(i => 100.0 + i).ToArray()
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.Empty(preset.Frequencies);
    }

    [Fact]
    public void Normalize_resizes_a_stored_frequency_array_to_the_band_count()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "short",
            Name = "Short",
            NumBands = 10,
            Bands = new double[10],
            Frequencies = new[] { 70.0, 100.0 }
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.Equal(10, preset.Frequencies.Length);
        Assert.Equal(70.0, preset.Frequencies[0]);
    }

    [Fact]
    public void Normalize_keeps_a_stored_frequency_that_is_already_legal()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset
        {
            Id = "tuned",
            Name = "Tuned",
            NumBands = 10,
            Bands = new double[10],
            Frequencies = new[] { 70.0, 100.0, 200.0, 400.0, 700.0, 1400.0, 2500.0, 5000.0, 9000.0, 14000.0 }
        });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.Equal(70.0, preset.Frequencies[0]);
        Assert.Equal(14000.0, preset.Frequencies[9]);
    }

    [Fact]
    public void Normalize_falls_back_to_a_known_band_count()
    {
        AppSettings settings = new();
        settings.CustomAudioPresets.Add(new AudioPreset { Id = "odd", Name = "Odd", NumBands = 7, Bands = new double[7] });

        AudioPreset preset = Assert.Single(ProfileManager.Normalize(settings).CustomAudioPresets);

        Assert.Equal(AudioPreset.PresetBandCount, preset.NumBands);
        Assert.Equal(AudioPreset.PresetBandCount, preset.Bands.Length);
    }

    [Fact]
    public void Normalize_restores_the_default_fxsound_path_when_it_is_blank()
    {
        AppSettings settings = new() { FxSoundPath = "   " };

        Assert.Equal(AudioService.DefaultFxSoundPath, ProfileManager.Normalize(settings).FxSoundPath);
    }
}
