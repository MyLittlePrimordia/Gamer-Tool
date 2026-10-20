using System;
using System.IO;
using System.Text;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The file behind <see cref="DisplayService.RestoreAfterCrash"/>.
/// <para>
/// The bug being guarded is not "a crash leaves a tint". It is that the next run
/// reads that tint back as the original and makes it permanent. So the cases
/// that matter are the ones that decide whether a stored baseline is trusted:
/// the round trip, the boot comparison, and a file that will not parse.
/// </para>
/// </summary>
public sealed class GammaBaselineStoreTests : IDisposable
{
    private const string MonitorId = @"MONITOR\AOCA610\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008";

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "GamerToolTests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "display-baseline.json");

    public GammaBaselineStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A test that left a handle open is not worth failing the suite for.
        }
    }

    [Fact]
    public void ARampSurvivesTheRoundTripThroughDisk()
    {
        ushort[] ramp = GammaBaselineStore.Identity();
        ramp[0] = 1234;
        ramp[255] = 65000;
        ramp[256] = 77;

        var store = new GammaBaselineStore(Path_);
        store.BeginDirty(MonitorId, ramp);

        var reopened = new GammaBaselineStore(Path_);

        Assert.True(reopened.Dirty);
        Assert.Equal(ramp, reopened.Find(MonitorId, string.Empty));
    }

    [Fact]
    public void TheFallbackRampIsIdentityAtBothEnds()
    {
        ushort[] identity = GammaBaselineStore.Identity();

        Assert.Equal((ushort)0, identity[0]);
        Assert.Equal((ushort)257, identity[1]);
        Assert.Equal((ushort)65535, identity[255]);

        // The three channels are the same table laid end to end, so the same two
        // endpoints hold for green and blue.
        Assert.Equal((ushort)0, identity[GammaBaselineStore.RampSize]);
        Assert.Equal((ushort)65535, identity[(GammaBaselineStore.RampSize * 3) - 1]);
    }

    [Fact]
    public void ADirtyMarkerFromThisBootIsTheOnlyOneActedOn()
    {
        // A clock rather than the real one, because the question is a comparison
        // against boot time and the test should not have to reboot a machine.
        DateTime now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var store = new GammaBaselineStore(Path_, () => now);
        store.BeginDirty(MonitorId, GammaBaselineStore.Identity());

        // Written at noon, machine booted at nine: same boot.
        Assert.True(store.DirtyAppliesToThisBoot(now, TimeSpan.FromHours(3)));

        // Written at noon, machine has since rebooted at 12:50 and it is now one
        // o'clock: the marker predates the boot, so Windows has already cleared the
        // table and it is not ours.
        Assert.False(store.DirtyAppliesToThisBoot(now.AddHours(1), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void MarkingCleanSurvivesAReload()
    {
        var store = new GammaBaselineStore(Path_);
        store.BeginDirty(MonitorId, GammaBaselineStore.Identity());
        store.MarkClean();

        var reopened = new GammaBaselineStore(Path_);

        Assert.False(reopened.Dirty);

        // The baseline is kept, not thrown away with the flag. A later crash in
        // the same session still has something to put back.
        Assert.NotNull(reopened.Find(MonitorId, string.Empty));
    }

    [Fact]
    public void ACorruptFileLoadsAsEmptyAndClean()
    {
        File.WriteAllText(Path_, "{ this is not json");

        var store = new GammaBaselineStore(Path_);

        Assert.False(store.Dirty);
        Assert.True(store.IsEmpty);
        Assert.Null(store.Find(MonitorId, string.Empty));
    }

    [Fact]
    public void AFileWrittenByTheOlderBuildIsReadRatherThanDiscarded()
    {
        // The exact shape the previous implementation left in %AppData%, including
        // the identity table and the PnP-id key. Ignoring it would be harmless, but
        // the point is that a user who ran that build is not left with an
        // unrecognised file and no baseline.
        string identity = Convert.ToBase64String(EncodeIdentity());

        File.WriteAllText(
            Path_,
            "{\"Version\":1,\"Dirty\":false,\"DirtySinceUtc\":\"2026-10-19T01:52:58.7563881Z\","
            + "\"Ramps\":{\"" + MonitorId.Replace("\\", "\\\\") + "\":\"" + identity + "\"}}",
            new UTF8Encoding(false));

        var store = new GammaBaselineStore(Path_);

        Assert.False(store.Dirty);
        Assert.Equal(GammaBaselineStore.Identity(), store.Find(MonitorId, string.Empty));
    }

    private static byte[] EncodeIdentity()
    {
        ushort[] ramp = GammaBaselineStore.Identity();
        byte[] bytes = new byte[ramp.Length * 2];

        for (int i = 0; i < ramp.Length; i++)
        {
            bytes[i * 2] = (byte)(ramp[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)(ramp[i] >> 8);
        }

        return bytes;
    }
}
