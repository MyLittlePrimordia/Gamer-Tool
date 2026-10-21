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
/// The movement a crosshair can be given, if any.
/// <para>
/// Stored as text for the same reason <see cref="CrosshairShape"/> is, and empty -
/// not a value here - means "still". A preset saved before this existed has no
/// effect field at all, and has to keep drawing exactly as it did, which an empty
/// string does and a default of, say, <c>Pulse</c> would not.
/// </para>
/// <para>
/// Every one of these is decoration: none of them makes anyone aim better, and
/// several are in tension with a steady sight picture. They are here because they
/// were asked for, and they are drawn in the same place as the still crosshair so a
/// player can see what they are getting before they go and get killed by it.
/// </para>
/// </summary>
public enum CrosshairEffect
{
    /// <summary>Still. The default, and what every crosshair meant before this existed.</summary>
    None,

    /// <summary>The whole crosshair swells and settles. Nothing about it changes but its size.</summary>
    Pulse,

    /// <summary>A continuous turn, on top of whatever angle the crosshair is already set to.</summary>
    Spin,

    /// <summary>
    /// A slow dimming and brightening, deep enough to be seen. Nothing moves, which is
    /// the point.
    /// </summary>
    Breathe,

    /// <summary>A soft highlight passing across the shape, like light catching it.</summary>
    Sheen,

    /// <summary>Small sparks thrown out from the shape's own edge and dying before they get far.</summary>
    Sparks,

    /// <summary>
    /// Two bands in the shape of the crosshair itself that leave it and fade as they go:
    /// a cross sends out crosses, a box boxes, a ring rings.
    /// </summary>
    Ripple,

    /// <summary>The colour drifts round the wheel, which is also the only effect that spreads wear.</summary>
    ColourCycle
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

    public const int CenterDotSizeMin = 1;

    /// <summary>
    /// The widest the dot can be. Above the thickest line, because a dot exists to
    /// be seen on its own: an eight pixel line is allowed, and a dot that follows
    /// it is nine pixels across, which is where this ceiling comes from.
    /// </summary>
    public const int CenterDotSizeMax = 12;

    public const int EffectSpeedMin = 1;

    public const int EffectSpeedMax = 100;

    public const int EffectAmountMin = 0;

    public const int EffectAmountMax = 100;

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

    /// <summary>
    /// The same slider for a picture: how wide a hole is cleared at its middle, in
    /// pixels. Kept apart from <see cref="Gap"/> because every preset already has a
    /// Gap of 4 stored whether or not it is a cross, and a picture saved before
    /// pictures had a gap must not suddenly lose the middle of itself. Zero, the
    /// default, leaves the picture exactly as it is.
    /// </summary>
    public int PictureGap { get; set; }

    /// <summary>Percent, so the number on screen is the number stored.</summary>
    public int Opacity { get; set; } = 100;

    /// <summary>Degrees clockwise, 0 to 359. Turns the whole crosshair, shape or picture.</summary>
    public int Rotation { get; set; }

    /// <summary>A small dot at the exact middle, drawn on top of whatever else is showing.</summary>
    public bool CenterDot { get; set; }

    /// <summary>
    /// #RRGGBB for the centre dot on its own, or empty for "whatever
    /// <see cref="Color"/> is".
    /// <para>
    /// Empty is the default on purpose. The dot used to have no colour of its own,
    /// so every preset saved before this existed means "follow the crosshair" and
    /// must keep drawing that way; and a dot that silently stopped matching when
    /// the crosshair colour changed would be a worse default than one that did.
    /// </para>
    /// </summary>
    public string CenterDotColor { get; set; } = string.Empty;

    /// <summary>
    /// The centre dot's width in pixels, or zero for "as wide as the line is".
    /// <para>
    /// A size of its own, because the two are different jobs: the line is tuned to
    /// be read at a glance and the dot to mark one pixel, and a dot that only ever
    /// inherited the line was either invisible on a one pixel crosshair or wide
    /// enough to fill the gap on a thick one. Every game that lets players tune a
    /// dot gives it its own size, and so does this.
    /// </para>
    /// <para>
    /// Zero is the default on purpose, for the same reason the dot's colour is empty
    /// by default: every preset saved before this existed meant "follow the line",
    /// and has to keep drawing exactly as it did.
    /// </para>
    /// </summary>
    public int CenterDotSize { get; set; }

