using System.Text.Json;
using WinUse.Core.Refs;

namespace WinUse.Core.Tests.Refs;

public class RefRegistryTests
{
    [Fact]
    public void Register_AssignsKindPrefixedSequentialIds()
    {
        var reg = new RefRegistry();
        var w = new WindowLocator("^Untitled", null, null);
        Assert.Equal("win1", reg.Register(RefKind.Window, w, (nint)1).Id);
        Assert.Equal("u1", reg.Register(RefKind.Uia, new UiaLocator(w, []), null).Id);
        Assert.Equal("u2", reg.Register(RefKind.Uia, new UiaLocator(w, []), null).Id);
        Assert.Equal("w1", reg.Register(RefKind.Dom, new DomLocator(w, "#x"), null).Id);
        Assert.True(reg.TryGet("u2", out var r) && r.Kind == RefKind.Uia);
        Assert.False(reg.TryGet("u9", out _));
    }

    [Fact]
    public void Locator_RoundTripsThroughJson()
    {
        Locator l = new DomLocator(new WindowLocator("MDium", "mdium", null), "[data-testid=\"editor\"]");
        var json = JsonSerializer.Serialize(l);
        var back = JsonSerializer.Deserialize<Locator>(json);
        Assert.Equal(l, back);
    }

    [Fact]
    public void UiaLocator_RoundTripsWithDiscriminatorAndPath()
    {
        Locator l = new UiaLocator(new WindowLocator("x", null, "Notepad"),
            [new UiaPathSegment("Edit", "id1", null, 0)]);
        var json = JsonSerializer.Serialize(l);
        Assert.Contains("\"$type\":\"uia\"", json);
        var back = Assert.IsType<UiaLocator>(JsonSerializer.Deserialize<Locator>(json));
        Assert.Equal(new UiaPathSegment("Edit", "id1", null, 0), Assert.Single(back.Path));
    }

    [Fact]
    public void BeginSnapshot_KeepsOldRefsAndContinuesNumbering()
    {
        var reg = new RefRegistry();
        var w = new WindowLocator("a", null, null);
        reg.Register(RefKind.Uia, new UiaLocator(w, []), null);
        reg.BeginSnapshot();
        Assert.Equal(1, reg.CurrentSnapshot);
        Assert.True(reg.TryGet("u1", out _));
        Assert.Equal("u2", reg.Register(RefKind.Uia, new UiaLocator(w, []), null).Id);
    }

    [Fact]
    public void ElementRef_SerializesWithoutHandle()
    {
        var r = new ElementRef("u1", RefKind.Uia, new DomLocator(new WindowLocator("a", null, null), "#x"), new object());
        var json = JsonSerializer.Serialize(r);
        Assert.DoesNotContain("Handle", json);
    }
}
