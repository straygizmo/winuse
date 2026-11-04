using System.Text.RegularExpressions;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Uia;

public class UiTreeScopeTests
{
    private static UiNode Node(string id, string type, string? name = null, bool focused = false, params UiNode[] children) =>
        new(id, type, name, null, null, true, false, null, focused, new Rect(1, 2, 30, 40), 0, children);

    /// <summary>A browser: toolbar, favourites, tab strip, a side pane with its own document and the active tab's page.</summary>
    private static UiNode Browser() =>
        Node("u1", "Window", "Page - Browser", true,
            Node("u2", "ToolBar", "App bar", false, Node("u3", "Edit", "Address"), Node("u4", "Button", "Chat")),
            Node("u5", "ToolBar", "Favourites", false, Node("u6", "ListItem", "Mail"), Node("u7", "ListItem", "News")),
            Node("u8", "Pane", "Side pane", false,
                Node("u9", "Document", "Assistant", false, Node("u10", "Edit", "Ask me anything"), Node("u11", "Button", "Send"))),
            Node("u12", "Pane", null, false,
                Node("u13", "Document", "Page", false, Node("u14", "Hyperlink", "Learn more", true), Node("u15", "Button", "Submit"))),
            Node("u16", "Tab", "Tab strip", false, Node("u17", "TabItem", "Page"), Node("u18", "TabItem", "Other")));

    [Fact]
    public void FindScope_ByType_PrefersTheElementContainingTheFocus()
    {
        var picked = UiTreeShaper.FindScope(Browser(), "document", null, out var matches);
        Assert.Equal("u13", picked!.Ref);
        Assert.Equal(["u9", "u13"], matches.Select(m => m.Ref));
    }

    [Fact]
    public void FindScope_ByTypeAndName_PicksTheNamedOne()
    {
        var picked = UiTreeShaper.FindScope(Browser(), "Document", new Regex("assist", RegexOptions.IgnoreCase), out _);
        Assert.Equal("u9", picked!.Ref);
        Assert.Equal(["u10", "u11"], picked.Children.Select(c => c.Ref));
    }

    [Fact]
    public void FindScope_ByNameAlone_TakesTheFirstMatch()
    {
        var picked = UiTreeShaper.FindScope(Browser(), null, new Regex("^Favourites$"), out var matches);
        Assert.Equal("u5", picked!.Ref);
        Assert.Single(matches);
    }

    [Fact]
    public void FindScope_WithoutFocus_TakesTheFirstInDocumentOrder()
    {
        var tree = Node("u1", "Window", "W", false, Node("u2", "Document", "A"), Node("u3", "Document", "B"));
        Assert.Equal("u2", UiTreeShaper.FindScope(tree, "Document", null, out _)!.Ref);
    }

    [Fact]
    public void FindScope_FocusedElementItselfWins()
    {
        var tree = Node("u1", "Window", "W", false, Node("u2", "Document", "A", false, Node("u4", "Edit", "x", true)), Node("u3", "Document", "B", true));
        Assert.Equal("u3", UiTreeShaper.FindScope(tree, "Document", null, out _)!.Ref);
    }

    [Fact]
    public void FindScope_NothingMatches_ReturnsNullAndNoMatches()
    {
        Assert.Null(UiTreeShaper.FindScope(Browser(), "Table", null, out var matches));
        Assert.Empty(matches);
        Assert.Null(UiTreeShaper.FindScope(Browser(), "Document", new Regex("nowhere"), out _));
    }

    [Fact]
    public void FindScope_NeverReturnsTheRoot()
    {
        Assert.Null(UiTreeShaper.FindScope(Browser(), "Window", null, out _));
    }

    [Fact]
    public void Rebase_CountsDepthFromTheScopedNode()
    {
        var deep = Node("u9", "Document", "D", false, Node("u10", "Edit", "E", false, Node("u11", "Button", "B"))) with { Depth = 5 };
        var r = UiTreeShaper.Rebase(deep);
        Assert.Equal(0, r.Depth);
        Assert.Equal(1, r.Children[0].Depth);
        Assert.Equal(2, r.Children[0].Children[0].Depth);
    }
}
