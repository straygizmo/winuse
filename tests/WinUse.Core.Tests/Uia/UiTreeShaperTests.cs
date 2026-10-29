using System.Text.RegularExpressions;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Uia;

public class UiTreeShaperTests
{
    private static UiNode Node(string id, string type, string? name = null, params UiNode[] children) =>
        new(id, type, name, null, null, true, false, null, false, new Rect(1, 2, 30, 40), 0, children);

    private static UiNode Dialog() =>
        Node("u1", "Window", "File dialog",
            Node("u2", "Pane", null,
                Node("u3", "List", "Items",
                    Node("u4", "ListItem", "row 1", Node("u5", "Edit", "Name"), Node("u6", "Edit", "Date")),
                    Node("u7", "ListItem", "row 2", Node("u8", "Edit", "Name"), Node("u9", "Edit", "Date"))),
                Node("u10", "Edit", "Folder:"),
                Node("u11", "Button", "Select Folder"),
                Node("u12", "Button", "Cancel")));

    [Fact]
    public void Filter_ByControlType_HoistsMatchesAndDropsTheRest()
    {
        var t = UiTreeShaper.Filter(Dialog(), ["edit", "Button"], null);
        Assert.Equal("u1", t.Ref);                                   // root always kept
        Assert.Equal(["u5", "u6", "u8", "u9", "u10", "u11", "u12"], t.Children.Select(c => c.Ref));
        Assert.All(t.Children, c => Assert.Empty(c.Children));
        Assert.All(t.Children, c => Assert.Equal(1, c.Depth));       // depths follow the new shape
    }

    [Fact]
    public void Filter_ByName_IsCombinedWithControlType()
    {
        var t = UiTreeShaper.Filter(Dialog(), ["Edit", "Button"], new Regex("^(Folder|Select)", RegexOptions.IgnoreCase));
        Assert.Equal(["u10", "u11"], t.Children.Select(c => c.Ref));
    }

    [Fact]
    public void Filter_KeepsMatchingAncestorsWithTheirMatchingDescendants()
    {
        var t = UiTreeShaper.Filter(Dialog(), ["ListItem", "Edit"], null);
        var rows = t.Children;
        Assert.Equal(["u4", "u7", "u10"], rows.Select(c => c.Ref));
        Assert.Equal(["u5", "u6"], rows[0].Children.Select(c => c.Ref));
    }

    [Fact]
    public void Filter_WithoutCriteria_ReturnsTheSameTree()
    {
        var d = Dialog();
        Assert.Same(d, UiTreeShaper.Filter(d, null, null));
        Assert.Same(d, UiTreeShaper.Filter(d, [], null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("Edit", new[] { "Edit" })]
    [InlineData("Edit, Button,,ComboBox ", new[] { "Edit", "Button", "ComboBox" })]
    public void ParseControlTypes_SplitsOnCommas(string? input, string[]? expected) =>
        Assert.Equal(expected, UiTreeShaper.ParseControlTypes(input));

    [Fact]
    public void Truncate_KeepsDocumentOrderAndCountsOmitted()
    {
        var d = Dialog();
        Assert.Equal(12, UiTreeShaper.Count(d));
        var (t, omitted) = UiTreeShaper.Truncate(d, 5);
        Assert.Equal(7, omitted);
        Assert.Equal(5, UiTreeShaper.Count(t));
        // u1 > u2 > u3 > u4 > u5: the first five in pre-order.
        Assert.Equal("u5", t.Children[0].Children[0].Children[0].Children[0].Ref);
        Assert.Single(t.Children[0].Children);                       // u10.. dropped
    }

    [Fact]
    public void Truncate_WithinBudget_ReturnsTheSameTree()
    {
        var d = Dialog();
        var (t, omitted) = UiTreeShaper.Truncate(d, 12);
        Assert.Same(d, t);
        Assert.Equal(0, omitted);
        Assert.Throws<ArgumentOutOfRangeException>(() => UiTreeShaper.Truncate(d, 0));
    }

    [Fact]
    public void Compact_OmitsDefaultsAndPacksRect()
    {
        var plain = new UiNode("u1", "Edit", null, null, null, true, false, null, false, new Rect(1, 2, 30, 40), 3, []);
        var o = UiTreeShaper.Compact(plain);
        Assert.Equal(["ref", "controlType", "rect"], o.Select(kv => kv.Key));
        Assert.Equal("1,2,30,40", o["rect"]!.GetValue<string>());

        var rich = new UiNode("u2", "CheckBox", "Enabled", "chk", "v", false, true, true, true, new Rect(0, 0, 0, 0), 0,
            [plain]);
        var r = UiTreeShaper.Compact(rich);
        Assert.Equal("Enabled", r["name"]!.GetValue<string>());
        Assert.Equal("chk", r["automationId"]!.GetValue<string>());
        Assert.Equal("v", r["value"]!.GetValue<string>());
        Assert.False(r["enabled"]!.GetValue<bool>());
        Assert.True(r["offscreen"]!.GetValue<bool>());
        Assert.True(r["checked"]!.GetValue<bool>());
        Assert.True(r["focused"]!.GetValue<bool>());
        Assert.Null(r["depth"]);
        Assert.Single(r["children"]!.AsArray());
    }
}
