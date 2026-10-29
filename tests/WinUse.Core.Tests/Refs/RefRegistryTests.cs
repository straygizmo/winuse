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
    public void Register_Window_ReusesTheRefForTheSameHandle()
    {
        var reg = new RefRegistry();
        var first = reg.Register(RefKind.Window, new WindowLocator("^Untitled - Notepad$", "notepad", null), (nint)0x1234);
        reg.BeginSnapshot();
        var again = reg.Register(RefKind.Window, new WindowLocator("^hello - Notepad$", "notepad", null), (nint)0x1234);
        var other = reg.Register(RefKind.Window, new WindowLocator("^Calculator$", "calc", null), (nint)0x5678);
        Assert.Equal("win1", first.Id);
        Assert.Equal("win1", again.Id);
        Assert.Equal("win2", other.Id);
        Assert.True(reg.TryGet("win1", out var r));
        Assert.Equal("^hello - Notepad$", ((WindowLocator)r.Locator).TitleRegex); // locator follows the current title
        Assert.Equal((nint)0x1234, r.Handle);
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

    [Fact]
    public void PointLocator_RoundTrips()
    {
        var l = new PointLocator(new WindowLocator("^VirtualBox$", "VirtualBoxVM", null), 0.25, 0.75);
        var json = JsonSerializer.Serialize<Locator>(l);
        Assert.Contains("\"$type\":\"point\"", json);
        var back = Assert.IsType<PointLocator>(JsonSerializer.Deserialize<Locator>(json));
        Assert.Equal(l, back);
    }

    [Fact]
    public void Register_Point_UsesThePtPrefix()
    {
        var reg = new RefRegistry();
        var r = reg.Register(RefKind.Point, new PointLocator(new WindowLocator("t", null, null), 0.5, 0.5), (10, 20));
        Assert.Equal("pt1", r.Id);
        Assert.Equal("pt2", reg.Register(RefKind.Point, r.Locator, null).Id);
    }
    [Fact]
    public void Register_JavaRef_UsesJPrefix()
    {
        var reg = new RefRegistry();
        var w = new WindowLocator("^Swing", null, "SunAwtFrame");
        Assert.Equal("j1", reg.Register(RefKind.Java, new JavaLocator(w, []), null).Id);
        Assert.Equal("j2", reg.Register(RefKind.Java, new JavaLocator(w, []), null).Id);
        Assert.True(reg.TryGet("j2", out var r) && r.Kind == RefKind.Java);
    }

    [Fact]
    public void JavaLocator_RoundTripsWithDiscriminatorAndRole()
    {
        Locator l = new JavaLocator(new WindowLocator("x", "javaw", "SunAwtFrame"), [new JavaPathSegment("push button", "Save", 1)]);
        var json = JsonSerializer.Serialize(l);
        Assert.Contains("\"$type\":\"java\"", json);
        var back = Assert.IsType<JavaLocator>(JsonSerializer.Deserialize<Locator>(json));
        Assert.Equal(new JavaPathSegment("push button", "Save", 1), Assert.Single(back.Path));
        Assert.Equal("SunAwtFrame", back.Window.ClassName);
    }
}
