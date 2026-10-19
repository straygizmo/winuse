using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Uia;

public class TreeRetryTests
{
    private static UiNode Node(params UiNode[] children) =>
        new("u1", "Window", "w", null, null, true, false, null, false, new Rect(0, 0, 10, 10), 0, children);

    [Fact]
    public async Task ChildlessRoot_IsReadExactlyOnceMore()
    {
        var calls = 0;
        var tree = await UiaService.RetryIfChildlessAsync(() => { calls++; return Task.FromResult(Node()); }, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, calls);
        Assert.Empty(tree.Children);
    }

    [Fact]
    public async Task SecondRead_IsReturned()
    {
        var calls = 0;
        var tree = await UiaService.RetryIfChildlessAsync(
            () => Task.FromResult(++calls == 1 ? Node() : Node(Node())), TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, calls);
        Assert.Single(tree.Children);
    }

    [Fact]
    public async Task RootWithChildren_IsNotReread()
    {
        var calls = 0;
        await UiaService.RetryIfChildlessAsync(() => { calls++; return Task.FromResult(Node(Node())); }, TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, calls);
    }
}
