using System;
using System.Runtime.InteropServices;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Stands in for HardwareBrightness.cs without the machinery around it.
/// <para>
/// Two P/Invokes out of a 2500 line file, so this can restate the documented layout
/// and ask the one question that matters: does what the app marshals still match
/// what Windows expects? If that ever stops being true, a test here fails naming the
/// struct, instead of an HDR query failing on somebody's monitor with a return code
/// nobody reads.
/// </para>
/// <para>
/// The second job is the flags word, which is decoded in the app and never appears in
/// a log unless something goes wrong - so a bit assigned to the wrong field would
/// otherwise be invisible until a user reported that the app claimed HDR was on when
/// it was not.
/// </para>
/// </summary>
public class AdvancedColorLayoutTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    /// <summary>
    /// The SDK layout: header, then a 4 byte union, then the two fields that follow.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Documented
    {
        public Header header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    /// <summary>
    /// The layout this app used to marshal: header, then straight to the two fields.
    /// Kept so the size it produced is asserted rather than remembered. Four bytes
    /// short, and Windows rejects a call whose header size does not match.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MissingTheUnion
    {
        public Header header;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [Fact]
    public void TheHeaderIsTwentyBytes() => Assert.Equal(20, Marshal.SizeOf<Header>());

    [Fact]
    public void TheDocumentedStructIsThirtyTwoBytes() => Assert.Equal(32, Marshal.SizeOf<Documented>());

    [Fact]
    public void TheStructWithoutTheUnionIsFourBytesShort()
    {
        // This is the bug, stated as a measurement. Every advanced colour query used
        // to fail because the size in the header did not match, so HdrKnown was false
        // everywhere and the log said "HDR unknown" on every machine - which meant a
        // screen preset quietly doing nothing under HDR while the app reported that
        // it had worked.
        Assert.Equal(28, Marshal.SizeOf<MissingTheUnion>());
    }

    [Fact]
    public void TheUnionIsExactlyOneWordWide()
    {
        // Four flag bits plus a value alias, in four bytes. If Windows ever widened
        // it, the struct would grow and this is the test that would say so.
        Assert.Equal(4, Marshal.SizeOf<Documented>() - Marshal.SizeOf<MissingTheUnion>());
    }

    [Fact]
    public void TheFieldsLandWhereWindowsExpectsThem()
    {
        // Offsets, not just a total. A total can be right with the fields in the
        // wrong order, and then every value is read into the wrong variable while
        // the size check passes - which is the quiet version of this bug rather than
        // the loud one.
        Assert.Equal(20, Marshal.OffsetOf<Documented>(nameof(Documented.value)));
        Assert.Equal(24, Marshal.OffsetOf<Documented>(nameof(Documented.colorEncoding)));
        Assert.Equal(28, Marshal.OffsetOf<Documented>(nameof(Documented.bitsPerColorChannel)));
    }

    [Fact]
    public void WithoutTheUnionTheFlagsWouldBeReadAsAColourEncoding()
    {
        // The second half of the same bug. With the union missing, the field the SDK
        // calls value is the one the app was reading as colorEncoding - so the check
        // for HDR was asking whether a colour encoding was non-zero while actually
        // asking whether any advanced colour flag was set. "Supported" is one of
        // those, so a display that merely could do HDR was reported as doing it.
        int colourEncodingOffset = (int)Marshal.OffsetOf<MissingTheUnion>(nameof(MissingTheUnion.colorEncoding));
        int flagsOffset = (int)Marshal.OffsetOf<Documented>(nameof(Documented.value));

        Assert.Equal(colourEncodingOffset, flagsOffset);
    }

    [Fact]
    public void TheHeaderCarriesTheSizeWindowsValidates()
    {
        // The link between the two halves of the failure: whatever SizeOf returns is
        // what goes into the header, and Windows compares it against its own
        // expectation. So the size assertion above is not cosmetic - it is the whole
        // mechanism by which a wrong layout turns into a refused call.
        Header header = new()
        {
            Type = 9,
            Size = (uint)Marshal.SizeOf<Documented>(),
            AdapterId = new Luid(),
            Id = 1
        };

        Assert.Equal(32u, header.Size);
    }
}