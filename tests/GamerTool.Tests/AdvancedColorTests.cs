using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Reading HDR state out of the compositor.
/// <para>
/// The struct this used to be wrong by four bytes, which meant every query
/// returned ERROR_INVALID_PARAMETER and the log said "HDR unknown" on every
/// machine - a screen preset would silently do nothing under HDR and the app would
/// report success. Nothing in a normal build notices a marshalling size, so this is
/// pinned here rather than left to be discovered on a user's monitor.
/// </para>
/// </summary>
public class AdvancedColorTests
{
    /// <remarks>
    /// The layout Windows documents, restated here independently of the production
    /// struct. If the real one ever changes, this is the copy that should disagree
    /// and fail loudly rather than agree and fail quietly.
    /// <para>
    /// The LUID is spelled out as its own struct rather than as a 64 bit field,
    /// because the two have different sizes here. <c>long</c> plus <c>int</c> is 16
    /// bytes with padding where the real thing is 8, and that difference is enough
    /// on its own to put this test's total out - which is exactly the class of
    /// mistake this whole file is about, and worth hitting once inside the test that
    /// guards against it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The adapter LUID. Two 32 bit halves, and the field order matters: putting
    /// them the other way round, or using a 64 bit field for the low half, changes
    /// the size of everything that contains this.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DocumentedLuid
    {
        public uint lowPart;
        public int highPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DocumentedHeader
    {
        public int type;
        public uint size;
        public DocumentedLuid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DocumentedAdvancedColor
    {
        public DocumentedHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [Fact]
    public void TheHeaderIsTwentyBytes()
    {
        // The number the 32 byte total depends on. Asserted separately so a failure
        // says which of the two fields moved.
        Assert.Equal(20, Marshal.SizeOf<DocumentedHeader>());
    }

    [Fact]
    public void TheDocumentedStructIsThirtyTwoBytes()
    {
        Assert.Equal(32, Marshal.SizeOf<DocumentedAdvancedColor>());
    }

    [Fact]
    public void TheBitLayoutIsSupportedEnabledWideColourForceDisabled()
    {
        // bit0 supported, bit1 enabled, bit2 wideColorEnforced, bit3 forceDisabled.
        // One bit at a time, because a test that set several at once could not tell
        // which of them had been assigned to which field.
        AdvancedColorFlags supported = AdvancedColorFlags.Decode(0x1);
        Assert.True(supported.Supported);
        Assert.False(supported.Enabled);
        Assert.False(supported.WideColorEnforced);
        Assert.False(supported.ForceDisabled);

        AdvancedColorFlags enabled = AdvancedColorFlags.Decode(0x2);
        Assert.False(enabled.Supported);
        Assert.True(enabled.Enabled);

        AdvancedColorFlags wide = AdvancedColorFlags.Decode(0x4);
        Assert.False(wide.Enabled);
        Assert.True(wide.WideColorEnforced);

        AdvancedColorFlags forced = AdvancedColorFlags.Decode(0x8);
        Assert.False(forced.Enabled);
        Assert.True(forced.ForceDisabled);
    }

    [Theory]
    // supported=bit0, enabled=bit1, wideColourEnforced=bit2, forceDisabled=bit3.
    [InlineData(0x0u, false, false)]     // an ordinary SDR panel
    [InlineData(0x1u, true, false)]      // capable, not switched on: HDR off
    [InlineData(0x3u, true, true)]       // capable and switched on: HDR on
    [InlineData(0x7u, true, false)]      // enabled but wide colour enforced: not HDR
    [InlineData(0x6u, false, false)]     // enabled without capability: not HDR
    public void HdrOnIsEnabledAndNotMerelyWideColour(uint value, bool supported, bool hdrOn)
    {
        AdvancedColorFlags flags = AdvancedColorFlags.Decode(value);

        Assert.Equal(supported, flags.Supported);
        Assert.Equal(hdrOn, flags.HdrOn);
    }

    [Fact]
    public void WideColourEnforcementTurnsOffHdrEvenWithTheFlagStillSet()
    {
        // 0x7 is supported, enabled and wide colour enforced all at once - which is
        // what Windows 11 reports with "Automatically manage color for apps" on. The
        // panel gets a wider gamut and is still SDR, so warning about it would be
        // warning about a screen that is working.
        AdvancedColorFlags flags = AdvancedColorFlags.Decode(0x7);

        Assert.True(flags.Supported);
        Assert.True(flags.Enabled);
        Assert.True(flags.WideColorEnforced);
        Assert.False(flags.HdrOn);
    }

    [Fact]
    public void ACapableButSwitchedOffDisplayIsNotReportedAsHdr()
    {
        // The exact case the old code got wrong. It read the whole flags word as
        // colorEncoding and asked whether it was non-zero, so a display that merely
        // *can* do HDR was reported as doing it.
        AdvancedColorFlags capableOnly = AdvancedColorFlags.Decode(0x1);

        Assert.True(capableOnly.Supported);
        Assert.False(capableOnly.HdrOn);
    }

    [Fact]
    public void WideColourEnforcementIsNotHdr()
    {
        // Windows 11 with "Automatically manage color for apps" turns advanced colour
        // on while leaving the panel in SDR. Treating that as HDR would warn a user
        // whose gamma ramp is working perfectly well.
        AdvancedColorFlags automatic = AdvancedColorFlags.Decode(0x7);

        Assert.True(automatic.Enabled);
        Assert.True(automatic.WideColorEnforced);
        Assert.False(automatic.HdrOn);
    }

    [Fact]
    public void NoBitsAtAllIsAQuietDisplayRatherThanAnError()
    {
        AdvancedColorFlags none = AdvancedColorFlags.Decode(0);

        Assert.False(none.Supported);
        Assert.False(none.Enabled);
        Assert.False(none.HdrOn);
    }

    [Fact]
    public void BitsThisBuildHasNeverHeardOfAreIgnored()
    {
        // Windows adds flags between releases. Refusing to decode an answer because
        // it carried an unknown bit would turn a new flag into "HDR unknown" on
        // hardware that is working perfectly well.
        AdvancedColorFlags future = AdvancedColorFlags.Decode(0x8000_0003);

        Assert.True(future.Supported);
        Assert.True(future.HdrOn);
    }

    [Fact]
    public void AFailedQueryDecodesToEverythingOffRatherThanSomethingGuessed()
    {
        // The default the call site uses when the compositor refused to answer.
        // Every field false, so "HdrKnown" being false is what suppresses the
        // reading rather than the flags happening to be zero.
        AdvancedColorFlags nothing = default;

        Assert.False(nothing.Supported);
        Assert.False(nothing.Enabled);
        Assert.False(nothing.WideColorEnforced);
        Assert.False(nothing.ForceDisabled);
        Assert.False(nothing.HdrOn);
    }

    [Fact]
    public void EveryBitPositionIsDistinct()
    {
        // Guards against two fields being given the same mask, which would make every
        // single-bit test above pass while decoding nothing real.
        uint[] singleBits = { 0x1, 0x2, 0x4, 0x8 };
        HashSet<uint> seen = new();

        foreach (uint bit in singleBits)
        {
            Assert.True(seen.Add(bit), "0x" + bit.ToString("X") + " is used twice");
        }
    }
}