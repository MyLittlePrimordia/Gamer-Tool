using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The parts of animated and imported crosshairs that are arithmetic on pixels and
/// numbers: building the picture a GIF frame means, shrinking it, timing it, and
/// deciding where the preview aims. None of it touches WPF, which is what makes
/// "does a frame with disposal 3 put the old picture back" answerable without a
/// screen. The decoder and the drawing are the thin parts left over.
/// </summary>
public class CrosshairAnimationTests
{
    private static byte[] Solid(int width, int height, byte b, byte g, byte r, byte a)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = b;
            pixels[p + 1] = g;
            pixels[p + 2] = r;
            pixels[p + 3] = a;
        }

        return pixels;
    }

    private static byte[] Pixel(byte[] canvas, int width, int x, int y)
    {
        int at = ((y * width) + x) * 4;
        return new[] { canvas[at], canvas[at + 1], canvas[at + 2], canvas[at + 3] };
    }

    /// <summary>Compose and keep a copy of every canvas, because the compositor reuses its array.</summary>
    private static List<byte[]> Run(int width, int height, params GifRawFrame[] frames)
    {
        var canvases = new List<byte[]>();
        GifFrameCompositor.Compose(width, height, frames.Length, i => frames[i], (_, canvas) => canvases.Add((byte[])canvas.Clone()));
        return canvases;
    }

    // ------------------------------------------------------------ GIF frames

    [Fact]
    public void Every_frame_is_handed_out_once_in_order()
    {
        var seen = new List<int>();
        GifFrameCompositor.Compose(
            2, 2, 3,
            _ => new GifRawFrame(0, 0, 2, 2, Solid(2, 2, 255, 255, 255, 255), 0),
            (i, _) => seen.Add(i));

        Assert.Equal(new[] { 0, 1, 2 }, seen);
    }

    [Fact]
    public void A_frame_that_only_holds_the_changed_part_is_drawn_over_the_last_picture()
    {
        // The usual GIF: frame 0 is the whole picture, frame 1 is a 1x1 patch.
        var canvases = Run(
            2, 2,
            new GifRawFrame(0, 0, 2, 2, Solid(2, 2, 10, 20, 30, 255), 1),
            new GifRawFrame(1, 1, 1, 1, Solid(1, 1, 200, 200, 200, 255), 1));

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, Pixel(canvases[1], 2, 0, 0));
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, Pixel(canvases[1], 2, 1, 1));
    }

    [Fact]
    public void A_transparent_pixel_in_a_frame_leaves_what_was_underneath()
    {
        var canvases = Run(
            1, 1,
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 50, 60, 70, 255), 1),
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 0, 0, 0, 0), 1));

        Assert.Equal(new byte[] { 50, 60, 70, 255 }, Pixel(canvases[1], 1, 0, 0));
    }

    [Fact]
    public void Disposal_two_clears_the_frame_rectangle_before_the_next_frame()
    {
        var canvases = Run(
            2, 1,
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 255, 0, 0, 255), 2),
            new GifRawFrame(1, 0, 1, 1, Solid(1, 1, 0, 255, 0, 255), 1));

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(canvases[1], 2, 0, 0));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(canvases[1], 2, 1, 0));
    }

    [Fact]
    public void Disposal_one_leaves_the_frame_in_place()
    {
        var canvases = Run(
            2, 1,
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 255, 0, 0, 255), 1),
            new GifRawFrame(1, 0, 1, 1, Solid(1, 1, 0, 255, 0, 255), 1));

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(canvases[1], 2, 0, 0));
    }

    [Fact]
    public void Disposal_three_puts_back_what_was_there_before_the_frame()
    {
        // Frame 1 paints over the whole canvas and asks to be undone; frame 2 is
        // a transparent patch, so what it shows is whatever was restored.
        var canvases = Run(
            1, 1,
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 9, 9, 9, 255), 1),
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 99, 99, 99, 255), 3),
            new GifRawFrame(0, 0, 1, 1, Solid(1, 1, 0, 0, 0, 0), 1));

        Assert.Equal(new byte[] { 99, 99, 99, 255 }, Pixel(canvases[1], 1, 0, 0));
        Assert.Equal(new byte[] { 9, 9, 9, 255 }, Pixel(canvases[2], 1, 0, 0));
    }

    [Fact]
    public void A_frame_hanging_off_the_canvas_is_clipped_not_an_error()
    {
        var canvases = Run(
            2, 2,
            new GifRawFrame(1, 1, 3, 3, Solid(3, 3, 5, 5, 5, 255), 2),
            new GifRawFrame(-2, -2, 3, 3, Solid(3, 3, 7, 7, 7, 255), 1));

        Assert.Equal(new byte[] { 5, 5, 5, 255 }, Pixel(canvases[0], 2, 1, 1));
        Assert.Equal(new byte[] { 7, 7, 7, 255 }, Pixel(canvases[1], 2, 0, 0));
    }

    [Fact]
    public void A_frame_with_too_few_pixels_is_skipped_rather_than_read_past_its_end()
    {
        var canvases = Run(1, 1, new GifRawFrame(0, 0, 4, 4, new byte[4], 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(canvases[0], 1, 0, 0));
    }

    // ---------------------------------------------------------------- resize

    [Fact]
    public void A_picture_that_already_fits_is_not_enlarged()
    {
        byte[] source = Solid(8, 8, 1, 2, 3, 255);
        byte[] result = PixelResize.FitSquare(source, 8, 8, 256, out int side);

        Assert.Equal(8, side);
        Assert.Equal(source, result);
    }

    [Fact]
    public void A_big_solid_picture_stays_the_same_colour_when_shrunk()
    {
        byte[] result = PixelResize.FitSquare(Solid(64, 64, 40, 80, 120, 255), 64, 64, 16, out int side);

        Assert.Equal(16, side);
        Assert.Equal(new byte[] { 40, 80, 120, 255 }, Pixel(result, 16, 0, 0));
        Assert.Equal(new byte[] { 40, 80, 120, 255 }, Pixel(result, 16, 15, 15));
    }

    [Fact]
    public void A_wide_picture_is_centred_in_a_transparent_square()
    {
        byte[] result = PixelResize.FitSquare(Solid(40, 10, 255, 255, 255, 255), 40, 10, 20, out int side);

        Assert.Equal(20, side);
        // Scaled to 20 x 5 and centred: the first row is bar, the middle is picture.
        Assert.Equal(0, Pixel(result, 20, 10, 0)[3]);
        Assert.Equal(255, Pixel(result, 20, 10, 10)[3]);
        Assert.Equal(0, Pixel(result, 20, 10, 19)[3]);
    }

    [Fact]
    public void Shrinking_does_not_darken_a_half_transparent_edge()
    {
        // Premultiplied white at half alpha is (128,128,128,128). Averaged with
        // nothing but itself it has to stay that, not drift towards black.
        byte[] result = PixelResize.FitSquare(Solid(8, 8, 128, 128, 128, 128), 8, 8, 4, out int _);
        Assert.Equal(new byte[] { 128, 128, 128, 128 }, Pixel(result, 4, 1, 1));
    }

    [Fact]
    public void Scaling_nothing_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PixelResize.FitSquare(new byte[0], 0, 0, 16, out int _));
        Assert.Throws<ArgumentException>(() => PixelResize.FitSquare(new byte[4], 4, 4, 16, out int _));
    }

    // ---------------------------------------------------------------- timing

    [Theory]
    [InlineData(0, 100)]   // browsers treat 0 and 1 as a tenth of a second
    [InlineData(1, 100)]
    [InlineData(2, 20)]
    [InlineData(5, 50)]
    [InlineData(10, 100)]
    [InlineData(30000, 10000)]
    public void A_gif_delay_is_turned_into_milliseconds_the_way_a_browser_would(int hundredths, int expected)
    {
        Assert.Equal(expected, AnimationPlan.DelayMs(hundredths));
    }

    [Theory]
    [InlineData(100, 100, 60, true)]
    [InlineData(1024, 1024, 64, true)]   // exactly the budget
    [InlineData(1025, 10, 2, false)]     // side limit
    [InlineData(1024, 1024, 65, false)]  // one frame over the pixel budget
    [InlineData(0, 10, 2, false)]
    [InlineData(10, 10, 0, false)]
    public void An_animation_is_taken_only_inside_the_size_and_memory_limits(int width, int height, int frames, bool expected)
    {
        Assert.Equal(expected, AnimationPlan.WithinBudget(width, height, frames));
    }

    [Fact]
    public void An_animation_within_the_frame_limit_keeps_every_frame_and_delay()
    {
        var kept = AnimationPlan.Reduce(new[] { 40, 50, 60 }, AnimationPlan.MaxFrames);
        Assert.Equal(new[] { (0, 40), (1, 50), (2, 60) }, kept);
    }

    [Fact]
    public void A_long_animation_is_thinned_evenly_and_keeps_its_running_time()
    {
        int[] delays = Enumerable.Repeat(40, 150).ToArray();
        var kept = AnimationPlan.Reduce(delays, 60);

        Assert.Equal(60, kept.Count);
        Assert.Equal(0, kept[0].Index);
        Assert.True(kept.Select(k => k.Index).SequenceEqual(kept.Select(k => k.Index).Distinct().OrderBy(i => i)));
        Assert.Equal(delays.Sum(), kept.Sum(k => k.DelayMs));
    }

    // --------------------------------------------------------------- sidecar

    [Fact]
    public void Animation_info_survives_a_round_trip()
    {
        var info = new AnimationInfo(128, new[] { 40, 40, 100 });

        Assert.True(AnimationInfo.TryParse(info.ToJson(), out AnimationInfo back));
        Assert.Equal(128, back.Side);
        Assert.Equal(new[] { 40, 40, 100 }, back.DelaysMs);
        Assert.Equal(3, back.Frames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Side\":128,\"Delays\":[40]}")]                // one frame is a picture
    [InlineData("{\"Side\":2,\"Delays\":[40,40]}")]               // too small
    [InlineData("{\"Side\":4096,\"Delays\":[40,40]}")]            // too big
    public void A_file_that_does_not_describe_an_animation_is_refused(string? json)
    {
        Assert.False(AnimationInfo.TryParse(json, out AnimationInfo info));
        Assert.Equal(0, info.Frames);
    }

    [Fact]
    public void A_file_asking_for_thousands_of_frames_is_refused()
    {
        string delays = string.Join(",", Enumerable.Repeat("40", 5000));
        Assert.False(AnimationInfo.TryParse("{\"Side\":128,\"Delays\":[" + delays + "]}", out AnimationInfo _));
    }

    [Fact]
    public void Delays_out_of_range_are_pulled_in_not_refused()
    {
        Assert.True(AnimationInfo.TryParse("{\"Side\":64,\"Delays\":[0,999999]}", out AnimationInfo info));
        Assert.Equal(new[] { AnimationPlan.MinDelayMs, AnimationPlan.MaxDelayMs }, info.DelaysMs);
    }

    // ------------------------------------------------------------ file kinds

    [Theory]
    [InlineData("a.png", PictureKind.Png)]
    [InlineData("A.PNG", PictureKind.Png)]
    [InlineData("spin.gif", PictureKind.Gif)]
    [InlineData("photo.jpg", PictureKind.Jpeg)]
    [InlineData("photo.JPEG", PictureKind.Jpeg)]
    [InlineData("old.bmp", PictureKind.Bmp)]
    [InlineData("drawing.svg", PictureKind.None)]   // left out on purpose
    [InlineData("notes.txt", PictureKind.None)]
    [InlineData("noextension", PictureKind.None)]
    [InlineData("", PictureKind.None)]
    [InlineData(null, PictureKind.None)]
    public void The_kind_of_picture_comes_from_the_extension(string? path, PictureKind expected)
    {
        Assert.Equal(expected, CrosshairFileKinds.FromPath(path));
        Assert.Equal(expected != PictureKind.None, CrosshairFileKinds.IsSupported(path));
    }

    [Fact]
    public void The_dialog_filter_and_the_extension_list_name_the_same_files()
    {
        foreach (string extension in CrosshairFileKinds.Extensions)
        {
            Assert.Contains("*" + extension, CrosshairFileKinds.OpenFilter);
            Assert.True(CrosshairFileKinds.IsSupported("x" + extension));
        }
    }

    // ----------------------------------------------------------- the preview

    [Fact]
    public void In_a_square_box_the_aim_point_is_centred_across_and_above_the_middle_down()
    {
        (double dx, double dy) = CrosshairScene.Offset(410, 410);

        Assert.Equal(0.0, dx, 6);
        Assert.Equal((CrosshairScene.AimY - 0.5) * 410, dy, 6);
        Assert.True(dy < 0, "the chest is above the middle of the picture, not on the belt");
    }

    [Fact]
    public void A_box_wider_than_the_picture_crops_it_and_the_aim_point_moves_with_the_scale()
    {
        // 800 x 400 shows the picture at 800 wide, cropped top and bottom.
        (double dx, double dy) = CrosshairScene.Offset(800, 400);
        double drawn = 800.0;
        double top = (400 - drawn) / 2.0;

        Assert.Equal(0.0, dx, 6);
        Assert.Equal(top + (CrosshairScene.AimY * drawn) - 200.0, dy, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 100)]
    [InlineData(100, 0)]
    public void An_empty_box_has_nothing_to_aim_at(double width, double height)
    {
        Assert.Equal((0.0, 0.0), CrosshairScene.Offset(width, height));
    }
}
