using System.Collections.Concurrent;
using static WinUse.Core.Java.JabFieldKind;

namespace WinUse.Core.Java;

/// <summary>The packets winuse sends, named after the <c>IJabAccess</c> call that uses them.</summary>
public enum JabPacketKind
{
    IsJavaWindow, GetContextFromHwnd, GetContextInfo, GetChild, GetParent, ReleaseObject, GetVersion, GetActions, DoActions,
    GetTextInfo, GetTextRange, GetValue, AddSelection, ClearSelection, GetSelection, GetSelectionCount, SetText, GetVirtualName,
    RequestFocus, MemoryMappedFileCreated, GetTableInfo, GetTableCellInfo, GetTableRowHeader, GetTableColumnHeader,
}

/// <summary>
/// Packet schemas from OpenJDK's <c>AccessBridgePackages.h</c>: type ids and field lists. Field indexes are exposed as constants
/// of the nested classes so callers never count fields themselves.
/// </summary>
internal static class JabPackets
{
    public const int MaxStringSize = 1024;
    public const int ShortStringSize = 256;
    public const int MaxBufferSizeChars = 10240;
    public const int MaxActionInfo = 256;
    public const int MaxActionsToDo = 32;
    public const int MemoryMappedNameSize = 255;
    /// <summary><c>AccessibleContextInfo</c>: six fixed strings (1024, 1024, 256, 256, 256, 256 wchar) and eleven <c>jint</c>s.</summary>
    public const int AccessibleContextInfoSize = (MaxStringSize * 2 + ShortStringSize * 4) * 2 + 11 * 4;
    /// <summary><c>AccessibleActions</c>: <c>jint actionsCount</c> + 256 names of 256 wchar.</summary>
    public const int AccessibleActionsSize = 4 + MaxActionInfo * ShortStringSize * 2;
    /// <summary><c>AccessibleActionsToDo</c>: <c>jint actionsCount</c> + 32 names of 256 wchar.</summary>
    public const int AccessibleActionsToDoSize = 4 + MaxActionsToDo * ShortStringSize * 2;
    /// <summary>Sent by the JVM over WM_COPYDATA when it shuts down (<c>cJavaShutdownPackage</c>, payload <c>long vmID</c>).</summary>
    public const int JavaShutdownPackageType = 0x1010;

    public static class IsJavaWindow { public const int Window = 0, Result = 1; }
    public static class GetContextFromHwnd { public const int Window = 0, VmId = 1, Result = 2; }
    public static class GetContextInfo { public const int VmId = 0, Ac = 1, Info = 2; }
    public static class GetChild { public const int VmId = 0, Ac = 1, ChildIndex = 2, Result = 3; }
    public static class GetParent { public const int VmId = 0, Ac = 1, Result = 2; }
    public static class ReleaseObject { public const int VmId = 0, Object = 1; }
    public static class GetVersion { public const int VmId = 0, VmVersion = 1, JavaClassVersion = 2, JavaDllVersion = 3, WinDllVersion = 4; }
    public static class GetActions { public const int VmId = 0, Ac = 1, Actions = 2; }
    public static class DoActions { public const int VmId = 0, Ac = 1, ToDo = 2, Result = 3, Failure = 4; }
    public static class GetTextInfo { public const int VmId = 0, Ac = 1, X = 2, Y = 3, CharCount = 4, CaretIndex = 5, IndexAtPoint = 6; }
    public static class GetTextRange { public const int VmId = 0, Ac = 1, Start = 2, End = 3, Text = 4; }
    public static class GetValue { public const int VmId = 0, Ac = 1, Value = 2; }
    public static class AddSelection { public const int VmId = 0, Ac = 1, Index = 2; }
    public static class ClearSelection { public const int VmId = 0, Ac = 1; }
    public static class GetSelection { public const int VmId = 0, Ac = 1, Index = 2, Result = 3; }
    public static class GetSelectionCount { public const int VmId = 0, Ac = 1, Count = 2; }
    public static class SetText { public const int VmId = 0, Ac = 1, Text = 2, Result = 3; }
    public static class GetVirtualName { public const int VmId = 0, Ac = 1, Name = 2, Len = 3; }
    public static class RequestFocus { public const int VmId = 0, Ac = 1; }
    public static class MemoryMappedFileCreated { public const int BridgeWindow = 0, Filename = 1; }
    /// <summary>Also the layout of <see cref="JabPacketKind.GetTableRowHeader"/> / <see cref="JabPacketKind.GetTableColumnHeader"/>: the nested <c>AccessibleTableInfo</c> flattened.</summary>
    public static class GetTableInfo { public const int VmId = 0, Ac = 1, Caption = 2, Summary = 3, RowCount = 4, ColumnCount = 5, Context = 6, Table = 7; }
    /// <summary>The nested <c>AccessibleTableCellInfo</c> flattened.</summary>
    public static class GetTableCellInfo { public const int VmId = 0, Table = 1, Row = 2, Column = 3, Context = 4, Index = 5, CellRow = 6, CellColumn = 7, RowExtent = 8, ColumnExtent = 9, Selected = 10; }

