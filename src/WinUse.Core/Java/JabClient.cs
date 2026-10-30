using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static WinUse.Core.Java.JabPackets;

namespace WinUse.Core.Java;

/// <summary>
/// What inspecting a JVM process tells the client: its wire format, bitness and loaded bridge DLL, each null when not (yet)
/// readable; <paramref name="Error"/> when the process could not be opened at all (elevated, other user).
/// </summary>
internal sealed record JabJvmProbe(JabLayout? Layout, bool? Is32Bit, string? BridgeModule = null, string? Error = null);

/// <summary>The outcome of opening a channel: the channel, or why the handshake failed.</summary>
internal sealed record JabChannelOpen(IJabChannel? Channel, string? Failure = null);

/// <summary>
/// What <see cref="JabClient"/> needs from the outside world, injectable for tests. <paramref name="OpenChannel"/> performs
/// the memory-mapped-file handshake with a JVM window and reports the failure when it fails; <paramref name="Inspect"/> reads
/// the JVM process (loaded bridge DLL, bitness); <paramref name="PidOf"/> and <paramref name="IsWindow"/> wrap the user32
/// calls; <paramref name="ClientHwnd"/> is the client window the JVMs talk to (for diagnoses).
/// </summary>
internal sealed record JabClientServices(
    Func<nint, JabChannelOpen> OpenChannel,
    Func<int, JabJvmProbe> Inspect,
    Func<nint, int> PidOf,
    Func<nint, bool> IsWindow,
    Func<nint>? ClientHwnd = null);

/// <summary>A JVM the client has shaken hands with.</summary>
internal sealed class JabJvm
{
    public required nint JavaHwnd { get; init; }
    public required int VmId { get; init; }
    public required int Pid { get; init; }
    public required IJabChannel Channel { get; init; }
    /// <summary>Null until the JVM process could be inspected; <see cref="JabClient"/> retries before the first packet.</summary>
    public JabLayout? Layout { get; set; }
    /// <summary>True when <see cref="Layout"/> is the wide default taken after inspection kept failing, not a detection.</summary>
    public bool LayoutAssumed { get; set; }
    public int LayoutAttempts { get; set; }
    public bool? Is32Bit { get; set; }
    public string? BridgeModule { get; set; }
    /// <summary>How the last request to this JVM ended, and which one it was when it failed.</summary>
    public JabExchange LastOutcome { get; set; } = JabExchange.Answered;
    public JabPacketKind? LastFailedKind { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    /// <summary>
    /// When a <c>doAccessibleActions</c> request timed out and nothing has been answered since: the action most likely opened
    /// a modal dialog, and the JVM's bridge thread waits inside it until the dialog closes. Cleared by the next answer.
    /// </summary>
    public DateTimeOffset? BlockedActionAt { get; set; }
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
    private const int MaxRememberedHandshakeFailures = 32;

    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly List<JabJvm> _jvms = [];
    private readonly Dictionary<nint, (int Pid, string Failure, DateTimeOffset At)> _handshakeFailures = new();

    /// <summary>Raised when a JVM says hello, before the handshake (for tests).</summary>
    public event Action<nint, int>? Connecting;

    /// <summary>The JVMs currently connected (dead ones until the next call prunes them).</summary>
    public IReadOnlyList<JabJvm> Jvms => _jvms;

    /// <summary>The request timeout a diagnosis reports.</summary>
    public int TimeoutMs { get; init; } = (int)JabMemoryChannel.SendTimeoutMs;

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
        var probe = Detect(pid);
        var open = services.OpenChannel(javaHwnd);
        if (open.Channel is null)
        {
            var failure = open.Failure ?? "the JVM did not answer the handshake";
            if (_handshakeFailures.Count >= MaxRememberedHandshakeFailures)
                _handshakeFailures.Remove(_handshakeFailures.OrderBy(kv => kv.Value.At).First().Key);
            _handshakeFailures[javaHwnd] = (pid, failure, DateTimeOffset.Now);
            _log.LogWarning("JAB handshake with JVM window 0x{Hwnd:X} (pid {Pid}) failed: {Failure}", javaHwnd, pid, failure);
            return;
        }
        _handshakeFailures.Remove(javaHwnd);
        _jvms.Add(new JabJvm
        {
            JavaHwnd = javaHwnd, VmId = vmId, Pid = pid, Channel = open.Channel, Layout = probe.Layout, Is32Bit = probe.Is32Bit,
            BridgeModule = probe.BridgeModule, LayoutAttempts = 1,
        });
        _log.LogInformation("JAB connected to JVM pid {Pid} (vmID {VmId}, {Layout} wire format)", pid, vmId, Name(probe.Layout));
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
        return $"{_jvms.Count} JVMs: " + string.Join(", ", _jvms.Select(j =>
            $"pid {j.Pid} {(j.Is32Bit switch { true => "32-bit ", false => "64-bit ", null => "" })}{Name(j.Layout)}"));
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

    private JabJvmProbe Detect(int pid)
    {
        try { return services.Inspect(pid); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "JAB process inspection for pid {Pid} threw", pid);
            return new JabJvmProbe(null, null, null, ex.Message);
        }
    }

