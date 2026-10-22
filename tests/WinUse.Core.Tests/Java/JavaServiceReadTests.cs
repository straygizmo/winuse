using WinUse.Core.Input;
using WinUse.Core.Java;
using WinUse.Core.Refs;
using WinUse.Core.Threading;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

/// <summary>A JavaService over a fake tree and a fake window; shared by the read and action tests.</summary>
internal sealed class JavaServiceHarness : IDisposable
{
    public const nint Hwnd = 0x1234;
    public FakeJabAccess Jab { get; }
    public FakeJabBridge Bridge { get; }
    public RefRegistry Refs { get; } = new();
    public AutomationDispatcher Dispatcher { get; } = new();
    public JavaService Service { get; }
    public WindowInfo Window { get; set; }
    public List<(int X, int Y)> Clicks { get; } = [];
    public List<KeyChord> Pressed { get; } = [];

    public JavaServiceHarness(FakeNode? root = null, bool available = true, string? reason = null, Rect? windowRect = null)
    {
        Jab = new FakeJabAccess(root ?? FakeTrees.Fixture(), Hwnd);
        Bridge = new FakeJabBridge(Jab, available, reason);
        Window = new WindowInfo(Hwnd, "WinUse Swing Fixture", "SunAwtFrame", "java", 1, windowRect ?? new Rect(100, 100, 640, 480), WindowState.Normal);
        var windows = new WindowService(Dispatcher);
        Service = new JavaService(Bridge, Refs, windows, new InputService(Dispatcher))
        {
            WindowLookup = h => Task.FromResult(h == Hwnd ? Window : null),
            WindowFind = _ => Task.FromResult<WindowInfo?>(Window),
            DpiScale = _ => 1.0,
            ClickAt = (x, y) => { Clicks.Add((x, y)); return Task.CompletedTask; },
            Press = k => { Pressed.Add(k); return Task.CompletedTask; },
            Activate = _ => Task.CompletedTask,
        };
    }

    public Task<UiNode> TreeAsync() => Service.GetTreeAsync(Hwnd, new TreeOptions());

    public static UiNode? Find(UiNode n, string type, string? name) =>
        n.ControlType == type && n.Name == name ? n : n.Children.Select(c => Find(c, type, name)).FirstOrDefault(x => x is not null);

    public void Dispose()
    {
        Service.Dispose();
        Dispatcher.Dispose();
    }
}

public class JavaServiceReadTests
{
    [Fact]
    public async Task GetVersionAsync_ReturnsTheJvmVersion_AndReleasesTheRoot()
    {
        using var h = new JavaServiceHarness();
        var v = await h.Service.GetVersionAsync(h.Window);
        Assert.Equal("fake 1.8.0", v!.VmVersion);
        Assert.Equal(0, h.Jab.LiveHandles);
    }

    [Fact]
    public async Task GetLayoutAsync_ReturnsTheLayoutTheClientSendsWith()
    {
        using var h = new JavaServiceHarness();
        Assert.Equal(JabLayout.Wide, await h.Service.GetLayoutAsync(h.Window));
        Assert.Null(await h.Service.GetLayoutAsync(h.Window with { Hwnd = 0x9999 }));
    }

    [Fact]
    public async Task GetVersionAsync_IsNull_WhenTheBridgeIsUnavailable()
    {
        using var h = new JavaServiceHarness(available: false, reason: "WINUSE_JAB=off");
        Assert.Null(await h.Service.GetVersionAsync(h.Window));
    }

    [Fact]
    public async Task GetTree_UsesCalibratedCoordinates()
    {
        using var h = new JavaServiceHarness();
        var tree = await h.TreeAsync();
        Assert.Equal(new Rect(120, 160, 80, 24), JavaServiceHarness.Find(tree, "Button", "Increment")!.Rect);

        // The same Java coordinates seen through a 2x virtualized window map onto the doubled rectangle.
        h.Window = h.Window with { Rect = new Rect(200, 200, 1280, 960) };
        var scaled = await h.TreeAsync();
        Assert.Equal(new Rect(240, 320, 160, 48), JavaServiceHarness.Find(scaled, "Button", "Increment")!.Rect);
    }

    [Fact]
    public async Task GetElement_ReadsValueForEdit()
    {
        using var h = new JavaServiceHarness();
        var tree = await h.TreeAsync();
        var edit = JavaServiceHarness.Find(tree, "Edit", "Name")!;
        var el = await h.Service.GetElementAsync(edit.Ref);
        Assert.Equal("hello", el.Value);
        Assert.Equal(edit.Ref, el.Ref);
        Assert.Equal(new Rect(200, 160, 100, 24), await h.Service.GetRectAsync(edit.Ref));
    }

