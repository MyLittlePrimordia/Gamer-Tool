using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GamerTool.Models;
using Point = System.Windows.Point;

namespace GamerTool.Services;

/// <summary>
/// One small filled circle an effect draws, in crosshair pixels.
/// <para>
/// <see cref="Lift"/> is how far its colour is pulled towards white, 0 to 1. A light
/// that ran along a crosshair in the crosshair's own colour would be invisible, so
/// the lights that travel use it to read as light rather than as more ink.
/// </para>
/// </summary>
public readonly record struct CrosshairDot(double X, double Y, double Radius, double Alpha, double Lift = 0);

/// <summary>
/// One band of a ripple, in crosshair pixels: how far its near edge has travelled from
/// the ink it came off, how wide the band is, and how much of its colour is left.
/// <para>
/// A band that follows the outline of the shape rather than a circle: a cross sends out
/// a cross, a box a box, a ring a ring. Distance is measured from the ink itself, so a
/// band at zero is hugging the shape and one at twenty has left it behind on all sides
/// at once - which is what a ripple is, and what a circle drawn round a square is not.
/// </para>
/// </summary>
public readonly record struct CrosshairRipple(double Distance, double Width, double Alpha);

/// <summary>
/// Where an effect has got to at one instant: everything the drawing needs that is
/// not in the preset.
/// <para>
/// A frame is derived from the preset and a time, and from nothing else. No clock, no
/// window and no stopwatch, so the whole animation can be stepped through at any rate
/// in a test and the overlay and the preview cannot disagree about what second three
/// looks like. <see cref="Still"/> is the frame a crosshair with no effect draws, and
/// it is the identity for all of it.
/// </para>
/// </summary>
public readonly record struct CrosshairFrame
{
    /// <summary>Multiplier on the crosshair's own opacity. 1 is unchanged.</summary>
    public double Opacity { get; init; }

    /// <summary>Uniform scale about the middle. 1 is unchanged.</summary>
    public double Scale { get; init; }

    /// <summary>Degrees added to the preset's own rotation.</summary>
    public double Rotation { get; init; }

    /// <summary>Degrees added to the preset's hue, for the one effect that moves colour.</summary>
    public double Hue { get; init; }

    /// <summary>
    /// Where the sheen is across the shape, as a fraction of its half width: -1.2 is
    /// off one side, 0 is the middle, 1.2 is off the other. Meaningless when
    /// <see cref="SheenAlpha"/> is zero.
    /// </summary>
    public double Sheen { get; init; }

    /// <summary>How strong the sheen is this instant. Zero means there is no highlight.</summary>
    public double SheenAlpha { get; init; }

    /// <summary>
    /// The bands a ripple is sending out this instant, nearest the ink first. Empty when
    /// there is no ripple, and most of the time there is none: a band is only there
    /// while it is still bright enough to be seen.
    /// </summary>
    public IReadOnlyList<CrosshairRipple> Ripples { get; init; }

    /// <summary>
    /// Small circles this frame draws: the sparks, and the lights twinkling on the ends
    /// of the shape. One list, because they are the same mark in two places - and one
    /// draw call in the view.
    /// </summary>
    public IReadOnlyList<CrosshairDot> Dots { get; init; }

    /// <summary>No effect at all: the frame every crosshair drew before any of this existed.</summary>
    public static CrosshairFrame Still => new()
    {
        Opacity = 1,
        Scale = 1,
        Dots = Array.Empty<CrosshairDot>(),
        Ripples = Array.Empty<CrosshairRipple>()
    };
}

