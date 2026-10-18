using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace GamerTool.Models;

/// <summary>
/// The shapes the crosshair tab offers. Six minimal designs and one slot for an
/// image the user brings, and deliberately no more: the point of the feature is
/// that every combination is a good one.
/// </summary>
public enum CrosshairShape
{
    Cross,
    Dot,
    X,
    Circle,
    Square,
    Chevrons,
    Triangle,

    /// <summary>The classic "T": an arm either side and one below, none above.</summary>
    T,

    /// <summary>A picture, from the pack or imported. Tinted, outlined, glowed and rotated like any other shape.</summary>
    Custom
}

/// <summary>
/// One crosshair: a shape, a colour and four numbers.
/// <para>
/// Plain data and nothing else, the same as <see cref="DisplayPreset"/>. Drawing
/// it is <c>CrosshairView</c>'s job and the geometry is
/// <c>CrosshairGeometry</c>'s, so this can be saved, copied and compared without
/// touching WPF.
/// </para>
/// <para>
/// The shape is stored as text rather than as the enum. A settings file is
/// edited by hand and restored from backups written by other versions, and a
/// numeric or misspelt enum value would fail the read of the whole profile for
/// the sake of one crosshair. Text that does not parse falls back to a cross.
/// </para>
/// </summary>
public sealed class CrosshairPreset
{
    public const int SizeMin = 6;

    public const int SizeMax = 64;

    public const int ThicknessMin = 1;

    public const int ThicknessMax = 8;

    public const int GapMin = 0;

    public const int GapMax = 16;

    public const int OpacityMin = 20;

    public const int OpacityMax = 100;

    public const int RotationMax = 359;

    /// <summary>
    /// The six colours. High contrast on both a dark tunnel and a bright sky.
    /// Red leads because it is the colour most people reach for first. Anyone for
    /// whom red and green are hard to tell apart has cyan, magenta, yellow and
    /// white as well, and the colour picker for the rest.
    /// </summary>
    public static readonly string[] Palette =
    {
        "#FF3B3B", // red
        "#3CFF5A", // green
        "#1FE0FF", // cyan
        "#FF2BB0", // magenta
        "#FFE633", // yellow
        "#FFFFFF"  // white
    };

    public static readonly string[] PaletteNames = { "Red", "Green", "Cyan", "Magenta", "Yellow", "White" };

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>One of <see cref="CrosshairShape"/> by name. See the type remarks for why this is text.</summary>
    public string Shape { get; set; } = nameof(CrosshairShape.Cross);

    /// <summary>#RRGGBB. Anything else is repaired to green on load.</summary>
    public string Color { get; set; } = Palette[1];

    public int Size { get; set; } = 20;

    public int Thickness { get; set; } = 2;

    /// <summary>The empty space at the middle of a cross, in pixels.</summary>
    public int Gap { get; set; } = 4;

    /// <summary>Percent, so the number on screen is the number stored.</summary>
    public int Opacity { get; set; } = 100;

    /// <summary>Degrees clockwise, 0 to 359. Turns the whole crosshair, shape or picture.</summary>
    public int Rotation { get; set; }

    /// <summary>A small dot at the exact middle, drawn on top of whatever else is showing.</summary>
    public bool CenterDot { get; set; }

    public bool Glow { get; set; }

    /// <summary>
    /// A thin black edge round the shape. On by default because it is the single
    /// thing that keeps a bright crosshair visible against a bright scene.
    /// </summary>
    public bool Outline { get; set; } = true;

    /// <summary>
    /// File name, not a path, of the imported image inside the crosshair folder.
    /// A name rather than a path so a portable install can be moved and a
    /// restored backup cannot point the app at an arbitrary file.
    /// </summary>
    public string CustomImage { get; set; } = string.Empty;

    [JsonIgnore]
    public CrosshairShape ShapeKind
    {
        get => Enum.TryParse(Shape, ignoreCase: true, out CrosshairShape kind) && Enum.IsDefined(kind)
            ? kind
            : CrosshairShape.Cross;
        set => Shape = value.ToString();
    }