    /// <summary>One more inspection of a JVM; true when it now reports a layout (which replaces the current one).</summary>
    private bool Redetect(JabJvm j)
    {
        var probe = Detect(j.Pid);
        j.Is32Bit ??= probe.Is32Bit;
        j.BridgeModule ??= probe.BridgeModule;
        if (probe.Layout is null) return false;
        j.Layout = probe.Layout;
        j.LayoutAssumed = false;
        return true;
    }

    /// <summary>The layout to send with: retried while unknown, then wide with a warning (and flagged as assumed).</summary>
    private JabLayout LayoutOf(JabJvm j)
    {
        while (j.Layout is null && j.LayoutAttempts < LayoutRetries)
        {
            Thread.Sleep(LayoutRetryDelay);
            j.LayoutAttempts++;
            Redetect(j);
        }
        if (j.Layout is null)
        {
            _log.LogWarning("JAB could not tell the wire format of JVM pid {Pid}; assuming wide", j.Pid);
            j.Layout = JabLayout.Wide;
            j.LayoutAssumed = true;
        }
        return j.Layout.Value;
    }

    private static string Name(JabLayout? layout) => JavaProcessInfo.FormatName(layout);

    // ---- diagnosis ---------------------------------------------------------------------------------------------------

    public JabWindowDiagnosis Diagnose(nint hwnd, int pid)
    {
        Prune();
        var client = services.ClientHwnd?.Invoke() ?? 0;
        var connected = _jvms.Select(j => new JabJvmSummary(j.Pid, Name(j.Layout), j.LayoutAssumed, j.Is32Bit, FailureText(j))).ToList();
        var jvm = _jvms.FirstOrDefault(j => j.Pid == pid);
        if (jvm is not null)
        {
            var format = Name(jvm.Layout) + (jvm.LayoutAssumed ? ", assumed" : "");
            var kind = jvm.LastFailedKind?.ToString() ?? "the request";
            var (stage, reason) = jvm.LastOutcome switch
            {
                JabExchange.Timeout when jvm.BlockedActionAt is { } at => (JabStage.Timeout,
                    $"JVM pid {pid} is connected ({format} wire format) but did not answer {kind} within {TimeoutMs} ms: "
                    + $"an action (invoke) sent at {at:HH:mm:ss} has not returned, which is what a Swing action that opens a modal dialog does, "
                    + "so that dialog is most likely open and the JVM's bridge waits inside it until the dialog closes. Read the dialog with "
                    + "screenshot or the UIA tree, operate it with click / type_text / press_keys, or close it; the Java tree is readable again "
                    + "afterwards. Buttons that open dialogs are best invoked with method \"mouse\"."),
                JabExchange.Timeout => (JabStage.Timeout,
                    $"JVM pid {pid} is connected ({format} wire format) but did not answer {kind} within {TimeoutMs} ms: "
                    + "the Java side is not processing bridge requests (its event thread is blocked, or the process is suspended)."),
                JabExchange.SendFailed => (JabStage.SendFailed,
                    $"JVM pid {pid} is connected but {kind} could not be delivered to its bridge window: the JVM is exiting or its window is gone."),
                JabExchange.NoAnswer => (JabStage.NoAnswer,
                    $"JVM pid {pid} acknowledged {kind} but wrote no answer: the packet size did not match its wire format ({format})."),
                _ => (JabStage.NotOwned,
                    $"JVM pid {pid} is connected ({format} wire format) and answers, but reports that window 0x{hwnd:X} is not one of its Java windows "
                    + "(not an AWT top-level window, or not shown yet)."),
            };
            return new JabWindowDiagnosis(stage, reason, pid, client, jvm.BridgeModule, Name(jvm.Layout), jvm.Is32Bit, true, TimeoutMs, connected,
                jvm.LastOutcome == JabExchange.Timeout ? jvm.BlockedActionAt : null);
        }

        var failed = _handshakeFailures.Where(kv => kv.Value.Pid == pid).Select(kv => kv.Value).OrderByDescending(f => f.At).FirstOrDefault();
        var probe = Detect(pid);
        if (failed.Failure is not null)
        {
            return new JabWindowDiagnosis(JabStage.HandshakeFailed,
                $"JVM pid {pid} announced itself to the client window 0x{client:X} at {failed.At:HH:mm:ss} but the memory-mapped-file handshake failed: {failed.Failure}",
                pid, client, probe.BridgeModule, Name(probe.Layout), probe.Is32Bit, false, TimeoutMs, connected);
        }
        var others = connected.Count == 0 ? "no JVM is connected" : $"{connected.Count} other JVM(s) are connected";
        var reasonNoHello = probe.Error is not null
            ? $"JVM pid {pid} could not be inspected ({probe.Error}) and never announced itself to the client window 0x{client:X}: "
              + "a JVM running elevated or as another user is out of reach."
            : probe.BridgeModule is null
                ? $"JVM pid {pid} has no JavaAccessBridge*.dll loaded, so it cannot announce itself to the client window 0x{client:X}: "
                  + "accessibility was not enabled when it started (enable it with winuse jab enable or start it through process_run, then restart the app; see winuse doctor)."
                : $"JVM pid {pid} has {probe.BridgeModule} loaded but never announced itself to the client window 0x{client:X} (no AccessBridge-FromJava-Hello): "
                  + "its bridge thread is not pumping messages, or its messages do not reach this process (app elevated or in another session).";
        return new JabWindowDiagnosis(JabStage.NoHello, $"{reasonNoHello} {others}.",
            pid, client, probe.BridgeModule, Name(probe.Layout), probe.Is32Bit, false, TimeoutMs, connected);
    }

