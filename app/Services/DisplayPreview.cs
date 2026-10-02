using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>Which picture the preview is showing.</summary>
public enum PreviewScene
{
    Day = 0,
    Night = 1,

    /// <summary>The looping day to night clip, when it is there to show.</summary>
    Transition = 2
}


public sealed class DisplayPreview
{
    private const string AnimatedAsset = "animated.gif";

    /// <summary>
    /// One scene's pixels, with the size they actually are.
    /// <para>
    /// The size lives here rather than on the class because the scenes are not the
    /// same size. It used to be read once from the day image and thrown away for
    /// every other one, which was harmless while there were two matching JPEGs and
    /// would have thrown on the first scene that differed.
    /// </para>
    /// </summary>
    private sealed class Scene
    {
        public byte[]? Still { get; init; }

        public List<byte[]> Frames { get; } = new();

        public List<int> Delays { get; } = new();

        public int Width { get; set; }

        public int Height { get; set; }

        public int Stride { get; set; }

        /// <summary>
        /// The picture the frames are drawn into, kept between draws.
        /// <para>
        /// A fresh bitmap per frame is a fresh half megabyte per frame, and at this
        /// size that is large enough to go on the large object heap, which does not
        /// get compacted. One surface per scene, written into again, allocates
        /// nothing once the first frame has been drawn.
        /// </para>
        /// </summary>
        public WriteableBitmap? Surface { get; set; }

        public bool Animated => Frames.Count > 0;

        public int Count => Animated ? Frames.Count : (Still is null ? 0 : 1);

        public byte[]? FrameAt(int index)
        {
            if (Still is not null)
            {
                return Still;
            }

            if (Frames.Count == 0)
            {
                return null;
            }

            int i = index % Frames.Count;
            return Frames[i < 0 ? i + Frames.Count : i];
        }

        /// <summary>
        /// How long this frame is on screen for, which is what keeps the loop at
        /// the speed it was made at rather than at whatever the timer happens to
        /// be set to.
        /// </summary>
        public int DelayAt(int index)
        {
            if (Delays.Count == 0)
            {
                return 100;
            }

            int i = index % Delays.Count;
            if (i < 0)
            {
                i += Delays.Count;
            }

            // Below this the loop would look like a strobe rather than a scene.
            return Delays[i] < 30 ? 30 : Delays[i];
        }
    }

    private readonly Scene? _day;

    private readonly Scene? _night;

    private Scene? _transition;

    /// <summary>
    /// Guards <see cref="_transition"/>. The loop is decoded on a worker and read
    /// on the UI thread, and a <see cref="Scene"/> is not complete until every
    /// frame has been laid over the one before it, so publishing the reference
    /// without a barrier would let a reader see a half-built frame list.
    /// </summary>
    private readonly object _transitionGate = new();

    private Task? _transitionLoad;

    /// <summary>
    /// Reused between renders. The animation calls this sixty times a minute for as
    /// long as the app is open, and a fresh buffer each time is megabytes a minute
    /// of garbage for a picture the size of a playing card.
    /// </summary>
    private byte[]? _scratch;

    /// <summary>
    /// The ramp table for the preset currently being previewed, kept across
    /// frames. It was rebuilt on every call, which is a fresh ramp and a fresh
    /// 768 byte table each time, and the loop calls this at its own frame rate.
    /// </summary>
    private byte[]? _lut;

    private DisplayPreset? _lutSource;

    public DisplayPreview()
    {
        _day = LoadStill(ReadAsset("day.jpg"));
        _night = LoadStill(ReadAsset("night.jpg"));
    }

    public bool Ready => _day is not null;

    public bool IsAnimated(PreviewScene scene)
    {
        return DataFor(scene)?.Animated == true;
    }

    public int FrameCount(PreviewScene scene)
    {
        return DataFor(scene)?.Count ?? 0;
    }

