using WinUse.Core.Grounding;
using WinUse.Core.Windows;
using static WinUse.Core.Tests.Grounding.G;

namespace WinUse.Core.Tests.Grounding;

public class CandidateBuilderTests
{
    static readonly Rect Win = new(0, 0, 900, 900);

    [Fact]
    public void Build_DropsUiaNodesInsideWebViewRect_AndPrefersDom()
    {
        var uia = Tree(Button("Save", new Rect(10, 10, 50, 20)), Button("WebBtn", new Rect(300, 300, 50, 20)));
        var dom = new[] { Dom("button", "WebBtn", "[data-testid=\"web\"]", new Rect(300, 300, 50, 20)) };
        var c = CandidateBuilder.Build(uia, dom, Win, new Rect(200, 200, 600, 600));
        Assert.Equal(2, c.Count);
        Assert.Contains(c, x => x.Id.StartsWith("w") && x.Label == "WebBtn");
        Assert.Equal("top-left", c.Single(x => x.Label == "Save").Region);
        Assert.Equal("web", c.Single(x => x.Id.StartsWith("w")).Container);
        Assert.Equal("Main", c.Single(x => x.Label == "Save").Container);
    }

    [Fact]
    public void Build_SkipsTextAndContainers()
    {
        var uia = Tree(Node("Text", "Hello", new Rect(0, 0, 10, 10)), Button("OK", new Rect(0, 0, 10, 10)));
        var c = CandidateBuilder.Build(uia, [], Win, null);
        Assert.Equal("OK", Assert.Single(c).Label);
    }

    [Fact]
    public void Build_LabelFallbackOrder()
    {
        var r = new Rect(0, 0, 10, 10);
        var uia = Tree(
            Node("Button", "N", r, "A", "V"),
            Node("Button", null, r, "A", "V"),
            Node("Button", "", r, "A", null),
            Node("Button", null, r, null, null));
        var c = CandidateBuilder.Build(uia, [Dom("button", "", "#sel", r)], Win, null);
        Assert.Equal(["N", "V", "A", "Button", "#sel"], c.Select(x => x.Label));
    }

    [Theory]
    [InlineData(10, 10, "top-left")]
    [InlineData(440, 10, "top-center")]
    [InlineData(880, 10, "top-right")]
    [InlineData(10, 440, "middle-left")]
    [InlineData(440, 440, "middle-center")]
    [InlineData(880, 440, "middle-right")]
    [InlineData(10, 880, "bottom-left")]
    [InlineData(440, 880, "bottom-center")]
    [InlineData(880, 880, "bottom-right")]
    [InlineData(-500, 2000, "bottom-left")]
    public void Build_RegionBuckets_AllNine(int x, int y, string region)
    {
        var uia = Tree(Button("B", new Rect(x - 5, y - 5, 10, 10)));
        var c = CandidateBuilder.Build(uia, [], Win, null);
        Assert.Equal(region, Assert.Single(c).Region);
    }

    [Fact]
    public void Build_EditableRoles()
    {
        var r = new Rect(0, 0, 10, 10);
        var c = CandidateBuilder.Build(Tree(Node("Edit", "e", r), Button("b", r)), [Dom("textbox", "t", "#t", r), Dom("button", "d", "#d", r)], Win, null);
        Assert.Equal([true, false, true, false], c.Select(x => x.Editable));
    }

    [Fact]
    public void SplitForChoice_KeepsGroupsUnder255()
    {
        var many = Enumerable.Range(0, 600).Select(i => Cand($"u{i}", container: $"Group{i / 100}")).ToList();
        var groups = CandidateBuilder.SplitForChoice(many);
        Assert.True(groups.Count >= 3);
        Assert.All(groups, g => Assert.True(g.Count <= 254));
        Assert.Equal(600, groups.Sum(g => g.Count));
    }

    [Fact]
    public void SplitForChoice_SmallIsOneGroup()
    {
        var groups = CandidateBuilder.SplitForChoice([Cand("a"), Cand("b", container: "z")]);
        Assert.Single(groups);
    }

    [Fact]
    public void SplitForChoice_ChunksOversizeGroup()
    {
        var many = Enumerable.Range(0, 600).Select(i => Cand($"u{i}")).ToList();
        var groups = CandidateBuilder.SplitForChoice(many);
        Assert.Equal([254, 254, 92], groups.Select(g => g.Count));
        Assert.Equal("u0", groups[0][0].Id);
    }
}
