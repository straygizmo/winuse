using System.Net;
using System.Net.Sockets;
using WinUse.Core.Refs;
using WinUse.Core.Web;

namespace WinUse.Core.Tests.Web;

[Trait("Category", "Integration")]
[Collection("Desktop")]
public class WebServiceTests(EdgeCdpFixture fx) : IClassFixture<EdgeCdpFixture>
{
    private WebService Web => fx.Web;

    private async Task Set(string html) => await Web.CurrentPage!.SetContentAsync(html);

    [Fact]
    public async Task GetDom_ListsInteractiveElementsWithStableSelectors()
    {
        await Set("<button data-testid='save'>Save</button><input id='q'><p>text</p>");
        var nodes = await Web.GetDomAsync();
        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, n => n.Selector == "[data-testid=\"save\"]" && n.Role == "button" && n.Name == "Save");
        Assert.Contains(nodes, n => n.Selector == "#q" && n.Role == "textbox");
    }

    [Fact]
    public async Task Fill_ThenGetText_RoundTrips()
    {
        await Set("<button data-testid='save'>Save</button><input id='q'><p>text</p>");
        var nodes = await Web.GetDomAsync();
        var input = nodes.Single(n => n.Selector == "#q");
        await Web.FillAsync(input.Ref, "abc");
        Assert.Equal("abc", await Web.GetTextAsync(input.Ref));
    }

    [Fact]
    public async Task Click_ChangesDom()
    {
        await Set("<title>t</title><button id='b' onclick=\"document.title='clicked'\">Go</button>");
        var b = (await Web.GetDomAsync()).Single(n => n.Selector == "#b");
        await Web.ClickAsync(b.Ref);
        Assert.Equal("clicked", await Web.CurrentPage!.TitleAsync());
        Assert.Contains(await Web.ListPagesAsync(), p => p.Title == "clicked");
    }

    [Fact]
    public async Task SelectOption_ByLabel()
    {
        await Set("<select id='s'><option value='1'>One</option><option value='2'>Two</option></select>");
        var s = (await Web.GetDomAsync()).Single(n => n.Selector == "#s");
        Assert.Equal("combobox", s.Role);
        await Web.SelectOptionAsync(s.Ref, "Two");
        Assert.Equal("2", await Web.GetTextAsync(s.Ref));
    }

    [Fact]
    public async Task Resolve_ReturnsSelectorOnlyWhenUnique()
    {
        await Set("<button class='x'>A</button><button class='x'>B</button><input id='q'>");
        var win = new WindowLocator(".", null, null);
        Assert.Equal("#q", await Web.ResolveAsync(new DomLocator(win, "#q")));
        Assert.Null(await Web.ResolveAsync(new DomLocator(win, "button.x")));
        Assert.Null(await Web.ResolveAsync(new DomLocator(win, "#missing")));
        Assert.True(await Web.ExistsAsync("button.x"));
        Assert.Equal("A", await Web.GetTextBySelectorAsync("button.x >> nth=0"));
    }

    [Fact]
    public async Task GetDom_RoleNameSelector_WhenNoTestIdOrId()
    {
        await Set("<button>A</button><button>B</button>");
        var nodes = await Web.GetDomAsync();
        Assert.Contains(nodes, n => n.Selector == "role=button[name=\"A\"]");
        Assert.Contains(nodes, n => n.Selector == "role=button[name=\"B\"]");
    }

    [Fact]
    public async Task ListPages_And_SelectPage()
    {
        var pages = await Web.ListPagesAsync();
        Assert.NotEmpty(pages);
        await Web.SelectPageAsync(pages[0].Id);
    }

    [Fact]
    public async Task UnknownRef_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Web.ClickAsync("nope"));
    }

    [Fact]
    public async Task ConnectAsync_TimesOutWhenNothingListens()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await using var w = new WebService(new RefRegistry());
        await Assert.ThrowsAsync<TimeoutException>(() => w.ConnectAsync(port, TimeSpan.FromSeconds(1)));
    }
}