    /// <summary>Byte offsets inside the <c>AccessibleContextInfo</c> blob (same in both formats).</summary>
    public static class ContextInfo
    {
        public const int Name = 0;
        public const int Description = Name + MaxStringSize * 2;
        public const int Role = Description + MaxStringSize * 2;
        public const int RoleEn = Role + ShortStringSize * 2;
        public const int States = RoleEn + ShortStringSize * 2;
        public const int StatesEn = States + ShortStringSize * 2;
        public const int IndexInParent = StatesEn + ShortStringSize * 2;
        public const int ChildrenCount = IndexInParent + 4;
        public const int X = ChildrenCount + 4;
        public const int Y = X + 4;
        public const int Width = Y + 4;
        public const int Height = Width + 4;
        public const int AccessibleComponent = Height + 4;
        public const int AccessibleAction = AccessibleComponent + 4;
        public const int AccessibleSelection = AccessibleAction + 4;
        public const int AccessibleText = AccessibleSelection + 4;
        public const int AccessibleInterfaces = AccessibleText + 4;
    }

    /// <summary>Inside <c>AccessibleActions</c> / <c>AccessibleActionsToDo</c>: the count, then names of 256 wchar each.</summary>
    public static class Actions
    {
        public const int Count = 0;
        public static int Name(int i) => 4 + i * ShortStringSize * 2;
    }

    private static readonly ConcurrentDictionary<(JabPacketKind, JabLayout), JabPacketLayout> Cache = new();

    public static JabPacketLayout For(JabPacketKind kind, JabLayout layout) => Cache.GetOrAdd((kind, layout), k => Build(k.Item1, k.Item2));

    public static JabPacket New(JabPacketKind kind, JabLayout layout) => new(For(kind, layout));

    /// <summary>The largest packet in either format, which sizes the memory-mapped file shared with each JVM.</summary>
    public static int MaxBufferSize { get; } = Enum.GetValues<JabPacketKind>()
        .SelectMany(k => new[] { For(k, JabLayout.Wide).BufferSize, For(k, JabLayout.Legacy).BufferSize }).Max();

    /// <summary>The kind whose type id and struct size match a received buffer, for tests and diagnostics; null when none does.</summary>
    public static (JabPacketKind Kind, JabLayout Layout)? Identify(int typeId, int bufferSize)
    {
        foreach (var kind in Enum.GetValues<JabPacketKind>())
            foreach (var layout in new[] { JabLayout.Wide, JabLayout.Legacy })
            {
                var l = For(kind, layout);
                if (l.TypeId == typeId && l.BufferSize == bufferSize) return (kind, layout);
            }
        return null;
    }

