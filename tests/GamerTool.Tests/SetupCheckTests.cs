using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The checks a user cannot run by themselves, and the two things they can break.
/// <para>
/// Both probes move a real system setting, so the interesting property is not
/// that they report pass or fail but that they put the machine back - including
/// when the call they were testing fails. Those are the cases here, with the
/// hardware faked at the delegate the real code takes its readings from.
/// </para>
/// </summary>
public class SetupCheckTests
{
    private static SetupScreen Screen(bool hdr = false, bool active = false) =>
        new(@"\\.\DISPLAY1", @"MONITOR\ABC", "Primary", hdr, active);

    private static SetupCheckItem Pass(string label) => new(label, "fine", SetupCheckState.Pass);

    // ---------- the report ----------

    [Fact]
    public async Task A_machine_where_everything_answers_is_reported_as_ready()
    {
        SetupCheckRunner runner = new(
            () => new[] { Screen(hdr: true) },
            _ => Pass("GAMMA"),
            _ => Task.FromResult(Pass("HDR")),
            () => new[] { Pass("AUDIO"), Pass("MIC") });

        SetupCheckReport report = await runner.RunAsync();

        Assert.False(report.HasFailure);
        Assert.Equal("EVERYTHING CHECKED OUT", report.Summary);
        Assert.Equal(new[] { "SCREEN", "GAMMA", "HDR", "AUDIO", "MIC" }, report.Items.Select(i => i.Label));
        Assert.Contains("GAMMA", report.LogText, StringComparison.Ordinal);
        Assert.Contains("[x] GAMMA", report.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_screen_says_so_once_and_stops()
    {
        int gamma = 0;
        int hdr = 0;
        int routes = 0;

        SetupCheckRunner runner = new(
            () => Array.Empty<SetupScreen>(),
            _ => { gamma++; return Pass("GAMMA"); },
            _ => { hdr++; return Task.FromResult(Pass("HDR")); },
            () => { routes++; return new[] { Pass("AUDIO") }; });

        SetupCheckReport report = await runner.RunAsync();

        Assert.True(report.HasFailure);
        Assert.Equal("ONE CHECK FAILED", report.Summary);
        Assert.Single(report.Items);
        Assert.Equal(SetupCheckState.Fail, report.Items[0].State);

        // Reporting "no screen" four times would be noise, and every step below
        // would be testing nothing.
        Assert.Equal(0, gamma);
        Assert.Equal(0, hdr);
        Assert.Equal(0, routes);
    }

    [Fact]
    public async Task A_screen_without_hdr_skips_that_check_instead_of_failing_it()
    {
        bool hdrCalled = false;

        SetupCheckRunner runner = new(
            () => new[] { Screen() },
            _ => Pass("GAMMA"),
            _ => { hdrCalled = true; return Task.FromResult(Pass("HDR")); },
            () => new[] { Pass("AUDIO") });

        SetupCheckReport report = await runner.RunAsync();

        Assert.False(hdrCalled);
        Assert.False(report.HasFailure);

        SetupCheckItem hdr = report.Items.Single(i => i.Label == "HDR");
        Assert.Equal(SetupCheckState.Skip, hdr.State);
        Assert.Contains("no screen here supports HDR", hdr.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hdr_check_runs_on_the_first_screen_that_supports_it()
    {
        SetupScreen plain = new(@"\\.\DISPLAY1", "MONITOR\\A", "Left", false, false);
        SetupScreen capable = new(@"\\.\DISPLAY2", "MONITOR\\B", "Right", true, false);
        SetupScreen? tested = null;

        SetupCheckRunner runner = new(
            () => new[] { plain, capable },
            _ => Pass("GAMMA"),
            screen => { tested = screen; return Task.FromResult(Pass("HDR")); },
            () => new[] { Pass("AUDIO") });

        SetupCheckReport report = await runner.RunAsync();

        Assert.Equal(capable, tested);

        // One line for the screens rather than one each, and it names them, so a
        // machine with the game on the second panel can see which was picked.
        Assert.Contains("2 screens: Left, Right", report.Items[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_throws_is_one_failed_line_not_the_whole_report()
    {
        SetupCheckRunner runner = new(
            () => new[] { Screen(hdr: true) },
            _ => throw new InvalidOperationException("the driver went away"),
            _ => Task.FromResult(Pass("HDR")),
            () => new[] { Pass("AUDIO") });

        SetupCheckReport report = await runner.RunAsync();

        Assert.True(report.HasFailure);

        SetupCheckItem gamma = report.Items.Single(i => i.Label == "GAMMA");
        Assert.Equal(SetupCheckState.Fail, gamma.State);
        Assert.Contains("did not finish", gamma.Detail, StringComparison.Ordinal);
        Assert.Contains("the driver went away", gamma.Detail, StringComparison.Ordinal);

        // The rest still ran, which is the point: one dead check must not hide the
        // answers to the other three.
        Assert.Contains(report.Items, i => i.Label == "HDR");
        Assert.Contains(report.Items, i => i.Label == "AUDIO");
    }

    [Fact]
    public async Task A_sound_step_that_throws_fails_only_the_sound_line()
    {
        SetupCheckRunner runner = new(
            () => new[] { Screen() },
            _ => Pass("GAMMA"),
            _ => Task.FromResult(Pass("HDR")),
            () => throw new InvalidOperationException("no endpoints"));

        SetupCheckReport report = await runner.RunAsync();

        SetupCheckItem audio = report.Items.Single(i => i.Label == "AUDIO");
        Assert.Equal(SetupCheckState.Fail, audio.State);
        Assert.Contains("no endpoints", audio.Detail, StringComparison.Ordinal);
        Assert.Equal(1, report.Failures);
    }

    // ---------- the HDR probe ----------

    private sealed class FakeHdr
    {
        public readonly List<bool> Sets = new();

        public bool Active { get; set; }

        public bool Known { get; set; } = true;

        public bool Supported { get; set; } = true;

        /// <summary>When set, the setter refuses this value.</summary>
        public bool? Refuse { get; set; }

        /// <summary>When true, the setter claims success and changes nothing.</summary>
        public bool IgnoreWrites { get; set; }

        public HdrControl Build(Func<DateTime> clock) => new(
            _ => new HdrReading(Known, Supported, Active, Active, false, HdrReadSource.Info2),
            (_, enable) =>
            {
                Sets.Add(enable);

                if (Refuse == enable)
                {
                    return HdrSetResult.Failure("refused");
                }

                if (!IgnoreWrites)
                {
                    Active = enable;
                }

                return HdrSetResult.Success;
            },
            clock);
    }

    [Fact]
    public async Task The_hdr_check_flips_the_screen_and_puts_it_back()
    {
        FakeHdr fake = new() { Active = false };
        HdrControl hdr = fake.Build(() => DateTime.UtcNow);

        HdrProbeResult result = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(400));

        Assert.True(result.Ok);
        Assert.Equal(new[] { true, false }, fake.Sets);
        Assert.False(fake.Active);
    }

    [Fact]
    public async Task A_refused_flip_still_leaves_the_screen_as_it_was()
    {
        FakeHdr fake = new() { Active = false, Refuse = true };
        HdrControl hdr = fake.Build(() => DateTime.UtcNow);

        HdrProbeResult result = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(200));

        Assert.False(result.Ok);
        Assert.False(result.Skipped);
        Assert.Contains("refused", result.Detail, StringComparison.Ordinal);

        // It was asked to put the screen back even though nothing had changed.
        Assert.Contains(false, fake.Sets);
        Assert.False(fake.Active);
    }

    [Fact]
    public async Task A_flip_windows_ignores_is_a_failure_and_is_rolled_back()
    {
        FakeHdr fake = new() { Active = false, IgnoreWrites = true };
        HdrControl hdr = fake.Build(() => DateTime.UtcNow);

        HdrProbeResult result = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(100));

        Assert.False(result.Ok);
        Assert.Contains("did not switch", result.Detail, StringComparison.Ordinal);

        // Asked to switch on, then asked to put back the state that never changed.
        Assert.Equal(new[] { true, false }, fake.Sets);
    }

    [Fact]
    public async Task A_screen_that_reports_nothing_is_skipped_without_a_write()
    {
        FakeHdr unknown = new() { Known = false };
        HdrProbeResult result = await unknown.Build(() => DateTime.UtcNow)
            .ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(100));

        Assert.True(result.Skipped);
        Assert.Empty(unknown.Sets);

        FakeHdr unsupported = new() { Supported = false };
        HdrProbeResult second = await unsupported.Build(() => DateTime.UtcNow)
            .ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(100));

        Assert.True(second.Skipped);
        Assert.Empty(unsupported.Sets);
    }

    [Fact]
    public async Task A_second_check_straight_after_the_first_waits_its_turn()
    {
        // The probe respects the same quiet period a slot does, so pressing the row
        // twice does not blink the screen twice in a row.
        FakeHdr fake = new() { Active = false };
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        HdrControl hdr = fake.Build(() => now);

        HdrProbeResult first = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(400));
        HdrProbeResult second = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(400));

        Assert.True(first.Ok);
        Assert.True(second.Skipped);
        Assert.Contains("a moment ago", second.Detail, StringComparison.Ordinal);

        now = now.AddSeconds(30);
        HdrProbeResult third = await hdr.ProbeAsync(@"\\.\DISPLAY1", "K", TimeSpan.FromMilliseconds(400));

        Assert.True(third.Ok);
    }

