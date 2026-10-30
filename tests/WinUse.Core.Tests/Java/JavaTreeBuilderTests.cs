using WinUse.Core.Java;
using WinUse.Core.Refs;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

public class JavaTreeBuilderTests
{
    private static readonly WindowLocator Wloc = new("^WinUse Swing Fixture$", "java", "SunAwtFrame");

    private static (UiNode Tree, JavaTreeBuilder Builder, FakeJabAccess Jab, RefRegistry Refs, List<(string, JabHandle)> Kept)
        Build(FakeNode root, TreeOptions? opt = null)
    {
        var jab = new FakeJabAccess(root);
        var refs = new RefRegistry();
        var kept = new List<(string, JabHandle)>();
        var builder = new JavaTreeBuilder(jab, refs, Wloc, opt ?? new TreeOptions(), new JavaTransform(1, 0, 0), (id, h) => kept.Add((id, h)));
        var tree = builder.BuildRoot(jab.GetContextFromHwnd(jab.Hwnd)!.Value, []);
        return (tree, builder, jab, refs, kept);
    }

    private static UiNode? Find(UiNode n, string type, string? name) =>
        n.ControlType == type && n.Name == name ? n : n.Children.Select(c => Find(c, type, name)).FirstOrDefault(x => x is not null);

    [Fact]
    public void Build_CollapsesUnnamedPanes_AndKeepsInteractive()
    {
        var (tree, _, _, _, _) = Build(FakeTrees.Fixture());
        Assert.Equal("Window", tree.ControlType);
        Assert.Equal(["MenuBar", "Button", "Button", "List", "Tab", "Edit", "Text"], tree.Children.Select(c => c.ControlType));
        Assert.Equal(["Increment", "Deep"], tree.Children.Where(c => c.ControlType == "Button").Select(c => c.Name));
        Assert.DoesNotContain("Pane", Flatten(tree).Select(n => n.ControlType));
    }

    [Fact]
    public void Build_MenuItemsInClosedMenu_AreKeptOffscreen_WithParentRect()
    {
        var (tree, _, _, _, _) = Build(FakeTrees.Fixture());
        var file = Find(tree, "MenuItem", "File")!;
        Assert.False(file.Offscreen);
        var save = Assert.Single(file.Children, c => c.Name == "Save");
        Assert.Equal("MenuItem", save.ControlType);
        Assert.True(save.Offscreen);
        Assert.Equal(file.Rect, save.Rect);
        Assert.Equal(new Rect(100, 130, 40, 20), save.Rect);
    }

    [Fact]
    public void Build_DeepButton_WithinDefaultMaxDepth()
    {
        var (tree, _, _, _, _) = Build(FakeTrees.Fixture());
        var deep = Find(tree, "Button", "Deep");
        Assert.NotNull(deep);
        Assert.True(deep!.Depth <= 2, $"depth {deep.Depth}");
    }

    [Fact]
    public void Build_ListChildren_BecomeListItems()
    {
        var (tree, _, _, refs, _) = Build(FakeTrees.Fixture());
        var list = Find(tree, "List", null)!;
        Assert.Equal(["Alpha", "Beta", "Gamma"], list.Children.Select(c => c.Name));
        Assert.All(list.Children, c => Assert.Equal("ListItem", c.ControlType));
        var loc = (JavaLocator)Get(refs, list.Children[1].Ref).Locator;
        Assert.Equal(new JavaPathSegment("label", "Beta", 0), loc.Path[^1]);
    }

