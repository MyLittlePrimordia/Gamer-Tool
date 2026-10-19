using System;

namespace GamerTool.Services;

/// <summary>One frame of a GIF as the file stores it: a rectangle of pixels and what to do with it afterwards.</summary>
/// <param name="Left">Where the rectangle sits on the canvas.</param>
/// <param name="Top">Where the rectangle sits on the canvas.</param>
/// <param name="Width">Size of the rectangle.</param>
/// <param name="Height">Size of the rectangle.</param>
/// <param name="Pixels">Premultiplied BGRA, <c>Width * Height * 4</c> bytes.</param>
/// <param name="Disposal">The GIF disposal method: 0 and 1 leave it, 2 clear it to the background, 3 put back what was under it.</param>
internal readonly record struct GifRawFrame(int Left, int Top, int Width, int Height, byte[] Pixels, int Disposal);

/// <summary>
/// Turns the frames a GIF stores into the pictures a viewer would show.
/// <para>
/// Most GIFs do not store whole pictures. To stay small, each frame holds only the
/// rectangle that changed, placed at an offset, and says what should happen to
/// that rectangle before the next one is drawn. Showing the stored rectangles one
/// after another as if they were pictures makes an animation flicker, jump or
/// smear, which is the usual way a hand-rolled GIF player goes wrong. This builds
/// the full canvas each frame by drawing the rectangles in order and honouring the
/// disposal method.
/// </para>
/// <para>
/// Plain arrays and no WPF, so it can run off the UI thread and be tested.
/// </para>
/// </summary>
internal static class GifFrameCompositor
{
    /// <summary>
    /// Walk the frames in order and hand the finished canvas for each to
    /// <paramref name="emit"/>. The array passed to it is reused for the next
    /// frame, so the caller must copy or consume it before returning. Handing
    /// frames out one at a time rather than returning a list keeps only a canvas
    /// and a backup in memory instead of every frame at full size.
    /// </summary>
    /// <param name="canvasWidth">The logical screen of the GIF.</param>
    /// <param name="canvasHeight">The logical screen of the GIF.</param>
    /// <param name="count">How many frames there are.</param>
    /// <param name="frameAt">Reads one frame. Called once per frame, in order.</param>
    /// <param name="emit">Receives the frame index and the finished canvas, premultiplied BGRA.</param>
    public static void Compose(
        int canvasWidth,
        int canvasHeight,
        int count,
        Func<int, GifRawFrame> frameAt,
        Action<int, byte[]> emit)
    {
        ArgumentNullException.ThrowIfNull(frameAt);
        ArgumentNullException.ThrowIfNull(emit);

        if (canvasWidth < 1 || canvasHeight < 1)
        {
            throw new ArgumentException("The canvas has no size.");
        }

        byte[] canvas = new byte[canvasWidth * canvasHeight * 4];

        for (int i = 0; i < count; i++)
        {
            GifRawFrame frame = frameAt(i);

            // Disposal 3 means "after this frame, put back what was here before it".
            // That has to be remembered before the frame is drawn, not after.
            byte[]? before = frame.Disposal == 3 ? (byte[])canvas.Clone() : null;

            Draw(canvas, canvasWidth, canvasHeight, frame);
            emit(i, canvas);

            if (frame.Disposal == 2)
            {
                Clear(canvas, canvasWidth, canvasHeight, frame);
            }
            else if (before is not null)
            {
                Buffer.BlockCopy(before, 0, canvas, 0, canvas.Length);
            }
        }
    }

    /// <summary>
    /// Draw the rectangle over the canvas: premultiplied "over", so a transparent
    /// pixel of the frame leaves what is underneath alone, which is what a GIF's
    /// transparent colour means.
    /// </summary>
    private static void Draw(byte[] canvas, int canvasWidth, int canvasHeight, GifRawFrame frame)
    {
        if (frame.Pixels.Length < frame.Width * frame.Height * 4)
        {
            return;
        }

        for (int y = 0; y < frame.Height; y++)
        {
            int cy = frame.Top + y;
            if (cy < 0 || cy >= canvasHeight)
            {
                continue;
            }

            for (int x = 0; x < frame.Width; x++)
            {
                int cx = frame.Left + x;
                if (cx < 0 || cx >= canvasWidth)
                {
                    continue;
                }

                int from = ((y * frame.Width) + x) * 4;
                int alpha = frame.Pixels[from + 3];
                if (alpha == 0)
                {
                    continue;
                }

                int to = ((cy * canvasWidth) + cx) * 4;
                int under = 255 - alpha;
                canvas[to] = (byte)Math.Min(255, frame.Pixels[from] + (canvas[to] * under / 255));
                canvas[to + 1] = (byte)Math.Min(255, frame.Pixels[from + 1] + (canvas[to + 1] * under / 255));
                canvas[to + 2] = (byte)Math.Min(255, frame.Pixels[from + 2] + (canvas[to + 2] * under / 255));
                canvas[to + 3] = (byte)Math.Min(255, alpha + (canvas[to + 3] * under / 255));
            }
        }
    }

    /// <summary>Make the frame's rectangle transparent again, clipped to the canvas.</summary>
    private static void Clear(byte[] canvas, int canvasWidth, int canvasHeight, GifRawFrame frame)
    {
        int left = Math.Max(0, frame.Left);
        int top = Math.Max(0, frame.Top);
        int right = Math.Min(canvasWidth, frame.Left + frame.Width);
        int bottom = Math.Min(canvasHeight, frame.Top + frame.Height);

        for (int y = top; y < bottom; y++)
        {
            if (right > left)
            {
                Array.Clear(canvas, ((y * canvasWidth) + left) * 4, (right - left) * 4);
            }
        }
    }
}