    [Fact]
    public async Task GetText_PrefersAccessibleText_ThenValue_ThenName()
    {
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("text", "Name").With(text: "typed"),
            new FakeNode("slider").With(value: "7"),
            new FakeNode("push button", "Go").With(actions: ["click"]));
        using var h = new JavaServiceHarness(root);
        var tree = await h.TreeAsync();
        Assert.Equal("typed", await h.Service.GetTextAsync(JavaServiceHarness.Find(tree, "Edit", "Name")!.Ref));
        Assert.Equal("7", await h.Service.GetTextAsync(JavaServiceHarness.Find(tree, "Slider", null)!.Ref));
        Assert.Equal("Go", await h.Service.GetTextAsync(JavaServiceHarness.Find(tree, "Button", "Go")!.Ref));
    }

    [Fact]
    public async Task IsJavaWindow_FalseForNonSunAwt_WithoutTouchingBridge()
    {
        using var h = new JavaServiceHarness(available: false);
        var notepad = new WindowInfo(0x99, "Untitled - Notepad", "Notepad", "notepad", 2, new Rect(0, 0, 10, 10), WindowState.Normal);
        Assert.False(JavaService.LooksLikeJavaWindow(notepad));
        Assert.False(await h.Service.IsJavaWindowAsync(notepad));
        Assert.Null(h.Service.UnavailableReason(notepad));
    }

    [Fact]
    public async Task IsJavaWindow_FalseWithReason_WhenBridgeUnavailable()
    {
        using var h = new JavaServiceHarness(available: false, reason: "not found (searched: nowhere)");
        Assert.True(JavaService.LooksLikeJavaWindow(h.Window));
        Assert.False(await h.Service.IsJavaWindowAsync(h.Window));
        Assert.Equal("not found (searched: nowhere)", h.Service.UnavailableReason(h.Window));
    }

    [Fact]
    public async Task IsJavaWindow_FalseWithReason_WhenBridgeNotResponding()
    {
        using var h = new JavaServiceHarness();
        h.Jab.JavaWindow = false;
        Assert.False(await h.Service.IsJavaWindowAsync(h.Window));
        Assert.Contains("winuse doctor", h.Service.UnavailableReason(h.Window));
    }

    [Fact]
    public async Task IsJavaWindow_True_ClearsReason()
    {
        using var h = new JavaServiceHarness();
        Assert.True(await h.Service.IsJavaWindowAsync(h.Window));
        Assert.Null(h.Service.UnavailableReason(h.Window));
    }

    [Fact]
    public async Task GetTree_ReleasesHandles_TwoSnapshotsLater()
    {
        using var h = new JavaServiceHarness();
        var first = await h.TreeAsync();
        var firstHandles = h.Jab.LiveHandles;
        var second = await h.TreeAsync();
        Assert.Equal(firstHandles * 2, h.Jab.LiveHandles); // both snapshots alive
        await h.TreeAsync();
        Assert.Equal(firstHandles * 2, h.Jab.LiveHandles); // first snapshot released, two newest alive
        // The first snapshot's ref still works: it re-resolves through its locator instead of touching the released handle.
        var el = await h.Service.GetElementAsync(JavaServiceHarness.Find(first, "Button", "Increment")!.Ref);
        Assert.Equal("Increment", el.Name);
        _ = second;
    }

    [Fact]
    public async Task RegisterResolved_LazyRegistersEvenWhenMissing()
    {
        using var h = new JavaServiceHarness();
        var w = new WindowLocator("^WinUse Swing Fixture$", "java", "SunAwtFrame");
        var missing = new JavaLocator(w, [new JavaPathSegment("push button", "Nope", 0)]);
        Assert.Null(await h.Service.RegisterResolvedAsync(missing, lazy: false));
        var lazyId = await h.Service.RegisterResolvedAsync(missing, lazy: true);
        Assert.NotNull(lazyId);
        Assert.StartsWith("j", lazyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.GetElementAsync(lazyId!));

        var save = new JavaLocator(w, [
            new("root pane", null, 0), new("layered pane", null, 0), new("panel", null, 0),
            new("menu bar", null, 0), new("menu", "File", 0), new("menu item", "Save", 0)]);
        var id = await h.Service.RegisterResolvedAsync(save, lazy: false);
        Assert.NotNull(id);
        Assert.Equal("Save", (await h.Service.GetElementAsync(id!)).Name);
    }

    [Fact]
    public async Task GetTree_WithScope_ReturnsSubtree()
    {
        using var h = new JavaServiceHarness();
        var tree = await h.TreeAsync();
        var list = JavaServiceHarness.Find(tree, "List", null)!;
        var sub = await h.Service.GetTreeAsync(JavaServiceHarness.Hwnd, new TreeOptions(ScopeRef: list.Ref));
        Assert.Equal("List", sub.ControlType);
        Assert.Equal(3, sub.Children.Count);
    }

    [Fact]
    public async Task GetTree_WhenBridgeUnavailable_Throws()
    {
        using var h = new JavaServiceHarness(available: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.TreeAsync());
        Assert.Contains("not found", ex.Message);
    }
}