    public bool Glow { get; set; }

    /// <summary>
    /// One of <see cref="CrosshairEffect"/> by name, or empty for still. Text for the
    /// same reason <see cref="Shape"/> is: an unreadable value falls back to still
    /// rather than failing the whole profile.
    /// </summary>
    public string Effect { get; set; } = string.Empty;

    /// <summary>
    /// How fast it moves, 1 to 100. A rate rather than a duration, so the number on
    /// the slider means the same thing whichever effect is selected.
    /// </summary>
    public int EffectSpeed { get; set; } = 50;

    /// <summary>
    /// How far it moves, 0 to 100, in whatever unit that effect counts in: how deep
    /// the dimming goes, how big the sparks are, how far a ripple reaches.
    /// Zero is legal and means "animate, but imperceptibly", which is how someone can
    /// try an effect without committing to it.
    /// </summary>
    public int EffectAmount { get; set; } = 50;

    /// <summary>
    /// A thin black edge round the shape. On by default because it is the single
    /// thing that keeps a bright crosshair visible against a bright scene.
    /// </summary>
    public bool Outline { get; set; } = true;

    /// <summary>
    /// Draw an imported picture in the colours it came with, instead of multiplying it
    /// by <see cref="Color" />. Off by default, which is what every crosshair did before
    /// this setting existed: the pack is white line art, and a white picture takes the
    /// chosen colour exactly.
    /// <para>
    /// On is the setting for a multi-coloured animated GIF or a photograph - multiplying
    /// those by one colour throws away everything that made them worth importing. The
    /// colour still paints the glow, so the swatches stay useful either way. Shapes
    /// ignore this: there are no pixels of their own to keep.
    /// </para>
    /// </summary>
    public bool KeepOwnColours { get; set; }

    /// <summary>
    /// File name, not a path, of the imported picture inside the crosshair folder.
    /// An animation is stored as a strip of frames with a small timing file beside
    /// it, and is named by its strip like any other picture.
    /// A name rather than a path so a portable install can be moved and a
    /// restored backup cannot point the app at an arbitrary file.
    /// </summary>
    public string CustomImage { get; set; } = string.Empty;

    /// <summary>
    /// The effect actually drawn. Empty or unreadable is <see cref="CrosshairEffect.None"/>.
    /// </summary>
    [JsonIgnore]
    public CrosshairEffect EffectKind
    {
        get => Enum.TryParse(Effect, ignoreCase: true, out CrosshairEffect kind)
            && Enum.IsDefined(kind) && kind != CrosshairEffect.None
            ? kind
            : CrosshairEffect.None;
        set => Effect = value == CrosshairEffect.None ? string.Empty : value.ToString();
    }