    /// <summary>
    /// How long the frame is on screen for, which is what keeps the loop at the
    /// speed it was made at.
    /// </summary>
    public int FrameDelayMs(PreviewScene scene, int index)
    {
        return DataFor(scene)?.DelayAt(index) ?? 100;
    }

    /// <summary>
    /// Makes sure a scene can be shown, loading the loop if it is not in yet.
    /// <para>
    /// The clip is decoded on a worker and only the first time it is asked for, so
    /// an install of the app that never opens that scene never pays for it, and
    /// never waits for it at launch. The task is remembered either way, so a scene
    /// that has been tried is not decoded again.
    /// </para>
    /// </summary>
    public Task EnsureSceneReadyAsync(PreviewScene scene)
    {
        if (scene != PreviewScene.Transition)
        {
            return Task.CompletedTask;
        }

        lock (_transitionGate)
        {
            if (_transition is not null)
            {
                return Task.CompletedTask;
            }

            return _transitionLoad ??= Task.Run(() =>
            {
                Scene? loaded = LoadTransition();

                lock (_transitionGate)
                {
                    _transition = loaded;
                }
            });
        }
    }

    /// <summary>
    /// The preview picture with the preset's ramp applied to it, which is the whole
    /// point of the box: it shows what the monitor is about to be told to do.
    /// <para>
    /// One frame of the clip per call, at the rate the clip was drawn at. The
    /// frames in between are deliberately not invented: mixing two of them to fill
    /// the gap gives a moving subject as a pair of ghosts rather than as motion,
    /// which on a clip with people in it looks far worse than the step it was
    /// meant to hide. A clip plays at the rate it has.
    /// </para>
    /// </summary>
    public BitmapSource? Render(DisplayPreset preset, PreviewScene scene, int frameIndex)
    {
        Scene? data = DataFor(scene);
        byte[]? source = data?.FrameAt(frameIndex);
        if (data is null || source is null || data.Width <= 0 || data.Height <= 0)
        {
            return null;
        }

        if (_scratch is null || _scratch.Length != source.Length)
        {
            _scratch = new byte[source.Length];
        }

        byte[] pixels = _scratch;
        Buffer.BlockCopy(source, 0, pixels, 0, source.Length);

        byte[] lut = LutFor(preset);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i];
            int g = pixels[i + 1];
            int r = pixels[i + 2];
            pixels[i] = lut[(b * 3) + 2];
            pixels[i + 1] = lut[(g * 3) + 1];
            pixels[i + 2] = lut[(r * 3) + 0];
        }

        WriteableBitmap surface = data.Surface ??= new WriteableBitmap(
            data.Width,
            data.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null);

        surface.WritePixels(new Int32Rect(0, 0, data.Width, data.Height), pixels, data.Stride, 0);
        return surface;
    }

    /// <summary>
    /// The preview ramp for a preset, rebuilt only when the values behind it
    /// actually change. The loop redraws at its own rate and a slider drag
    /// redraws on a timer, so this was being asked for a fresh ramp and a fresh
    /// 768 byte table dozens of times a second to produce the same bytes.
    /// <para>
    /// Compared field by field rather than by building a key array, because
    /// building the key would allocate on the very path this is meant to make
    /// free. The blue light trim is folded in by the caller before it gets here,
    /// so the channel gains it arrives with are the ones that matter.
    /// </para>
    /// </summary>
    private byte[] LutFor(DisplayPreset preset)
    {
        if (_lut is not null && _lutSource is not null && SameCurve(_lutSource, preset))
        {
            return _lut;
        }

        _lutSource = preset.Copy();
        _lut = DisplayService.BuildPreviewLut(preset);
        return _lut;
    }

    private static bool SameCurve(DisplayPreset a, DisplayPreset b)
    {
        return a.Gamma.Equals(b.Gamma)
            && a.ShadowBoost.Equals(b.ShadowBoost)
            && a.Brightness.Equals(b.Brightness)
            && a.Contrast.Equals(b.Contrast)
            && a.RedGain.Equals(b.RedGain)
            && a.GreenGain.Equals(b.GreenGain)
            && a.BlueGain.Equals(b.BlueGain);
    }

    /// <summary>
    /// A scene that is not there falls back to a still one rather than to nothing.
    /// The switcher keeps working and the box keeps showing a picture, so a missing
    /// clip can never leave an empty hole in the panel.
    /// </summary>
    private Scene? DataFor(PreviewScene scene)
    {
        lock (_transitionGate)
        {
            return scene switch
            {
                PreviewScene.Night => _night ?? _day,
                PreviewScene.Transition => _transition ?? _night ?? _day,
                _ => _day
            };
        }
    }

    private static Scene? LoadStill(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            BitmapSource source = BitmapDecoder.Create(
                new MemoryStream(raw),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnLoad).Frames[0];

            byte[]? pixels = Flatten(source, out int width, out int height, out int stride);
            return pixels is null
                ? null
                : new Scene { Still = pixels, Width = width, Height = height, Stride = stride };
        }
        catch (Exception ex)
        {
            TraceLog.Write("PREVIEW IMAGE", ex);
            return null;
        }
    }

    /// <summary>
    /// Roughly how much memory the loop is allowed to sit on.
    /// <para>
    /// A budget on bytes rather than a cap on frames, because those are two
    /// different problems. Dropping frames to save memory is what makes a loop
    /// stutter, and the rate a clip plays at ought to be decided by the clip and
    /// not by how much room the machine happens to have. So the loop keeps all of
    /// its frames and gives up resolution instead, down towards the size it is
    /// actually drawn at, which costs nothing to look at because anything bigger
    /// than the box is thrown away by the scaling that draws it.
    /// </para>
    /// </summary>
    private const long MaxAnimatedBytes = 48L * 1024 * 1024;

    /// <summary>
    /// As wide as the preview panel is at the window's usual size, and the
    /// point below which the loop is not scaled any further. The row is a fixed
    /// 230 high and the width is whatever the window leaves over, so this is a
    /// design target rather than a measurement: a loop held well under the size it
    /// is shown at looks soft, and there is nothing to be gained by spending memory
    /// budget on pixels that only get magnified.
    /// </summary>
    private const int PanelWidth = 410;

    /// <summary>
    /// The width the still images are held at.
    /// <para>
    /// The panel draws its picture into 410 pixels, so anything wider is thrown
    /// away by the scaling that draws it. It used to be 900, which meant every
    /// frame the ramp was applied to was about 2.4 times larger than it needed to
    /// be, and the ramp is a per-pixel loop on the UI thread. This is the same
    /// reasoning as <see cref="FitWidth"/> applies to the loop, applied to the
    /// two stills, which were being decoded large and used small.
    /// </para>
    /// </summary>
    private static int WorkingWidth => PanelWidth;

    private Scene? LoadTransition()
    {
        try
        {
            byte[] raw = ReadAsset(AnimatedAsset);
            if (raw.Length == 0)
            {
                TraceLog.Write("PREVIEW ANIM no " + AnimatedAsset + " in the build");
                return null;
            }

            GifBitmapDecoder decoder = new(
                new MemoryStream(raw),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnLoad);

            BitmapFrame first = decoder.Frames[0];
            double aspect = first.PixelHeight / (double)first.PixelWidth;
            int targetWidth = FitWidth(first.PixelWidth, aspect, decoder.Frames.Count);
            int targetHeight = Math.Max(1, (int)Math.Round(targetWidth * aspect));

            // Dropping frames is the last thing done here, and only for a clip far
            // longer than this one, once even a panel sized loop will not fit. The
            // loop then plays faster than it was drawn, which is the honest way
            // round to have too many frames, and the log below says when it has
            // happened rather than letting it pass as normal.
            int stride = 1;
            long wanted = (long)targetWidth * targetHeight * 4 * decoder.Frames.Count;
            if (wanted > MaxAnimatedBytes)
            {
                stride = (int)Math.Ceiling(wanted / (double)MaxAnimatedBytes);
            }

            Scene scene = new();
            byte[]? canvas = null;
            int canvasWidth = 0;
            int canvasHeight = 0;
            int canvasStride = 0;

            // Every frame is walked, in order, and laid over the one before it.
            //
            // Both halves of that sentence matter. A GIF frame after the first is
            // only the part that changed, with the rest left undefined and marked
            // transparent, so a frame that is stepped over leaves a hole in
            // everything after it. Skipping frames to save work is what turns the
            // clip into speckle, and taking the stride over the loop instead of
            // over what is stored is how that gets done by accident.
            for (int i = 0; i < decoder.Frames.Count; i++)
            {
                FormatConvertedBitmap flat = new(decoder.Frames[i], PixelFormats.Bgra32, null, 0);
                int frameStride = flat.PixelWidth * 4;
                byte[] delta = new byte[frameStride * flat.PixelHeight];
                flat.CopyPixels(delta, frameStride, 0);

                if (canvas is null)
                {
                    canvas = delta;
                    canvasWidth = flat.PixelWidth;
                    canvasHeight = flat.PixelHeight;
                    canvasStride = frameStride;
                }
                else
                {
                    if (frameStride != canvasStride || flat.PixelHeight != canvasHeight)
                    {
                        // A frame that is not the size of the rest would need its
                        // own buffer. Skipped, because rescaling per frame is not
                        // worth the code and a mismatched frame has no meaning
                        // over a canvas of a different shape.
                        continue;
                    }

                    LayOver(canvas, delta, delta.Length);
                }

                if (i % stride != 0)
                {
                    continue;
                }

                byte[]? kept = Shrink(canvas, canvasWidth, canvasHeight, canvasStride, targetWidth, targetHeight);
                if (kept is null)
                {
                    continue;
                }

                scene.Frames.Add(kept);

                // The clip's own timing, and deliberately not multiplied up to
                // cover the frames that were not kept.
                //
                // That multiplication is the whole reason a loop like this one used
                // to crawl. Holding a kept frame for as long as the three it stands
                // in for keeps the total runtime right and the motion wrong: 122
                // frames shown 41 at a time over the same 12 seconds is 3fps, and
                // on a clip that is almost entirely a slow change of light there
                // is no moving detail to hide the steps behind, so the stutter is
                // the first and worst thing you see. Timing belongs to the clip.
                scene.Delays.Add(FrameDelayMs(decoder.Frames[i]));
            }

            if (scene.Frames.Count == 0)
            {
                TraceLog.Write("PREVIEW ANIM no usable frames in " + AnimatedAsset);
                return null;
            }

            // What is kept is panel sized, so this is the size the frames are in
            // and not the size the clip arrived at.
            scene.Width = targetWidth;
            scene.Height = targetHeight;
            scene.Stride = targetWidth * 4;

            int held = scene.Frames.Count;
            int loopMs = 0;
            foreach (int delay in scene.Delays)
            {
                loopMs += delay;
            }

            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;

            TraceLog.Write(
                "PREVIEW ANIM "
                + held.ToString(invariant) + " of " + decoder.Frames.Count.ToString(invariant) + " frames at "
                + targetWidth.ToString(invariant) + "x" + targetHeight.ToString(invariant)
                + ", plays at " + (1000.0 / (loopMs / (double)held)).ToString("0.0", invariant) + " fps over "
                + (loopMs / 1000.0).ToString("0.0", invariant) + "s, which is the clip's own rate"
                + ", about " + ((held * targetWidth * targetHeight * 4) / (1024 * 1024)).ToString(invariant) + " MB held"
                + (stride > 1
                    ? ", stride " + stride.ToString(invariant) + " so it plays faster than drawn"
                    : string.Empty));

            return scene;
        }
        catch (Exception ex)
        {
            TraceLog.Write("PREVIEW ANIM", ex);
            return null;
        }
    }

    /// <summary>
    /// The width to hold the loop at so that every frame of it fits the budget.
    /// <para>
    /// Never wider than the clip came in at, and never so narrow that the panel has
    /// to magnify it. The floor is there so that a very long clip ends up soft
    /// rather than ending up absurd, which is the right way round: a stutter is
    /// far more noticeable than a little softness.
    /// </para>
    /// </summary>
    private static int FitWidth(int sourceWidth, double aspect, int frames)
    {
        if (sourceWidth <= 0 || frames <= 0)
        {
            return Math.Max(1, sourceWidth);
        }

        int height = Math.Max(1, (int)Math.Round(sourceWidth * aspect));
        long perFrame = (long)sourceWidth * height * 4;
        if (perFrame * frames <= MaxAnimatedBytes)
        {
            return sourceWidth;
        }

        double scale = Math.Sqrt(MaxAnimatedBytes / (double)(perFrame * frames));
        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), Math.Min(PanelWidth, sourceWidth), sourceWidth);

        // Rounding the width can land a pixel wide and put the total back over the
        // budget, which trips the stride below and halves the frame rate over a
        // few hundred bytes. Walk back down until the dimensions that actually
        // come out of it fit, so that a stride of one is a real answer rather than
        // one that got rounded away.
        while (width < sourceWidth
            && (long)width * Math.Max(1, (int)Math.Round(width * aspect)) * 4 * frames > MaxAnimatedBytes)
        {
            width--;
        }

        return width;
    }


    /// <summary>
    /// Brings one composited frame down to the size the loop is kept at.
    /// <para>
    /// Averaged over the area each destination pixel covers rather than read from a
    /// single point, which at this ratio is a box filter over a pixel or two. That
    /// also takes the fizz out of the speckle a GIF picks up from being quantised
    /// to a couple of hundred colours, which is worth having on its own account.
    /// </para>
    /// <para>
    /// Alpha comes out opaque, which is what a composited frame is: the canvas
    /// starts as the whole of the first frame and is only ever painted over, and
    /// the ramp in <see cref="Render"/> has no use for a hole.
    /// </para>
    /// </summary>
    private static byte[]? Shrink(byte[] source, int srcW, int srcH, int srcStride, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
        {
            return null;
        }

        if (dstW == srcW && dstH == srcH)
        {
            return (byte[])source.Clone();
        }

        int dstStride = dstW * 4;
        byte[] result = new byte[dstStride * dstH];

        for (int y = 0; y < dstH; y++)
        {
            int y0 = (int)(((long)y * srcH) / dstH);
            int y1 = (int)((((long)y + 1) * srcH) / dstH);
            if (y1 <= y0)
            {
                y1 = y0 + 1;
            }

            if (y1 > srcH)
            {
                y1 = srcH;
            }

            for (int x = 0; x < dstW; x++)
            {
                int x0 = (int)(((long)x * srcW) / dstW);
                int x1 = (int)((((long)x + 1) * srcW) / dstW);
                if (x1 <= x0)
                {
                    x1 = x0 + 1;
                }

                if (x1 > srcW)
                {
                    x1 = srcW;
                }

                int b = 0;
                int g = 0;
                int r = 0;
                int n = 0;

                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcStride;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = row + (sx * 4);
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                        n++;
                    }
                }

                if (n == 0)
                {
                    n = 1;
                }

                int o = (y * dstStride) + (x * 4);
                result[o] = (byte)(b / n);
                result[o + 1] = (byte)(g / n);
                result[o + 2] = (byte)(r / n);
                result[o + 3] = 255;
            }
        }

        return result;
    }


    /// <summary>
    /// Lays one frame of the loop over the one before it, in place.
    /// <para>
    /// This is the step WPF leaves to the caller and most GIF decoders do quietly:
    /// a frame in a loop is only the part of the picture that moved, and whatever it
    /// did not touch is transparent, so it has to be drawn on top of what was already
    /// there. Rendered on its own, a frame past the first is a field of holes.
    /// </para>
    /// <para>
    /// The canvas is altered in place, which is safe because every frame that is kept
    /// is a copy and nothing else holds a reference to it. A frame that is fully
    /// opaque is copied rather than blended, since that is the common case and it
    /// costs three multiplies less per pixel.
    /// </para>
    /// </summary>
    private static void LayOver(byte[] canvas, byte[] delta, int length)
    {
        for (int i = 0; i < length; i += 4)
        {
            int alpha = delta[i + 3];
            if (alpha == 0)
            {
                // Nothing was painted here, so what is already on the canvas stands.
                continue;
            }

            if (alpha == 255)
            {
                canvas[i] = delta[i];
                canvas[i + 1] = delta[i + 1];
                canvas[i + 2] = delta[i + 2];
                canvas[i + 3] = 255;
                continue;
            }

            double a = alpha / 255.0;
            double inverse = 1.0 - a;
            canvas[i] = (byte)(delta[i] * a + canvas[i] * inverse);
            canvas[i + 1] = (byte)(delta[i + 1] * a + canvas[i + 1] * inverse);
            canvas[i + 2] = (byte)(delta[i + 2] * a + canvas[i + 2] * inverse);
            canvas[i + 3] = 255;
        }
    }

    /// <summary>
    /// The clip's own idea of how long each frame should be on screen, which is what
    /// keeps the loop at the speed it was made at rather than at whatever the timer
    /// happens to be set to.
    /// </summary>
    private static int FrameDelayMs(BitmapFrame frame)
    {
        try
        {
            // UInt16, not short. WPF hands back the GIF graphic control extension's
            // delay as an unsigned 16 bit value, and a boxed ushort is never `is
            // short`, so this branch could never be taken: every frame fell through
            // to the default and a clip authored at 20fps played at 10. That is
            // the opposite of what the comment above promises, and the reason it
            // survived is that a loop playing at the wrong speed looks like a
            // stylistic choice rather than a dead comparison.
            //
            // Convert.ToInt32 rather than a second type test, so a WPF that boxes
            // it differently still gets read rather than silently defaulted again.
            if (frame.Metadata is BitmapMetadata meta
                && meta.GetQuery("/grctlext/Delay") is { } raw
                && Convert.ToInt32(raw) is int delay
                && delay > 0)
            {
                return delay * 10;
            }
        }
        catch (Exception ex)
        {
            // No delay block on this frame, or one WPF will not convert. The
            // default below is fine, but it used to be dropped entirely, so a
            // frame whose timing could not be read was indistinguishable from one
            // that had none.
            TraceLog.Write("PREVIEW frame delay: " + ex.GetType().Name);
        }

        return 100;
    }

    /// <summary>
    /// Brings any image down to the preview's working width and into plain BGRA,
    /// which is the only form the ramp can be applied to and the only form the
    /// loop can cycle without re-decoding.
    /// </summary>
    private static byte[]? Flatten(BitmapSource source, out int width, out int height, out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return null;
        }

        int targetWidth = Math.Min(source.PixelWidth, WorkingWidth);
        int targetHeight = (int)Math.Round(source.PixelHeight * (targetWidth / (double)source.PixelWidth));
        if (targetHeight < 1)
        {
            targetHeight = 1;
        }

        TransformedBitmap scaled = new(
            source,
            new ScaleTransform(targetWidth / (double)source.PixelWidth, targetHeight / (double)source.PixelHeight));

        FormatConvertedBitmap flat = new(scaled, PixelFormats.Bgra32, null, 0);
        int bufferStride = flat.PixelWidth * 4;
        byte[] buffer = new byte[bufferStride * flat.PixelHeight];
        flat.CopyPixels(buffer, bufferStride, 0);

        width = flat.PixelWidth;
        height = flat.PixelHeight;
        stride = bufferStride;
        return buffer;
    }

    public static byte[] ReadAsset(string fileName)
    {
        Assembly assembly = typeof(DisplayPreview).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        return Array.Empty<byte>();
    }
}
