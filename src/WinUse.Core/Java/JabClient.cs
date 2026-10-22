using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static WinUse.Core.Java.JabPackets;

namespace WinUse.Core.Java;

/// <summary>
/// What <see cref="JabClient"/> needs from the outside world, injectable for tests. <paramref name="OpenChannel"/> performs
/// the memory-mapped-file handshake with a JVM window and returns null when it fails; <paramref name="DetectLayout"/> reads
/// the JVM process's loaded bridge DLL (null when the process cannot be inspected yet); <paramref name="PidOf"/> and
/// <paramref name="IsWindow"/> wrap the user32 calls.
/// </summary>
internal sealed record JabClientServices(
    Func<nint, IJabChannel?> OpenChannel,
    Func<int, JabLayout?> DetectLayout,
    Func<nint, int> PidOf,
    Func<nint, bool> IsWindow);

/// <summary>A JVM the client has shaken hands with.</summary>
internal sealed class JabJvm
{
    public required nint JavaHwnd { get; init; }
    public required int VmId { get; init; }
    public required int Pid { get; init; }
    public required IJabChannel Channel { get; init; }
    /// <summary>Null until the JVM process could be inspected; <see cref="JabClient"/> retries before the first packet.</summary>
    public JabLayout? Layout { get; set; }
    public int LayoutAttempts { get; set; }
    /// <summary>Set from the window procedure (JVM shutdown, AB_DLL_GOING_AWAY); the list is pruned at the next call.</summary>
    public volatile bool Dead;
}

/// <summary>
/// <see cref="IJabAccess"/> spoken directly in the Java Access Bridge wire format: one packet per call, sent to the JVM that
/// owns the handle's <c>vmID</c> in that JVM's layout. Dispatcher thread only, like every <see cref="IJabAccess"/>.
/// </summary>
internal sealed class JabClient(JabClientServices services, ILogger? log = null) : IJabAccess, IDisposable
{
    private const int LayoutRetries = 3;
    private static readonly TimeSpan LayoutRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly List<JabJvm> _jvms = [];

    /// <summary>Raised when a JVM says hello, before the handshake (for tests).</summary>
    public event Action<nint, int>? Connecting;

    /// <summary>The JVMs currently connected (dead ones until the next call prunes them).</summary>
    public IReadOnlyList<JabJvm> Jvms => _jvms;

    // ---- connection management ---------------------------------------------------------------------------------------

    /// <summary>Handles a JVM's hello: detects its wire format, performs the handshake and remembers it.</summary>
    public void Connect(nint javaHwnd, int vmId)
    {
        Connecting?.Invoke(javaHwnd, vmId);
        var existing = _jvms.FirstOrDefault(j => j.JavaHwnd == javaHwnd);
        if (existing is not null)
        {
            if (!existing.Dead && services.IsWindow(javaHwnd)) return;
            Remove(existing);
        }
        var pid = services.PidOf(javaHwnd);
        var layout = Detect(pid);
        var channel = services.OpenChannel(javaHwnd);
        if (channel is null)
        {
            _log.LogWarning("JAB handshake with JVM window 0x{Hwnd:X} (pid {Pid}) failed", javaHwnd, pid);
            return;
        }
        _jvms.Add(new JabJvm { JavaHwnd = javaHwnd, VmId = vmId, Pid = pid, Channel = channel, Layout = layout, LayoutAttempts = 1 });
        _log.LogInformation("JAB connected to JVM pid {Pid} (vmID {VmId}, {Layout} wire format)", pid, vmId, Name(layout));
    }

    /// <summary>Flags a JVM as gone without touching the list (safe from inside a window procedure).</summary>
    public void MarkDead(nint javaHwnd)
    {
        foreach (var j in _jvms)
            if (j.JavaHwnd == javaHwnd) j.Dead = true;
    }

    /// <summary>Flags the JVM with this vmID as gone (from a <c>cJavaShutdownPackage</c>).</summary>
    public void MarkDeadByVmId(int vmId)
    {
        foreach (var j in _jvms)
            if (j.VmId == vmId) j.Dead = true;
    }

    public string Describe()
    {
        Prune();
        if (_jvms.Count == 0) return "no JVMs connected";
        return $"{_jvms.Count} JVMs: " + string.Join(", ", _jvms.Select(j => $"pid {j.Pid} {Name(j.Layout)}"));
    }

    public void Dispose()
    {
        foreach (var j in _jvms) j.Channel.Dispose();
        _jvms.Clear();
    }

    private void Prune()
    {
        for (var i = _jvms.Count - 1; i >= 0; i--)
        {
            var j = _jvms[i];
            if (j.Dead || !services.IsWindow(j.JavaHwnd)) Remove(j);
        }
    }

    private void Remove(JabJvm j)
    {
        _jvms.Remove(j);
        j.Channel.Dispose();
        _log.LogInformation("JAB disconnected from JVM pid {Pid}", j.Pid);
    }