    /// <summary>Whether anything at all is animated. Asked often, so it reads well.</summary>
    [JsonIgnore]
    public bool Animated => EffectKind != CrosshairEffect.None;

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
            PictureGap = PictureGap,
            Opacity = Opacity,
            Rotation = Rotation,
            CenterDot = CenterDot,
            CenterDotColor = CenterDotColor,
            CenterDotSize = CenterDotSize,
            Glow = Glow,
            Effect = Effect,
            EffectSpeed = EffectSpeed,
            EffectAmount = EffectAmount,
            Outline = Outline,
            KeepOwnColours = KeepOwnColours,
            CustomImage = CustomImage
        };
    }

    /// <summary>
    /// Put every tuning field back to what a new crosshair has, keeping the shape, the
    /// picture and the name. Taken from a fresh preset rather than from a list of
    /// numbers here, so the two can never drift apart.
    /// <para>
    /// The colour goes back to the default green, which is what a new crosshair has;
    /// the caller decides about own colours, because that depends on the picture and
    /// not on the preset.
    /// </para>
    /// </summary>
    public void ResetTuning()
    {
        var fresh = new CrosshairPreset();
        Color = fresh.Color;
        Size = fresh.Size;
        Thickness = fresh.Thickness;
        Gap = fresh.Gap;
        PictureGap = fresh.PictureGap;
        Opacity = fresh.Opacity;
        Rotation = fresh.Rotation;
        CenterDot = fresh.CenterDot;
        CenterDotColor = fresh.CenterDotColor;
        CenterDotSize = fresh.CenterDotSize;
        Glow = fresh.Glow;
        Outline = fresh.Outline;
        Effect = fresh.Effect;
        EffectSpeed = fresh.EffectSpeed;
        EffectAmount = fresh.EffectAmount;
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
        PictureGap = Math.Clamp(PictureGap, GapMin, GapMax);
        Opacity = Math.Clamp(Opacity, OpacityMin, OpacityMax);
        Rotation = ((Rotation % 360) + 360) % 360;

        // Nothing readable means "follow the crosshair", which is a state the tab
        // can show; a mangled string is not, and a dot painted a nonsense colour
        // would be worse than one painted the crosshair's.
        CenterDotColor = TryParseColor(CenterDotColor, out byte dr, out byte dg, out byte db)
            ? FormatColor(dr, dg, db)
            : string.Empty;

        CenterDotSize = Math.Clamp(CenterDotSize, 0, CenterDotSizeMax);

        // The enum's own spelling, so "spin", "Spin" and "SPIN" all come back as the
        // one text that the dropdown can select. An unrecognised effect becomes
        // nothing at all: a crosshair that moves when it was told something nonsense
        // would be worse than one that sits still.
        Effect = EffectKind == CrosshairEffect.None ? string.Empty : EffectKind.ToString();
        EffectSpeed = Math.Clamp(EffectSpeed, EffectSpeedMin, EffectSpeedMax);
        EffectAmount = Math.Clamp(EffectAmount, EffectAmountMin, EffectAmountMax);

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
            && PictureGap == other.PictureGap
            && Opacity == other.Opacity
            && Rotation == other.Rotation
            && CenterDot == other.CenterDot
            && string.Equals(EffectiveCenterDotColor(), other.EffectiveCenterDotColor(), StringComparison.OrdinalIgnoreCase)
            && EffectiveCenterDotSize() == other.EffectiveCenterDotSize()
            && Glow == other.Glow
            && EffectKind == other.EffectKind
            && (EffectKind == CrosshairEffect.None || (EffectSpeed == other.EffectSpeed && EffectAmount == other.EffectAmount))
            && Outline == other.Outline
            && KeepOwnColours == other.KeepOwnColours
            && string.Equals(CustomImage, other.CustomImage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the dot has been given a colour of its own rather than following the crosshair.</summary>
    [JsonIgnore]
    public bool HasOwnCenterDotColor => !string.IsNullOrWhiteSpace(CenterDotColor);

    /// <summary>
    /// The colour the dot is actually drawn in: its own when it has a readable one,
    /// otherwise the crosshair's. One place for that decision, so the preview, the
    /// overlay and the export cannot disagree about the dot.
    /// </summary>
    public string EffectiveCenterDotColor()
    {
        return TryParseColor(CenterDotColor, out _, out _, out _) ? CenterDotColor : Color;
    }

    /// <summary>Whether the dot has been given a size of its own rather than following the line.</summary>
    [JsonIgnore]
    public bool HasOwnCenterDotSize => CenterDotSize > 0;

    /// <summary>
    /// How wide the dot is actually drawn, in pixels: its own size when it has one,
    /// otherwise the line's thickness plus one, which is what it drew before it could
    /// be sized. The one place that decides, so the geometry, the preview, the
    /// overlay and the number on the slider cannot disagree.
    /// </summary>
    public int EffectiveCenterDotSize()
    {
        return CenterDotSize > 0 ? CenterDotSize : Math.Max(2, Thickness + 1);
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
    /// What ships: the settings players actually converge on, not one preset per
    /// shape.
    /// <para>
    /// Sizes are screen pixels at 100 percent scale, which is the only unit this app
    /// can promise, so the numbers here are translated from published configurations
    /// rather than copied out of a game - a game's own units are not pixels and mean
    /// different things at different resolutions. The archetypes are the ones the
    /// research kept finding: a small static cross with a hair of gap, the same with a
    /// dot, a dot on its own, a wide cross with long arms for tracking and spraying,
    /// a ring for projectile leads, and the T that leaves the head clear.
    /// </para>
    /// <para>
    /// Names describe the job, in the same spirit as the display and audio tabs, and
    /// no name is a player's: a setting is not a person and this app is not endorsed
    /// by one. The ids are stable forever, because slots and backups point at them;
    /// retuning a preset happens in place, and anything new gets a new id.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CrosshairPreset> BuiltIns { get; } = new[]
    {
        // The one a new install starts on. A middle of the road cross: arms long
        // enough to read against a dark tunnel, a gap big enough to see the target
        // through. Default() is BuiltIns[0], so this stays first.
        Make("xh-classic", "Standard Cross", CrosshairShape.Cross, 1, size: 16, thickness: 2, gap: 4),

        // The tactical shooter default: two pixel arms, a two pixel line, a one pixel
        // gap, no outline. The crosshairs at the top of the CS2 ladder are this small.
        Make("xh-precision", "Precision Cross", CrosshairShape.Cross, 1, size: 6, thickness: 2, gap: 1, outline: false),

        // Arms for lining a target up, plus a two pixel dot for the pixel itself. The
        // dot is the point: it is sized on its own, not inherited from the line.
        Make("xh-crossdot", "Dot and Cross", CrosshairShape.Cross, 1, size: 12, thickness: 2, gap: 2, outline: false, dot: true, dotSize: 2),

        // The T: left, right and down, nothing above the middle, so the head you are
        // aiming at is never covered by your own crosshair.
        Make("xh-t", "Tactical T", CrosshairShape.T, 1, size: 10, thickness: 2, gap: 2),

        // Short arms, a wide gap: what hero shooters use for tracking, where the
        // middle stays clear and the arms are a frame rather than a sight.
        Make("xh-tracking", "Tracking Cross", CrosshairShape.Cross, 2, size: 26, thickness: 1, gap: 9),

        // A dot and nothing else, for a sight picture with no clutter. Two pixels is
        // the smallest dot that survives a bright wall.
        Make("xh-dot", "Pinpoint Dot", CrosshairShape.Dot, 2, size: 6, thickness: 1, gap: 0),

        // A thin open ring: for leading a projectile, where the edge of the ring is
        // the reference and the middle must stay empty.
        Make("xh-ring", "Range Ring", CrosshairShape.Circle, 2, size: 26, thickness: 1, gap: 0),

        // The box a tactical shooter offers as its square style. A frame to read
        // movement against, thin enough not to become a border round the target.
        Make("xh-square", "Square Frame", CrosshairShape.Square, 2, size: 20, thickness: 1, gap: 0, outline: false),

        // The three below have no game equivalent - no competitive shooter draws an X,
        // chevrons or a triangle - so they keep a shape's own tuning and say so in
        // their names. They exist because the shapes exist, not because anyone uses
        // them to win.
        Make("xh-x", "Wide X", CrosshairShape.X, 3, size: 16, thickness: 2, gap: 3),
        Make("xh-chevrons", "Chevron Marker", CrosshairShape.Chevrons, 5, size: 22, thickness: 2, gap: 4),
        Make("xh-triangle", "Triangle Marker", CrosshairShape.Triangle, 0, size: 22, thickness: 2, gap: 0)
    };

    private static CrosshairPreset Make(
        string id, string name, CrosshairShape shape, int colour,
        int size, int thickness, int gap,
        bool outline = true, bool dot = false, int dotSize = 0)
    {
        return new CrosshairPreset
        {
            Id = id,
            Name = name,
            ShapeKind = shape,
            Color = Palette[colour],
            Size = size,
            Thickness = thickness,
            Gap = gap,
            Outline = outline,
            CenterDot = dot,
            CenterDotSize = dotSize
        };
    }

    public static CrosshairPreset Default() => BuiltIns[0].Copy();
}
