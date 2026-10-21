using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The centre dot's size, which used to be the line's thickness in disguise.
/// <para>
/// A dot has one job: mark the pixel you are aiming at. Inheriting the thickness
/// made that job impossible at both ends of the slider - a one pixel crosshair put
/// a one pixel dot on the screen, which is gone against anything bright, and an
/// eight pixel crosshair drew a dot wide enough to fill the gap it was supposed to
/// sit inside. Every shooter that lets a player tune a dot gives it a size of its
/// own, so this one does too, and a preset that never asked for one keeps the size
/// it had.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class CrosshairCentreDotSizeTests
{
    [Fact]
    public void A_dot_with_no_size_of_its_own_is_as_wide_as_the_line()
    {
        // The width it inherited before it could be sized: the line plus one pixel.
        Assert.Equal(2, new CrosshairPreset { Thickness = 1 }.EffectiveCenterDotSize());
        Assert.Equal(3, new CrosshairPreset { Thickness = 2 }.EffectiveCenterDotSize());
        Assert.Equal(6, new CrosshairPreset { Thickness = 5 }.EffectiveCenterDotSize());
        Assert.Equal(9, new CrosshairPreset { Thickness = CrosshairPreset.ThicknessMax }.EffectiveCenterDotSize());

        Assert.False(new CrosshairPreset().HasOwnCenterDotSize);
        Assert.Equal(0, new CrosshairPreset().CenterDotSize);
    }

    [Fact]
    public void A_size_of_its_own_wins_over_the_line()
    {
        var preset = new CrosshairPreset { Thickness = 1, CenterDotSize = 7 };

        Assert.True(preset.HasOwnCenterDotSize);
        Assert.Equal(7, preset.EffectiveCenterDotSize());

        // The drawing follows the same number, so the slider cannot say one thing
        // while the screen shows another.
        Assert.Equal(3.5, CrosshairGeometry.CenterDotRadius(preset));

        // A dot can also be thinner than the line, which is the whole reason it has
        // a control of its own.
        var thin = new CrosshairPreset { Thickness = 8, CenterDotSize = 2 };
        Assert.Equal(1.0, CrosshairGeometry.CenterDotRadius(thin));
    }

    [Fact]
    public void The_size_is_copied_reset_and_pulled_into_range()
    {
        var preset = new CrosshairPreset { Thickness = 4, CenterDotSize = 7 };

        Assert.Equal(7, preset.Copy().CenterDotSize);

        // Reset means "a new crosshair", and a new crosshair follows the line.
        preset.ResetTuning();
        Assert.Equal(0, preset.CenterDotSize);
        Assert.False(preset.HasOwnCenterDotSize);

        // A hand edited file cannot ask for a dot wider than the window it is drawn in.
        Assert.Equal(CrosshairPreset.CenterDotSizeMax, new CrosshairPreset { CenterDotSize = 99 }.Sanitised().CenterDotSize);
        Assert.Equal(0, new CrosshairPreset { CenterDotSize = -4 }.Sanitised().CenterDotSize);
        Assert.Equal(1, new CrosshairPreset { CenterDotSize = 1 }.Sanitised().CenterDotSize);
    }

    [Fact]
    public void Two_presets_that_draw_the_same_dot_are_the_same_look()
    {
        // What is compared is the dot on the screen, not how it got there: a thick
        // line that happens to make a seven pixel dot looks the same as a seven
        // pixel dot that was set by hand.
        var following = new CrosshairPreset { Thickness = 6 };
        var sized = new CrosshairPreset { Thickness = 6, CenterDotSize = 7 };

        Assert.True(following.SameLook(sized));

        sized.CenterDotSize = 8;
        Assert.False(following.SameLook(sized));
    }

    [Fact]
    public void A_profile_written_before_the_size_existed_still_follows_the_line()
    {
        // There is no field in the JSON, which is what every saved profile looks like
        // on the way in. It has to read as "follow", not as "zero pixels".
        const string old = """
            {"Id":"xh-old","Name":"Old","Shape":"Cross","Color":"#3CFF5A","Size":16,
             "Thickness":2,"Gap":4,"Opacity":100,"Rotation":0,"CenterDot":true}
            """;

        var preset = JsonSerializer.Deserialize<CrosshairPreset>(old)!;

        Assert.Equal(0, preset.CenterDotSize);
        Assert.False(preset.HasOwnCenterDotSize);
        Assert.Equal(3, preset.EffectiveCenterDotSize());

        // And a size that is there survives the trip in both directions.
        preset.CenterDotSize = 5;
        var again = JsonSerializer.Deserialize<CrosshairPreset>(JsonSerializer.Serialize(preset))!;
        Assert.Equal(5, again.CenterDotSize);
    }

    [Fact]
    public void A_wider_dot_covers_pixels_a_thin_one_does_not()
    {
        WpfTestHost.Invoke(() =>
        {
            // A red cross with a deliberately wide gap, so the middle of the picture
            // is empty except for the dot. Two pixels off centre is inside a seven
            // pixel dot and outside a two pixel one.
            CrosshairPreset preset = Middle();

            preset.CenterDotSize = 2;
            Assert.Null(PixelAt(preset, 2, 0));

            preset.CenterDotSize = 7;
            (byte R, byte G, byte B) wide = PixelAt(preset, 2, 0)!.Value;

            // The dot's own colour, not the crosshair's.
            Assert.InRange(wide.R, 0x1D, 0x21);
            Assert.InRange(wide.G, 0xDE, 0xE2);
            Assert.InRange(wide.B, 0xFD, 0xFF);

            // Still something at the very centre of the two pixel dot, so the bigger
            // dot grew outwards rather than the whole thing moving.
            Assert.NotNull(PixelAt(Middle(), 0, 0));
        });
    }

    /// <summary>A red cross with a cyan dot, empty at the middle apart from the dot.</summary>
    private static CrosshairPreset Middle()
    {
        return new CrosshairPreset
        {
            ShapeKind = CrosshairShape.Cross,
            Color = "#FF0000",
            Size = 20,
            Thickness = 1,
            Gap = 8,
            Rotation = 0,
            Outline = false,
            Glow = false,
            CenterDot = true,
            CenterDotColor = "#1FE0FF"
        };
    }

    /// <summary>One pixel of the drawn crosshair, counted in pixels from the middle.</summary>
    private static (byte R, byte G, byte B)? PixelAt(CrosshairPreset preset, int dx, int dy)
    {
        const int side = 80;
        var view = new CrosshairView { Preset = preset, Magnification = 1.0, Width = side, Height = side };
        view.Measure(new Size(side, side));
        view.Arrange(new Rect(0, 0, side, side));

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);

        // Not premultiplied: the point is the colour the dot is painted, not how much
        // of it comes through the opacity.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[side * side * 4];
        converted.CopyPixels(pixels, side * 4, 0);

        int x = (side / 2) + dx;
        int y = (side / 2) + dy;
        int at = ((y * side) + x) * 4;

        return pixels[at + 3] == 0 ? null : (pixels[at + 2], pixels[at + 1], pixels[at]);
    }
}
