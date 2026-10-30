using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

/// <summary>Packet struct sizes and offsets must match the C header byte for byte, or the JVM silently drops the packet.</summary>
public class JabPacketTests
{
    public static IEnumerable<object[]> Sizes() =>
    [
        [JabPacketKind.IsJavaWindow, 8, 8], [JabPacketKind.GetContextFromHwnd, 16, 12], [JabPacketKind.GetContextInfo, 6208, 6196],
        [JabPacketKind.GetChild, 32, 16], [JabPacketKind.GetParent, 24, 12], [JabPacketKind.ReleaseObject, 16, 8],
        [JabPacketKind.GetVersion, 2052, 2052], [JabPacketKind.GetActions, 131096, 131084], [JabPacketKind.DoActions, 16416, 16404],
        [JabPacketKind.GetTextInfo, 40, 28], [JabPacketKind.GetTextRange, 20504, 20496], [JabPacketKind.GetValue, 528, 520],
        [JabPacketKind.AddSelection, 24, 12], [JabPacketKind.ClearSelection, 16, 8], [JabPacketKind.GetSelection, 32, 16],
        [JabPacketKind.GetSelectionCount, 24, 12], [JabPacketKind.SetText, 2072, 2060], [JabPacketKind.GetVirtualName, 2072, 2060],
        [JabPacketKind.RequestFocus, 16, 8], [JabPacketKind.MemoryMappedFileCreated, 260, 260],
        // vmID + ac + AccessibleTableInfo { 2 objects, 2 ints, 2 objects }: 8 + 8 + 40 wide, 4 + 4 + 24 legacy
        [JabPacketKind.GetTableInfo, 56, 32], [JabPacketKind.GetTableRowHeader, 56, 32], [JabPacketKind.GetTableColumnHeader, 56, 32],
        // vmID + table + row + column + AccessibleTableCellInfo { object, 5 ints, jboolean }: 24 + 32 wide, 16 + 28 legacy
        [JabPacketKind.GetTableCellInfo, 56, 44],
    ];

    [Theory, MemberData(nameof(Sizes))]
    public void StructSize_MatchesTheCHeader(JabPacketKind kind, int wide, int legacy)
    {
        Assert.Equal(wide, JabPackets.For(kind, JabLayout.Wide).StructSize);
        Assert.Equal(legacy, JabPackets.For(kind, JabLayout.Legacy).StructSize);
        Assert.Equal(4 + wide, JabPackets.For(kind, JabLayout.Wide).BufferSize);
    }

    [Fact]
    public void Offsets_FollowCAlignment()
    {
        var wide = JabPackets.For(JabPacketKind.GetChild, JabLayout.Wide);
        Assert.Equal(0, wide.Offset(JabPackets.GetChild.VmId));
        Assert.Equal(8, wide.Offset(JabPackets.GetChild.Ac));
        Assert.Equal(16, wide.Offset(JabPackets.GetChild.ChildIndex));
        Assert.Equal(24, wide.Offset(JabPackets.GetChild.Result));
        var legacy = JabPackets.For(JabPacketKind.GetChild, JabLayout.Legacy);
        Assert.Equal(4, legacy.Offset(JabPackets.GetChild.Ac));
        Assert.Equal(12, legacy.Offset(JabPackets.GetChild.Result));
        Assert.Equal(4, JabPackets.For(JabPacketKind.IsJavaWindow, JabLayout.Wide).Offset(JabPackets.IsJavaWindow.Result));
    }

    [Fact]
    public void TableCellInfo_NestedStructOffsets_MatchTheCHeader()
    {
        var wide = JabPackets.For(JabPacketKind.GetTableCellInfo, JabLayout.Wide);
        Assert.Equal(24, wide.Offset(JabPackets.GetTableCellInfo.Context));
        Assert.Equal(32, wide.Offset(JabPackets.GetTableCellInfo.Index));
        Assert.Equal(52, wide.Offset(JabPackets.GetTableCellInfo.Selected));
        var legacy = JabPackets.For(JabPacketKind.GetTableCellInfo, JabLayout.Legacy);
        Assert.Equal(16, legacy.Offset(JabPackets.GetTableCellInfo.Context));
        Assert.Equal(40, legacy.Offset(JabPackets.GetTableCellInfo.Selected));
        var info = JabPackets.For(JabPacketKind.GetTableInfo, JabLayout.Wide);
        Assert.Equal(32, info.Offset(JabPackets.GetTableInfo.RowCount));
        Assert.Equal(48, info.Offset(JabPackets.GetTableInfo.Table));
    }

