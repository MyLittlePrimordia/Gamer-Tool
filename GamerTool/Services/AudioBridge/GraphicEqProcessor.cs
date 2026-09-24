using NAudio.Dsp;
using NAudio.Wave;
using GamerTool.Models;

namespace GamerTool.Services.AudioBridge;

/// <summary>
/// Wraps a source ISampleProvider (fed by WASAPI loopback capture) with a
/// live 10-band peaking EQ + preamp, built from NAudio's BiQuadFilter (RBJ
/// Audio EQ Cookbook peaking-EQ formulas). This is the actual "equalizer" in
/// the no-signing-required Bridge architecture: instead of hooking into
/// audiodg.exe via an APO (which current Windows refuses to load unsigned —
/// see README), the same 10-band shaping happens here, in-process, on audio
/// already captured via WASAPI loopback.
///
/// One filter chain PER CHANNEL is required: BiQuadFilter is a stateful IIR
/// filter carrying its own sample history, so a single shared instance
/// processing interleaved stereo samples would corrupt the left/right
/// history against each other.
/// </summary>
public class GraphicEqProcessor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly object _lock = new();
    private BiQuadFilter[] _filters;
    private float _preampLinear = 1.0f;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public GraphicEqProcessor(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        _filters = BuildFilters(AudioPreset.Flat, source.WaveFormat.SampleRate);
    }

    /// <summary>Swaps in a new set of filter coefficients + preamp. Safe to
    /// call from the UI thread while the render thread is mid-Read() — the
    /// lock only guards the reference swap, not per-sample processing, so
    /// this never blocks audio for more than a few instructions.</summary>
    public void UpdatePreset(AudioPreset preset)
    {
        var newFilters = BuildFilters(preset, WaveFormat.SampleRate);
        float newPreamp = DbToLinear(EffectivePreampDb(preset));
        lock (_lock)
        {
            _filters = newFilters;
            _preampLinear = newPreamp;
        }
    }

    private static double EffectivePreampDb(AudioPreset preset)
    {
        double maxGain = 0;
        foreach (var g in preset.Bands)
            if (g > maxGain) maxGain = g;

        double preamp = preset.Preamp;
        if (preset.AntiClip && preamp + maxGain > 0)
            preamp -= preamp + maxGain;
        return preamp;
    }

    private BiQuadFilter[] BuildFilters(AudioPreset preset, int sampleRate)
    {
        var filters = new BiQuadFilter[AudioPreset.Frequencies.Length * _channels];
        for (int band = 0; band < AudioPreset.Frequencies.Length; band++)
        {
            float freq = AudioPreset.Frequencies[band];
            float gainDb = band < preset.Bands.Length ? (float)preset.Bands[band] : 0f;
            for (int ch = 0; ch < _channels; ch++)
            {
                filters[band * _channels + ch] =
                    BiQuadFilter.PeakingEQ(sampleRate, freq, 1.41f, gainDb);
            }
        }
        return filters;
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        BiQuadFilter[] filters;
        float preamp;
        lock (_lock)
        {
            filters = _filters;
            preamp = _preampLinear;
        }

        for (int i = 0; i < samplesRead; i++)
        {
            int ch = i % _channels;
            float sample = buffer[offset + i] * preamp;

            for (int band = 0; band < AudioPreset.Frequencies.Length; band++)
                sample = filters[band * _channels + ch].Transform(sample);

            // Soft-clip safety net: even with anti-clip preamp compensation,
            // transient peaks across several simultaneously-boosted bands can
            // still nudge past [-1, 1] for an instant. A hard clamp sounds
            // harsh (audible crackle); rounding just the very top of the peak
            // with a tanh curve is inaudible in normal listening.
            const float ceiling = 0.98f;
            if (sample > ceiling)
                sample = ceiling + (1 - ceiling) * (float)Math.Tanh((sample - ceiling) * 10);
            else if (sample < -ceiling)
                sample = -ceiling + (1 - ceiling) * (float)Math.Tanh((sample + ceiling) * 10);

            buffer[offset + i] = sample;
        }

        return samplesRead;
    }
}