/// <summary>
/// The arithmetic behind every animated crosshair: a preset and a time in, one frame
/// out.
/// <para>
/// Pure on purpose, the same as <see cref="CrosshairGeometry"/>. Nothing here knows
/// what a window is, so the effects can be checked frame by frame without standing up a
/// WPF application, and the one thing that really needs checking - that nothing an
/// effect draws ever leaves the window it is drawn in - can be checked for every
/// effect, at every phase, at every size.
/// </para>
/// <para>
/// Each effect owns a different axis, which is the rule that keeps them from being
/// variations of each other: the shape's size, its angle, its brightness, a highlight
/// crossing it, lights on its ends, sparks leaving it, rings coming off it, and what
/// colour it is.
/// </para>
/// <para>
/// The set is deliberately quiet. An effect sits in the middle of the screen for a whole
/// match, so the ones here move slowly and by a little: nothing orbits, tears, or
/// flickers, because anything doing that beside the aim point is something the eye
/// keeps being pulled back to. The aim point itself never moves - the middle of the
/// crosshair stays the middle - and nothing strobes, because a rate fast enough to look
/// lively is a rate fast enough to be a distraction, or worse.
/// </para>
/// </summary>
public static class CrosshairAnimation
{
    /// <summary>Nothing moves faster than this, whatever the speed slider says.</summary>
    public const double MinimumPeriodSeconds = 0.6;

    /// <summary>Every effect, in menu order, with <see cref="CrosshairEffect.None"/> first.</summary>
    public static IReadOnlyList<CrosshairEffect> Menu { get; } = new[]
    {
        CrosshairEffect.None,
        CrosshairEffect.Pulse,
        CrosshairEffect.Spin,
        CrosshairEffect.Breathe,
        CrosshairEffect.Sheen,
        CrosshairEffect.Sparks,
        CrosshairEffect.Ripple,
        CrosshairEffect.ColourCycle
    };

    /// <summary>What the dropdown says. Short, because it sits in a control.</summary>
    public static string Label(CrosshairEffect effect)
    {
        return effect switch
        {
            CrosshairEffect.Pulse => "Pulse",
            CrosshairEffect.Spin => "Spin",
            CrosshairEffect.Breathe => "Breathe",
            CrosshairEffect.Sheen => "Sheen",
            CrosshairEffect.Sparks => "Sparks",
            CrosshairEffect.Ripple => "Ripple",
            CrosshairEffect.ColourCycle => "Cycle",
            _ => "None"
        };
    }

    /// <summary>One line beside the dropdown: what it does.</summary>
    public static string Hint(CrosshairEffect effect)
    {
        return effect switch
        {
            CrosshairEffect.Pulse => "The whole crosshair swells and settles.",
            CrosshairEffect.Spin => "It turns. The middle stays put; the arms do not.",
            CrosshairEffect.Breathe => "It dims and brightens, slowly. Nothing moves.",
            CrosshairEffect.Sheen => "A soft highlight passes across it now and then.",
            CrosshairEffect.Sparks => "Sparks leave the edges of the shape and fade.",
            CrosshairEffect.Ripple => "Waves of its own shape leave it and fade outward.",
            CrosshairEffect.ColourCycle => "The colour drifts round the wheel. Also spreads subpixel wear.",
            _ => "Still. Nothing moves."
        };
    }

    /// <summary>Whether the amount slider does anything for this effect.</summary>
    public static bool UsesAmount(CrosshairEffect effect)
    {
        return effect is not (CrosshairEffect.None or CrosshairEffect.Spin);
    }

    /// <summary>Whether the speed slider does anything for this effect.</summary>
    public static bool UsesSpeed(CrosshairEffect effect) => effect != CrosshairEffect.None;

    /// <summary>
    /// Where the two sliders start when this effect is picked: each one needs its own
    /// rate and its own reach, and fifty for everything meant that picking a new effect
    /// usually landed on "too fast to read" or "too slight to see" rather than on what
    /// the effect is for.
    /// <para>
    /// A starting point and not a rule: the sliders are the user's the moment they
    /// touch them, and a preset that has already chosen numbers opens on those.
    /// </para>
    /// </summary>
    public static (int Speed, int Amount) Defaults(CrosshairEffect effect)
    {
        return effect switch
        {
            CrosshairEffect.Pulse => (50, 60),
            CrosshairEffect.Spin => (45, 50),

            // Slow enough to be a breath rather than a pulse, and deep enough to be
            // seen: the old depth was a tenth of the crosshair's opacity, which on a
            // dark scene looked exactly like no effect at all.
            CrosshairEffect.Breathe => (40, 70),

            CrosshairEffect.Sheen => (55, 60),
            CrosshairEffect.Sparks => (60, 45),
            CrosshairEffect.Ripple => (50, 60),
            CrosshairEffect.ColourCycle => (35, 80),
            _ => (50, 50)
        };
    }

