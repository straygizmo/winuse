using System.Runtime.InteropServices;
using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public partial class JabDispatcherTests
{
    private const uint WM_APP = 0x8000;

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [Fact]
    public async Task RunAsync_ExecutesOnDispatcherThread_AfterStartHook()
    {
        var startThread = 0;
        using var d = new JabDispatcher(() => startThread = Environment.CurrentManagedThreadId);
        var t = await d.RunAsync(() => Environment.CurrentManagedThreadId);
        Assert.NotEqual(0, startThread);
        Assert.Equal(startThread, t);
        Assert.NotEqual(Environment.CurrentManagedThreadId, t);
        Assert.False(d.IsOnDispatcherThread);
        Assert.True(await d.RunAsync(() => d.IsOnDispatcherThread));
    }

    [Fact]
    public async Task RunAsync_PumpsWindowMessages()
    {
        using var d = new JabDispatcher(() => { });
        await d.RunAsync(() => 0); // make sure the loop is up
        Assert.True(PostThreadMessageW(d.ThreadId, WM_APP + 1, 0, 0));
        await d.RunAsync(() => 0);
        Assert.True(d.PumpedMessages >= 1);
    }

    [Fact]
    public async Task Dispose_StopsThread_AndRejectsNewWork()
    {
        var d = new JabDispatcher(() => { });
        await d.RunAsync(() => 0);
        d.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => d.RunAsync(() => 0));
    }

    [Fact]
    public async Task RunAsync_PropagatesExceptions()
    {
        using var d = new JabDispatcher(() => { });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.RunAsync<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(1, await d.RunAsync(() => 1)); // still alive
    }
}