    private JabLayout? Detect(int pid)
    {
        try { return services.DetectLayout(pid); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "JAB layout detection for pid {Pid} threw", pid);
            return null;
        }
    }

    /// <summary>The layout to send with: retried while unknown, then wide with a warning.</summary>
    private JabLayout LayoutOf(JabJvm j)
    {
        while (j.Layout is null && j.LayoutAttempts < LayoutRetries)
        {
            Thread.Sleep(LayoutRetryDelay);
            j.LayoutAttempts++;
            j.Layout = Detect(j.Pid);
        }
        if (j.Layout is null)
        {
            _log.LogWarning("JAB could not tell the wire format of JVM pid {Pid}; assuming wide", j.Pid);
            j.Layout = JabLayout.Wide;
        }
        return j.Layout.Value;
    }

    private static string Name(JabLayout? layout) => JavaProcessInfo.FormatName(layout);

    // ---- sending -----------------------------------------------------------------------------------------------------

    private JabJvm? Find(int vmId)
    {
        Prune();
        return _jvms.FirstOrDefault(j => j.VmId == vmId);
    }

    /// <summary>Builds the packet for this JVM's layout, sets vmID (field 0) and the handle (field 1) when asked, and sends it.</summary>
    private JabPacket? Send(JabJvm j, JabPacketKind kind, Action<JabPacket> fill)
    {
        var p = New(kind, LayoutOf(j));
        fill(p);
        if (j.Channel.Exchange(p.Buffer, p.Size)) return p;
        _log.LogDebug("JAB {Kind} to pid {Pid} ({Layout}) got no answer", kind, j.Pid, Name(j.Layout));
        return null;
    }

    private JabPacket? SendFor(JabHandle h, JabPacketKind kind, Action<JabPacket>? fill = null)
    {
        var j = Find(h.VmId);
        if (j is null) return null;
        return Send(j, kind, p =>
        {
            p.WriteI32(0, h.VmId);
            p.WriteObj(1, h.Ac);
            fill?.Invoke(p);
        });
    }

    // ---- IJabAccess ---------------------------------------------------------------------------------------------------

    public bool IsJavaWindow(nint hwnd)
    {
        Prune();
        foreach (var j in _jvms.ToArray())
        {
            var p = Send(j, JabPacketKind.IsJavaWindow, p => p.WriteI32(JabPackets.IsJavaWindow.Window, (int)hwnd));
            if (p is not null && p.ReadBool8(JabPackets.IsJavaWindow.Result)) return true;
        }
        return false;
    }

    public JabHandle? GetContextFromHwnd(nint hwnd)
    {
        Prune();
        foreach (var j in _jvms.ToArray())
        {
            var p = Send(j, JabPacketKind.GetContextFromHwnd, p => p.WriteI32(JabPackets.GetContextFromHwnd.Window, (int)hwnd));
            if (p is null) continue;
            var ac = p.ReadObj(JabPackets.GetContextFromHwnd.Result);
            if (ac == 0) continue;
            var vm = p.ReadI32(JabPackets.GetContextFromHwnd.VmId);
            if (vm != j.VmId)
            {
                _log.LogDebug("JAB getAccessibleContextFromHWND: JVM pid {Pid} answered with vmID {Vm}, expected {Expected}", j.Pid, vm, j.VmId);
                continue;
            }
            return new JabHandle(j.VmId, ac);
        }
        return null;
    }

    public JabContextInfo? GetInfo(JabHandle h)
    {
        var p = SendFor(h, JabPacketKind.GetContextInfo);
        if (p is null) return null;
        var b = p.ReadBlob(JabPackets.GetContextInfo.Info);
        return new JabContextInfo(
            NullIfEmpty(JabPacket.ReadWCharsAt(b, ContextInfo.Name, MaxStringSize)),
            NullIfEmpty(JabPacket.ReadWCharsAt(b, ContextInfo.Description, MaxStringSize)),
            JabPacket.ReadWCharsAt(b, ContextInfo.RoleEn, ShortStringSize),
            JabPacket.ReadWCharsAt(b, ContextInfo.StatesEn, ShortStringSize),
            JabPacket.ReadI32At(b, ContextInfo.IndexInParent),
            JabPacket.ReadI32At(b, ContextInfo.ChildrenCount),
            JabPacket.ReadI32At(b, ContextInfo.X), JabPacket.ReadI32At(b, ContextInfo.Y),
            JabPacket.ReadI32At(b, ContextInfo.Width), JabPacket.ReadI32At(b, ContextInfo.Height),
            JabPacket.ReadI32At(b, ContextInfo.AccessibleAction) != 0,
            JabPacket.ReadI32At(b, ContextInfo.AccessibleSelection) != 0,
            JabPacket.ReadI32At(b, ContextInfo.AccessibleText) != 0,
            (JabPacket.ReadI32At(b, ContextInfo.AccessibleInterfaces) & 1) != 0);
    }

    public JabHandle? GetChild(JabHandle h, int index)
    {
        var p = SendFor(h, JabPacketKind.GetChild, p => p.WriteI32(JabPackets.GetChild.ChildIndex, index));
        var ac = p?.ReadObj(JabPackets.GetChild.Result) ?? 0;
        return ac == 0 ? null : new JabHandle(h.VmId, ac);
    }

    public void Release(JabHandle h) => SendFor(h, JabPacketKind.ReleaseObject);

    public IReadOnlyList<string> GetActions(JabHandle h)
    {
        var p = SendFor(h, JabPacketKind.GetActions);
        if (p is null) return [];
        var b = p.ReadBlob(JabPackets.GetActions.Actions);
        var n = Math.Clamp(JabPacket.ReadI32At(b, Actions.Count), 0, MaxActionInfo);
        var list = new string[n];
        for (var i = 0; i < n; i++) list[i] = JabPacket.ReadWCharsAt(b, Actions.Name(i), ShortStringSize);
        return list;
    }

    public bool DoAction(JabHandle h, string action)
    {
        var p = SendFor(h, JabPacketKind.DoActions, p =>
        {
            var todo = p.BlobSpan(JabPackets.DoActions.ToDo);
            JabPacket.WriteI32At(todo, Actions.Count, 1);
            JabPacket.WriteWCharsAt(todo, Actions.Name(0), ShortStringSize, action);
            p.WriteI32(JabPackets.DoActions.Failure, -1);
        });
        return p is not null && p.ReadI32(JabPackets.DoActions.Result) != 0;
    }

    public string? GetText(JabHandle h, int maxChars)
    {
        var info = SendFor(h, JabPacketKind.GetTextInfo);
        if (info is null) return null;
        var n = Math.Clamp(Math.Min(info.ReadI32(JabPackets.GetTextInfo.CharCount), maxChars), 0, MaxBufferSizeChars - 1);
        if (n == 0) return "";
        var range = SendFor(h, JabPacketKind.GetTextRange, p =>
        {
            p.WriteI32(JabPackets.GetTextRange.Start, 0);
            p.WriteI32(JabPackets.GetTextRange.End, n - 1);
        });
        if (range is null) return null;
        var text = range.ReadWChars(JabPackets.GetTextRange.Text);
        return text.Length > n ? text[..n] : text;
    }

    public string? GetValue(JabHandle h)
    {
        var p = SendFor(h, JabPacketKind.GetValue);
        return p is null ? null : NullIfEmpty(p.ReadWChars(JabPackets.GetValue.Value));
    }

    public bool SetText(JabHandle h, string text)
    {
        var p = SendFor(h, JabPacketKind.SetText, p => p.WriteWChars(JabPackets.SetText.Text, text));
        return p is not null && p.ReadI32(JabPackets.SetText.Result) != 0;
    }

    public bool RequestFocus(JabHandle h) => SendFor(h, JabPacketKind.RequestFocus) is not null;

    public void AddSelection(JabHandle h, int index) =>
        SendFor(h, JabPacketKind.AddSelection, p => p.WriteI32(JabPackets.AddSelection.Index, index));

    public void ClearSelection(JabHandle h) => SendFor(h, JabPacketKind.ClearSelection);

    public JabHandle? GetSelection(JabHandle h, int index)
    {
        var p = SendFor(h, JabPacketKind.GetSelection, p => p.WriteI32(JabPackets.GetSelection.Index, index));
        var ac = p?.ReadObj(JabPackets.GetSelection.Result) ?? 0;
        return ac == 0 ? null : new JabHandle(h.VmId, ac);
    }

    public int GetSelectionCount(JabHandle h) => SendFor(h, JabPacketKind.GetSelectionCount)?.ReadI32(JabPackets.GetSelectionCount.Count) ?? 0;

    public string? GetVirtualName(JabHandle h)
    {
        var p = SendFor(h, JabPacketKind.GetVirtualName, p => p.WriteI32(JabPackets.GetVirtualName.Len, MaxStringSize));
        return p is null ? null : NullIfEmpty(p.ReadWChars(JabPackets.GetVirtualName.Name));
    }

    public JabVersionInfo? GetVersion(int vmId)
    {
        var j = Find(vmId);
        if (j is null) return null;
        var p = Send(j, JabPacketKind.GetVersion, p => p.WriteI32(JabPackets.GetVersion.VmId, vmId));
        if (p is null) return null;
        var mine = typeof(JabClient).Assembly.GetName().Version;
        return new JabVersionInfo(
            p.ReadWChars(JabPackets.GetVersion.VmVersion),
            p.ReadWChars(JabPackets.GetVersion.JavaDllVersion),
            "winuse " + (mine is null ? "?" : mine.ToString(3)));
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
