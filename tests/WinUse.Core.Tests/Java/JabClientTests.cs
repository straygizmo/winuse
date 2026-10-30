using WinUse.Core.Java;
using static WinUse.Core.Java.JabPackets;

namespace WinUse.Core.Tests.Java;

public class JabClientTests
{
    private const nint HwndA = 0x10, HwndB = 0x20, HwndC = 0x30;
    private const int VmA = 100, VmB = 200;

    /// <summary>Two fake JVMs and the services that wire them to a <see cref="JabClient"/>.</summary>
    private sealed class Rig
    {
        public Dictionary<nint, FakeJabChannel> Channels { get; } = new();
        public Dictionary<int, JabLayout?> Layouts { get; } = new();
        public Dictionary<int, bool?> Bits { get; } = new();
        public HashSet<nint> Alive { get; } = [];
        public List<(int Start, int End)> TextRanges { get; } = [];

        public Dictionary<int, string?> Modules { get; } = new();
        public Dictionary<int, string?> InspectErrors { get; } = new();
        public string? HandshakeFailure { get; set; }
        public const nint ClientHwnd = 0xC1;

        public JabClientServices Services => new(
            OpenChannel: hwnd => Channels.TryGetValue(hwnd, out var c) ? new JabChannelOpen(c) : new JabChannelOpen(null, HandshakeFailure),
            Inspect: pid => new JabJvmProbe(Layouts.TryGetValue(pid, out var l) ? l : null, Bits.TryGetValue(pid, out var b) ? b : null,
                Modules.TryGetValue(pid, out var m) ? m : null, InspectErrors.TryGetValue(pid, out var e) ? e : null),
            PidOf: hwnd => (int)hwnd * 10,
            IsWindow: hwnd => Alive.Contains(hwnd),
            ClientHwnd: () => ClientHwnd);

        public FakeJabChannel Add(nint hwnd, JabLayout layout, int vmId, Func<JabPacketKind, JabPacket, bool>? handler = null, bool detectable = true)
        {
            var ch = new FakeJabChannel(layout, handler ?? Jvm(hwnd, vmId));
            Channels[hwnd] = ch;
            Alive.Add(hwnd);
            if (detectable)
            {
                Layouts[(int)hwnd * 10] = layout;
                Bits[(int)hwnd * 10] = layout == JabLayout.Legacy; // the legacy bridge only exists in 32-bit JREs
            }
            return ch;
        }

        /// <summary>A JVM owning one window: answers the discovery packets for it and serves a one-child frame.</summary>
        public Func<JabPacketKind, JabPacket, bool> Jvm(nint hwnd, int vmId) => (kind, p) =>
        {
            switch (kind)
            {
                case JabPacketKind.IsJavaWindow:
                    p.Buffer[4 + p.Layout.Offset(IsJavaWindow.Result)] = (byte)(p.ReadI32(IsJavaWindow.Window) == (int)hwnd ? 1 : 0);
                    break;
                case JabPacketKind.GetContextFromHwnd:
                    if (p.ReadI32(GetContextFromHwnd.Window) == (int)hwnd)
                    {
                        p.WriteI32(GetContextFromHwnd.VmId, vmId);
                        p.WriteObj(GetContextFromHwnd.Result, 0x8000_0001L);
                    }
                    break;
                case JabPacketKind.GetContextInfo:
                {
                    var blob = p.BlobSpan(GetContextInfo.Info);
                    JabPacket.WriteWCharsAt(blob, ContextInfo.Name, MaxStringSize, "root");
                    JabPacket.WriteWCharsAt(blob, ContextInfo.RoleEn, ShortStringSize, "frame");
                    JabPacket.WriteWCharsAt(blob, ContextInfo.StatesEn, ShortStringSize, "enabled,showing");
                    JabPacket.WriteI32At(blob, ContextInfo.ChildrenCount, 1);
                    JabPacket.WriteI32At(blob, ContextInfo.AccessibleAction, 1);
                    JabPacket.WriteI32At(blob, ContextInfo.AccessibleInterfaces, 1);
                    break;
                }
                case JabPacketKind.GetVersion:
                    p.WriteWChars(GetVersion.VmVersion, "1.8.0_472");
                    p.WriteWChars(GetVersion.JavaDllVersion, "2.0.3");
                    break;
                case JabPacketKind.GetTextInfo:
                    p.WriteI32(GetTextInfo.CharCount, 5);
                    break;
                case JabPacketKind.GetTextRange:
                    TextRanges.Add((p.ReadI32(GetTextRange.Start), p.ReadI32(GetTextRange.End)));
                    p.WriteWChars(GetTextRange.Text, "hello");
                    break;
                case JabPacketKind.DoActions:
                {
                    var todo = p.BlobSpan(DoActions.ToDo);
                    var ok = JabPacket.ReadI32At(todo, Actions.Count) == 1 && JabPacket.ReadWCharsAt(todo, Actions.Name(0), ShortStringSize) == "click";
                    p.WriteI32(DoActions.Result, ok ? 1 : 0);
                    break;
                }
            }
            return true;
        };
    }

