using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// An imported picture that keeps the colours it was drawn with.
/// <para>
/// Every crosshair picture used to be multiplied by the preset's colour, which is
/// right for the white line art the pack is made of and wrong for anything else: a
/// multi-coloured animated GIF came out one flat colour, and the only way back was
/// the white swatch, which nobody would guess means "leave it alone". These are the
/// parts that decide which pictures arrive as they came, and what the setting does
/// to a preset and a redraw.
/// </para>
/// </summary>
public sealed class CrosshairOwnColourTests
{
    [Fact]
    public void A_picture_preset_remembers_that_it_keeps_its_own_colours()
    {
        var preset = new CrosshairPreset { ShapeKind = CrosshairShape.Custom, KeepOwnColours = true };

        string json = JsonSerializer.Serialize(preset);
        CrosshairPreset? back = JsonSerializer.Deserialize<CrosshairPreset>(json);

        Assert.NotNull(back);
        Assert.True(back!.KeepOwnColours);
    }

    [Fact]
    public void A_preset_saved_before_this_setting_still_tints()
    {
        // Every existing crosshair, and every preset in an older backup, has no
        // member for this. They must keep drawing exactly as they did, which means
        // tinting, not silently switching to own colours.
        const string json = "{\"Id\":\"xh-old\",\"Name\":\"Old\",\"Shape\":\"Custom\",\"Color\":\"#3CFF5A\",\"CustomImage\":\"custom-aaaabbbb.png\"}";

        CrosshairPreset? back = JsonSerializer.Deserialize<CrosshairPreset>(json);

        Assert.NotNull(back);
        Assert.False(back!.KeepOwnColours);
    }

    [Fact]
    public void Copy_and_SameLook_carry_the_setting()
    {
        var preset = new CrosshairPreset { ShapeKind = CrosshairShape.Custom, KeepOwnColours = true };
        var tinted = new CrosshairPreset { ShapeKind = CrosshairShape.Custom };

        Assert.True(preset.Copy().KeepOwnColours);
        Assert.False(preset.SameLook(tinted));
        Assert.True(preset.SameLook(preset.Copy()));
    }

    [Fact]
    public void A_white_mask_has_no_colours_of_its_own()
    {
        Assert.False(CrosshairImageStore.HasOwnColours(Pixels(64, 255, 255, 255)));
    }

    [Fact]
    public void A_multi_coloured_picture_does()
    {
        Assert.True(CrosshairImageStore.HasOwnColours(Pixels(64, 0x20, 0xD0, 0x40)));
    }

    [Fact]
    public void The_odd_coloured_pixel_of_a_mask_does_not_count()
    {
        // A stray pixel in a corner of a white mask is not a design. If a handful of
        // them could switch the colour row off, importing the pack would start
        // turning the tint off at random.
        byte[] pixels = Pixels(256, 255, 255, 255);
        for (int i = 0; i < CrosshairImageStore.OwnColourPixels - 1; i++)
        {
            Set(pixels, i, 0, 0, 0xFF);
        }

        Assert.False(CrosshairImageStore.HasOwnColours(pixels));

        Set(pixels, CrosshairImageStore.OwnColourPixels - 1, 0, 0, 0xFF);
        Assert.True(CrosshairImageStore.HasOwnColours(pixels));
    }

    [Fact]
    public void A_faint_edge_does_not_count_as_colour()
    {
        // The fringe where a white shape fades out keeps whatever colour the edge
        // picked up. It is a few levels off grey and nearly invisible, so it is not
        // something to keep.
        Assert.False(CrosshairImageStore.HasOwnColours(Pixels(64, 0, 0, 0xFF, alpha: 95)));
    }

    [Fact]
    public void A_photograph_is_shown_as_it_came_even_in_grey()
    {
        // A JPEG or a BMP is never a white mask to be tinted, so it arrives in its own
        // colours whatever they are. This is the rule the old code applied by forcing
        // the colour to white on import.
        byte[] grey = Pixels(64, 128, 128, 128);

        Assert.True(CrosshairImageStore.KeepOwnColours(PictureKind.Jpeg, grey));
        Assert.True(CrosshairImageStore.KeepOwnColours(PictureKind.Bmp, grey));

        // A PNG or a GIF is the pack's format, and a white one goes on answering to
        // the colour swatches.
        Assert.False(CrosshairImageStore.KeepOwnColours(PictureKind.Png, grey));
        Assert.False(CrosshairImageStore.KeepOwnColours(PictureKind.Gif, grey));

        Assert.True(CrosshairImageStore.KeepOwnColours(PictureKind.Png, Pixels(64, 0x20, 0xD0, 0x40)));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Pixels(int count, byte r, byte g, byte b, byte alpha = 0xFF)
    {
        byte[] pixels = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            Set(pixels, i, r, g, b, alpha);
        }

        return pixels;
    }

    private static void Set(byte[] pixels, int index, byte r, byte g, byte b, byte alpha = 0xFF)
    {
        // Premultiplied BGRA, which is what the store reads everywhere.
        int at = index * 4;
        pixels[at] = (byte)(b * alpha / 255);
        pixels[at + 1] = (byte)(g * alpha / 255);
        pixels[at + 2] = (byte)(r * alpha / 255);
        pixels[at + 3] = alpha;
    }
}

/// <summary>
/// The composer's half: the setting has to change what is drawn, and drawing a
/// picture in its own colours has to be the same picture.
/// </summary>
[Collection("wpf")]
public sealed class CrosshairOwnColourRenderTests
{
    private static T OnSta<T>(Func<T> body) => WpfTestHost.Invoke(body);