    [Fact]
    public void TypeId_IsWrittenFirst()
    {
        var p = JabPackets.New(JabPacketKind.GetActions, JabLayout.Legacy);
        Assert.Equal(0x1502, BitConverter.ToInt32(p.Buffer, 0));
        Assert.Equal(p.Buffer.Length, p.Size);
    }

    [Fact]
    public void ReadObj_Legacy_IsUnsigned()
    {
        var p = JabPackets.New(JabPacketKind.GetChild, JabLayout.Legacy);
        BitConverter.GetBytes(unchecked((int)0x8000_0001)).CopyTo(p.Buffer, 4 + 12);
        Assert.Equal(0x8000_0001L, p.ReadObj(JabPackets.GetChild.Result));
    }

    [Fact]
    public void WriteObj_Legacy_WritesLow32Bits_AndWide_Writes64()
    {
        var legacy = JabPackets.New(JabPacketKind.ReleaseObject, JabLayout.Legacy);
        legacy.WriteObj(JabPackets.ReleaseObject.Object, 0x8000_0001L);
        Assert.Equal(unchecked((int)0x8000_0001), BitConverter.ToInt32(legacy.Buffer, 4 + 4));
        var wide = JabPackets.New(JabPacketKind.ReleaseObject, JabLayout.Wide);
        wide.WriteObj(JabPackets.ReleaseObject.Object, 0x1_2345_6789L);
        Assert.Equal(0x1_2345_6789L, BitConverter.ToInt64(wide.Buffer, 4 + 8));
    }

    [Fact]
    public void WriteWChars_TruncatesAndTerminates()
    {
        var p = JabPackets.New(JabPacketKind.GetValue, JabLayout.Wide);
        p.WriteWChars(JabPackets.GetValue.Value, new string('x', 300));
        Assert.Equal(new string('x', 255), p.ReadWChars(JabPackets.GetValue.Value));
    }

    [Fact]
    public void ReadWChars_StopsAtNulOrEnd()
    {
        var p = JabPackets.New(JabPacketKind.GetValue, JabLayout.Wide);
        p.WriteWChars(JabPackets.GetValue.Value, "ab");
        Assert.Equal("ab", p.ReadWChars(JabPackets.GetValue.Value));
        var off = 4 + p.Layout.Offset(JabPackets.GetValue.Value);
        for (var i = 0; i < 256; i++) BitConverter.GetBytes('z').CopyTo(p.Buffer, off + i * 2); // no NUL at all
        Assert.Equal(new string('z', 256), p.ReadWChars(JabPackets.GetValue.Value));
    }

    [Fact]
    public void WriteChars_IsAsciiNulTerminated()
    {
        var p = JabPackets.New(JabPacketKind.MemoryMappedFileCreated, JabLayout.Wide);
        p.WriteChars(JabPackets.MemoryMappedFileCreated.Filename, "winuse-jab-1-2");
        var off = 4 + p.Layout.Offset(JabPackets.MemoryMappedFileCreated.Filename);
        Assert.Equal((byte)'w', p.Buffer[off]);
        Assert.Equal(0, p.Buffer[off + 14]);
    }

    [Fact]
    public void ContextInfoBlob_OffsetsAreFixed()
    {
        Assert.Equal(6188, JabPackets.AccessibleContextInfoSize);
        Assert.Equal(4608, JabPackets.ContextInfo.RoleEn);
        Assert.Equal(5632, JabPackets.ContextInfo.StatesEn);
        Assert.Equal(6148, JabPackets.ContextInfo.ChildrenCount);
        Assert.Equal(6184, JabPackets.ContextInfo.AccessibleInterfaces);
    }

    [Fact]
    public void MaxBufferSize_IsTheWideGetActionsPacket() => Assert.Equal(4 + 131096, JabPackets.MaxBufferSize);
}
