using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;
using Color = System.Windows.Media.Color;

namespace GamerTool.Tests;

/// <summary>
/// Size measures the crosshair, not the file it came from.
/// <para>
/// A picture is stored at its canvas size, and a canvas is usually much bigger than
/// the art on it - an eight pixel animation drawn in a 512 pixel GIF, or a
/// screenshot-sized PNG with a small cross in the middle. Scaling the canvas meant
/// Size could never make that art any bigger than its own fraction of it, so an
/// imported crosshair could be stuck at a few pixels with the slider already at its
/// maximum. The fix is to draw the part of the picture that has pixels in it.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairSizingTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gamertool-sizing-" + Guid.NewGuid().ToString("N")[..8]);

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
    public void A_stored_still_knows_which_part_of_its_canvas_is_drawn()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(
            Path.Combine(_folder, "art.png"),
            Encode(new PngBitmapEncoder(), Art(64, 64, (24, 24, 16, 16), 0, 0, 0xFF)));

        CrosshairPicture? picture = CrosshairImageStore.LoadPicture("art.png", _folder);

        Assert.NotNull(picture);
        Assert.Equal(new Int32Rect(24, 24, 16, 16), picture!.Content);
    }

    [Fact]
    public void An_animation_is_cropped_to_the_box_every_frame_shares()
    {
        // One dot, in two places. The rectangle has to hold both, and be one
        // rectangle rather than one per frame, or a pulsing crosshair would appear
        // to breathe in and out as it played.
        Directory.CreateDirectory(_folder);
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Art(64, 64, (28, 28, 8, 8), 0, 0, 0xFF)));
        encoder.Frames.Add(BitmapFrame.Create(Art(64, 64, (28, 32, 8, 8), 0, 0, 0xFF)));
        Write(encoder, Path.Combine(_folder, "dots.gif"));

        (CrosshairImageStore.StoredPicture? stored, _) = Import(Path.Combine(_folder, "dots.gif"));
        Assert.NotNull(stored);

        CrosshairPicture? picture = CrosshairImageStore.LoadPicture(stored!.Name, _folder);

        Assert.NotNull(picture);
        Assert.Equal(new Int32Rect(28, 28, 8, 12), picture!.Content);
    }

    [Fact]
    public void The_art_fills_the_size_it_is_given()
    {
        // A sixteen pixel cross in a sixty-four pixel canvas, drawn at thirty-two:
        // before this, the art came out at eight pixels, which is the whole bug.
        (int width, int height) = OnSta(() => Drawn(
            Art(64, 64, (24, 24, 16, 16), 0xFF, 0xFF, 0xFF),
            new Int32Rect(24, 24, 16, 16),
            size: 32));

        Assert.InRange(width, 30, 32);
        Assert.InRange(height, 30, 32);
    }

    [Fact]
    public void A_wide_picture_is_not_squashed_into_a_square()
    {
        (int width, int height) = OnSta(() => Drawn(
            Art(64, 16, (0, 0, 64, 16), 0xFF, 0xFF, 0xFF),
            default,
            size: 32));

        Assert.InRange(width, 30, 32);
        Assert.InRange(height, 7, 8);
    }

    [Fact]
    public void The_stored_picture_hands_its_rectangle_to_the_drawing_code()
    {
        // The whole chain below the view: the file is imported, loaded, works out
        // which part of itself is drawn, and is composed with that rectangle. A
        // sixteen pixel cross in a sixty-four pixel file, asked for at thirty-two,
        // has to come out at thirty-two, which is what was impossible before.
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(
            Path.Combine(_folder, "tiny.png"),
            Encode(new PngBitmapEncoder(), Art(64, 64, (24, 24, 16, 16), 0xFF, 0xFF, 0xFF)));

        CrosshairPicture? picture = CrosshairImageStore.LoadPicture("tiny.png", _folder);
        Assert.NotNull(picture);

        (int width, int height) = OnSta(() =>
        {
            var preset = new CrosshairPreset
            {
                ShapeKind = CrosshairShape.Custom,
                Color = "#FFFFFF",
                Size = 32,
                Outline = false,
                Glow = false
            };

            BitmapSource? composed = CrosshairImageComposer.Compose(
                picture!.First, preset, 1.0, picture.Content, out _);
            Assert.NotNull(composed);

            (Int32Rect bounds, _) = VisibleBounds(composed!);
            return (bounds.Width, bounds.Height);
        });

        Assert.InRange(width, 30, 32);
        Assert.InRange(height, 30, 32);
    }

    [Fact]
    public void Without_a_content_rectangle_the_whole_picture_is_drawn()
    {
        (int width, int height) = OnSta(() => Drawn(
            Art(32, 32, (0, 0, 32, 32), 0xFF, 0xFF, 0xFF),
            default,
            size: 32));

        Assert.InRange(width, 30, 32);
        Assert.InRange(height, 30, 32);
    }

    // ------------------------------------------------------------------ helpers

    private static T OnSta<T>(Func<T> body) => WpfTestHost.Invoke(body);

    private (CrosshairImageStore.StoredPicture? Stored, string Problem) Import(string path)
    {
        return WpfTestHost.Invoke(() =>
        {
            CrosshairImageStore.StoredPicture? stored = CrosshairImageStore.Import(path, _folder, out string problem);
            return (stored, problem);
        });
    }

    /// <summary>Compose the picture and measure the rectangle that is not transparent.</summary>
    private static (int Width, int Height) Drawn(BitmapSource source, Int32Rect content, int size)
    {
        var preset = new CrosshairPreset
        {
            ShapeKind = CrosshairShape.Custom,
            Color = "#FFFFFF",
            Size = size,
            Thickness = 2,
            Outline = false,
            Glow = false
        };

        BitmapSource? composed = CrosshairImageComposer.Compose(source, preset, 1.0, content, out _);
        Assert.NotNull(composed);

        (Int32Rect bounds, _) = VisibleBounds(composed!);
        return (bounds.Width, bounds.Height);
    }

    private static (Int32Rect Bounds, byte[] Pixels) VisibleBounds(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(((y * width) + x) * 4) + 3] < 8)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left
            ? (default, pixels)
            : (new Int32Rect(left, top, right - left + 1, bottom - top + 1), pixels);
    }

    /// <summary>A transparent canvas with one opaque rectangle drawn on it.</summary>
    private static BitmapSource Art(int width, int height, (int X, int Y, int W, int H) box, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = box.Y; y < box.Y + box.H; y++)
        {
            for (int x = box.X; x < box.X + box.W; x++)
            {
                int at = (((y * width) + x) * 4);
                pixels[at] = b;
                pixels[at + 1] = g;
                pixels[at + 2] = r;
                pixels[at + 3] = 0xFF;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource frame)
    {
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void Write(BitmapEncoder encoder, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>
/// Reset: back to the default colour and tuning, keeping the shape and the picture,
/// and back to a picture's own colours when it has any.
/// </summary>
[Collection("wpf")]
public sealed class CrosshairResetTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gamertool-reset-" + Guid.NewGuid().ToString("N")[..8]);

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
    public void Reset_puts_every_tuning_field_back_and_keeps_what_identifies_the_crosshair()
    {
        var preset = new CrosshairPreset
        {
            Id = "xh-mine",
            Name = "Mine",
            ShapeKind = CrosshairShape.Custom,
            Color = "#FF2BB0",
            Size = 57,
            Thickness = 7,
            Gap = 0,
            PictureGap = 11,
            Opacity = 35,
            Rotation = 200,
            CenterDot = true,
            Glow = true,
            Outline = false,
            KeepOwnColours = true,
            CustomImage = "custom-aaaabbbb.png"
        };

        preset.ResetTuning();

        var fresh = new CrosshairPreset();
        Assert.Equal(fresh.Color, preset.Color);
        Assert.Equal(fresh.Size, preset.Size);
        Assert.Equal(fresh.Thickness, preset.Thickness);
        Assert.Equal(fresh.Gap, preset.Gap);
        Assert.Equal(fresh.PictureGap, preset.PictureGap);
        Assert.Equal(fresh.Opacity, preset.Opacity);
        Assert.Equal(fresh.Rotation, preset.Rotation);
        Assert.Equal(fresh.CenterDot, preset.CenterDot);
        Assert.Equal(fresh.Glow, preset.Glow);
        Assert.Equal(fresh.Outline, preset.Outline);

        Assert.Equal("xh-mine", preset.Id);
        Assert.Equal("Mine", preset.Name);
        Assert.Equal(CrosshairShape.Custom, preset.ShapeKind);
        Assert.Equal("custom-aaaabbbb.png", preset.CustomImage);

        // Own colours are the caller's call: they belong to the picture, not to the
        // tuning, and resetting must not silently tint a multi-coloured animation.
        Assert.True(preset.KeepOwnColours);
    }

    [Fact]
    public void A_picture_with_its_own_colours_is_recognised_from_the_stored_file()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "white.png"), Png(255, 255, 255));
        File.WriteAllBytes(Path.Combine(_folder, "cyan.png"), Png(0x1F, 0xE0, 0xFF));

        Assert.False(CrosshairImageStore.PictureHasOwnColours("white.png", _folder));
        Assert.True(CrosshairImageStore.PictureHasOwnColours("cyan.png", _folder));

        // A name that is not there is not an error, just nothing to keep.
        Assert.False(CrosshairImageStore.PictureHasOwnColours("gone.png", _folder));
        Assert.False(CrosshairImageStore.PictureHasOwnColours(string.Empty, _folder));
    }

    private static byte[] Png(byte r, byte g, byte b)
    {
        const int side = 32;
        byte[] pixels = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++)
        {
            int at = i * 4;
            pixels[at] = b;
            pixels[at + 1] = g;
            pixels[at + 2] = r;
            pixels[at + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, pixels, side * 4);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

/// <summary>
/// The frames a pack tile plays: small, thinned to a fixed limit, tinted when the
/// picture is one that answers to the swatches, and built off the UI thread.
/// </summary>
[Collection("wpf")]
public sealed class CrosshairPackPreviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gamertool-preview-" + Guid.NewGuid().ToString("N")[..8]);

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
    public void A_pack_animation_is_decoded_into_the_frames_a_tile_plays()
    {
        string path = WriteGif("two.gif", 2, (0x20, 0xD0, 0x40), (0xFF, 0x3B, 0x3B));

        CrosshairPackPreview? preview = Build(path, side: 64, tint: null);

        Assert.NotNull(preview);
        Assert.Equal(2, preview!.Frames.Count);

        // Never enlarged past its own size, and never bigger than the tile asked
        // for: every frame of one animation is the same square, or the tile would
        // twitch between frames.
        int side = preview.Frames[0].PixelWidth;
        Assert.InRange(side, 8, 64);
        Assert.All(preview.Frames, frame => Assert.Equal(side, frame.PixelWidth));

        // Both frames of a hundredth of a second GIF last the fallback tenth of a
        // second, which is what a browser shows.
        Assert.All(preview.DelaysMs, delay => Assert.Equal(100, delay));

        (int r0, int g0, int b0, int a0) = WpfTestHost.Invoke(() => Centre(preview.Frames[0]));
        (int r1, int g1, int b1, int a1) = WpfTestHost.Invoke(() => Centre(preview.Frames[1]));

        Assert.Equal(255, a0);
        Assert.Equal(255, a1);
        Assert.True(g0 > 180 && r0 < 90, $"the first frame should still be green, was {r0},{g0},{b0}");
        Assert.True(r1 > 180 && g1 < 120, $"the second frame should still be red, was {r1},{g1},{b1}");
    }

    [Fact]
    public void A_mask_animation_is_previewed_in_the_chosen_colour()
    {
        // What a white mask in the grid looks like: multiplied by the crosshair's
        // colour, the same as the composer will do to it once it is chosen.
        string path = WriteGif("white.gif", 2, (255, 255, 255), (255, 255, 255));

        CrosshairPackPreview? preview = Build(path, side: 48, tint: Color.FromRgb(0xFF, 0x3B, 0x3B));

        Assert.NotNull(preview);
        (int r, int g, int b, int _) = WpfTestHost.Invoke(() => Centre(preview!.Frames[0]));

        Assert.True(r > 200 && g < 100 && b < 100, $"should have been tinted red, was {r},{g},{b}");
    }

    [Fact]
    public void A_long_animation_is_thinned_to_the_preview_limit()
    {
        (byte R, byte G, byte B)[] frames = new (byte, byte, byte)[30];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = ((byte)(i * 8), (byte)(255 - (i * 8)), 0x40);
        }

        string path = WriteGif("long.gif", 30, frames);

        CrosshairPackPreview? preview = Build(path, side: 64, tint: null);

        Assert.NotNull(preview);
        Assert.Equal(CrosshairPackPreview.MaxFrames, preview!.Frames.Count);
    }

    [Fact]
    public void A_still_is_not_a_preview()
    {
        string png = Path.Combine(_folder, "still.png");
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(png, EncodePng((0x20, 0xD0, 0x40)));

        Assert.Null(Build(png, side: 64, tint: null));

        // A GIF with one frame is a still that happens to be a GIF.
        string single = WriteGif("single.gif", 1, (0x20, 0xD0, 0x40));
        Assert.Null(Build(single, side: 64, tint: null));

        // And a file that is not there is not an error.
        Assert.Null(Build(Path.Combine(_folder, "gone.gif"), side: 64, tint: null));
    }

    // ------------------------------------------------------------------ helpers

    private static CrosshairPackPreview? Build(string path, int side, Color? tint)
    {
        return WpfTestHost.Invoke(() => CrosshairPackPreview.Build(path, side, tint));
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

    private string WriteGif(string name, int count, params (byte R, byte G, byte B)[] frames)
    {
        Directory.CreateDirectory(_folder);
        var encoder = new GifBitmapEncoder();
        foreach ((byte r, byte g, byte b) in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(Sheet(r, g, b)));
        }

        string path = Path.Combine(_folder, name);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] EncodePng((byte R, byte G, byte B) colour)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Sheet(colour.R, colour.G, colour.B)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource Sheet(byte r, byte g, byte b)
    {
        const int side = 32;
        byte[] pixels = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++)
        {
            int at = i * 4;
            pixels[at] = b;
            pixels[at + 1] = g;
            pixels[at + 2] = r;
            pixels[at + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, pixels, side * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