    [Fact]
    public void TwoJvms_DifferentLayouts_RouteByVmId()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var b = rig.Add(HwndB, JabLayout.Legacy, VmB);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        client.Connect(HwndB, VmB);

        Assert.True(client.IsJavaWindow(HwndB));
        var h = client.GetContextFromHwnd(HwndB)!.Value;
        Assert.Equal(VmB, h.VmId);
        Assert.Equal(0x8000_0001L, h.Ac); // legacy handle read unsigned
        var info = client.GetInfo(h)!;
        Assert.Equal("frame", info.Role);
        Assert.Equal("root", info.Name);
        Assert.Equal(1, info.ChildrenCount);
        Assert.True(info.HasAction);
        Assert.True(info.HasValue);
        Assert.Equal([0x11, 0x10, 0x102], b.SentTypeIds.Distinct());
        Assert.DoesNotContain(0x102, a.SentTypeIds);
        Assert.Equal(JabLayout.Legacy, b.LastLayoutSeen);
        Assert.Equal(JabLayout.Wide, a.LastLayoutSeen);
    }

    /// <summary>Routing uses the vmID from the hello; a JVM that fills rVMID differently is still read (the OpenJDK client does the same).</summary>
    [Fact]
    public void GetContextFromHwnd_AcceptsAnswersWithForeignVmId_RoutingByTheHelloVmId()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA, (kind, p) =>
        {
            if (kind == JabPacketKind.GetContextFromHwnd)
            {
                p.WriteI32(GetContextFromHwnd.VmId, 999);
                p.WriteObj(GetContextFromHwnd.Result, 42);
            }
            return true;
        });
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.Equal(new JabHandle(VmA, 42), client.GetContextFromHwnd(HwndA));
    }

    /// <summary>A JVM whose process could not be inspected is assumed wide; an empty context answer triggers one more look.</summary>
    [Fact]
    public void AssumedWideLayout_IsRedetected_WhenAContextCallComesBackEmpty()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Legacy, VmA, detectable: false);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.True(client.IsJavaWindow(HwndA)); // same size in both formats, so it answers even with the wrong guess
        Assert.Equal(JabLayout.Wide, client.Jvms[0].Layout);
        Assert.Equal(JabLayout.Wide, client.LayoutForWindow(HwndA));
        Assert.Null(client.GetContextFromHwnd(HwndA)); // still undetectable: the wide packet is dropped

        rig.Layouts[(int)HwndA * 10] = JabLayout.Legacy; // the process became inspectable
        var h = client.GetContextFromHwnd(HwndA);
        Assert.Equal(VmA, h!.Value.VmId);
        Assert.Equal(JabLayout.Legacy, client.Jvms[0].Layout);
        Assert.Equal(JabLayout.Legacy, client.LayoutForWindow(HwndA));
        Assert.Equal(JabLayout.Legacy, a.LastLayoutSeen);
        Assert.Null(client.LayoutForWindow(HwndB));
    }

    [Fact]
    public void Exchange_Fails_ReturnsNullOrFalse()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA, (_, _) => false);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var h = new JabHandle(VmA, 1);
        Assert.False(client.IsJavaWindow(HwndA));
        Assert.Null(client.GetContextFromHwnd(HwndA));
        Assert.Null(client.GetInfo(h));
        Assert.Null(client.GetChild(h, 0));
        Assert.Empty(client.GetActions(h));
        Assert.Equal(JabActionOutcome.Unreachable, client.DoAction(h, "click"));
        Assert.Null(client.GetText(h, 100));
        Assert.Null(client.GetValue(h));
        Assert.False(client.SetText(h, "x"));
        Assert.False(client.RequestFocus(h));
        Assert.Null(client.GetSelection(h, 0));
        Assert.Equal(0, client.GetSelectionCount(h));
        Assert.Null(client.GetVirtualName(h));
        Assert.Null(client.GetVersion(VmA));
    }

    [Fact]
    public void UnknownVmId_ReturnsFailureValues()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.Null(client.GetInfo(new JabHandle(555, 1)));
        Assert.False(client.SetText(new JabHandle(555, 1), "x"));
        Assert.Null(client.GetVersion(555));
    }

    [Fact]
    public void DeadJvm_IsPrunedAndItsChannelDisposed()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.Single(client.Jvms);
        rig.Alive.Remove(HwndA);
        Assert.False(client.IsJavaWindow(HwndA));
        Assert.Empty(client.Jvms);
        Assert.True(a.Disposed);
    }

    [Fact]
    public void MarkDead_TakesEffectAtNextCall()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        client.MarkDead(HwndA);
        Assert.Single(client.Jvms); // the list is untouched inside the callback
        client.IsJavaWindow(HwndA);
        Assert.Empty(client.Jvms);
    }

    [Fact]
    public void Connect_Twice_KeepsOneJvm_AndFailedHandshake_AddsNone()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        rig.Alive.Add(HwndC); // no channel for C: OpenChannel returns null
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        client.Connect(HwndA, VmA);
        Assert.Single(client.Jvms);
        client.Connect(HwndC, 300);
        Assert.Single(client.Jvms);
    }

    [Fact]
    public void UnknownLayout_IsRetriedThenDefaultsToWide()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA, detectable: false);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.Null(client.Jvms[0].Layout);
        Assert.True(client.IsJavaWindow(HwndA));
        Assert.Equal(JabLayout.Wide, client.Jvms[0].Layout);
        Assert.Equal(JabLayout.Wide, a.LastLayoutSeen);
    }

    [Fact]
    public void UnknownLayout_PicksUpALateDetection()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Legacy, VmA, detectable: false);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        rig.Layouts[(int)HwndA * 10] = JabLayout.Legacy; // modules became readable after the hello
        Assert.True(client.IsJavaWindow(HwndA));
        Assert.Equal(JabLayout.Legacy, client.Jvms[0].Layout);
        Assert.Equal(JabLayout.Legacy, a.LastLayoutSeen);
    }

    [Fact]
    public void GetText_ClampsToCharCountAndMax()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var h = new JabHandle(VmA, 7);
        Assert.Equal("hel", client.GetText(h, 3));
        Assert.Equal((0, 2), rig.TextRanges[0]);
        Assert.Equal("hello", client.GetText(h, 100));
        Assert.Equal((0, 4), rig.TextRanges[1]);
    }

    [Fact]
    public void DoAction_SendsOneActionName_AndReadsResult()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Legacy, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.Equal(JabActionOutcome.Done, client.DoAction(new JabHandle(VmA, 7), "click"));
        Assert.Equal(JabActionOutcome.Failed, client.DoAction(new JabHandle(VmA, 7), "togglePopup"));
    }

    [Fact]
    public void DoAction_Timeout_IsReportedAsSuch_AndTheDiagnosisBlamesTheOpenDialog()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        a.Outcome = JabExchange.Timeout;
        Assert.Equal(JabActionOutcome.Timeout, client.DoAction(new JabHandle(VmA, 7), "click"));

        // Every later request times out too while the dialog is up; the diagnosis names the cause.
        Assert.False(client.IsJavaWindow(HwndA));
        var d = client.Diagnose(HwndA, (int)HwndA * 10);
        Assert.Equal(JabStage.Timeout, d.Stage);
        Assert.NotNull(d.BlockedByActionAt);
        Assert.Contains("modal dialog", d.Reason);
        Assert.Contains("method \"mouse\"", d.Reason);

        // The dialog closes: the next answer clears it.
        a.Outcome = null;
        Assert.True(client.IsJavaWindow(HwndA));
        Assert.Null(client.Diagnose(HwndA, (int)HwndA * 10).BlockedByActionAt);

        // A JVM that is gone (send fails) did not run the action.
        a.Outcome = JabExchange.SendFailed;
        Assert.Equal(JabActionOutcome.Unreachable, client.DoAction(new JabHandle(VmA, 7), "click"));
    }

    /// <summary>A text longer than the packet's 1024-wchar field must not be truncated and reported as success.</summary>
    [Fact]
    public void SetText_LongerThanThePacket_ReturnsFalse_WithoutSending()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA, (kind, p) => { if (kind == JabPacketKind.SetText) p.WriteI32(SetText.Result, 1); return true; });
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.True(client.SetText(new JabHandle(VmA, 7), new string('x', 1023)));
        Assert.False(client.SetText(new JabHandle(VmA, 7), new string('x', 1024)));
        Assert.Equal(1, a.SentTypeIds.Count(t => t == 0x1600));
    }

    /// <summary>doctor's version line must survive a wrong layout guess: it may only use packets without object handles.</summary>
    [Fact]
    public void GetVersionForWindow_NeedsNoObjectHandlePacket()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA, (kind, p) =>
        {
            switch (kind)
            {
                case JabPacketKind.IsJavaWindow:
                    p.Buffer[4 + p.Layout.Offset(IsJavaWindow.Result)] = (byte)(p.ReadI32(IsJavaWindow.Window) == (int)HwndA ? 1 : 0);
                    return true;
                case JabPacketKind.GetVersion:
                    p.WriteWChars(GetVersion.VmVersion, "1.7.0_45");
                    return true;
                default:
                    return false; // every handle-carrying packet goes unanswered, as after a layout mismatch
            }
        });
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var v = client.GetVersionForWindow(HwndA);
        Assert.Equal("1.7.0_45", v!.VmVersion);
        Assert.DoesNotContain(0x10, a.SentTypeIds);
        Assert.Null(client.GetVersionForWindow(HwndB));
    }

    [Fact]
    public void GetVersion_FillsWindowsDllVersionWithWinuse()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var v = client.GetVersion(VmA)!;
        Assert.Equal("1.8.0_472", v.VmVersion);
        Assert.Equal("2.0.3", v.JavaDllVersion);
        Assert.StartsWith("winuse ", v.WindowsDllVersion);
    }

    [Fact]
    public void Describe_ListsPidsAndLayouts()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        rig.Add(HwndB, JabLayout.Legacy, VmB);
        var client = new JabClient(rig.Services);
        Assert.Equal("no JVMs connected", client.Describe());
        client.Connect(HwndA, VmA);
        client.Connect(HwndB, VmB);
        Assert.Equal("2 JVMs: pid 160 64-bit wide, pid 320 32-bit legacy", client.Describe());
    }

    [Fact]
    public void Connecting_EventFires_BeforeTheHandshake()
    {
        var rig = new Rig();
        var client = new JabClient(rig.Services);
        var seen = new List<(nint, int)>();
        client.Connecting += (hwnd, vmId) => seen.Add((hwnd, vmId));
        client.Connect(HwndC, 300); // no channel: handshake fails, event still fired
        Assert.Equal([(HwndC, 300)], seen);
        Assert.Empty(client.Jvms);
    }

    // ---- diagnosis ------------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnose_NoHello_WithoutBridgeDll_SaysAccessibilityNotEnabled()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var d = client.Diagnose(0x999, 4242);
        Assert.Equal(JabStage.NoHello, d.Stage);
        Assert.Equal(4242, d.JvmPid);
        Assert.False(d.JvmConnected);
        Assert.Equal(Rig.ClientHwnd, d.ClientWindow);
        Assert.Null(d.BridgeModule);
        Assert.Contains("no JavaAccessBridge*.dll loaded", d.Reason);
        Assert.Contains("winuse jab enable", d.Reason);
        Assert.Contains("1 other JVM(s) are connected", d.Reason);
        Assert.Equal([(int)HwndA * 10], d.ConnectedJvms.Select(j => j.Pid));
        Assert.Null(d.ConnectedJvms[0].LastFailure);
        Assert.Equal(4000, d.TimeoutMs);
    }

    [Fact]
    public void Diagnose_NoHello_WithBridgeDllLoaded_SaysTheHelloNeverArrived()
    {
        var rig = new Rig();
        rig.Modules[4242] = "JavaAccessBridge-64.dll";
        rig.Layouts[4242] = JabLayout.Wide;
        rig.Bits[4242] = false;
        var d = new JabClient(rig.Services).Diagnose(0x999, 4242);
        Assert.Equal(JabStage.NoHello, d.Stage);
        Assert.Equal("JavaAccessBridge-64.dll", d.BridgeModule);
        Assert.Equal("wide", d.WireFormat);
        Assert.False(d.Is32Bit);
        Assert.Contains("never announced itself", d.Reason);
        Assert.Contains("no JVM is connected", d.Reason);
        Assert.Empty(d.ConnectedJvms);
    }

    [Fact]
    public void Diagnose_InspectionError_SaysTheJvmIsOutOfReach()
    {
        var rig = new Rig();
        rig.InspectErrors[4242] = "not accessible (error 5; running elevated or as another user?)";
        var d = new JabClient(rig.Services).Diagnose(0x999, 4242);
        Assert.Equal(JabStage.NoHello, d.Stage);
        Assert.Contains("could not be inspected (not accessible (error 5", d.Reason);
        Assert.Contains("elevated", d.Reason);
    }

    [Fact]
    public void Diagnose_HandshakeFailed_ReportsTheFailedStep()
    {
        var rig = new Rig { HandshakeFailure = "the JVM did not take the memory-mapped file announcement (WM_COPYDATA) within 4000 ms" };
        rig.Alive.Add(HwndC);
        var client = new JabClient(rig.Services);
        client.Connect(HwndC, 300); // no channel for HwndC: the handshake fails
        Assert.Empty(client.Jvms);
        var d = client.Diagnose(0x999, (int)HwndC * 10);
        Assert.Equal(JabStage.HandshakeFailed, d.Stage);
        Assert.False(d.JvmConnected);
        Assert.Contains("announced itself", d.Reason);
        Assert.Contains("WM_COPYDATA", d.Reason);
    }

    [Fact]
    public void Diagnose_Timeout_WhenTheConnectedJvmStopsAnswering()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        a.Outcome = JabExchange.Timeout;
        Assert.False(client.IsJavaWindow(HwndA));
        var d = client.Diagnose(HwndA, (int)HwndA * 10);
        Assert.Equal(JabStage.Timeout, d.Stage);
        Assert.True(d.JvmConnected);
        Assert.Equal("wide", d.WireFormat);
        Assert.Contains("did not answer IsJavaWindow within 4000 ms", d.Reason);
        Assert.Contains("event thread", d.Reason);
        Assert.StartsWith("Timeout on IsJavaWindow at ", d.ConnectedJvms.Single().LastFailure);

        // A later answer clears the failure.
        a.Outcome = null;
        Assert.True(client.IsJavaWindow(HwndA));
        Assert.Null(client.Diagnose(HwndA, (int)HwndA * 10).ConnectedJvms.Single().LastFailure);
    }

    [Fact]
    public void Diagnose_NoAnswer_PointsAtTheWireFormat()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        a.Outcome = JabExchange.NoAnswer;
        Assert.False(client.IsJavaWindow(HwndA));
        var d = client.Diagnose(HwndA, (int)HwndA * 10);
        Assert.Equal(JabStage.NoAnswer, d.Stage);
        Assert.Contains("wrote no answer", d.Reason);
        Assert.Contains("wire format (wide)", d.Reason);
    }

    [Fact]
    public void Diagnose_SendFailed_WhenTheJvmWindowIsGoneMidCall()
    {
        var rig = new Rig();
        var a = rig.Add(HwndA, JabLayout.Wide, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        a.Outcome = JabExchange.SendFailed;
        Assert.False(client.IsJavaWindow(HwndA));
        Assert.Equal(JabStage.SendFailed, client.Diagnose(HwndA, (int)HwndA * 10).Stage);
    }

    [Fact]
    public void Diagnose_NotOwned_WhenTheJvmAnswersButDisownsTheWindow()
    {
        var rig = new Rig();
        rig.Add(HwndA, JabLayout.Legacy, VmA);
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        Assert.False(client.IsJavaWindow(HwndB));
        var d = client.Diagnose(HwndB, (int)HwndA * 10);
        Assert.Equal(JabStage.NotOwned, d.Stage);
        Assert.True(d.JvmConnected);
        Assert.Contains("legacy wire format", d.Reason);
        Assert.Contains($"0x{HwndB:X}", d.Reason);
    }

    // ---- tables ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(JabLayout.Wide)]
    [InlineData(JabLayout.Legacy)]
    public void GetTableInfo_KeepsTheTableObject_ReleasesTheOthers_AndCellsRoundTrip(JabLayout layout)
    {
        var rig = new Rig();
        var released = new List<long>();
        rig.Add(HwndA, layout, VmA, (kind, p) =>
        {
            switch (kind)
            {
                case JabPacketKind.GetTableInfo when p.ReadObj(GetTableInfo.Ac) == 0x5000:
                    p.WriteObj(GetTableInfo.Caption, 0x11);
                    p.WriteObj(GetTableInfo.Summary, 0);
                    p.WriteI32(GetTableInfo.RowCount, 2);
                    p.WriteI32(GetTableInfo.ColumnCount, 3);
                    p.WriteObj(GetTableInfo.Context, 0x13);
                    p.WriteObj(GetTableInfo.Table, 0x14);
                    break;
                case JabPacketKind.GetTableColumnHeader when p.ReadObj(GetTableInfo.Ac) == 0x5000:
                    p.WriteI32(GetTableInfo.RowCount, 1);
                    p.WriteI32(GetTableInfo.ColumnCount, 3);
                    p.WriteObj(GetTableInfo.Table, 0x24);
                    break;
                case JabPacketKind.GetTableCellInfo when p.ReadObj(GetTableCellInfo.Table) == 0x14:
                    p.WriteObj(GetTableCellInfo.Context, 0x100 + p.ReadI32(GetTableCellInfo.Row) * 10 + p.ReadI32(GetTableCellInfo.Column));
                    p.WriteI32(GetTableCellInfo.RowExtent, 1);
                    p.WriteI32(GetTableCellInfo.ColumnExtent, 1);
                    break;
                case JabPacketKind.ReleaseObject:
                    released.Add(p.ReadObj(ReleaseObject.Object));
                    break;
            }
            return true;
        });
        var client = new JabClient(rig.Services);
        client.Connect(HwndA, VmA);
        var ac = new JabHandle(VmA, 0x5000);

        var t = client.GetTableInfo(ac)!;
        Assert.Equal((2, 3, 0x14L), (t.RowCount, t.ColumnCount, t.Table.Ac));
        Assert.Equal([0x11L, 0x13L], released); // caption and the duplicate context ref; a null summary is not released
        Assert.Equal(VmA, t.Table.VmId);

        var header = client.GetTableColumnHeader(ac)!;
        Assert.Equal((1, 3, 0x24L), (header.RowCount, header.ColumnCount, header.Table.Ac));

        var cell = client.GetTableCell(t.Table, 1, 2);
        Assert.Equal(new JabHandle(VmA, 0x100 + 12), cell);
        Assert.Null(client.GetTableCell(new JabHandle(VmA, 0x77), 0, 0));
        Assert.Null(client.GetTableInfo(new JabHandle(VmA, 0x6000)));
    }
}
