using System;

namespace GamerTool.Services;

/// <summary>
/// Turns the analyser's per frame band levels into the levels actually drawn.
/// <para>
/// This is arithmetic over doubles, so it lives here rather than in the view,
/// where it can be tested without a window. The view owns the pixels; this owns
/// the numbers.
/// </para>
/// <para>
/// It exists because a 46 ms window is a noisy estimate of a spectrum. Measured
/// against real music, consecutive frames of the same steady passage disagreed
/// by around 10 dB on a well resolved band, and the bottom bands, which the
/// window cannot resolve at all, disagreed by up to 100 dB. Drawn straight to
/// the screen that reads as violence: bars teleport rather than move.
/// </para>
/// <para>
/// The fix is in two parts, and the second is the larger of the two. Averaging
/// the band levels over a short time removes most of the frame to frame noise,
/// and slowing the rise and fall stops what is left from snapping. On real
/// music that takes the largest single frame jump from 35 dB to under 10.
/// </para>
/// </summary>
public sealed class SpectrumSmoother
{
    /// <summary>
    /// Time constant for averaging the analyser's output. Long enough to
    /// average several frames of noise, short enough that a drum hit still
    /// shows up as a hit.
    /// </summary>
    public const double AverageMs = 60.0;

    /// <summary>
    /// How fast a bar may rise. Deliberately unhurried: the previous value was
    /// quick enough that every burst of noise became a visible spike, which read
    /// as the display reacting to everything at once.
    /// </summary>
    public const double Attack = 14.0;

    /// <summary>
    /// How fast a bar may fall, slower than it rises, which is what makes a
    /// display read as a meter rather than a strobe.
    /// </summary>
    public const double Release = 5.5;

    /// <summary>How fast a bar falls when there is no audio at all to look at.</summary>
    public const double Decay = 6.0;

    /// <summary>Below this a bar is put back to zero, so silence is truly flat.</summary>
    public const double SilentBelow = 0.004;

    private readonly double[] _averaged;
    private readonly double[] _shown;

    public SpectrumSmoother(int barCount)
    {
        if (barCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(barCount));
        }

        _averaged = new double[barCount];
        _shown = new double[barCount];
    }

    /// <summary>The levels to draw, one per bar, in the analyser's own order.</summary>
    public ReadOnlySpan<double> Levels => _shown;

    /// <summary>Drops all history, so the next frame starts from silence.</summary>
    public void Reset()
    {
        Array.Clear(_averaged);
        Array.Clear(_shown);
    }

    /// <summary>
    /// Advances one drawn frame.
    /// <para>
    /// Every rate is per second and converted for the elapsed time, so the
    /// character of the display does not change with the frame rate. That was a
    /// real defect before: a fixed fraction per tick meant the bars moved at
    /// three different speeds depending on what the machine could manage.
    /// </para>
    /// </summary>
    /// <param name="raw">The analyser's output for this frame.</param>
    /// <param name="hasAudio">False when there was no window to analyse.</param>
    /// <param name="dtSeconds">Time since the previous frame.</param>
    public void Push(ReadOnlySpan<double> raw, bool hasAudio, double dtSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return;
        }

        double kAverage = 1.0 - Math.Exp(-dtSeconds / (AverageMs / 1000.0));
        double kAttack = 1.0 - Math.Exp(-Attack * dtSeconds);
        double kRelease = 1.0 - Math.Exp(-Release * dtSeconds);
        double kDecay = 1.0 - Math.Exp(-Decay * dtSeconds);

        int count = Math.Min(raw.Length, _shown.Length);
        for (int bar = 0; bar < count; bar++)
        {
            double target = 0.0;

            if (hasAudio)
            {
                _averaged[bar] += (raw[bar] - _averaged[bar]) * kAverage;
                target = _averaged[bar];
            }

            double k = hasAudio
                ? (target > _shown[bar] ? kAttack : kRelease)
                : kDecay;

            _shown[bar] += (target - _shown[bar]) * k;

            // A frame that is nearly but not quite silent would otherwise sit
            // there glowing, which reads as a stuck bar rather than a quiet one.
            if (_shown[bar] < SilentBelow)
            {
                _shown[bar] = 0.0;
            }
        }
    }
}
