using WinUse.Core.Web;

namespace WinUse.Core.Tests.Web;

[Trait("Category", "Integration")]
[Collection("Desktop")]
public class DomCollectorTests(EdgeCdpFixture fx) : IClassFixture<EdgeCdpFixture>
{
    private async Task<IReadOnlyList<DomNode>> Dom(string html, bool interactiveOnly = true)
    {
        await fx.Web.CurrentPage!.SetContentAsync(html);
        return await fx.Web.GetDomAsync(interactiveOnly);
    }

    [Fact]
    public async Task HiddenElements_AreExcluded()
    {
        var n = await Dom("<button id='a'>A</button><button id='b' style='display:none'>B</button><button id='c' style='visibility:hidden'>C</button><input type='hidden' id='h'>");
        Assert.Equal(["#a"], n.Select(x => x.Selector));
    }

    [Fact]
    public async Task Name_FromLabel_Placeholder_AriaLabel()
    {
        var n = await Dom("<label for='e'>Email</label><input id='e'><input id='p' placeholder='Phone'><input id='z' aria-label='Zip'><label>Wrapped <input id='w'></label>");
        Assert.Equal("Email", n.Single(x => x.Selector == "#e").Name);
        Assert.Equal("Phone", n.Single(x => x.Selector == "#p").Name);
        Assert.Equal("Zip", n.Single(x => x.Selector == "#z").Name);
        Assert.Contains("Wrapped", n.Single(x => x.Selector == "#w").Name);
    }

    [Fact]
    public async Task Checkbox_ValueAndPassword()
    {
        var n = await Dom("<input type='checkbox' id='c' checked><input type='password' id='p' value='secret'><input id='t' value='hi' disabled>");
        Assert.True(n.Single(x => x.Selector == "#c").Checked);
        Assert.Null(n.Single(x => x.Selector == "#p").Value);
        var t = n.Single(x => x.Selector == "#t");
        Assert.Equal("hi", t.Value);
        Assert.False(t.Enabled);
        Assert.Null(t.Checked);
    }

    [Fact]
    public async Task NonInteractive_IncludesTextElements()
    {
        var n = await Dom("<h1>Title</h1><p>text</p><button id='b'>Go</button>", interactiveOnly: false);
        Assert.Contains(n, x => x.Tag == "h1" && x.Name == "Title");
        Assert.Contains(n, x => x.Tag == "p");
        Assert.Contains(n, x => x.Selector == "#b");
    }

    [Fact]
    public async Task CssPath_Fallback_ForDuplicateNames()
    {
        var n = await Dom("<div><a href='#'>Same</a><a href='#'>Same</a></div>");
        Assert.Equal(2, n.Count);
        Assert.All(n, x => Assert.StartsWith("body", x.Selector));
        Assert.Equal(2, n.Select(x => x.Selector).Distinct().Count());
    }
}
