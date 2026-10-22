using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Services;
using SkiaSharp;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// WebP: the one picture format Windows cannot decode without a codec from the
/// Store, so the app brings its own.
/// <para>
/// What is asserted is that a WebP arrives in the same form as everything else - a
/// still as one square PNG, an animation as a strip with its timings beside it - so
/// the overlay, the browser and the backup code never need to know a second format
/// exists.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairWebpTests : IDisposable
{
    /// <summary>
    /// Two frames, red then blue, 16 by 16, a tenth of a second each. Nothing in this
    /// build encodes an animated WebP - the decoder is Skia's and it only reads - so
    /// the file is here whole rather than made by the test. ffmpeg produced it from
    /// two solid colour sources, which is the shape most animated crosshairs have.
    /// </summary>
    private const string AnimatedWebpBase64 =
        "UklGRsAAAABXRUJQVlA4WAoAAAACAAAADwAADwAAQU5JTQYAAAD/////AABBTk1GSAAAAAAAAAAAAA8AAA8AAGQAAAJWUDggMAAAANABAJ0BKhAAEAACADQloAJ0ugH4AAOwAP7w6Pf/ILlhdcjX/yA/5Af8gP/48gAAAEFOTUZEAAAAAAAAAAAADwAADwAAZAAAAFZQOCAsAAAAlAEAnQEqEAAQAAAANCWgAnS6AAOYAP75k2//kB//kB//kB//ID/iF3sgMAA=";

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gamertool-webp-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    [Fact]
    public void A_webp_still_becomes_a_square_png_like_every_other_picture()
    {
        string path = WriteStill("art.webp", new SKColor(0x20, 0xD0, 0x40));

        (CrosshairImageStore.StoredPicture? stored, string problem) = Import(path);

        Assert.NotNull(stored);
        Assert.Empty(problem);
        Assert.True(stored!.OwnColours, "a coloured WebP arrives in its own colours");

        CrosshairPicture? picture = CrosshairImageStore.LoadPicture(stored.Name, _folder);
        Assert.NotNull(picture);
        Assert.False(picture!.IsAnimated);
        Assert.Equal(picture.First.PixelWidth, picture.First.PixelHeight);

        (int r, int g, int b, int a) = WpfTestHost.Invoke(() => Centre(picture.First));
        Assert.Equal(255, a);
        Assert.True(g > 150 && r < 110 && b < 130, $"should still be green, was {r},{g},{b}");
    }

    [Fact]
    public void A_webp_animation_becomes_a_strip_with_its_timings()
    {
        string path = WriteAnimated("spin.webp");

        (CrosshairImageStore.StoredPicture? stored, string problem) = Import(path);

        Assert.NotNull(stored);
        Assert.Empty(problem);

        CrosshairPicture? picture = CrosshairImageStore.LoadPicture(stored!.Name, _folder);
        Assert.NotNull(picture);
        Assert.True(picture!.IsAnimated);
        Assert.Equal(2, picture.Frames.Count);
        Assert.All(picture.DelaysMs, delay => Assert.Equal(100, delay));

        (int r0, int g0, int b0, int a0) = WpfTestHost.Invoke(() => Centre(picture.Frames[0]));
        (int r1, int g1, int b1, int a1) = WpfTestHost.Invoke(() => Centre(picture.Frames[1]));

        Assert.Equal(255, a0);
        Assert.Equal(255, a1);
        Assert.True(r0 > 180 && g0 < 90 && b0 < 90, $"the first frame should be red, was {r0},{g0},{b0}");
        Assert.True(b1 > 180 && r1 < 90 && g1 < 90, $"the second frame should be blue, was {r1},{g1},{b1}");
    }

    [Fact]
    public void A_webp_animation_plays_in_the_pack_browser()
    {
        string path = WriteAnimated("spin.webp");

        CrosshairPackPreview? preview = WpfTestHost.Invoke(() => CrosshairPackPreview.Build(path, 64, null));

        Assert.NotNull(preview);
        Assert.Equal(2, preview!.Frames.Count);
        Assert.All(preview.DelaysMs, delay => Assert.Equal(100, delay));

        // Every frame of one animation is the same square, or the tile twitches.
        int side = preview.Frames[0].PixelWidth;
        Assert.InRange(side, 8, 64);
        Assert.All(preview.Frames, frame => Assert.Equal(side, frame.PixelWidth));
    }

    [Fact]
    public void A_webp_still_is_not_a_preview()
    {
        string path = WriteStill("still.webp", new SKColor(0x20, 0xD0, 0x40));

        Assert.Null(WpfTestHost.Invoke(() => CrosshairPackPreview.Build(path, 64, null)));
    }

    [Fact]
    public void A_picture_that_is_not_a_webp_is_refused_rather_than_trusted()
    {
        // The importer checks contents against the extension for every kind, and
        // Skia decodes PNG too: without the file's own header a renamed PNG would
        // have come in as a WebP.
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "renamed.webp");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Sheet(32, 32, 0x20, 0xD0, 0x40)));
        using (FileStream stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        (CrosshairImageStore.StoredPicture? stored, string problem) = Import(path);

        Assert.Null(stored);
        Assert.Contains("not the kind of picture", problem);
    }

    [Fact]
    public void The_first_frame_of_a_webp_opens_for_a_tile()
    {
        string path = WriteAnimated("spin.webp");

        (byte[] Pixels, int Width, int Height)? frame = WebpReader.ReadFirstFrame(path);

        Assert.NotNull(frame);
        Assert.Equal(16, frame!.Value.Width);
        Assert.Equal(16, frame.Value.Height);
        Assert.Equal(16 * 16 * 4, frame.Value.Pixels.Length);

        // A file that is gone is not an error, it is a tile the browser leaves out.
        Assert.Null(WebpReader.ReadFirstFrame(Path.Combine(_folder, "gone.webp")));
    }

    // ------------------------------------------------------------------ helpers

    private (CrosshairImageStore.StoredPicture? Stored, string Problem) Import(string path)
    {
        return WpfTestHost.Invoke(() =>
        {
            CrosshairImageStore.StoredPicture? stored = CrosshairImageStore.Import(path, _folder, out string problem);
            return (stored, problem);
        });
    }

    private string WriteAnimated(string name)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Convert.FromBase64String(AnimatedWebpBase64));
        return path;
    }

    private string WriteStill(string name, SKColor colour)
    {
        Directory.CreateDirectory(_folder);
        using var bitmap = new SKBitmap(24, 24);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static BitmapSource Sheet(int width, int height, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int at = i * 4;
            pixels[at] = b;
            pixels[at + 1] = g;
            pixels[at + 2] = r;
            pixels[at + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int R, int G, int B, int A) Centre(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth;
        byte[] pixels = new byte[width * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        int at = (((converted.PixelHeight / 2) * width) + (width / 2)) * 4;
        return (pixels[at + 2], pixels[at + 1], pixels[at], pixels[at + 3]);
    }
}