    public CrosshairPreset Copy()
    {
        return new CrosshairPreset
        {
            Id = Id,
            Name = Name,
            Shape = Shape,
            Color = Color,
            Size = Size,
            Thickness = Thickness,
            Gap = Gap,
            Opacity = Opacity,
            Rotation = Rotation,
            CenterDot = CenterDot,
            Glow = Glow,
            Outline = Outline,
            CustomImage = CustomImage
        };
    }

    /// <summary>
    /// Pull every field back into range. Called on anything that came off disk,
    /// because a hand edited file with a 4000 pixel crosshair would otherwise be
    /// drawn as asked.
    /// </summary>
    public CrosshairPreset Sanitised()
    {
        Shape = ShapeKind.ToString();
        Color = TryParseColor(Color, out byte r, out byte g, out byte b)
            ? FormatColor(r, g, b)
            : Palette[1];
        Size = Math.Clamp(Size, SizeMin, SizeMax);
        Thickness = Math.Clamp(Thickness, ThicknessMin, ThicknessMax);
        Gap = Math.Clamp(Gap, GapMin, GapMax);
        Opacity = Math.Clamp(Opacity, OpacityMin, OpacityMax);
        Rotation = ((Rotation % 360) + 360) % 360;
        CustomImage = CustomImage ?? string.Empty;
        Name ??= string.Empty;
        Id ??= string.Empty;
        return this;
    }

    /// <summary>True when the visible parts match. Id and name are not compared.</summary>
    public bool SameLook(CrosshairPreset other)
    {
        return ShapeKind == other.ShapeKind
            && string.Equals(Color, other.Color, StringComparison.OrdinalIgnoreCase)
            && Size == other.Size
            && Thickness == other.Thickness
            && Gap == other.Gap
            && Opacity == other.Opacity
            && Rotation == other.Rotation
            && CenterDot == other.CenterDot
            && Glow == other.Glow
            && Outline == other.Outline
            && string.Equals(CustomImage, other.CustomImage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accepts #RGB, #RRGGBB and the same without the hash.</summary>
    public static bool TryParseColor(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length != 6)
        {
            return false;
        }

        return byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    public static string FormatColor(byte r, byte g, byte b)
    {
        return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
            + g.ToString("X2", CultureInfo.InvariantCulture)
            + b.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What ships. Seven, one per classic shape and spread across the colours, so
    /// the dropdown on a fresh install already shows the range. Anything else is the
    /// user's own and lives in <see cref="AppSettings.CustomCrosshairs"/>.
    /// </summary>
    public static IReadOnlyList<CrosshairPreset> BuiltIns { get; } = new[]
    {
        Make("xh-classic", "Classic Cross", CrosshairShape.Cross, 1, size: 20, thickness: 2, gap: 4),
        Make("xh-dot", "Pinpoint Dot", CrosshairShape.Dot, 2, size: 9, thickness: 2, gap: 0),
        Make("xh-x", "Magenta X", CrosshairShape.X, 3, size: 18, thickness: 2, gap: 3),
        Make("xh-ring", "Yellow Ring", CrosshairShape.Circle, 4, size: 22, thickness: 2, gap: 0),
        Make("xh-chevrons", "White Chevrons", CrosshairShape.Chevrons, 5, size: 22, thickness: 2, gap: 4),
        Make("xh-triangle", "Red Triangle", CrosshairShape.Triangle, 0, size: 22, thickness: 2, gap: 0),
        Make("xh-t", "Cyan T", CrosshairShape.T, 2, size: 20, thickness: 2, gap: 3)
    };

    private static CrosshairPreset Make(
        string id, string name, CrosshairShape shape, int colour, int size, int thickness, int gap)
    {
        return new CrosshairPreset
        {
            Id = id,
            Name = name,
            ShapeKind = shape,
            Color = Palette[colour],
            Size = size,
            Thickness = thickness,
            Gap = gap
        };
    }

    public static CrosshairPreset Default() => BuiltIns[0].Copy();
}