    /// <summary>
    /// The crosshair drawn beside an effect's name in the dropdown: a real one, with
    /// that effect on it at the settings it will start with.
    /// <para>
    /// Its amount is capped, because a ripple or a shower of sparks reaches further than
    /// the twenty-four pixel swatch it is drawn in and the frame crops the motion rather
    /// than the swatch growing to fit every effect.
    /// </para>
    /// </summary>
    public static CrosshairPreset Thumbnail(CrosshairEffect effect)
    {
        (int speed, int amount) = Defaults(effect);

        return new CrosshairPreset
        {
            ShapeKind = CrosshairShape.Cross,
            Size = 12,
            Thickness = 2,
            Color = "#3CFF5A",
            EffectKind = effect,
            EffectSpeed = speed,
            EffectAmount = Math.Min(amount, 35)
        };
    }

    /// <summary>
    /// The frame at <paramref name="seconds"/> into the animation. Negative times are
    /// treated as zero, so a clock that has not started yet draws the first frame
    /// rather than an arbitrary one.
    /// </summary>
    public static CrosshairFrame At(CrosshairPreset preset, double seconds)
    {
        CrosshairEffect effect = preset.EffectKind;
        if (effect == CrosshairEffect.None)
        {
            return CrosshairFrame.Still;
        }

        double t = Math.Clamp((seconds > 0 ? seconds : 0) + PhaseOffset(preset), 0, 1e7);
        double amount = Math.Clamp(preset.EffectAmount, CrosshairPreset.EffectAmountMin, CrosshairPreset.EffectAmountMax) / 100.0;
        double speed = Math.Clamp(preset.EffectSpeed, CrosshairPreset.EffectSpeedMin, CrosshairPreset.EffectSpeedMax) / 100.0;

        return effect switch
        {
            CrosshairEffect.Pulse => Pulse(t, amount, speed),
            CrosshairEffect.Spin => Spin(t, speed),
            CrosshairEffect.Breathe => Breathe(t, amount, speed),
            CrosshairEffect.Sheen => Sheen(t, amount, speed),
            CrosshairEffect.Sparks => Sparks(preset, t, amount, speed),
            CrosshairEffect.Ripple => Ripple(t, amount, speed),
            CrosshairEffect.ColourCycle => ColourCycle(t, amount, speed),
            _ => CrosshairFrame.Still
        };
    }

    /// <summary>
    /// Extra room, in pixels, that an effect can need beyond the shape's own reach, so
    /// the overlay window can be made big enough to hold it. Zero for the effects that
    /// only move the shape, its colour or its copy.
    /// </summary>
    public static int ReachPx(CrosshairPreset preset)
    {
        double amount = Math.Clamp(preset.EffectAmount, CrosshairPreset.EffectAmountMin, CrosshairPreset.EffectAmountMax) / 100.0;

        return preset.EffectKind switch
        {
            // Sparks start at the shape's own edge and travel one burst past it.
            CrosshairEffect.Sparks => (int)Math.Ceiling(SparkBurstPx + 2),

            // A ripple's outermost band leaves the shape behind by the width of the
            // wave, measured from the ink rather than from the middle.
            CrosshairEffect.Ripple => (int)Math.Ceiling(RippleReach(amount) + RippleWidthPx + 1),
            _ => 0
        };
    }

    /// <summary>
    /// How much bigger than its own size a crosshair can be drawn, as a fraction: the
    /// pulse swells the whole drawing, and the window has to be sized for the largest it
    /// ever gets rather than the size it is set to.
    /// </summary>
    public static double ScaleGrowth(CrosshairPreset preset)
    {
        double amount = Math.Clamp(preset.EffectAmount, CrosshairPreset.EffectAmountMin, CrosshairPreset.EffectAmountMax) / 100.0;
        return preset.EffectKind == CrosshairEffect.Pulse ? PulseAmplitude * amount : 0;
    }

