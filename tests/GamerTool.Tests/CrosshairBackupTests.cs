using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Crosshair pictures in a backup, which is the one part of the setup that version
/// 1 left behind.
/// <para>
/// A preset names its picture by file name inside this app's own folder, and a
/// backup carried only the settings - so restoring on another PC brought back
/// presets pointing at pictures that were not there. The preset quietly drew as a
/// shape, and the next save pruned the picture files as unused. Nothing failed and
/// nothing was said, which is the worst shape for a bug in the artefact people keep
/// specifically so they do not lose their setup.
/// </para>
/// <para>
/// The folder is passed in rather than taken from the live profile, the same way
/// <see cref="ProfileManager"/> takes one, so these run in a temp folder and never
/// touch the user's own crosshairs.
/// </para>
/// </summary>
public sealed class CrosshairBackupTests : IDisposable
{
    private const string Picture = "custom-aaaabbbb.png";

    private readonly string _root;
    private readonly string _pictures;

    public CrosshairBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gamertool-crosshair-backup-" + Guid.NewGuid().ToString("N")[..8]);
        _pictures = Path.Combine(_root, "crosshairs");
        Directory.CreateDirectory(_pictures);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    [Fact]
    public void A_backup_carries_the_picture_its_preset_names()
    {
        byte[] png = MakePng(16, 16);
        File.WriteAllBytes(Path.Combine(_pictures, Picture), png);
        string path = Path.Combine(_root, "backup.json");

        int carried = new BackupService(_pictures).Export(WithPicture(Picture), path);

        Assert.Equal(1, carried);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement pictures = document.RootElement.GetProperty("Pictures");

        Assert.Equal(1, pictures.GetArrayLength());
        Assert.Equal(Picture, pictures[0].GetProperty("Name").GetString());
        Assert.Equal(Convert.ToBase64String(png), pictures[0].GetProperty("Data").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void A_profile_with_no_pictures_exports_exactly_as_it_did_before()
    {
        // No Pictures member at all, rather than an empty one: an unchanged export
        // stays byte-for-byte reproducible and an older build sees nothing new.
        string path = Path.Combine(_root, "backup.json");

        int carried = new BackupService(_pictures).Export(WithPicture(string.Empty), path);

        Assert.Equal(0, carried);
        Assert.DoesNotContain("Pictures", File.ReadAllText(path));
    }

    [Fact]
    public void Restoring_writes_the_picture_back_where_the_preset_looks_for_it()
    {
        byte[] png = MakePng(16, 16);
        File.WriteAllBytes(Path.Combine(_pictures, Picture), png);

        string path = Path.Combine(_root, "backup.json");
        new BackupService(_pictures).Export(WithPicture(Picture), path);

        // Where a backup lands: a PC that has the profile but never had the picture.
        File.Delete(Path.Combine(_pictures, Picture));

        AppSettings? loaded = new BackupService(_pictures).Import(path, out RestoreReport report, out string error);

        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, error);
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(_pictures, Picture)));
        Assert.Equal(1, report.PicturesRestored);
        Assert.Equal(0, report.PicturesMissing);
        Assert.Equal(0, report.PicturesRejected);
    }

    [Fact]
    public void An_animation_travels_with_the_timings_that_make_it_move()
    {
        string name = "custom-ccccdddd.png";
        byte[] strip = MakePng(32, 16);
        string timing = new AnimationInfo(16, new[] { 120, 90 }).ToJson();

        File.WriteAllBytes(Path.Combine(_pictures, name), strip);
        File.WriteAllText(Path.Combine(_pictures, "custom-ccccdddd.anim.json"), timing);

        string path = Path.Combine(_root, "backup.json");
        new BackupService(_pictures).Export(WithPicture(name), path);

        File.Delete(Path.Combine(_pictures, name));
        File.Delete(Path.Combine(_pictures, "custom-ccccdddd.anim.json"));

        new BackupService(_pictures).Import(path, out RestoreReport report, out _);

        Assert.Equal(1, report.PicturesRestored);
        Assert.Equal(strip, File.ReadAllBytes(Path.Combine(_pictures, name)));

        Assert.True(AnimationInfo.TryParse(
            File.ReadAllText(Path.Combine(_pictures, "custom-ccccdddd.anim.json")),
            out AnimationInfo restored));
        Assert.Equal(2, restored.Frames);
        Assert.Equal(120, restored.DelaysMs[0]);
        Assert.Equal(90, restored.DelaysMs[1]);
    }

    [Fact]
    public void A_preset_whose_picture_is_in_neither_place_is_reported()
    {
        // The version 1 case, and the reason this exists: the file has the preset
        // and not the picture, and this PC has never seen it either.
        string path = WriteBackup("nopicture.json", WithPicture("custom-deadbeef.png"));
        BackupService service = new(_pictures);

        AppSettings? loaded = service.Import(path, out RestoreReport report, out _);
        Assert.NotNull(loaded);

        service.Repair(
            loaded!,
            Array.Empty<MonitorChoice>(),
            Array.Empty<string>(),
            Array.Empty<AppCandidate>(),
            null,
            Array.Empty<string>(),
            report);

        Assert.Equal(1, report.PicturesMissing);
        Assert.Equal(0, report.PicturesRestored);
        Assert.Contains("CROSSHAIR PICTURE MISSING", report.Summary);
        Assert.True(report.AnythingToReport);
        Assert.Contains(report.Notes, note => note.Contains("crosshair picture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_picture_this_PC_already_has_is_not_called_missing()
    {
        // Restoring your own backup on the machine it came from. Every picture is
        // absent from the file only because the file is version 1, and announcing
        // that they are all missing would teach the user to ignore the one line
        // that matters.
        File.WriteAllBytes(Path.Combine(_pictures, Picture), MakePng(16, 16));
        string path = WriteBackup("nopicture.json", WithPicture(Picture));

        new BackupService(_pictures).Import(path, out RestoreReport report, out _);

        Assert.Equal(0, report.PicturesMissing);
        Assert.False(report.AnythingToReport);
    }

    [Fact]
    public void A_picture_name_that_is_a_path_is_refused_and_nothing_is_written()
    {
        byte[] png = MakePng(16, 16);

        bool written = CrosshairImageStore.WriteFromBackup(
            @"..\evil.png", png, null, _pictures, out string problem);

        Assert.False(written);
        Assert.NotEqual(string.Empty, problem);
        Assert.False(File.Exists(Path.Combine(_root, "evil.png")));
        Assert.False(File.Exists(Path.Combine(_pictures, "evil.png")));
    }

    [Fact]
    public void A_hostile_name_in_the_file_never_reaches_the_filesystem()
    {
        // End to end: the preset names the path, and the file carries bytes for it.
        // The reference filter is what has to hold, because a name that never
        // becomes a path cannot be written whatever the payload is.
        string name = @"..\escaped.png";
        string path = WriteBackup("hostile.json", WithPicture(name), new BackupPicture
        {
            Name = name,
            Data = Convert.ToBase64String(MakePng(16, 16))
        });

        new BackupService(_pictures).Import(path, out RestoreReport report, out _);

        Assert.Equal(0, report.PicturesRestored);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.png")));
        Assert.False(File.Exists(Path.Combine(_pictures, "escaped.png")));
    }

    [Fact]
    public void A_payload_that_is_not_a_picture_is_skipped_rather_than_written()
    {
        string path = Path.Combine(_root, "badpicture.json");

        WriteBackup(path, WithPicture(Picture), new BackupPicture
        {
            Name = Picture,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a PNG"))
        });

        new BackupService(_pictures).Import(path, out RestoreReport report, out _);

        Assert.Equal(1, report.PicturesRejected);
        Assert.Equal(0, report.PicturesRestored);
        Assert.False(File.Exists(Path.Combine(_pictures, Picture)));
        Assert.Contains("CROSSHAIR PICTURE SKIPPED", report.Summary);
    }

    [Fact]
    public void A_payload_larger_than_any_stored_picture_is_refused()
    {
        // The cap the store applies to a file, reached through a backup: a payload
        // that could not be a stored picture is rejected rather than decoded and
        // then rejected, so a file claiming a hundred megabyte crosshair costs
        // nothing to refuse beyond the string it already carries.
        string path = Path.Combine(_root, "huge.json");

        WriteBackup(path, WithPicture(Picture), new BackupPicture
        {
            Name = Picture,
            Data = new string('A', 4 * 1024 * 1024)
        });

        new BackupService(_pictures).Import(path, out RestoreReport report, out _);

        Assert.Equal(1, report.PicturesRejected);
        Assert.False(File.Exists(Path.Combine(_pictures, Picture)));
    }

    [Fact]
    public void Timings_that_do_not_match_the_strip_are_refused_with_the_picture()
    {
        // A strip with timings that describe a different picture draws as nothing at
        // all, because BuildPicture throws and LoadPicture swallows it. Writing the
        // two anyway would restore a crosshair that silently refuses to load, so both
        // halves are refused together.
        byte[] strip = MakePng(32, 16);

        bool written = CrosshairImageStore.WriteFromBackup(
            "custom-eeeeffff.png",
            strip,
            new AnimationInfo(16, new[] { 100, 100, 100 }).ToJson(),
            _pictures,
            out string problem);

        Assert.False(written);
        Assert.NotEqual(string.Empty, problem);
        Assert.False(File.Exists(Path.Combine(_pictures, "custom-eeeeffff.png")));
        Assert.False(File.Exists(Path.Combine(_pictures, "custom-eeeeffff.anim.json")));
    }

    [Fact]
    public void A_version_1_backup_still_imports()
    {
        File.WriteAllBytes(Path.Combine(_pictures, Picture), MakePng(16, 16));
        string path = Path.Combine(_root, "v1.json");

        File.WriteAllText(path, JsonSerializer.Serialize(new BackupFile
        {
            Format = BackupService.FormatTag,
            Version = 1,
            Settings = WithPicture(Picture)
        }));

        AppSettings? loaded = new BackupService(_pictures).Import(path, out RestoreReport report, out string error);

        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, error);
        Assert.Equal(0, report.PicturesRestored);
        Assert.Equal(0, report.PicturesMissing);
        Assert.Single(loaded!.CustomCrosshairs);
    }

    // ------------------------------------------------------------------ helpers

    private static AppSettings WithPicture(string name)
    {
        AppSettings settings = new();
        settings.CustomCrosshairs.Add(new CrosshairPreset
        {
            Id = "xh-mine",
            Name = "Mine",
            ShapeKind = CrosshairShape.Custom,
            CustomImage = name
        });

        return settings;
    }

    private string WriteBackup(string fileName, AppSettings settings, params BackupPicture[] pictures)
    {
        string path = Path.Combine(_root, fileName);
        BackupFile file = new()
        {
            Format = BackupService.FormatTag,
            Version = BackupService.FormatVersion,
            Settings = settings
        };

        if (pictures.Length > 0)
        {
            file.Pictures = new List<BackupPicture>(pictures);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(file));
        return path;
    }

    /// <summary>A real PNG, because everything downstream decodes it.</summary>
    private static byte[] MakePng(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0xD0;
            pixels[i + 2] = 0x40;
            pixels[i + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using MemoryStream output = new();
        encoder.Save(output);
        return output.ToArray();
    }
}
