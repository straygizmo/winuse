using WinUse.Core.Input;
using WinUse.Core.Java;
using WinUse.Core.Uia;

namespace WinUse.Core.Tests.Java;

public class JavaServiceActionTests
{
    private static async Task<(JavaServiceHarness H, UiNode Tree)> Up(FakeNode? root = null)
    {
        var h = new JavaServiceHarness(root);
        return (h, await h.TreeAsync());
    }

    private static string Ref(UiNode tree, string type, string? name) => JavaServiceHarness.Find(tree, type, name)!.Ref;

    [Fact]
    public async Task Invoke_UsesClickAction()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.InvokeAsync(Ref(tree, "Button", "Increment"));
            var (node, action) = Assert.Single(h.Jab.Actions);
            Assert.Equal("Increment", node.Name);
            Assert.Equal("click", action);
            Assert.Empty(h.Clicks);
        }
    }

    [Fact]
    public async Task Invoke_OnClosedMenuItem_DoesNotOpenMenu()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.InvokeAsync(Ref(tree, "MenuItem", "Save"));
            var (node, action) = Assert.Single(h.Jab.Actions);
            Assert.Equal("Save", node.Name);
            Assert.Equal("click", action);
            Assert.DoesNotContain(h.Jab.Actions, a => a.Node.Name == "File");
        }
    }

    [Fact]
    public async Task Invoke_FallsBackToParentSelection_ForTab()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.InvokeAsync(Ref(tree, "TabItem", "Second"));
            Assert.Empty(h.Jab.Actions);
            var (parent, index) = Assert.Single(h.Jab.Selections);
            Assert.Equal("page tab list", parent.Role);
            Assert.Equal(1, index);
        }
    }

    [Fact]
    public async Task Invoke_FallsBackToCenterClick_WhenNothingElse()
    {
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(
            new FakeNode("panel", x: 0, y: 0, w: 400, h: 300).Add(new FakeNode("hyperlink", "Docs", x: 10, y: 20, w: 100, h: 20)));
        var (h, tree) = await Up(root);
        using (h)
        {
            h.Window = h.Window with { Rect = new WinUse.Core.Windows.Rect(0, 0, 400, 300) };
            await h.Service.InvokeAsync(Ref(tree, "Hyperlink", "Docs"));
            Assert.Equal((60, 30), Assert.Single(h.Clicks));
        }
    }

    [Fact]
    public async Task Invoke_Throws_WhenActionFails()
    {
        var (h, tree) = await Up();
        using (h)
        {
            h.Jab.OnAction = (_, _) => false;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InvokeAsync(Ref(tree, "Button", "Increment")));
            Assert.Contains("click", ex.Message);
        }
    }

    [Fact]
    public async Task Invoke_AfterThreeSnapshots_ReresolvesInsteadOfUsingReleasedHandle()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var old = Ref(tree, "Button", "Increment");
            await h.TreeAsync();
            await h.TreeAsync(); // the first snapshot's handles are released now
            await h.Service.InvokeAsync(old); // the fake throws on use-after-release
            var (node, action) = Assert.Single(h.Jab.Actions);
            Assert.Equal("Increment", node.Name);
            Assert.Equal("click", action);
        }
    }

    [Fact]
    public async Task SetText_UsesSetTextContents()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world");
            Assert.Equal("world", h.Jab.Root.Find("text", "Name")!.Text);
            Assert.Empty(h.Pressed);
        }
    }

    [Fact]
    public async Task SetText_FallsBackToFocusAndTyping_WhenRejected()
    {
        var (h, tree) = await Up();
        using (h)
        {
            h.Jab.Root.Find("text", "Name")!.RejectSetText = true;
            var typed = new List<string>();
            h.Service.TypeText = t => { typed.Add(t); return Task.CompletedTask; };
            await h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world");
            Assert.Contains(h.Jab.Focused, n => n.Name == "Name");
            var chord = Assert.Single(h.Pressed);
            Assert.Equal(KeyChord.Parse("ctrl+a").Key, chord.Key);
            Assert.Equal(KeyChord.Parse("ctrl+a").Modifiers, chord.Modifiers);
            Assert.Equal("world", Assert.Single(typed));
        }
    }

    [Fact]
    public async Task SetChecked_ClicksUntilStateMatches_MaxThree()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var wrap = h.Jab.Root.Find("menu item", "Save")!; // not a check box: build one in the Edit menu
            var edit = new FakeNode("menu", "Edit", x: 140, y: 130, w: 40, h: 20).With(actions: ["click"])
                .Add(new FakeNode("check box", "Word Wrap", "enabled,visible").With(actions: ["click"]));
            h.Jab.Root.Find("menu bar", null)!.Add(edit);
            tree = await h.TreeAsync();
            var item = h.Jab.Root.Find("check box", "Word Wrap")!;
            h.Jab.OnAction = (n, a) =>
            {
                if (n == item) n.States = n.States.Contains("checked") ? "enabled,visible" : "enabled,visible,checked";
                return true;
            };
            var id = Ref(tree, "CheckBox", "Word Wrap");
            await h.Service.SetCheckedAsync(id, true);
            Assert.Single(h.Jab.Actions);
            await h.Service.SetCheckedAsync(id, true); // already on: no click
            Assert.Single(h.Jab.Actions);

            h.Jab.OnAction = (_, _) => true; // state never changes
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SetCheckedAsync(id, false));
            Assert.Equal(4, h.Jab.Actions.Count);
            _ = wrap;
        }
    }

    [Fact]
    public async Task Focus_RequestsFocus()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.FocusAsync(Ref(tree, "Edit", "Name"));
            Assert.Equal("Name", Assert.Single(h.Jab.Focused).Name);
        }
    }

    [Fact]
    public async Task SelectItem_List_ClearsThenAddsSelectionByName()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.SelectItemAsync(Ref(tree, "List", null), "Beta");
            Assert.Equal("list", Assert.Single(h.Jab.Cleared).Role);
            Assert.Equal(1, Assert.Single(h.Jab.Selections).Index);
            await h.Service.SelectItemAsync(Ref(tree, "List", null), "gam"); // substring, case-insensitive
            Assert.Equal(2, h.Jab.Selections[^1].Index);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "List", null), "Delta"));
        }
    }

    [Fact]
    public async Task SelectItem_Tab_SelectsByName()
    {
        var (h, tree) = await Up();
        using (h)
        {
            await h.Service.SelectItemAsync(Ref(tree, "Tab", null), "Second");
            Assert.Equal(("page tab list", 1), (h.Jab.Selections[^1].Node.Role, h.Jab.Selections[^1].Index));
        }
    }

    [Fact]
    public async Task SelectItem_Combo_OpensPopup_ThenClicksShowingItem()
    {
        var combo = new FakeNode("combo box", x: 10, y: 10, w: 100, h: 24).With(actions: ["togglePopup"], hasSelection: true);
        var popupList = new FakeNode("list", "", "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(
            new FakeNode("label", "One", "enabled,visible", x: 10, y: 34, w: 100, h: 20),
            new FakeNode("label", "Two", "enabled,visible", x: 10, y: 54, w: 100, h: 20),
            new FakeNode("label", "Three", "enabled,visible", x: 10, y: 74, w: 100, h: 20));
        popupList.Name = null;
        var popup = new FakeNode("popup menu", "", "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(popupList);
        popup.Name = null;
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(
            new FakeNode("root pane", x: 0, y: 0, w: 400, h: 300).Add(
                new FakeNode("layered pane", x: 0, y: 0, w: 400, h: 300).Add(combo, popup)));
        var (h, tree) = await Up(root);
        using (h)
        {
            h.Window = h.Window with { Rect = new WinUse.Core.Windows.Rect(0, 0, 400, 300) };
            h.Jab.OnAction = (n, a) =>
            {
                if (n == combo && a == "togglePopup")
                {
                    var show = popup.States.Contains("showing") ? "enabled,visible" : "enabled,visible,showing";
                    popup.States = show;
                    popupList.States = show;
                    foreach (var c in popupList.Children) c.States = show;
                }
                return true;
            };
            await h.Service.SelectItemAsync(Ref(tree, "ComboBox", null), "Two");
            Assert.Contains(h.Jab.Actions, a => a.Node == combo && a.Action == "togglePopup");
            Assert.Equal((60, 64), Assert.Single(h.Clicks));
            Assert.Empty(h.Pressed);
        }
    }

    [Fact]
    public async Task SelectItem_Combo_PressesEscape_WhenItemMissing()
    {
        var combo = new FakeNode("combo box", x: 10, y: 10, w: 100, h: 24).With(actions: ["togglePopup"], hasSelection: true);
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(combo);
        var (h, tree) = await Up(root);
        using (h)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "ComboBox", null), "Nope"));
            Assert.Equal(KeyChord.Parse("escape").Key, Assert.Single(h.Pressed).Key);
            Assert.Empty(h.Clicks);
        }
    }

    [Fact]
    public async Task GetListItems_ReturnsChildNames()
    {
        var (h, tree) = await Up();
        using (h)
        {
            Assert.Equal(["Alpha", "Beta", "Gamma"], await h.Service.GetListItemsAsync(Ref(tree, "List", null)));
            Assert.Equal(["First", "Second"], await h.Service.GetListItemsAsync(Ref(tree, "Tab", null)));
        }
    }

    [Fact]
    public async Task GetTable_Throws_NotSupported()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => h.Service.GetTableAsync(Ref(tree, "List", null)));
            Assert.Contains("get_table is not supported for Java elements yet", ex.Message);
        }
    }
}