    /// <summary>
    /// The base colour with its hue moved by <paramref name="degrees"/>, and its value
    /// left alone. Brightness is deliberately not part of the drift: a crosshair that
    /// changed brightness on its way round would read as a flicker, and the point of
    /// the outline and the colour is to stay equally readable at every hue.
    /// </summary>
    public static (byte R, byte G, byte B) ShiftHue(byte r, byte g, byte b, double degrees)
    {
        double turn = degrees % 360;
        if (turn < 0)
        {
            turn += 360;
        }

        if (turn < 0.001)
        {
            return (r, g, b);
        }

        ToHsv(r, g, b, out double h, out double s, out double v);
        h = (h + turn) % 360;
        ToRgb(h, s, v, out r, out g, out b);
        return (r, g, b);
    }

    /// <summary>Below this saturation a colour is a grey, and turning a grey's hue changes nothing.</summary>
    private const double GreyBelow = 0.2;

    /// <summary>How bright a grey, white or black crosshair is made when it is given a hue to cycle through.</summary>
    private const double GreyLift = 0.85;

    /// <summary>
    /// The colour the cycle shows: <see cref="ShiftHue"/> for anything that has a hue,
    /// and for a white, grey or black crosshair - which has none to move - a vivid colour
    /// that starts at red and is turned from there.
    /// <para>
    /// White is in the colour row and is the usual choice for a crosshair, and turning
    /// the hue of white gives white. The cycle was therefore doing exactly nothing on the
    /// colour most people have it set to, which is the "effect claims to work and does
    /// not" case.
    /// </para>
    /// </summary>
    public static (byte R, byte G, byte B) CycleHue(byte r, byte g, byte b, double degrees)
    {
        ToHsv(r, g, b, out _, out double s, out double v);
        if (s >= GreyBelow)
        {
            return ShiftHue(r, g, b, degrees);
        }

        double turn = ((degrees % 360) + 360) % 360;
        ToRgb(turn, 1.0, Math.Max(v, GreyLift), out byte red, out byte green, out byte blue);
        return (red, green, blue);
    }

    /// <summary>
    /// A sentence for the cases where an effect is on and cannot show on what it is on,
    /// or empty when there is nothing to say. Said on the tab rather than left for the
    /// user to find out, because an effect that does nothing looks like a broken one.
    /// </summary>
    public static string Caveat(CrosshairEffect effect, CrosshairShape shape, bool keepsOwnColours)
    {
        if (effect == CrosshairEffect.Spin && shape is (CrosshairShape.Dot or CrosshairShape.Circle))
        {
            return "A ring or a dot looks the same turned, so there is nothing to see. Try Pulse or Ripple.";
        }

        if (effect == CrosshairEffect.ColourCycle && shape == CrosshairShape.Custom && keepsOwnColours)
        {
            return "This picture keeps its own colours, so only its glow changes colour.";
        }

        return string.Empty;
    }

    /// <summary>
    /// The soft edge a wave in this colour needs to be seen on a light background, or
    /// null when the colour stands on its own.
    /// <para>
    /// White is the case this exists for. A ripple is the crosshair's own colour drawn
    /// at a few tenths of its opacity, so a white one over a bright scene was the same
    /// colour as the scene and looked like the effect was not on at all. A wave in a
    /// very light colour is therefore given a shadow in its own hue: white sends out a
    /// grey line under the white one, pale yellow a dark yellow. Colours that already
    /// carry themselves - the green, cyan, magenta and red in the row - are left alone.
    /// </para>
    /// <para>
    /// The threshold is a luminance of 0.8, which is above every colour in the palette
    /// except white and yellow, so picking one of the ordinary colours changes nothing
    /// about how the effect looks.
    /// </para>
    /// </summary>
    public static (byte R, byte G, byte B)? WaveEdge(byte r, byte g, byte b)
    {
        double luminance = ((0.299 * r) + (0.587 * g) + (0.114 * b)) / 255.0;
        if (luminance <= 0.8)
        {
            return null;
        }

        // About a quarter of the brightness, in the colour's own hue.
        double take = 0.25 / luminance;
        return ((byte)Math.Round(r * take), (byte)Math.Round(g * take), (byte)Math.Round(b * take));
    }

    // ------------------------------------------------------------------- the eight

    private const double PulseAmplitude = 0.30;

    /// <summary>How opaque it stays at the bottom of a breath at full amount.</summary>
    private const double BreatheDepth = 0.55;

    /// <summary>How many bands a ripple has in flight at once.</summary>
    private const int RippleWaves = 2;