    // ---------- the gamma probe ----------

    [Fact]
    public void The_gamma_probe_darkens_a_copy_rather_than_choosing_a_look()
    {
        ushort[] source = Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();

        ushort[] scaled = DisplayService.ScaleGammaChannel(source, 0.9);

        Assert.Equal(256, scaled.Length);
        Assert.Equal(0, scaled[0]);
        Assert.Equal((ushort)Math.Round(source[255] * 0.9), scaled[255]);

        // Monotonic, so the test ramp cannot invert a channel and turn a probe into
        // a visible glitch on a screen that does apply it.
        for (int i = 1; i < scaled.Length; i++)
        {
            Assert.True(scaled[i] >= scaled[i - 1]);
            Assert.True(scaled[i] <= source[i]);
        }
    }

    [Fact]
    public void A_short_channel_is_not_read_past_its_end()
    {
        ushort[] shortChannel = { 100, 200 };

        ushort[] scaled = DisplayService.ScaleGammaChannel(shortChannel, 0.5);

        Assert.Equal(256, scaled.Length);
        Assert.Equal(50, scaled[0]);
        Assert.Equal(100, scaled[1]);
        Assert.Equal(0, scaled[2]);
    }

    [Fact]
    public void The_gamma_probe_always_writes_the_screen_own_table_back()
    {
        // The write is the test, and the restore is the safety. Read-back, then the
        // original table, then the verdict - on the failure path too, because a
        // probe that returns early on a refused write would leave its darkened copy
        // on the screen if the driver had applied it and reported otherwise.
        string service = AppSource("Services/DisplayService.cs");
        string body = Body(service, "public GammaProbe ProbeGamma(string device)");

        int write = body.IndexOf("SetDeviceGammaRamp(hdc, ref test)", StringComparison.Ordinal);
        int readBack = body.IndexOf("GetDeviceGammaRamp(hdc, ref after)", StringComparison.Ordinal);
        int restore = body.IndexOf("SetDeviceGammaRamp(hdc, ref live)", StringComparison.Ordinal);
        int firstReturn = body.IndexOf("if (!wrote)", StringComparison.Ordinal);

        Assert.True(write > 0 && readBack > write, "the probe decides before it reads the table back");
        Assert.True(restore > readBack, "the probe does not put the screen's own table back");
        Assert.True(restore < firstReturn, "a refused write returns before the screen is restored");
    }

    private static string AppSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", name));
    }

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "could not find " + signature);

        int open = source.IndexOf('{', at);
        Assert.True(open > 0);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(open, i - open);
                }
            }
        }

        Assert.Fail("the body of " + signature + " is not closed");
        return string.Empty;
    }
}
