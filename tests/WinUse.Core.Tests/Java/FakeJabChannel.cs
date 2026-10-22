using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

/// <summary>
/// A JVM's end of the channel, in-process: identifies each request by type id and size, hands it to <paramref name="handler"/>
/// as a decoded <see cref="JabPacket"/> and copies the handler's writes back. Like a real JVM it acknowledges a packet of the
/// wrong wire format without touching it (the done flag is set before the size check in <c>JavaAccessBridge.cpp</c>).
/// </summary>
internal sealed class FakeJabChannel(JabLayout layout, Func<JabPacketKind, JabPacket, bool> handler) : IJabChannel
{
    public JabLayout Layout { get; } = layout;
    public List<int> SentTypeIds { get; } = [];
    public JabLayout? LastLayoutSeen { get; private set; }
    public bool Disposed { get; private set; }

    public bool Exchange(byte[] buffer, int size)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeJabChannel));
        var typeId = BitConverter.ToInt32(buffer, 0);
        SentTypeIds.Add(typeId);
        var id = JabPackets.Identify(typeId, size) ?? throw new InvalidOperationException($"unknown packet 0x{typeId:X} of {size} bytes");
        var mine = JabPackets.For(id.Kind, Layout);
        // Packets without an object handle have the same size in both formats; a real JVM cannot tell them apart either.
        LastLayoutSeen = mine.BufferSize == size ? Layout : id.Layout;
        if (mine.BufferSize != size) return true; // size mismatch: the JVM drops it silently
        var p = new JabPacket(mine);
        buffer.AsSpan(0, size).CopyTo(p.Buffer);
        var ok = handler(id.Kind, p);
        p.Buffer.CopyTo(buffer, 0);
        return ok;
    }

    public void Dispose() => Disposed = true;
}