    private static string? FailureText(JabJvm j) => j.LastOutcome switch
    {
        JabExchange.Answered => null,
        var o => $"{o} on {j.LastFailedKind} at {j.LastFailureAt:HH:mm:ss}",
    };

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
        var outcome = j.Channel.Exchange(p.Buffer, p.Size);
        j.LastOutcome = outcome;
        if (outcome == JabExchange.Answered)
        {
            j.BlockedActionAt = null;
            return p;
        }
        j.LastFailedKind = kind;
        j.LastFailureAt = DateTimeOffset.Now;
        if (kind == JabPacketKind.DoActions && outcome == JabExchange.Timeout) j.BlockedActionAt ??= j.LastFailureAt;
        _log.LogDebug("JAB {Kind} to pid {Pid} ({Layout}) got no answer: {Outcome}", kind, j.Pid, Name(j.Layout), outcome);
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
            var ac = ContextFromHwnd(j, hwnd);
            // An assumed-wide JVM that drops the packet may be legacy after all: look at the process once more and retry.
            if (ac == 0 && j.LayoutAssumed && Redetect(j)) ac = ContextFromHwnd(j, hwnd);
            if (ac != 0) return new JabHandle(j.VmId, ac);
        }
        return null;
    }

    /// <summary>The window's root context from one JVM, or 0. Routing stays on the hello's vmID even if the JVM reports another.</summary>
    private long ContextFromHwnd(JabJvm j, nint hwnd)
    {
        var p = Send(j, JabPacketKind.GetContextFromHwnd, p => p.WriteI32(JabPackets.GetContextFromHwnd.Window, (int)hwnd));
        if (p is null) return 0;
        var ac = p.ReadObj(JabPackets.GetContextFromHwnd.Result);
        if (ac == 0) return 0;
        var vm = p.ReadI32(JabPackets.GetContextFromHwnd.VmId);
        if (vm != j.VmId)
            _log.LogDebug("JAB getAccessibleContextFromHWND: JVM pid {Pid} answered with vmID {Vm}, routing by {Expected}", j.Pid, vm, j.VmId);
        return ac;
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

    public JabHandle? GetParent(JabHandle h)
    {
        var p = SendFor(h, JabPacketKind.GetParent);
        var ac = p?.ReadObj(JabPackets.GetParent.Result) ?? 0;
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

    public JabActionOutcome DoAction(JabHandle h, string action)
    {
        var j = Find(h.VmId);
        if (j is null) return JabActionOutcome.Unreachable;
        var p = Send(j, JabPacketKind.DoActions, p =>
        {
            p.WriteI32(0, h.VmId);
            p.WriteObj(1, h.Ac);
            var todo = p.BlobSpan(JabPackets.DoActions.ToDo);
            JabPacket.WriteI32At(todo, Actions.Count, 1);
            JabPacket.WriteWCharsAt(todo, Actions.Name(0), ShortStringSize, action);
            p.WriteI32(JabPackets.DoActions.Failure, -1);
        });
        if (p is null) return j.LastOutcome == JabExchange.Timeout ? JabActionOutcome.Timeout : JabActionOutcome.Unreachable;
        return p.ReadI32(JabPackets.DoActions.Result) != 0 ? JabActionOutcome.Done : JabActionOutcome.Failed;
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
        // The packet holds MaxStringSize - 1 characters; a longer text would be truncated and reported as success, so let
        // the caller fall back to typing it instead.
        if (text.Length >= MaxStringSize) return false;
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

    // ---- tables ----------------------------------------------------------------------------------------------------

    public JabTableInfo? GetTableInfo(JabHandle h) => TableInfo(h, JabPacketKind.GetTableInfo);

    public JabTableInfo? GetTableColumnHeader(JabHandle h) => TableInfo(h, JabPacketKind.GetTableColumnHeader);

    /// <summary>
    /// Sends one of the <c>AccessibleTableInfo</c> packets and keeps only the <c>accessibleTable</c> object; the caption,
    /// summary and context objects the JVM also pinned are released right away.
    /// </summary>
    private JabTableInfo? TableInfo(JabHandle h, JabPacketKind kind)
    {
        var p = SendFor(h, kind);
        if (p is null) return null;
        var table = p.ReadObj(JabPackets.GetTableInfo.Table);
        foreach (var field in new[] { JabPackets.GetTableInfo.Caption, JabPackets.GetTableInfo.Summary, JabPackets.GetTableInfo.Context })
        {
            var o = p.ReadObj(field);
            if (o != 0 && o != table && o != h.Ac) Release(new JabHandle(h.VmId, o));
        }
        if (table == 0) return null;
        return new JabTableInfo(p.ReadI32(JabPackets.GetTableInfo.RowCount), p.ReadI32(JabPackets.GetTableInfo.ColumnCount), new JabHandle(h.VmId, table));
    }

    public JabHandle? GetTableCell(JabHandle table, int row, int column)
    {
        var p = SendFor(table, JabPacketKind.GetTableCellInfo, p =>
        {
            p.WriteI32(JabPackets.GetTableCellInfo.Row, row);
            p.WriteI32(JabPackets.GetTableCellInfo.Column, column);
        });
        var ac = p?.ReadObj(JabPackets.GetTableCellInfo.Context) ?? 0;
        return ac == 0 ? null : new JabHandle(table.VmId, ac);
    }

    // ---- versions ------------------------------------------------------------------------------------------------------

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

    public JabVersionInfo? GetVersionForWindow(nint hwnd)
    {
        var j = OwnerOf(hwnd);
        return j is null ? null : GetVersion(j.VmId);
    }

    public JabLayout? LayoutForWindow(nint hwnd) => OwnerOf(hwnd)?.Layout;

    /// <summary>The connected JVM that answers isJavaWindow for this window (a handle-free packet), or null.</summary>
    private JabJvm? OwnerOf(nint hwnd)
    {
        Prune();
        foreach (var j in _jvms.ToArray())
        {
            var p = Send(j, JabPacketKind.IsJavaWindow, p => p.WriteI32(JabPackets.IsJavaWindow.Window, (int)hwnd));
            if (p is not null && p.ReadBool8(JabPackets.IsJavaWindow.Result)) return j;
        }
        return null;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
