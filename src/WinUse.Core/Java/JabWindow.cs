using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Windows;
using static WinUse.Core.Windows.NativeMethods;

namespace WinUse.Core.Java;

/// <summary>
/// The assistive-technology side of the Java Access Bridge handshake: a hidden top-level window that broadcasts
/// <c>AccessBridge-FromWindows-Hello</c>, receives <c>AccessBridge-FromJava-Hello</c> from every JVM (wParam = the JVM's
/// bridge window, lParam = its vmID) and hands them to <see cref="JabClient.Connect"/>. It also hears a JVM leave
/// (<c>AB_DLL_GOING_AWAY</c>, or a <c>cJavaShutdownPackage</c> over <c>WM_COPYDATA</c>) and ignores the event packets JVMs send.
/// Must be created, used and disposed on the <see cref="JabDispatcher"/> thread, which pumps its messages. The window is a
/// real top-level window because <c>HWND_BROADCAST</c> does not reach message-only windows.
/// </summary>
internal sealed unsafe class JabWindow : IDisposable
{
    private const string ClassName = "WinUseJabClient";
    private static readonly ConcurrentDictionary<nint, JabWindow> Windows = new();
    private static readonly object ClassGate = new();
    private static bool s_classRegistered;
    private static uint s_fromJavaHello, s_fromWindowsHello;

    private readonly JabClient _client;
    private readonly ILogger _log;

    private JabWindow(JabClient client, ILogger log)
    {
        _client = client;
        _log = log;
    }

    public nint Hwnd { get; private set; }

    public static uint FromJavaHello
    {
        get { EnsureMessages(); return s_fromJavaHello; }
    }

    public static uint FromWindowsHello
    {
        get { EnsureMessages(); return s_fromWindowsHello; }
    }

    /// <summary>Creates the window on the calling (dispatcher) thread and says hello to every JVM.</summary>
    public static JabWindow Create(JabClient client, ILogger? log)
    {
        ArgumentNullException.ThrowIfNull(client);
        EnsureClass();
        var w = new JabWindow(client, log ?? NullLogger.Instance);
        var hwnd = CreateWindowExW((uint)WS_EX_TOOLWINDOW, ClassName, "winuse Java Access Bridge client", WS_POPUP, 0, 0, 0, 0, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW for the Java Access Bridge client failed");
        w.Hwnd = hwnd;
        Windows[hwnd] = w;
        if (!PostMessageW(HWND_BROADCAST, FromWindowsHello, hwnd, 0))
            w._log.LogWarning("JAB hello broadcast failed (error {Error})", Marshal.GetLastPInvokeError());
        return w;
    }

    private static void EnsureMessages()
    {
        if (s_fromJavaHello != 0) return;
        lock (ClassGate)
        {
            if (s_fromJavaHello != 0) return;
            var java = RegisterWindowMessageW(FromJavaHelloMessage);
            var windows = RegisterWindowMessageW(FromWindowsHelloMessage);
            if (java == 0 || windows == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterWindowMessage for the Java Access Bridge failed");
            s_fromWindowsHello = windows;
            s_fromJavaHello = java;
        }
    }

    private static void EnsureClass()
    {
        EnsureMessages();
        lock (ClassGate)
        {
            if (s_classRegistered) return;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = (nint)name,
                };
                if (RegisterClassExW(ref wc) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassExW for the Java Access Bridge client failed");
            }
            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var w))
        {
            try
            {
                if (w.Handle(msg, wParam, lParam, out var result)) return result;
            }
            catch (Exception ex)
            {
                w._log.LogWarning(ex, "JAB window message 0x{Msg:X} threw", msg);
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <returns>True when the message was ours.</returns>
    private bool Handle(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == s_fromJavaHello)
        {
            _client.Connect(wParam, (int)lParam);
            return true;
        }
        if (msg == AB_DLL_GOING_AWAY)
        {
            _client.MarkDead(wParam);
            return true;
        }
        if (msg == WM_COPYDATA)
        {
            if (wParam != Hwnd && lParam != 0)
            {
                var cds = (COPYDATASTRUCT*)lParam;
                if (cds->cbData >= 8 && cds->lpData != 0)
                {
                    var data = new ReadOnlySpan<byte>((void*)cds->lpData, (int)cds->cbData);
                    if (JabPacket.ReadI32At(data, 0) == JabPackets.JavaShutdownPackageType)
                        _client.MarkDeadByVmId(JabPacket.ReadI32At(data, 4));
                    // Event packets (focus, property changes) arrive here too; winuse polls instead and drops them.
                }
            }
            result = 1;
            return true;
        }
        return false;
    }

    /// <summary>Tells every connected JVM that this client is leaving and destroys the window. Dispatcher thread only.</summary>
    public void Dispose()
    {
        if (Hwnd == 0) return;
        foreach (var jvm in _client.Jvms)
        {
            if (jvm.Dead || !IsWindow(jvm.JavaHwnd)) continue;
            SendMessageTimeoutW(jvm.JavaHwnd, AB_DLL_GOING_AWAY, Hwnd, 0, SMTO_BLOCK | SMTO_NOTIMEOUTIFNOTHUNG, 1000, out _);
        }
        Windows.TryRemove(Hwnd, out _);
        DestroyWindow(Hwnd);
        Hwnd = 0;
    }
}
