using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Uia;

public class UiaVisibilityTests
{
    private static readonly UiaClip List = new("List", "Messages", new Rect(0, 100, 400, 300));

    [Fact]
    public void InsideTheViewport_IsClickableAtItsFullRect()
    {
        var v = UiaVisibility.Compute(new Rect(10, 150, 100, 20), false, [List]);
        Assert.Equal(new Rect(10, 150, 100, 20), v.VisibleRect);
        Assert.Null(v.ClippedBy);
        Assert.Null(v.NotClickableReason("u1"));
    }

    [Fact]
    public void ScrolledOutOfTheViewport_IsRefusedAndNamesTheContainer()
    {
        // Below the list's bottom edge (y 400): its centre would be on whatever lies under the list.
        var v = UiaVisibility.Compute(new Rect(10, 450, 100, 20), false, [List]);
        Assert.True(v.VisibleRect.IsEmpty);
        Assert.Equal(List, v.ClippedBy);
        var reason = v.NotClickableReason("u9");
        Assert.NotNull(reason);
        Assert.Contains("u9", reason);
        Assert.Contains("List 'Messages'", reason);
        Assert.Contains("Nothing was clicked", reason);
    }

    [Fact]
    public void PartlyVisible_IsClippedToTheVisiblePart()
    {
        var v = UiaVisibility.Compute(new Rect(10, 390, 100, 40), false, [List]);
        Assert.Equal(new Rect(10, 390, 100, 10), v.VisibleRect);
        Assert.Null(v.NotClickableReason("u1"));
    }

    [Fact]
    public void ASliverAtTheViewportEdge_IsRefused()
    {
        // One pixel of the item shows above the list's bottom edge; its centre is on whatever lies below the list.
        var v = UiaVisibility.Compute(new Rect(10, 399, 100, 30), false, [List]);
        Assert.Equal(1, v.VisibleRect.Height);
        Assert.Contains("almost entirely outside", v.NotClickableReason("u5"));
        // A small element that is wholly visible stays clickable.
        Assert.Null(UiaVisibility.Compute(new Rect(10, 150, 3, 3), false, [List]).NotClickableReason("u6"));
    }

    [Fact]
    public void NestedViewports_AreAllApplied_InnermostFirst()
    {
        var outer = new UiaClip("Document", "Page", new Rect(0, 0, 400, 200));
        var v = UiaVisibility.Compute(new Rect(10, 250, 100, 20), false, [List, outer]);
        Assert.True(v.VisibleRect.IsEmpty);
        Assert.Equal(outer, v.ClippedBy);
    }

    [Fact]
    public void AnEmptyAncestorRect_IsIgnored()
    {
        var v = UiaVisibility.Compute(new Rect(10, 150, 100, 20), false, [new UiaClip("Pane", null, new Rect(0, 0, 0, 0))]);
        Assert.Null(v.NotClickableReason("u1"));
    }

    [Fact]
    public void OffscreenOrEmpty_IsRefused()
    {
        Assert.Contains("offscreen", UiaVisibility.Compute(new Rect(10, 150, 100, 20), true, []).NotClickableReason("u1"));
        Assert.Contains("no visible area", UiaVisibility.Compute(new Rect(10, 150, 0, 20), false, []).NotClickableReason("u1"));
    }
}
