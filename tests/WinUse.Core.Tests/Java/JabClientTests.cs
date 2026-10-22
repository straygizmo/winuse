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

        public JabClientServices Services => new(
            OpenChannel: hwnd => Channels.TryGetValue(hwnd, out var c) ? c : null,
            Inspect: pid => new JabJvmProbe(Layouts.TryGetValue(pid, out var l) ? l : null, Bits.TryGetValue(pid, out var b) ? b : null),
            PidOf: hwnd => (int)hwnd * 10,
            IsWindow: hwnd => Alive.Contains(hwnd));

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
        Assert.False(client.DoAction(h, "click"));
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
        Assert.True(client.DoAction(new JabHandle(VmA, 7), "click"));
        Assert.False(client.DoAction(new JabHandle(VmA, 7), "togglePopup"));
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
}
