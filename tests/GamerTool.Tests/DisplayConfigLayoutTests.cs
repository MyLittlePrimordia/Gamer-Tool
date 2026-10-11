using System;
using System.Runtime.InteropServices;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Stands in for the DisplayConfig structs in HardwareBrightness.cs without the
/// 2500 lines around them.
/// <para>
/// Every one of these structs is load-bearing in a way nothing in a build notices.
/// The header sizes go into the packets Windows validates; the path struct's size
/// decides where its fields are read from. Both were wrong, for different reasons,
/// and both produced ERROR_INVALID_PARAMETER rather than an obvious failure - so
/// HDR read as "unknown" on every machine and the log printed a connection type it
/// had no business reporting.
/// </para>
/// <para>
/// The declarations here are copied independently from the SDK documentation
/// rather than from the app, so a mistake in the app cannot be copied into the test
/// and then agree with itself.
/// </para>
/// </summary>
public class DisplayConfigLayoutTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint videoOutputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;
        public uint statusFlags;
    }

    /// <summary>
    /// The SDK layout. Note there is no sourceModeInfoIdx member: that field lives
    /// in the unions inside the source and target info structs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public SourceInfo sourceInfo;
        public TargetInfo targetInfo;
        public uint flags;
    }

    /// <summary>The version this file used to have, with the phantom field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfoWithPhantomField
    {
        public SourceInfo sourceInfo;
        public uint sourceModeInfoIdx;
        public TargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColor
    {
        public Header header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorWithoutUnion
    {
        public Header header;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [Fact]
    public void LuidIsTwoThirtyTwoBitHalves() => Assert.Equal(8, Marshal.SizeOf<Luid>());

    [Fact]
    public void TheDeviceInfoHeaderIsTwentyBytes()
    {
        // 4 + 4 + 8 + 4. This goes into every request packet and Windows compares it
        // against its own expectation, so it is checked rather than assumed.
        Assert.Equal(20, Marshal.SizeOf<Header>());
    }

    [Fact]
    public void ThePathIsSeventyTwoBytes()
    {
        // 20 + 48 + 4. Getting this wrong is what broke every target query.
        Assert.Equal(72, Marshal.SizeOf<PathInfo>());
    }

    [Fact]
    public void ThePhantomSourceModeInfoIdxFieldMadeItFourBytesLong()
    {
        // The bug, stated as a measurement.
        Assert.Equal(76, Marshal.SizeOf<PathInfoWithPhantomField>());
    }

    [Fact]
    public void ThePhantomFieldPushedEveryTargetFieldFourBytesLate()
    {
        // Offsets rather than just a total, because a total can be wrong while every
        // offset looks plausible, and the symptom here was a target adapter id that
        // was a perfectly well formed number belonging to nothing.
        Assert.Equal(20, (int)Marshal.OffsetOf<PathInfo>(nameof(PathInfo.targetInfo)));
        Assert.Equal(24, (int)Marshal.OffsetOf<PathInfoWithPhantomField>(
            nameof(PathInfoWithPhantomField.targetInfo)));
    }

    [Fact]
    public void TheAdapterIdSitsAtTheStartOfBothHalves()
    {
        // sourceInfo(20 bytes) + targetInfo(48) + flags(4) = 72. The adapter id is
        // the first field of each half, so it lands at 0 and 20 respectively. Both
        // offsets matter: the target one is what the request packets are built from,
        // and reading it four bytes late produced a well formed LUID belonging to
        // nothing, which is what every ERROR_INVALID_PARAMETER came from.
        Assert.Equal(0, (int)Marshal.OffsetOf<SourceInfo>(nameof(SourceInfo.adapterId)));
        Assert.Equal(0, (int)Marshal.OffsetOf<TargetInfo>(nameof(TargetInfo.adapterId)));
        Assert.Equal(0, (int)Marshal.OffsetOf<PathInfo>(nameof(PathInfo.sourceInfo)));
        Assert.Equal(20, (int)Marshal.OffsetOf<PathInfo>(nameof(PathInfo.targetInfo)));
        Assert.Equal(68, (int)Marshal.OffsetOf<PathInfo>(nameof(PathInfo.flags)));
    }

    [Fact]
    public void TheAdvancedColourBlockIsThirtyTwoBytes()
    {
        // 20 + 4 + 4 + 4, the middle 4 being the union of the flag bits and the
        // value word. This size is validated by Windows on every call.
        Assert.Equal(32, Marshal.SizeOf<AdvancedColor>());
    }

    [Fact]
    public void TheAdvancedColourBlockWithoutItsUnionIsFourBytesShort()
    {
        Assert.Equal(28, Marshal.SizeOf<AdvancedColorWithoutUnion>());
    }

    [Fact]
    public void TheAdvancedColourFieldsLandWhereWindowsExpectsThem()
    {
        Assert.Equal(20, Marshal.OffsetOf<AdvancedColor>(nameof(AdvancedColor.value)));
        Assert.Equal(24, Marshal.OffsetOf<AdvancedColor>(nameof(AdvancedColor.colorEncoding)));
        Assert.Equal(28, Marshal.OffsetOf<AdvancedColor>(nameof(AdvancedColor.bitsPerColorChannel)));
    }

    [Fact]
    public void WithoutTheUnionTheFlagsAreReadAsAColourEncoding()
    {
        // The second half of that bug: the field Windows calls value is the one the
        // old code was reading as colorEncoding.
        int encodingOffset = (int)Marshal.OffsetOf<AdvancedColorWithoutUnion>(
            nameof(AdvancedColorWithoutUnion.colorEncoding));
        int flagsOffset = (int)Marshal.OffsetOf<AdvancedColor>(nameof(AdvancedColor.value));

        Assert.Equal(encodingOffset, flagsOffset);
    }

    [Fact]
    public void EveryHeaderCarriesItsOwnStructsSize()
    {
        // The mechanism by which both bugs turned into refused calls rather than
        // wrong answers: whatever SizeOf returns goes into the header, and Windows
        // compares it against what it expects for that type.
        Header pathPacket = new()
        {
            type = 9,
            size = (uint)Marshal.SizeOf<AdvancedColor>(),
            adapterId = new Luid(),
            id = 264
        };

        Assert.Equal(32u, pathPacket.size);
    }

    [Fact]
    public void TheModeIndexIsTheSourcesOwnFieldNotAPathsOwn()
    {
        // Recorded because the app read path.sourceModeInfoIdx, which only existed
        // because of the phantom field. Where does it actually live? Inside the
        // source union, which the SDK documents as desktopModeInfoIdx /
        // sourceModeInfoIdx when the path is virtual-aware and plain modeInfoIdx
        // when it is not. It is a field of SourceInfo, never of PATH_INFO.
        Assert.Equal(12, (int)Marshal.OffsetOf<SourceInfo>(nameof(SourceInfo.modeInfoIdx)));
        Assert.False(
            typeof(PathInfo).GetField("sourceModeInfoIdx") != null,
            "PATH_INFO must not declare sourceModeInfoIdx; it belongs to the unions "
            + "inside the source and target info structs.");
    }
}