    [Fact]
    public void Build_IndexCountsEverySibling()
    {
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("push button").With(actions: ["click"]),
            new FakeNode("label", "between"),
            new FakeNode("push button").With(actions: ["click"]),
            new FakeNode("push button").With(actions: ["click"]));
        var (tree, _, _, refs, _) = Build(root);
        var buttons = tree.Children.Where(c => c.ControlType == "Button").ToList();
        Assert.Equal(3, buttons.Count);
        var indexes = buttons.Select(b => ((JavaLocator)Get(refs, b.Ref).Locator).Path[^1].Index);
        Assert.Equal([0, 1, 2], indexes);
    }

    [Fact]
    public void Build_RegistersJavaRefs_AndKeepsHandles_ReleasesTheRest()
    {
        var (tree, _, jab, _, kept) = Build(FakeTrees.Fixture());
        var all = Flatten(tree).ToList();
        Assert.All(all, n => Assert.StartsWith("j", n.Ref));
        Assert.Equal(all.Count, kept.Count);
        Assert.Equal(all.Count, jab.LiveHandles);           // only kept handles are alive
        Assert.NotEmpty(jab.Released);                       // the collapsed panes were released
    }

    [Fact]
    public void Build_StopsAtMaxNodes()
    {
        var list = new FakeNode("list").With(hasSelection: true);
        for (var i = 0; i < JavaTreeBuilder.MaxNodes + 1000; i++) list.Children.Add(new FakeNode("label", "item " + i));
        var (tree, builder, _, _, _) = Build(new FakeNode("frame", "F").Add(list));
        Assert.True(builder.Truncated);
        Assert.True(Flatten(tree).Count() <= JavaTreeBuilder.MaxNodes + 2);
    }

    [Fact]
    public void Build_NamedPane_IsKept_WhenItHasKeptChildren()
    {
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("panel", "Options").Add(new FakeNode("check box", "Beep").With(actions: ["click"])),
            new FakeNode("panel", "Empty").Add(new FakeNode("label")));
        var (tree, _, _, _, _) = Build(root);
        var options = Assert.Single(tree.Children);
        Assert.Equal("Pane", options.ControlType);
        Assert.Equal("Options", options.Name);
        Assert.Equal("CheckBox", Assert.Single(options.Children).ControlType);
    }

    [Fact]
    public void Build_NameFallsBackToDescription_ThenVirtualName()
    {
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("push button").With(actions: ["click"], description: "Open the file"),
            new FakeNode("push button").With(actions: ["click"], virtualName: "Virtual"),
            new FakeNode("push button").With(actions: ["click"]));
        var (tree, _, _, _, _) = Build(root);
        Assert.Equal(["Open the file", "Virtual", null], tree.Children.Select(c => c.Name));
    }

    [Fact]
    public void Build_ReadsValueForEditAndComboAndChecked()
    {
        var combo = new FakeNode("combo box").With(hasSelection: true, actions: ["togglePopup"]).Add(new FakeNode("label", "Two"));
        combo.Selected.Add(0);
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("text", "Name").With(text: "hello"),
            combo,
            new FakeNode("check box", "Enabled", "enabled,visible,showing,checked").With(actions: ["click"]),
            new FakeNode("slider").With(value: "42"));
        var (tree, _, _, _, _) = Build(root);
        Assert.Equal("hello", Find(tree, "Edit", "Name")!.Value);
        Assert.Equal("Two", Find(tree, "ComboBox", null)!.Value);
        Assert.True(Find(tree, "CheckBox", "Enabled")!.Checked);
        Assert.Equal("42", Find(tree, "Slider", null)!.Value);
    }

    [Fact]
    public void Build_IncludeOffscreen_KeepsHiddenButtons_AndMaxDepthCountsOutputDepth()
    {
        var hidden = new FakeNode("push button", "Hidden", "enabled,visible").With(actions: ["click"]);
        var root = new FakeNode("frame", "F").Add(new FakeNode("panel").Add(hidden));
        var (tree, _, _, _, _) = Build(root, new TreeOptions(IncludeOffscreen: true));
        Assert.NotNull(Find(tree, "Button", "Hidden"));
        var (shallow, _, _, _, _) = Build(root, new TreeOptions(MaxDepth: 0, IncludeOffscreen: true));
        Assert.Empty(shallow.Children);
    }

    private static IEnumerable<UiNode> Flatten(UiNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));

    private static ElementRef Get(RefRegistry refs, string id) => refs.TryGet(id, out var r) ? r : throw new KeyNotFoundException(id);
    [Fact]
    public void Build_ComboPopup_IsNotListedWhileClosed()
    {
        // Swing's JComboBox exposes its popup (role "popup menu") as a child even while closed.
        var popup = new FakeNode("popup menu", null, "enabled,visible").Add(
            new FakeNode("list", null, "enabled,visible").Add(new FakeNode("label", "One", "enabled,visible")));
        var combo = new FakeNode("combo box").With(actions: ["togglePopup"], hasSelection: true).Add(popup);
        var (tree, _, _, _, _) = Build(new FakeNode("frame", "F").Add(combo));
        var cb = Assert.Single(tree.Children);
        Assert.Equal("ComboBox", cb.ControlType);
        Assert.Empty(cb.Children);
        Assert.DoesNotContain("Menu", Flatten(tree).Select(n => n.ControlType));
    }

    [Fact]
    public void Build_AsksForVirtualName_OnceAndNeverForPanes()
    {
        var root = new FakeNode("frame", "F").Add(
            new FakeNode("panel").Add(new FakeNode("push button").With(actions: ["click"], virtualName: "Virtual")));
        var (tree, _, jab, _, _) = Build(root);
        Assert.Equal("Virtual", Assert.Single(tree.Children).Name);
        Assert.Equal(1, jab.VirtualNameCalls);
    }

    [Fact]
    public void Build_KeepsATableAsOneNode_WithoutItsCells()
    {
        var root = FakeTrees.Fixture();
        root.Add(FakeTrees.Table());
        var (tree, _, _, _, _) = Build(root);
        var table = Find(tree, "Table", "Connections")!;
        Assert.Empty(table.Children);
        Assert.Null(Find(tree, "Text", "alpha"));
        Assert.Null(Find(tree, "CheckBox", null));
    }

    [Fact]
    public void Build_WithInteractiveOnlyOff_WalksTheCells()
    {
        var root = FakeTrees.Fixture();
        root.Add(FakeTrees.Table());
        var (tree, _, _, _, _) = Build(root, new TreeOptions(InteractiveOnly: false));
        var table = Find(tree, "Table", "Connections")!;
        Assert.Equal(6, table.Children.Count);
        Assert.Equal("alpha", table.Children[0].Name);
    }

    [Fact]
    public void Build_RootedAtATable_ListsItsCells()
    {
        var (tree, _, _, _, _) = Build(FakeTrees.Table());
        Assert.Equal("Table", tree.ControlType);
        Assert.Equal(["alpha", "8080", null, "beta", "9090", null], tree.Children.Select(c => c.Name));
        Assert.Equal(["Text", "Text", "CheckBox", "Text", "Text", "CheckBox"], tree.Children.Select(c => c.ControlType));
    }

    /// <summary>Real JTable cells carry neither a "showing" state nor a position; inside a showing table they are listed as visible with the table's rectangle.</summary>
    [Fact]
    public void Build_TableCellsWithoutShowingState_AreVisibleWithTheTableRect()
    {
        var table = new FakeNode("table", "Connections", x: 100, y: 300, w: 300, h: 60) { Columns = 2 }.Add(
            new FakeNode("label", "alpha", "enabled,visible"),
            new FakeNode("label", "8080", "enabled,visible"));
        var (tree, _, _, _, _) = Build(table);
        Assert.Equal(2, tree.Children.Count);
        Assert.False(tree.Children[0].Offscreen);
        Assert.Equal(new Rect(100, 300, 300, 60), tree.Children[1].Rect);

        var hidden = new FakeNode("table", "Connections", "enabled,visible", x: 100, y: 300, w: 300, h: 60) { Columns = 2 }.Add(
            new FakeNode("label", "alpha", "enabled,visible", x: 100, y: 300, w: 150, h: 20));
        var (hiddenTree, _, _, _, _) = Build(hidden);
        Assert.Empty(hiddenTree.Children); // a table that is not showing hides its cells like any other node
    }
}
