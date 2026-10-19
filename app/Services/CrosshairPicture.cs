using System;
using System.Collections.Generic;
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
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(delaysMs);

        if (frames.Count == 0 || delaysMs.Count != frames.Count)
        {
            throw new ArgumentException("A picture needs at least one frame and a delay for each.");
        }

        Frames = frames;
        DelaysMs = delaysMs;
    }

    public IReadOnlyList<BitmapSource> Frames { get; }

    /// <summary>Milliseconds each frame stays up. Meaningless, and zero, for a still.</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    public bool IsAnimated => Frames.Count > 1;

    /// <summary>The frame a still shows, and the one an animation starts on.</summary>
    public BitmapSource First => Frames[0];
}