    private static JabPacketLayout Build(JabPacketKind kind, JabLayout layout)
    {
        static JabField I() => new(I32);
        static JabField O() => new(Obj);
        static JabField W(int n) => new(WChars, n);
        static JabField Blob(int n) => new(JabFieldKind.Blob, n, 4);
        return kind switch
        {
            JabPacketKind.IsJavaWindow => JabPacketLayout.Build(0x11, layout, I(), new JabField(Bool8)),
            JabPacketKind.GetContextFromHwnd => JabPacketLayout.Build(0x10, layout, I(), I(), O()),
            JabPacketKind.GetContextInfo => JabPacketLayout.Build(0x102, layout, I(), O(), Blob(AccessibleContextInfoSize)),
            JabPacketKind.GetChild => JabPacketLayout.Build(0x103, layout, I(), O(), I(), O()),
            JabPacketKind.GetParent => JabPacketLayout.Build(0x104, layout, I(), O(), O()),
            JabPacketKind.ReleaseObject => JabPacketLayout.Build(0x1, layout, I(), O()),
            JabPacketKind.GetVersion => JabPacketLayout.Build(0x2, layout, I(), W(ShortStringSize), W(ShortStringSize), W(ShortStringSize), W(ShortStringSize)),
            JabPacketKind.GetActions => JabPacketLayout.Build(0x1502, layout, I(), O(), Blob(AccessibleActionsSize)),
            JabPacketKind.DoActions => JabPacketLayout.Build(0x1503, layout, I(), O(), Blob(AccessibleActionsToDoSize), I(), I()),
            JabPacketKind.GetTextInfo => JabPacketLayout.Build(0x200, layout, I(), O(), I(), I(), I(), I(), I()),
            JabPacketKind.GetTextRange => JabPacketLayout.Build(0x206, layout, I(), O(), I(), I(), W(MaxBufferSizeChars)),
            JabPacketKind.GetValue => JabPacketLayout.Build(0x300, layout, I(), O(), W(ShortStringSize)),
            JabPacketKind.AddSelection => JabPacketLayout.Build(0x400, layout, I(), O(), I()),
            JabPacketKind.ClearSelection => JabPacketLayout.Build(0x401, layout, I(), O()),
            JabPacketKind.GetSelection => JabPacketLayout.Build(0x402, layout, I(), O(), I(), O()),
            JabPacketKind.GetSelectionCount => JabPacketLayout.Build(0x403, layout, I(), O(), I()),
            JabPacketKind.SetText => JabPacketLayout.Build(0x1600, layout, I(), O(), W(MaxStringSize), I()),
            JabPacketKind.GetVirtualName => JabPacketLayout.Build(0x1700, layout, I(), O(), W(MaxStringSize), I()),
            JabPacketKind.RequestFocus => JabPacketLayout.Build(0x1701, layout, I(), O()),
            JabPacketKind.MemoryMappedFileCreated => JabPacketLayout.Build(0x11000, layout, I(), new JabField(Chars, MemoryMappedNameSize)),
            // AccessibleTableInfo { JOBJECT64 caption, summary; jint rowCount, columnCount; JOBJECT64 accessibleContext, accessibleTable }
            // starts at an Obj-aligned offset in both formats, so flattening it keeps every offset and the struct size.
            JabPacketKind.GetTableInfo => JabPacketLayout.Build(0x1200, layout, I(), O(), O(), O(), I(), I(), O(), O()),
            // AccessibleTableCellInfo { JOBJECT64 accessibleContext; jint index, row, column, rowExtent, columnExtent; jboolean isSelected }
            JabPacketKind.GetTableCellInfo => JabPacketLayout.Build(0x1201, layout, I(), O(), I(), I(), O(), I(), I(), I(), I(), I(), new JabField(Bool8)),
            JabPacketKind.GetTableRowHeader => JabPacketLayout.Build(0x1202, layout, I(), O(), O(), O(), I(), I(), O(), O()),
            JabPacketKind.GetTableColumnHeader => JabPacketLayout.Build(0x1203, layout, I(), O(), O(), O(), I(), I(), O(), O()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