    /// <summary>How wide a band is, in crosshair pixels.</summary>
    private const double RippleWidthPx = 1.6;

    /// <summary>How far past the ink the first band travels at no amount at all.</summary>
    private const double RippleReachMinPx = 7.0;

    /// <summary>And how much further at full amount: the amount slider is the reach.</summary>
    private const double RippleReachGainPx = 15.0;

    private static double RippleReach(double amount) => RippleReachMinPx + (RippleReachGainPx * amount);

    private const double SparkBurstPx = 16;

    /// <summary>
    /// How strong the sheen is at the middle of its pass at full amount. It used to be
    /// half of that, and on a two pixel line a highlight at a quarter of full strength
    /// (the default amount, then) was the sort of thing nobody could be sure they had
    /// seen, which read as the effect being broken.
    /// </summary>
    private const double SheenPeak = 0.9;

    private static CrosshairFrame Pulse(double seconds, double amount, double speed)
    {
        // Size only. Brightness is the breathe's axis, so the two cannot look like the
        // same effect with the numbers moved.
        double period = Period(speed, 5.0, 1.2);
        double wave = Math.Sin(Tau * Fraction(seconds / period));
        return CrosshairFrame.Still with { Scale = 1 + (PulseAmplitude * amount * wave) };
    }

    private static CrosshairFrame Breathe(double seconds, double amount, double speed)
    {
        // Slow on purpose: the shortest cycle here is three and a half seconds, and the
        // longest eight. Anything faster reads as a flicker beside the aim point.
        //
        // It has to be deep, too. This used to move the opacity by twelve percent of a
        // crosshair that is already mostly transparent on a dark scene, which was not a
        // subtle effect but an invisible one; a breath now goes to about half the
        // crosshair's own opacity at full amount, and to about two thirds at the default.
        double period = Period(speed, 8.0, 3.5);
        double wave = 0.5 - (0.5 * Math.Cos(Tau * Fraction(seconds / period)));
        return CrosshairFrame.Still with { Opacity = 1 - (BreatheDepth * amount * wave) };
    }

    private static CrosshairFrame Sheen(double seconds, double amount, double speed)
    {
        // A highlight that crosses the shape and is gone: most of the cycle is spent
        // waiting, which is what keeps it from being a moving thing on the screen.
        double period = Period(speed, 7.0, 3.0);
        double progress = Fraction(seconds / period);
        const double Pass = 0.32;

        if (progress > Pass)
        {
            return CrosshairFrame.Still;
        }

        double t = progress / Pass;
        return CrosshairFrame.Still with
        {
            Sheen = -1.25 + (2.5 * t),
            SheenAlpha = SheenPeak * Math.Sin(Math.PI * t) * amount
        };
    }

    private static CrosshairFrame Spin(double seconds, double speed)
    {
        double rate = 8 + (172 * speed);
        return CrosshairFrame.Still with { Rotation = (seconds * rate) % 360 };
    }

    private static CrosshairFrame Sparks(CrosshairPreset preset, double seconds, double amount, double speed)
    {
        double life = Period(speed, 1.8, 0.5);
        int count = 4 + (int)Math.Round(12 * amount);
        var dots = new List<CrosshairDot>(count);

        // Sparks leave the edges of the thing that is drawn, not the middle of it. A
        // cross sheds them from its four tips, a ring from around its rim, a picture
        // from its border - so the shower belongs to the crosshair instead of hanging in
        // the empty space it is aiming at.
        IReadOnlyList<CrosshairAnchor> anchors = CrosshairGeometry.Anchors(preset);

        for (int i = 0; i < count; i++)
        {
            CrosshairAnchor anchor = anchors[i % anchors.Count];
            double start = Math.Sqrt((anchor.At.X * anchor.At.X) + (anchor.At.Y * anchor.At.Y));

            // Each spark has a direction of its own and a start of its own, both from
            // its index, so the shower is the same every time it is drawn from zero and
            // needs no state between frames.
            double spread = (Hash01((i * 2) + 1) - 0.5) * 36;
            double angle = (anchor.Degrees + spread) * Math.PI / 180.0;
            double stagger = Hash01((i * 2) + 2);
            double progress = Fraction((seconds / life) + stagger);

            // Slowing down as it goes: fast off the edge, drifting at the end.
            double ease = 1 - ((1 - progress) * (1 - progress));
            double distance = start + (SparkBurstPx * (0.45 + (0.55 * Hash01((i * 3) + 7))) * ease);

            dots.Add(new CrosshairDot(
                Math.Cos(angle) * distance,
                Math.Sin(angle) * distance,
                0.6 + (0.9 * (1 - progress)),
                (1 - progress) * 0.85));
        }

        // Farthest first, so the sparks nearest the shape are painted over the ones that
        // have already left.
        dots.Sort((a, b) => ((b.X * b.X) + (b.Y * b.Y)).CompareTo((a.X * a.X) + (a.Y * a.Y)));
        return CrosshairFrame.Still with { Dots = dots };
    }

