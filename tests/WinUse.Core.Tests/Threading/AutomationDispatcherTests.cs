using WinUse.Core.Threading;

namespace WinUse.Core.Tests.Threading;

public class AutomationDispatcherTests
{
    [Fact]
    public async Task RunAsync_ExecutesOnStaThreadAndSerializes()
    {
        using var d = new AutomationDispatcher();
        var state = await d.RunAsync(() => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.STA, state);

        var order = new List<int>();
        var t1 = d.RunAsync(() => { Thread.Sleep(50); order.Add(1); });
        var t2 = d.RunAsync(() => order.Add(2));
        await Task.WhenAll(t1, t2);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task RunAsync_PropagatesException()
    {
        using var d = new AutomationDispatcher();
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.RunAsync(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task RunAsync_AfterDispose_Throws()
    {
        var d = new AutomationDispatcher();
        d.Dispose();
        d.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => d.RunAsync(() => 1));
    }

    [Fact]
    public async Task RunAsync_FromDispatcherThread_RunsInline()
    {
        using var d = new AutomationDispatcher();
        Assert.False(d.IsOnDispatcherThread);
        var result = await d.RunAsync(() => d.RunAsync(() => d.IsOnDispatcherThread).GetAwaiter().GetResult());
        Assert.True(result);
    }

    [Fact]
    public async Task RunAsync_CancelledWhileQueued_IsCancelled()
    {
        using var d = new AutomationDispatcher();
        using var gate = new ManualResetEventSlim();
        var blocker = d.RunAsync(() => gate.Wait());
        using var cts = new CancellationTokenSource();
        var queued = d.RunAsync(() => 1, cts.Token);
        cts.Cancel();
        gate.Set();
        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }
}
