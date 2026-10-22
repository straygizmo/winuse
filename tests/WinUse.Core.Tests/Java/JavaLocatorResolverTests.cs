using WinUse.Core.Java;
using WinUse.Core.Refs;

namespace WinUse.Core.Tests.Java;

public class JavaLocatorResolverTests
{
    private static readonly JavaPathSegment[] ToSave =
    [
        new("root pane", null, 0), new("layered pane", null, 0), new("panel", null, 0),
        new("menu bar", null, 0), new("menu", "File", 0), new("menu item", "Save", 0),
    ];

    [Fact]
    public void Resolve_FollowsRoleNameIndex()
    {
        var jab = new FakeJabAccess(FakeTrees.Fixture());
        var root = jab.GetContextFromHwnd(jab.Hwnd)!.Value;
        var h = JavaLocatorResolver.Resolve(jab, root, ToSave);
        Assert.NotNull(h);
        Assert.Same(jab.Root.Find("menu item", "Save"), jab.NodeOf(h!.Value));
    }

    [Fact]
    public void Resolve_PicksTheIndexedSibling()
    {
        var jab = new FakeJabAccess(new FakeNode("frame", "F").Add(
            new FakeNode("push button"), new FakeNode("push button"), new FakeNode("push button")));
        var root = jab.GetContextFromHwnd(jab.Hwnd)!.Value;
        var h = JavaLocatorResolver.Resolve(jab, root, [new JavaPathSegment("push button", null, 2)]);
        Assert.Same(jab.Root.Children[2], jab.NodeOf(h!.Value));
    }

    [Fact]
    public void Resolve_FallsBackToUniqueName_WhenIndexMissing()
    {
        var jab = new FakeJabAccess(new FakeNode("frame", "F").Add(new FakeNode("push button", "Save")));
        var root = jab.GetContextFromHwnd(jab.Hwnd)!.Value;
        var h = JavaLocatorResolver.Resolve(jab, root, [new JavaPathSegment("push button", "Save", 1)]);
        Assert.Same(jab.Root.Children[0], jab.NodeOf(h!.Value));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenAmbiguousOrMissing()
    {
        var jab = new FakeJabAccess(new FakeNode("frame", "F").Add(
            new FakeNode("push button", "Save"), new FakeNode("push button", "Save")));
        var root = jab.GetContextFromHwnd(jab.Hwnd)!.Value;
        Assert.Null(JavaLocatorResolver.Resolve(jab, root, [new JavaPathSegment("push button", "Save", 5)]));
        Assert.Null(JavaLocatorResolver.Resolve(jab, root, [new JavaPathSegment("push button", "Other", 0)]));
    }

    [Fact]
    public void Resolve_ReleasesIntermediateHandles_ButNotRootOrResult()
    {
        var jab = new FakeJabAccess(FakeTrees.Fixture());
        var root = jab.GetContextFromHwnd(jab.Hwnd)!.Value;
        var h = JavaLocatorResolver.Resolve(jab, root, ToSave)!.Value;
        Assert.Equal(2, jab.LiveHandles); // root + result
        Assert.DoesNotContain(root.Ac, jab.Released);
        Assert.DoesNotContain(h.Ac, jab.Released);
        Assert.NotEmpty(jab.Released);
    }
}
