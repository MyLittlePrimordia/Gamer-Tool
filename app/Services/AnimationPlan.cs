using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GamerTool.Services;

/// <summary>
/// The rules for an animated crosshair that do not need a picture to state: how
/// long a frame lasts, how many frames are kept, how big an animation may be, and
/// the small file that remembers the timings.
/// </summary>
internal static class AnimationPlan
{
    /// <summary>
    /// Most frames kept. A crosshair is a few dozen pixels on the screen, and a
    /// sixty frame loop is already two seconds at a lively 30 frames a second. An
    /// animation with more has frames dropped evenly rather than being cut short,
    /// so its loop still ends where the author meant it to.
    /// </summary>
    public const int MaxFrames = 60;

    /// <summary>Shortest a frame is allowed to last. 20 ms is 50 frames a second, past which nobody can tell.</summary>
    public const int MinDelayMs = 20;

    /// <summary>Longest a single frame may hold. A stuck frame is a broken file, not a design.</summary>
    public const int MaxDelayMs = 10_000;

    /// <summary>
    /// What a delay of zero or one hundredth of a second means. Many GIFs store
    /// nothing useful there, and every browser shows them at a tenth of a second,
    /// so an animation made for the web plays at the speed its author saw.
    /// </summary>
    public const int FallbackDelayMs = 100;

    /// <summary>
    /// Width times height times frames, in pixels, above which an animation is
    /// refused. Sixty four million pixels is 256 MB once decoded, which no
    /// crosshair needs, and it is what stops a small file that expands enormously
    /// from taking the machine down.
    /// </summary>
    public const long MaxPixelBudget = 64L * 1024 * 1024;

    /// <summary>The longest side of an animation taken in, before decoding it in full.</summary>
    public const int MaxSide = 1024;

    /// <summary>A GIF stores delays in hundredths of a second. This is what that means for the player.</summary>
    public static int DelayMs(int hundredths)
    {
        if (hundredths <= 1)
        {
            return FallbackDelayMs;
        }

        return (int)Math.Clamp((long)hundredths * 10, MinDelayMs, MaxDelayMs);
    }

    /// <summary>True when a decoded animation of this size is small enough to take.</summary>
    public static bool WithinBudget(int width, int height, int frames)
    {
        return width >= 1
            && height >= 1
            && frames >= 1
            && width <= MaxSide
            && height <= MaxSide
            && (long)width * height * frames <= MaxPixelBudget;
    }

    /// <summary>
    /// Which frames to keep, and how long each now lasts. At or under
    /// <paramref name="max"/> frames every one is kept as it is. Over it, frames
    /// are picked evenly and each keeps the time of the ones skipped after it, so
    /// the loop takes as long as it did.
    /// </summary>
    public static List<(int Index, int DelayMs)> Reduce(IReadOnlyList<int> delaysMs, int max)
    {
        ArgumentNullException.ThrowIfNull(delaysMs);

        int count = delaysMs.Count;
        max = Math.Max(1, max);
        List<(int, int)> kept = new();

        if (count <= max)
        {
            for (int i = 0; i < count; i++)
            {
                kept.Add((i, delaysMs[i]));
            }

            return kept;
        }

        for (int k = 0; k < max; k++)
        {
            int start = (int)((long)k * count / max);
            int end = k + 1 < max ? (int)((long)(k + 1) * count / max) : count;

            long total = 0;
            for (int i = start; i < end; i++)
            {
                total += delaysMs[i];
            }

            kept.Add((start, (int)Math.Clamp(total, MinDelayMs, MaxDelayMs)));
        }

        return kept;
    }
}

/// <summary>
/// What an animated crosshair's strip picture needs alongside it to be played: the
/// side of each square frame and how long each lasts.
/// <para>
/// A small text file next to the strip, not something inside the PNG, because the
/// strip is written by this app and read by this app and a few lines of JSON are
/// easier to verify than a custom PNG chunk. It is read back with the same
/// suspicion as a settings file: a hand edit, a restored backup or a half written
/// file must not be able to ask for a ten thousand frame animation.
/// </para>
/// </summary>
internal sealed record AnimationInfo(int Side, int[] DelaysMs)
{
    /// <summary>Largest frame side the file may claim.</summary>
    private const int SideLimit = 512;

    public int Frames => DelaysMs.Length;

    public string ToJson()
    {
        return JsonSerializer.Serialize(new Dto { Side = Side, Delays = DelaysMs });
    }

    /// <summary>
    /// Read the file. False, with an empty result, for anything that does not
    /// describe a playable animation: not JSON, a side or frame count outside the
    /// limits, or no delays. Delays out of range are pulled in rather than refused.
    /// </summary>
    public static bool TryParse(string? json, out AnimationInfo info)
    {
        info = new AnimationInfo(0, Array.Empty<int>());

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            Dto? dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto?.Delays is null
                || dto.Side < 4
                || dto.Side > SideLimit
                || dto.Delays.Length < 2
                || dto.Delays.Length > AnimationPlan.MaxFrames)
            {
                return false;
            }

            int[] delays = new int[dto.Delays.Length];
            for (int i = 0; i < delays.Length; i++)
            {
                delays[i] = Math.Clamp(dto.Delays[i], AnimationPlan.MinDelayMs, AnimationPlan.MaxDelayMs);
            }

            info = new AnimationInfo(dto.Side, delays);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class Dto
    {
        public int Side { get; set; }

        public int[]? Delays { get; set; }
    }
}
