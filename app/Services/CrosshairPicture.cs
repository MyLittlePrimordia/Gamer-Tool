using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GamerTool.Services;

/// <summary>
/// A crosshair picture ready to draw: one frame for a still, several for an
/// animation, with how long each lasts.
/// <para>
/// A still is simply an animation of one frame, so everything that draws or
/// composes a picture handles both the same way and only the clock cares which it is.
/// </para>
/// </summary>
public sealed class CrosshairPicture
{
    public CrosshairPicture(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysMs)
        : this(frames, delaysMs, default)
    {
    }

    /// <param name="content">
    /// The part of each frame that has pixels in it, or empty for the whole frame.
    /// The drawing code crops to it, so Size measures the crosshair rather than the
    /// transparent margin round it.
    /// </param>
    public CrosshairPicture(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysMs, Int32Rect content)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(delaysMs);

        if (frames.Count == 0 || delaysMs.Count != frames.Count)
        {
            throw new ArgumentException("A picture needs at least one frame and a delay for each.");
        }

        Frames = frames;
        DelaysMs = delaysMs;
        Content = content;
    }

    public IReadOnlyList<BitmapSource> Frames { get; }

    /// <summary>Milliseconds each frame stays up. Meaningless, and zero, for a still.</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    /// <summary>
    /// The rectangle, in frame pixels, that every frame has something drawn in, or
    /// empty when the whole frame is the picture.
    /// <para>
    /// One rectangle for the whole animation, not one per frame: a crosshair that
    /// pulses would otherwise be cropped to a different box on each frame and appear
    /// to breathe in and out of its own accord.
    /// </para>
    /// </summary>
    public Int32Rect Content { get; }

    public bool IsAnimated => Frames.Count > 1;

    /// <summary>The frame a still shows, and the one an animation starts on.</summary>
    public BitmapSource First => Frames[0];
}
