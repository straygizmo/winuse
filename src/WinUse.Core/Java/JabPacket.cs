using System.Buffers.Binary;
using System.Text;

namespace WinUse.Core.Java;

/// <summary>Field types of a JAB packet struct, with the C size and alignment each one has in the given <see cref="JabLayout"/>.</summary>
internal enum JabFieldKind
{
    /// <summary><c>jint</c>, <c>long</c>, <c>BOOL</c>, <c>int</c>, <c>ABHWND64</c>: 4 bytes.</summary>
    I32,
    /// <summary><c>jboolean</c>: 1 byte.</summary>
    Bool8,
    /// <summary><c>JOBJECT64</c>: 8 bytes (wide) or 4 bytes (legacy).</summary>
    Obj,
    /// <summary><c>wchar_t[Count]</c>.</summary>
    WChars,
    /// <summary><c>char[Count]</c>.</summary>
    Chars,
    /// <summary>An opaque nested struct of <c>Count</c> bytes with the given alignment.</summary>
    Blob,
}

internal readonly record struct JabField(JabFieldKind Kind, int Count = 1, int Align = 0);

/// <summary>
/// The byte layout of one packet struct in one wire format, computed with C's natural-alignment rules: each field starts
/// at a multiple of its alignment and the struct is padded to a multiple of its largest alignment. On the wire the struct
/// follows the 4-byte <c>PackageType</c> without any padding, so <see cref="BufferSize"/> is <c>4 + StructSize</c>.
/// </summary>
internal sealed class JabPacketLayout
{
    private readonly JabField[] _fields;
    private readonly int[] _offsets;
    private readonly int[] _sizes;

    private JabPacketLayout(int typeId, JabLayout layout, JabField[] fields, int[] offsets, int[] sizes, int structSize)
    {
        TypeId = typeId;
        Layout = layout;
        _fields = fields;
        _offsets = offsets;
        _sizes = sizes;
        StructSize = structSize;
    }

    public int TypeId { get; }
    public JabLayout Layout { get; }
    public int StructSize { get; }
    public int BufferSize => 4 + StructSize;
    public int FieldCount => _fields.Length;

    /// <summary>Offset of the field inside the struct (add 4 for the position in the buffer).</summary>
    public int Offset(int field) => _offsets[field];
    public int Size(int field) => _sizes[field];
    public JabField Field(int field) => _fields[field];

    public static JabPacketLayout Build(int typeId, JabLayout layout, params JabField[] fields)
    {
        var offsets = new int[fields.Length];
        var sizes = new int[fields.Length];
        var pos = 0;
        var maxAlign = 1;
        for (var i = 0; i < fields.Length; i++)
        {
            var (size, align) = Measure(fields[i], layout);
            pos = AlignUp(pos, align);
            offsets[i] = pos;
            sizes[i] = size;
            pos += size;
            maxAlign = Math.Max(maxAlign, align);
        }
        return new JabPacketLayout(typeId, layout, fields, offsets, sizes, AlignUp(pos, maxAlign));
    }

    public static int ObjSize(JabLayout layout) => layout == JabLayout.Wide ? 8 : 4;

    private static (int Size, int Align) Measure(JabField f, JabLayout layout) => f.Kind switch
    {
        JabFieldKind.I32 => (4, 4),
        JabFieldKind.Bool8 => (1, 1),
        JabFieldKind.Obj => (ObjSize(layout), ObjSize(layout)),
        JabFieldKind.WChars => (f.Count * 2, 2),
        JabFieldKind.Chars => (f.Count, 1),
        JabFieldKind.Blob => (f.Count, f.Align > 0 ? f.Align : throw new ArgumentException("Blob fields need an alignment.")),
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    private static int AlignUp(int value, int align) => (value + align - 1) / align * align;
}

/// <summary>One packet buffer: the type id followed by the struct, read and written by field index through its layout.</summary>
internal sealed class JabPacket
{
    public JabPacket(JabPacketLayout layout)
    {
        Layout = layout;
        Buffer = new byte[layout.BufferSize];
        BinaryPrimitives.WriteInt32LittleEndian(Buffer, layout.TypeId);
    }

    public JabPacketLayout Layout { get; }
    public byte[] Buffer { get; }
    public int Size => Buffer.Length;

    private int At(int field) => 4 + Layout.Offset(field);
    private Span<byte> SpanOf(int field) => Buffer.AsSpan(At(field), Layout.Size(field));

    public void WriteI32(int field, int value) => BinaryPrimitives.WriteInt32LittleEndian(SpanOf(field), value);

    public int ReadI32(int field) => BinaryPrimitives.ReadInt32LittleEndian(SpanOf(field));

    public bool ReadBool8(int field) => Buffer[At(field)] != 0;

    /// <summary>Writes a <c>JOBJECT64</c>: all 64 bits in the wide format, the low 32 bits in the legacy one.</summary>
    public void WriteObj(int field, long value)
    {
        if (Layout.Layout == JabLayout.Wide) BinaryPrimitives.WriteInt64LittleEndian(SpanOf(field), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(SpanOf(field), unchecked((uint)value));
    }

    /// <summary>Reads a <c>JOBJECT64</c>; a legacy handle is a 32-bit pointer and comes back zero-extended, never sign-extended.</summary>
    public long ReadObj(int field) => Layout.Layout == JabLayout.Wide
        ? BinaryPrimitives.ReadInt64LittleEndian(SpanOf(field))
        : BinaryPrimitives.ReadUInt32LittleEndian(SpanOf(field));

    /// <summary>Writes a NUL-terminated UTF-16 string, truncated to <c>Count - 1</c> characters.</summary>
    public void WriteWChars(int field, string value)
    {
        var span = SpanOf(field);
        span.Clear();
        var n = Math.Min(value.Length, Layout.Field(field).Count - 1);
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2), value[i]);
    }

    public string ReadWChars(int field) => ReadWCharsAt(Buffer, At(field), Layout.Field(field).Count);

    /// <summary>Writes a NUL-terminated ASCII string, truncated to <c>Count - 1</c> bytes.</summary>
    public void WriteChars(int field, string ascii)
    {
        var span = SpanOf(field);
        span.Clear();
        var n = Math.Min(ascii.Length, Layout.Field(field).Count - 1);
        Encoding.ASCII.GetBytes(ascii.AsSpan(0, n), span);
    }

    public ReadOnlySpan<byte> ReadBlob(int field) => SpanOf(field);

    public Span<byte> BlobSpan(int field) => SpanOf(field);

    /// <summary>Decodes up to <paramref name="count"/> UTF-16 characters at <paramref name="offset"/>, stopping at the first NUL.</summary>
    public static string ReadWCharsAt(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        var chars = new char[count];
        var n = 0;
        while (n < count)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + n * 2));
            if (c == '\0') break;
            chars[n++] = c;
        }
        return new string(chars, 0, n);
    }

    public static int ReadI32At(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset));

    public static void WriteI32At(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset), value);

    /// <summary>Writes a NUL-terminated UTF-16 string into a <paramref name="count"/>-character slot inside a blob.</summary>
    public static void WriteWCharsAt(Span<byte> bytes, int offset, int count, string value)
    {
        bytes.Slice(offset, count * 2).Clear();
        var n = Math.Min(value.Length, count - 1);
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset + i * 2), value[i]);
    }
}
