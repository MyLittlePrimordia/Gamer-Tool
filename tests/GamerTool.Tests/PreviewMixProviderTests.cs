using System;
using GamerTool.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The preview's volume and stereo panning, as arithmetic.
/// <para>
/// This exists because the gain and balance moved out of the playback engine and
/// into the sample chain. WPF's MediaPlayer had them as properties, and
/// NAudio's WasapiOut has no per-channel balance at all, so the app grew its own
/// panner. A panner is exactly the sort of thing that is quietly wrong in a way
/// nothing notices: an inverted side, a centre that is not unity, or a hard
/// deflection that cancels a channel rather than fading it, all produce audio
/// that sounds broadly plausible and is quietly wrong.
/// </para>
/// <para>
/// None of that needs a sound card to detect, which is the only sort of machine
/// CI has.
/// </para>
/// </summary>
public class PreviewMixProviderTests
{
    private const int Rate = 48000;

    /// <summary>
    /// A stereo source of a constant level, so attenuation is readable directly.
    /// <para>
    /// Implements IDisposable because the provider under test disposes its
    /// source through that interface, and NAudio's own sample providers all
    /// implement it. A double that quietly did not would make the disposal test
    /// pass for the wrong reason, or fail for the wrong one.
    /// </para>
    /// </summary>
    private sealed class ConstantStereo : ISampleProvider, IDisposable
    {
        private readonly float _value;

        public ConstantStereo(float value)
        {
            _value = value;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = _value;
            }