    private static CrosshairFrame Ripple(double seconds, double amount, double speed)
    {
        // Two bands at a time, half a cycle apart, so there is always one on its way out
        // rather than a single band that blinks back onto the shape.
        double period = Period(speed, 3.0, 1.4);
        double reach = RippleReach(amount);
        var ripples = new List<CrosshairRipple>(RippleWaves);

        for (int i = 0; i < RippleWaves; i++)
        {
            double progress = Fraction((seconds / period) + (i / (double)RippleWaves));

            // The band leaves the ink at once and is gone before it reaches the edge of
            // the wave, so nothing hangs about at the outside as a faint outline that
            // looks like part of the crosshair. The distance is linear because a wave
            // that slows as it goes spends most of its life in one place, which is a
            // halo, not a ripple.
            double distance = reach * progress;
            double alpha = 0.85 * Math.Min(1, progress / 0.08) * Math.Pow(1 - progress, 1.3);

            if (alpha < 0.02)
            {
                continue;
            }

            ripples.Add(new CrosshairRipple(distance, RippleWidthPx, alpha));
        }

        return CrosshairFrame.Still with { Ripples = ripples };
    }

    private static CrosshairFrame ColourCycle(double seconds, double amount, double speed)
    {
        // A back and forth swing rather than a one way lap: a lap ends by snapping from
        // the far end of the wheel back to where it started, which reads as a fault.
        // The span is what the amount slider is for - a narrow wobble around the chosen
        // colour at one end, the whole wheel at the other.
        double period = Period(speed, 24.0, 4.0);
        double span = 60 + (300 * amount);
        return CrosshairFrame.Still with
        {
            Hue = (span / 2) * Math.Sin(Tau * Fraction(seconds / period))
        };
    }

    // ------------------------------------------------------------------- helpers

    private const double Tau = Math.PI * 2;

    private static double Period(double speed, double slow, double fast)
    {
        return Math.Max(MinimumPeriodSeconds, slow - ((slow - fast) * speed));
    }

    private static double Fraction(double value)
    {
        double f = value - Math.Floor(value);
        return f < 0 ? f + 1 : f;
    }

    /// <summary>
    /// Where in the cycle a preset starts, spread across the presets so two of them with
    /// the same effect are not breathing in lockstep. It comes from the id, which is
    /// stable, rather than from the clock, and an unsaved crosshair gets zero.
    /// </summary>
    private static double PhaseOffset(CrosshairPreset preset)
    {
        if (string.IsNullOrEmpty(preset.Id))
        {
            return 0;
        }

        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in preset.Id)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (hash % 1000) / 1000.0;
        }
    }

    /// <summary>A repeatable number in [0,1) from an integer, so sparks do not wander between frames.</summary>
    private static double Hash01(int value)
    {
        unchecked
        {
            uint x = (uint)value * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private static void ToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double red = r / 255.0;
        double green = g / 255.0;
        double blue = b / 255.0;
        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
        }
        else if (max == red)
        {
            h = 60 * (((green - blue) / delta) % 6);
        }
        else if (max == green)
        {
            h = 60 * (((blue - red) / delta) + 2);
        }
        else
        {
            h = 60 * (((red - green) / delta) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }
    }

    private static void ToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        double m = v - c;

        (double red, double green, double blue) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        r = Channel(red + m);
        g = Channel(green + m);
        b = Channel(blue + m);
    }

    private static byte Channel(double value)
    {
        return (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }
}
