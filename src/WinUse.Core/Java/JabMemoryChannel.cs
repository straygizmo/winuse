using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// The request/response channel to one JVM, as <c>AccessBridgeJavaVMInstance</c> does it: a named memory-mapped file
/// announced over <c>WM_COPYDATA</c> (the JVM answers "OK?" with "OK!"), then each request copied into the file and
/// signalled with <c>AB_MESSAGE_WAITING</c>; the JVM writes its answer in place and sets the byte after the packet to 1.
/// Dispatcher thread only.
/// </summary>
internal sealed unsafe class JabMemoryChannel : IJabChannel
{
    private const uint SendFlags = NativeMethods.SMTO_BLOCK | NativeMethods.SMTO_NOTIMEOUTIFNOTHUNG;
    private const uint SendTimeoutMs = 4000;
    private static readonly byte[] OkQuery = "OK?\0"u8.ToArray();
    private static readonly byte[] OkAnswer = "OK!"u8.ToArray();

    private readonly nint _ourHwnd;
    private readonly nint _javaHwnd;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly ILogger _log;
    private bool _disposed;

    private JabMemoryChannel(nint ourHwnd, nint javaHwnd, MemoryMappedFile file, MemoryMappedViewAccessor view, ILogger log)
    {
        _ourHwnd = ourHwnd;
        _javaHwnd = javaHwnd;
        _file = file;
        _view = view;
        _log = log;
    }

    /// <summary>The capacity every channel allocates: the largest packet in either format plus the completion byte.</summary>
    public static int Capacity => JabPackets.MaxBufferSize + 8;

    public static string MapName(nint ourHwnd, nint javaHwnd) => $"winuse-jab-{ourHwnd:X}-{javaHwnd:X}";

    /// <summary>Creates the file and performs the handshake; null when the JVM did not answer "OK!".</summary>
    public static JabMemoryChannel? Open(nint ourHwnd, nint javaHwnd, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var name = MapName(ourHwnd, javaHwnd);
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.CreateNew(name, Capacity);
            view = file.CreateViewAccessor(0, Capacity);
            view.WriteArray(0, OkQuery, 0, OkQuery.Length);

            var pkt = JabPackets.New(JabPacketKind.MemoryMappedFileCreated, JabLayout.Wide);
            pkt.WriteI32(JabPackets.MemoryMappedFileCreated.BridgeWindow, (int)ourHwnd);
            pkt.WriteChars(JabPackets.MemoryMappedFileCreated.Filename, name);
            fixed (byte* data = pkt.Buffer)
            {
                var cds = new NativeMethods.COPYDATASTRUCT { dwData = 0, cbData = (uint)pkt.Size, lpData = (nint)data };
                var sent = NativeMethods.SendMessageTimeoutW(javaHwnd, NativeMethods.WM_COPYDATA, ourHwnd, (nint)(&cds), SendFlags, SendTimeoutMs, out _);
                if (sent == 0)
                {
                    log.LogWarning("JAB handshake: JVM window 0x{Hwnd:X} did not take the memory-mapped file announcement", javaHwnd);
                    return null;
                }
            }
            var answer = new byte[OkAnswer.Length];
            view.ReadArray(0, answer, 0, answer.Length);
            if (!answer.AsSpan().SequenceEqual(OkAnswer))
            {
                log.LogWarning("JAB handshake: JVM window 0x{Hwnd:X} answered {Answer} instead of OK!", javaHwnd, Encoding.ASCII.GetString(answer));
                return null;
            }
            var channel = new JabMemoryChannel(ourHwnd, javaHwnd, file, view, log);
            file = null;
            view = null;
            return channel;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "JAB handshake with JVM window 0x{Hwnd:X} failed", javaHwnd);
            return null;
        }
        finally
        {
            view?.Dispose();
            file?.Dispose();
        }
    }

    public bool Exchange(byte[] buffer, int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size + 1 > Capacity) throw new ArgumentOutOfRangeException(nameof(size));
        _view.WriteArray(0, buffer, 0, size);
        _view.Write(size, (byte)0);
        var sent = NativeMethods.SendMessageTimeoutW(_javaHwnd, NativeMethods.AB_MESSAGE_WAITING, _ourHwnd, size, SendFlags, SendTimeoutMs, out _);
        if (sent == 0) return false;
        if (_view.ReadByte(size) != 1) return false;
        _view.ReadArray(0, buffer, 0, size);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Dispose();
        _file.Dispose();
    }
}
