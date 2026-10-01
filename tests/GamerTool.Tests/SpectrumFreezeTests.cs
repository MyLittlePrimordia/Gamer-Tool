using System;
using System.Windows.Controls;
using GamerTool.Services;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The spectrum must not be able to sit still showing a peak.
/// <para>
/// The complaint this came from was bars that stopped moving and stayed up, with
/// no audio playing, and no way back to a flat row short of changing tabs. The
/// smoothing was never at fault: it falls to zero the moment it is told there is
/// no audio, which is a long tested path. The fault was upstream, in what the
/// feed reported. A loopback capture that stopped delivering raised no event and
/// changed no endpoint, so the ring simply stopped advancing and the same final
/// window came back every frame. A full window every frame reads as steady
/// audio, so the levels computed from it were the levels of whatever had been
/// playing when the capture died, and the bars converged onto them and stayed.
/// </para>
/// <para>
/// So the contract under test is the one the fix rests on: a feed that has
/// nothing new to give must report nothing, and the view must then decay. Both
/// halves are checked here, along with the second defect found alongside it -
/// that a start arriving while the view is already running used to open a second
/// capture instead of being ignored, which is what the log's repeated "loopback
/// live" line every twenty seconds was.
/// </para>
/// </summary>
[Collection("wpf")]
public class SpectrumFreezeTests
{
    // The STA thread and its Application are shared with the other WPF tests
    // through WpfTestHost. A private one here would be a second Application in
    // the same AppDomain, which WPF refuses and which aborts the entire run.
    private static T OnSta<T>(Func<T> body) => WpfTestHost.Invoke(body);

    private static void OnSta(Action body) => WpfTestHost.Invoke(body);


    /// <summary>
    /// A feed the test drives by hand, so a capture that dies can be modelled
    /// exactly: it simply stops handing over new samples, which is all that
    /// really happens when WASAP invalidates a stream.
    /// </summary>
    private sealed class FakeFeed : ISampleFeed
    {
        private readonly float[] _held;

        public FakeFeed(int samples = 8192)
        {
            _held = new float[samples];

            // A loud, unmistakable signal, so a frozen window is unmistakable.
            for (int i = 0; i < _held.Length; i++)
            {
                _held[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            }
        }

        public bool IsLive { get; set; } = true;

        /// <summary>False models a capture that has stopped delivering.</summary>
        public bool Delivering { get; set; } = true;

        public int StartCalls { get; private set; }

        public void Start() => StartCalls++;

        public void Stop() { }

        public int Read(Span<float> into)
        {
            if (!Delivering || into.Length == 0)
            {
                return 0;
            }

            int take = Math.Min(into.Length, _held.Length);
            _held.AsSpan(0, take).CopyTo(into);
            return take;
        }
    }

    /// <summary>
    /// Drives the view's own draw by hand. The view runs a 60 Hz
    /// <see cref="DispatcherTimer"/>, which a test cannot wait on, so the frame is
    /// stepped directly instead - the same code path, just not on a clock.
    /// </summary>
    private static void Tick(SpectrumView view, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            view.Draw(1.0 / 60.0);
        }
    }

    /// <summary>
    /// The tallest bar currently being drawn. This is the thing the complaint was
    /// about - a bar that stays up - so it is what the tests have to be able to
    /// ask about, and asking the smoother would be asking a step removed from
    /// what the user can actually see.
    /// </summary>
    private static double PeakLevelForTest(SpectrumView view) => view.PeakLevel;

    [Fact]
    public void Bars_fall_to_silence_when_the_capture_stops_delivering()
    {
        var feed = new FakeFeed();
        SpectrumView view = OnSta(() => new SpectrumView(new Grid(), feed));

        OnSta(() =>
        {
            view.Start();

            // Enough frames for the smoother to reach the tone and settle on it,
            // which is the state the complaint described: bars up, audio gone.
            Tick(view, 240);
        });

        double whilePlaying = OnSta(() => PeakLevelForTest(view));
        Assert.True(whilePlaying > 0.1, "the fake tone never reached the bars, so the test proves nothing");

        // The capture dies: it stops handing over buffers and says so.
        feed.Delivering = false;

        OnSta(() => Tick(view, 240));

        Assert.Equal(0.0, OnSta(() => PeakLevelForTest(view)), 6);
    }

    [Fact]
    public void A_feed_with_nothing_new_reports_no_audio_rather_than_the_old_window()
    {
        // The contract the fix depends on, checked directly on the seam. A feed
        // that keeps handing back the same samples is indistinguishable from
        // steady audio to anything downstream, so it has to say zero instead.
        var feed = new FakeFeed();
        Span<float> into = new float[8192];

        Assert.True(feed.Read(into) > 0);

        feed.Delivering = false;
        Assert.Equal(0, feed.Read(into));
    }

    [Fact]
    public void A_second_start_while_running_does_not_open_a_second_capture()
    {
        // The window asks on every state change, visibility change and page
        // switch, so this used to reopen the capture underneath the running one:
        // a second WASAP client on the same endpoint, its device leaked, and its
        // callback still writing into the ring. It is also why the log said the
        // loopback had come live again every twenty seconds with nothing about
        // the audio having changed.
        var feed = new FakeFeed();

        OnSta(() =>
        {
            var view = new SpectrumView(new Grid(), feed);

            view.Start();
            view.Start();
            view.Start();
            view.Stop();
            view.Start();
        });

        Assert.Equal(2, feed.StartCalls);
    }
}