            return count;
        }
    }

    private static float[] Render(PreviewMixProvider mix, int frames = 8)
    {
        float[] buffer = new float[frames * 2];
        int read = mix.Read(buffer, 0, buffer.Length);
        float[] result = new float[read];

        Array.Copy(buffer, result, read);
        return result;
    }

    [Fact]
    public void The_default_mix_leaves_the_samples_exactly_as_they_were()
    {
        // Unity gain and centred panning must be a no-op, so a track plays back
        // bit for bit as it was mastered until the user actually moves something.
        var mix = new PreviewMixProvider(new ConstantStereo(0.5f));
        float[] out_ = Render(mix);

        for (int i = 0; i < out_.Length; i++)
        {
            Assert.Equal(0.5f, out_[i], 6);
        }
    }

    [Fact]
    public void Volume_scales_both_sides_together()
    {
        var mix = new PreviewMixProvider(new ConstantStereo(0.8f)) { Volume = 0.5f };
        float[] out_ = Render(mix);

        for (int i = 0; i < out_.Length; i++)
        {
            Assert.Equal(0.4f, out_[i], 6);
        }
    }

    [Fact]
    public void Pushing_the_balance_right_fades_the_right_channel_only()
    {
        var mix = new PreviewMixProvider(new ConstantStereo(0.8f)) { Balance = 0.5f };
        float[] out_ = Render(mix);

        for (int frame = 0; frame * 2 + 1 < out_.Length; frame++)
        {
            Assert.Equal(0.8f, out_[frame * 2], 6);      // left untouched
            Assert.Equal(0.4f, out_[(frame * 2) + 1], 6); // right at half
        }
    }

    [Fact]
    public void Pushing_the_balance_left_fades_the_left_channel_only()
    {
        var mix = new PreviewMixProvider(new ConstantStereo(0.8f)) { Balance = -0.5f };
        float[] out_ = Render(mix);

        for (int frame = 0; frame * 2 + 1 < out_.Length; frame++)
        {
            Assert.Equal(0.4f, out_[frame * 2], 6);      // left at half
            Assert.Equal(0.8f, out_[(frame * 2) + 1], 6); // right untouched
        }
    }

    [Fact]
    public void A_full_deflection_silences_the_opposite_channel_rather_than_inverting_it()
    {
        // The failure mode this guards is a sign flip: an inverted side sounds
        // like a broken cable, and at full deflection it is the difference
        // between silence and a channel playing backwards.
        var right = new PreviewMixProvider(new ConstantStereo(0.8f)) { Balance = 1.0f };
        float[] toRight = Render(right);

        for (int frame = 0; frame * 2 + 1 < toRight.Length; frame++)
        {
            Assert.Equal(0.8f, toRight[frame * 2], 6);
            Assert.Equal(0.0f, toRight[(frame * 2) + 1], 6);
        }

        var left = new PreviewMixProvider(new ConstantStereo(0.8f)) { Balance = -1.0f };
        float[] toLeft = Render(left);

        for (int frame = 0; frame * 2 + 1 < toLeft.Length; frame++)
        {
            Assert.Equal(0.0f, toLeft[frame * 2], 6);
            Assert.Equal(0.8f, toLeft[(frame * 2) + 1], 6);
        }
    }

    [Fact]
    public void Balance_and_volume_combine()
    {
        // The Audio tab drives both from the preset at once, so they have to be
        // independent rather than one overwriting the other.
        var mix = new PreviewMixProvider(new ConstantStereo(0.8f))
        {
            Volume = 0.5f,
            Balance = 0.5f
        };

        float[] out_ = Render(mix);

        for (int frame = 0; frame * 2 + 1 < out_.Length; frame++)
        {
            Assert.Equal(0.4f, out_[frame * 2], 6);
            Assert.Equal(0.2f, out_[(frame * 2) + 1], 6);
        }
    }

    [Fact]
    public void Out_of_range_values_are_clamped_rather_than_amplified_or_inverted()
    {
        // A value from a hand-edited settings file, or from a preset whose slider
        // was pushed past its end, must not produce a gain above full scale.
        var loud = new PreviewMixProvider(new ConstantStereo(0.5f)) { Volume = 4.0f };
        foreach (float sample in Render(loud))
        {
            Assert.Equal(0.5f, sample, 6);
        }

        var over = new PreviewMixProvider(new ConstantStereo(0.5f)) { Balance = 3.0f };
        float[] out_ = Render(over);
        Assert.Equal(0.0f, out_[1], 6);
    }

    [Fact]
    public void Mono_is_gained_but_never_panned()
    {
        // Panning a mono track as if it were stereo would give it a phase
        // difference between the channels it does not have, which is a
        // cancellation nobody asked for.
        var source = new ConstantMono(0.5f);
        var mix = new PreviewMixProvider(source) { Volume = 0.5f, Balance = 1.0f };

        float[] out_ = Render(mix);

        Assert.Equal(1, mix.WaveFormat.Channels);
        for (int i = 0; i < out_.Length; i++)
        {
            Assert.Equal(0.25f, out_[i], 6);
        }
    }

    [Fact]
    public void A_read_of_nothing_does_not_advance_the_position()
    {
        // End of loop. If this counted frames anyway the position would run away
        // from the track on every wrap.
        var mix = new PreviewMixProvider(new SilentSource());

        Assert.Equal(0, mix.Read(Array.Empty<float>(), 0, 0));
        Assert.Equal(0L, mix.SamplesPlayed);
    }

    [Fact]
    public void The_position_counts_frames_rather_than_samples()
    {
        // One frame is one sample per channel. Counting raw samples would report
        // a stereo track as playing at twice its length.
        var mix = new PreviewMixProvider(new ConstantStereo(0.1f));
        Render(mix, frames: 16);

        Assert.Equal(16L, mix.SamplesPlayed);
    }

    [Fact]
    public void Disposing_the_mix_disposes_what_it_wraps()
    {
        var source = new ConstantStereo(0.5f);
        var mix = new PreviewMixProvider(source);

        mix.Dispose();

        Assert.True(source.Disposed, "the source reader was left open when the mix was disposed");
    }

    private sealed class ConstantMono : ISampleProvider
    {
        private readonly float _value;

        public ConstantMono(float value)
        {
            _value = value;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = _value;
            }

            return count;
        }
    }

    private sealed class SilentSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public int Read(float[] buffer, int offset, int count) => 0;
    }
}
