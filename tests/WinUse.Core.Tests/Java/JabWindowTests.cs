using WinUse.Core.Java;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

/// <summary>The hidden client window alone, without a JVM: it must be a real top-level window that reacts to a JVM's hello.</summary>
public class JabWindowTests
{
    [Fact]
    public async Task Create_MakesATopLevelWindow_ThatAnswersHello_AndDisposeDestroysIt()
    {
        var connects = new List<(nint, int)>();
        var client = new JabClient(new JabClientServices(_ => null, _ => new JabJvmProbe(null, null), _ => 0, _ => true));
        client.Connecting += (hwnd, vmId) => connects.Add((hwnd, vmId));
        JabWindow? w = null;
        using var d = new JabDispatcher(() => w = JabWindow.Create(client, null));
        Assert.NotNull(w);
        Assert.True(NativeMethods.IsWindow(w!.Hwnd));
        Assert.Equal(0, NativeMethods.GetParent(w.Hwnd)); // top-level: HWND_BROADCAST reaches it

        // Pretend to be a JVM: post FromJavaHello to the window and let the loop pump it.
        var hello = NativeMethods.RegisterWindowMessageW("AccessBridge-FromJava-Hello");
        Assert.NotEqual(0u, hello);
        NativeMethods.PostMessageW(w.Hwnd, hello, 0x1234, 0x1234);
        await d.RunAsync(() => { }); // queued behind the posted message
        Assert.Equal([(0x1234, 0x1234)], connects); // OpenChannel returned null, so nothing connected, but the hello was handled

        await d.RunAsync(() => w.Dispose());
        Assert.False(NativeMethods.IsWindow(w.Hwnd));
    }
}
