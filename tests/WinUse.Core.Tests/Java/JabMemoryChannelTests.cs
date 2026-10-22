using System.IO.MemoryMappedFiles;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WinUse.Core.Java;
using WinUse.Core.Windows;
using static WinUse.Core.Windows.NativeMethods;

namespace WinUse.Core.Tests.Java;

/// <summary>
/// The Java side of the channel, in-process and without a JVM: a window that opens the announced memory-mapped file,
/// answers "OK?" with "OK!" and, on <c>AB_MESSAGE_WAITING</c>, marks IsJavaWindow packets true and sets the done byte.
/// Lives on a <see cref="JabDispatcher"/> thread so its messages are pumped.
/// </summary>
internal sealed unsafe class FakeJvmWindow : IDisposable
{
    private const string ClassName = "WinUseFakeJvm";
    private static readonly ConcurrentDictionary<nint, FakeJvmWindow> Windows = new();
    private static readonly object Gate = new();
    private static bool s_registered;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;

    public nint Hwnd { get; private set; }
    /// <summary>When false the fake ignores the announcement, like a JVM that never opened the file.</summary>
    public bool AnswersHandshake { get; init; } = true;
    public List<int> ReceivedTypeIds { get; } = [];
    public string? OpenedMapName { get; private set; }

    public static FakeJvmWindow Create(bool answersHandshake = true)
    {
        lock (Gate)
        {
            if (!s_registered)
            {
                fixed (char* name = ClassName)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
                        hInstance = GetModuleHandleW(null),
                        lpszClassName = (nint)name,
                    };
                    if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassExW failed: " + Marshal.GetLastPInvokeError());
                }
                s_registered = true;
            }
        }
        var w = new FakeJvmWindow { AnswersHandshake = answersHandshake };
        var hwnd = CreateWindowExW((uint)WS_EX_TOOLWINDOW, ClassName, "fake JVM", WS_POPUP, 0, 0, 0, 0, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) throw new InvalidOperationException("CreateWindowExW failed: " + Marshal.GetLastPInvokeError());
        w.Hwnd = hwnd;
        Windows[hwnd] = w;
        return w;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var w))
        {
            try
            {
                if (msg == WM_COPYDATA) { w.OnCopyData((COPYDATASTRUCT*)lParam); return 1; }
                if (msg == AB_MESSAGE_WAITING) { w.OnMessageWaiting((int)lParam); return 0; }
            }
            catch
            {
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnCopyData(COPYDATASTRUCT* cds)
    {
        var data = new ReadOnlySpan<byte>((void*)cds->lpData, (int)cds->cbData);
        var typeId = JabPacket.ReadI32At(data, 0);
        ReceivedTypeIds.Add(typeId);
        if (typeId != 0x11000 || !AnswersHandshake) return;
        var layout = JabPackets.For(JabPacketKind.MemoryMappedFileCreated, JabLayout.Wide);
        var nameBytes = data.Slice(4 + layout.Offset(JabPackets.MemoryMappedFileCreated.Filename), JabPackets.MemoryMappedNameSize);
        var end = nameBytes.IndexOf((byte)0);
        OpenedMapName = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]);
        _file = MemoryMappedFile.OpenExisting(OpenedMapName);
        _view = _file.CreateViewAccessor();
        var q = new byte[3];
        _view.ReadArray(0, q, 0, 3);
        if (Encoding.ASCII.GetString(q) == "OK?") _view.WriteArray(0, "OK!"u8.ToArray(), 0, 3);
    }

    private void OnMessageWaiting(int size)
    {
        if (_view is null) return;
        var buf = new byte[size];
        _view.ReadArray(0, buf, 0, size);
        var typeId = JabPacket.ReadI32At(buf, 0);
        ReceivedTypeIds.Add(typeId);
        if (typeId == 0x11)
        {
            var layout = JabPackets.For(JabPacketKind.IsJavaWindow, JabLayout.Wide);
            buf[4 + layout.Offset(JabPackets.IsJavaWindow.Result)] = 1;
            _view.WriteArray(0, buf, 0, size);
        }
        _view.Write(size, (byte)1);
    }

    public void Dispose()
    {
        Windows.TryRemove(Hwnd, out _);
        if (Hwnd != 0) DestroyWindow(Hwnd);
        Hwnd = 0;
        _view?.Dispose();
        _file?.Dispose();
    }
}

public class JabMemoryChannelTests
{
    private const nint OurHwnd = 0x5151;

    [Fact]
    public async Task Open_ShakesHands_AndExchange_RoundTripsAPacket()
    {
        FakeJvmWindow? jvm = null;
        using var d = new JabDispatcher(() => jvm = FakeJvmWindow.Create());
        try
        {
            using var channel = JabMemoryChannel.Open(OurHwnd, jvm!.Hwnd);
            Assert.NotNull(channel);
            Assert.Equal(JabMemoryChannel.MapName(OurHwnd, jvm.Hwnd), jvm.OpenedMapName);

            var p = JabPackets.New(JabPacketKind.IsJavaWindow, JabLayout.Wide);
            p.WriteI32(JabPackets.IsJavaWindow.Window, 0x77);
            Assert.True(channel!.Exchange(p.Buffer, p.Size));
            Assert.True(p.ReadBool8(JabPackets.IsJavaWindow.Result));
            Assert.Equal([0x11000, 0x11], jvm.ReceivedTypeIds);
        }
        finally
        {
            await d.RunAsync(() => jvm?.Dispose());
        }
    }

    [Fact]
    public async Task Open_ReturnsNull_WhenTheJvmDoesNotAnswerOk()
    {
        FakeJvmWindow? jvm = null;
        using var d = new JabDispatcher(() => jvm = FakeJvmWindow.Create(answersHandshake: false));
        try
        {
            Assert.Null(JabMemoryChannel.Open(OurHwnd, jvm!.Hwnd));
        }
        finally
        {
            await d.RunAsync(() => jvm?.Dispose());
        }
    }

    [Fact]
    public async Task Open_ReturnsNull_WhenTheMapNameIsTaken()
    {
        FakeJvmWindow? jvm = null;
        using var d = new JabDispatcher(() => jvm = FakeJvmWindow.Create());
        try
        {
            using var squatter = MemoryMappedFile.CreateNew(JabMemoryChannel.MapName(OurHwnd, jvm!.Hwnd), 1024);
            Assert.Null(JabMemoryChannel.Open(OurHwnd, jvm.Hwnd));
            Assert.Empty(jvm.ReceivedTypeIds);
        }
        finally
        {
            await d.RunAsync(() => jvm?.Dispose());
        }
    }

    [Fact]
    public void Exchange_ReturnsFalse_WhenTheWindowIsGone()
    {
        FakeJvmWindow? jvm = null;
        nint hwnd;
        using (var d = new JabDispatcher(() => jvm = FakeJvmWindow.Create()))
        {
            hwnd = jvm!.Hwnd;
            using var channel = JabMemoryChannel.Open(OurHwnd, hwnd);
            Assert.NotNull(channel);
            d.RunAsync(() => jvm.Dispose()).GetAwaiter().GetResult();
            var p = JabPackets.New(JabPacketKind.IsJavaWindow, JabLayout.Wide);
            Assert.False(channel!.Exchange(p.Buffer, p.Size));
        }
    }
}