    [Fact]
    public void A_white_mask_takes_the_chosen_colour()
    {
        (int r, int g, int b, int a) = OnSta(() => Centre(Preset(ownColours: false), Solid(64, 255, 255, 255)));

        Assert.Equal(255, a);
        Assert.True(r > 200, "the mask should have come out red, was " + r);
        Assert.True(b < 100, "nothing of the white should be left, was " + b);
    }

    [Fact]
    public void A_picture_that_keeps_its_colours_is_drawn_as_it_came()
    {
        (int r, int g, int b, int a) = OnSta(() => Centre(Preset(ownColours: true), Solid(64, 0, 0, 0xFF)));

        Assert.Equal(255, a);
        Assert.True(b > 200, "blue should have survived untouched, was " + b);
        Assert.True(r < 60, "red should not have been multiplied in, was " + r);
        Assert.True(g < 60, "green should not have been multiplied in, was " + g);
    }

    [Fact]
    public void White_leaves_a_picture_as_it_came_too()
    {
        // The old way of asking for this: pick white. It has to keep working, because
        // every saved preset that did it that way still has a white colour stored.
        (int r, int g, int b, int _) = OnSta(() => Centre(Preset(ownColours: false, colour: "#FFFFFF"), Solid(64, 0, 0, 0xFF)));

        Assert.True(b > 200, "blue should have survived a white multiply, was " + b);
        Assert.True(r < 60, "red should not have been multiplied in, was " + r);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Compose the picture with the preset and read the middle pixel.</summary>
    private static (int R, int G, int B, int A) Centre(CrosshairPreset preset, BitmapSource source)
    {
        BitmapSource? composed = CrosshairImageComposer.Compose(source, preset, 1.0, out _);
        Assert.NotNull(composed);

        var converted = new FormatConvertedBitmap(composed!, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth;
        byte[] pixels = new byte[width * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        int at = (((converted.PixelHeight / 2) * width) + (width / 2)) * 4;
        return (pixels[at + 2], pixels[at + 1], pixels[at], pixels[at + 3]);
    }

    private static CrosshairPreset Preset(bool ownColours, string colour = "#FF3B3B")
    {
        return new CrosshairPreset
        {
            ShapeKind = CrosshairShape.Custom,
            Color = colour,
            Size = 20,
            Thickness = 2,
            Outline = false,
            Glow = false,
            KeepOwnColours = ownColours
        };
    }

    /// <summary>A solid opaque square, the simplest picture that has a colour to lose.</summary>
    private static BitmapSource Solid(int side, byte r, byte g, byte b)
    {
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

/// <summary>
/// The import itself, through real files in a throwaway folder.
/// <para>
/// The flag is decided from the pixels that were imported, and an animation is
/// decided from the strip it is stored as, so these are the two places where the
/// rule could quietly disagree with the tests above that only feed it pixels. A
/// multi-colored GIF is the case that started this: it used to arrive as one flat
/// color with no way back.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairOwnColourImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gamertool-import-" + Guid.NewGuid().ToString("N")[..8]);

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
    public void A_multi_coloured_animation_arrives_with_its_colours()
    {
        (CrosshairImageStore.StoredPicture? stored, string problem) = Import(
            WriteGif("loop.gif", (0x20, 0xD0, 0x40), (0xFF, 0x3B, 0x3B)));

        Assert.NotNull(stored);
        Assert.Equal(string.Empty, problem);
        Assert.True(stored!.OwnColours, "a two-colour animation should not be tinted on import");

        // It still went in as an animation: the strip and its timings are both there.
        Assert.True(File.Exists(Path.Combine(_folder, stored.Name)));
        string timings = Path.Combine(_folder, Path.GetFileNameWithoutExtension(stored.Name) + ".anim.json");
        Assert.True(File.Exists(timings), "the timings should have been written beside the strip");
        Assert.True(AnimationInfo.TryParse(File.ReadAllText(timings), out AnimationInfo info));
        Assert.Equal(2, info.Frames);
    }

    [Fact]
    public void A_white_animation_stays_a_mask()
    {
        (CrosshairImageStore.StoredPicture? stored, _) = Import(WriteGif("white.gif", (255, 255, 255), (255, 255, 255)));

        Assert.NotNull(stored);
        Assert.False(stored!.OwnColours, "white line art should still answer to the color swatches");
    }

    [Fact]
    public void A_coloured_still_arrives_with_its_colours_and_a_white_one_does_not()
    {
        (CrosshairImageStore.StoredPicture? coloured, _) = Import(WritePng("coloured.png", (0x20, 0xD0, 0x40)));
        (CrosshairImageStore.StoredPicture? white, _) = Import(WritePng("white.png", (255, 255, 255)));

        Assert.NotNull(coloured);
        Assert.NotNull(white);
        Assert.True(coloured!.OwnColours);
        Assert.False(white!.OwnColours);
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

    private string WriteGif(string name, params (byte R, byte G, byte B)[] frames)
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

    private string WritePng(string name, (byte R, byte G, byte B) colour)
    {
        Directory.CreateDirectory(_folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Sheet(colour.R, colour.G, colour.B)));

        string path = Path.Combine(_folder, name);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>An opaque square of one colour, built on the WPF thread that will encode it.</summary>
    private static BitmapSource Sheet(byte r, byte g, byte b)
    {
        return WpfTestHost.Invoke(() =>
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
        });
    }
}
