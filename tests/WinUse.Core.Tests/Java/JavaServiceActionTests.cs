using WinUse.Core.Input;
using WinUse.Core.Java;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

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
    public async Task Invoke_PushButton_ClicksWithTheMouse_ByDefault()
    {
        // The click action of a button runs on the event thread of the JVM and returns only when it is done, so a button that
        // opens a modal dialog would hold the bridge until the dialog closes: visible buttons are clicked like a user does.
        var (h, tree) = await Up();
        using (h)
        {
            var r = await h.Service.InvokeAsync(Ref(tree, "Button", "Increment"));
            Assert.Equal(("mouse", InvokeResult.Done), (r.Method, r.Outcome));
            Assert.Equal((160, 172), Assert.Single(h.Clicks));
            Assert.Empty(h.Jab.Actions);
        }
    }

    [Fact]
    public async Task Invoke_MethodAction_UsesClickAction()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var r = await h.Service.InvokeAsync(Ref(tree, "Button", "Increment"), InvokeMethod.Action);
            Assert.Equal(("action", "click", InvokeResult.Done), (r.Method, r.Action, r.Outcome));
            var (node, action) = Assert.Single(h.Jab.Actions);
            Assert.Equal("Increment", node.Name);
            Assert.Equal("click", action);
            Assert.Empty(h.Clicks);
        }
    }

    [Fact]
    public async Task Invoke_PushButton_FallsBackToTheAction_WhenAnotherProcessCoversThePoint()
    {
        var (h, tree) = await Up();
        using (h)
        {
            h.Service.WindowAt = (_, _) => Task.FromResult<WindowInfo?>(new WindowInfo(0x99, "Taskbar", "Shell_TrayWnd", "explorer", 4, new Rect(0, 0, 10, 10), WindowState.Normal));
            var r = await h.Service.InvokeAsync(Ref(tree, "Button", "Increment"));
            Assert.Equal("action", r.Method);
            Assert.Empty(h.Clicks);
            Assert.Single(h.Jab.Actions);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InvokeAsync(Ref(tree, "Button", "Increment"), InvokeMethod.Mouse));
            Assert.Contains("explorer", ex.Message);
        }
    }

    [Fact]
    public async Task Invoke_ActionTimesOut_ReportsUnknownOutcome_WithTheDialogNote()
    {
        var (h, tree) = await Up();
        using (h)
        {
            h.Jab.ActionOutcome = (_, _) => JabActionOutcome.Timeout;
            var r = await h.Service.InvokeAsync(Ref(tree, "MenuItem", "Save"));
            Assert.Equal(("action", "click", InvokeResult.Unknown), (r.Method, r.Action, r.Outcome));
            Assert.Contains("modal dialog", r.Note);
            Assert.Contains("Do not repeat", r.Note);
        }
    }

    [Fact]
    public async Task Invoke_ScrolledOutButton_UsesTheAction_AndMouseRefuses()
    {
        var viewport = new FakeNode("viewport", x: 0, y: 0, w: 200, h: 100).Add(
            new FakeNode("panel", x: 0, y: 0, w: 200, h: 300).Add(
                new FakeNode("push button", "Below", x: 10, y: 150, w: 80, h: 24).With(actions: ["click"])));
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(viewport);
        var (h, tree) = await Up(root);
        using (h)
        {
            h.Window = h.Window with { Rect = new Rect(0, 0, 400, 300) };
            // Not in the default tree (offscreen); listed with include_offscreen.
            Assert.Null(JavaServiceHarness.Find(tree, "Button", "Below"));
            var all = await h.Service.GetTreeAsync(JavaServiceHarness.Hwnd, new TreeOptions(IncludeOffscreen: true));
            var below = JavaServiceHarness.Find(all, "Button", "Below")!;
            Assert.True(below.Offscreen);
            Assert.Equal(new Rect(10, 150, 80, 24), below.Rect);

            var r = await h.Service.InvokeAsync(below.Ref);
            Assert.Equal("action", r.Method);
            Assert.Empty(h.Clicks);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InvokeAsync(below.Ref, InvokeMethod.Mouse));
            Assert.Contains("outside the on-screen area", ex.Message);
            Assert.Contains("Nothing was clicked", ex.Message);
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
            Assert.Equal("selection", (await h.Service.InvokeAsync(Ref(tree, "TabItem", "Second"))).Method);
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
            var r = await h.Service.InvokeAsync(Ref(tree, "Hyperlink", "Docs"));
            Assert.Equal("mouse", r.Method);
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
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InvokeAsync(Ref(tree, "Button", "Increment"), InvokeMethod.Action));
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
            await h.Service.InvokeAsync(old, InvokeMethod.Action); // the fake throws on use-after-release
            var (node, action) = Assert.Single(h.Jab.Actions);
            Assert.Equal("Increment", node.Name);
            Assert.Equal("click", action);
        }
    }

    [Fact]
    public async Task SetText_UsesSetTextContents_AndSaysSo()
    {
        var (h, tree) = await Up();
        using (h)
        {
            Assert.Equal(TextEntryPath.AccessibleText, await h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world"));
            Assert.Equal("world", h.Jab.Root.Find("text", "Name")!.Text);
            Assert.Empty(h.Pressed);
        }
    }

    [Fact]
    public async Task SetText_MethodKeyboard_TypesEvenWhenTheApiWouldWork()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var typed = new List<string>();
            h.Service.TypeText = t => { typed.Add(t); return Task.CompletedTask; };
            Assert.Equal(TextEntryPath.Keyboard, await h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world", TextEntryMethod.Keyboard));
            Assert.Equal("hello", h.Jab.Root.Find("text", "Name")!.Text); // untouched by the API
            Assert.Equal("world", Assert.Single(typed));
            Assert.Contains(h.Jab.Focused, n => n.Name == "Name");
        }
    }

    [Fact]
    public async Task SetText_MethodApi_FailsInsteadOfTyping_WhenRejected()
    {
        var (h, tree) = await Up();
        using (h)
        {
            h.Jab.Root.Find("text", "Name")!.RejectSetText = true;
            await Assert.ThrowsAsync<NotSupportedException>(() => h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world", TextEntryMethod.Api));
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
            Assert.Equal(TextEntryPath.Keyboard, await h.Service.SetTextAsync(Ref(tree, "Edit", "Name"), "world"));
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
    public async Task SelectItem_List_ClearsThenAddsSelectionByName_AndReadsItBack()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var r = await h.Service.SelectItemAsync(Ref(tree, "List", null), "Beta");
            Assert.Equal((SelectItemResult.Done, "Beta", "Beta"), (r.Outcome, r.Item, r.Selected));
            Assert.Equal("list", Assert.Single(h.Jab.Cleared).Role);
            Assert.Equal(1, Assert.Single(h.Jab.Selections).Index);
            r = await h.Service.SelectItemAsync(Ref(tree, "List", null), "gam"); // substring, case-insensitive
            Assert.Equal(2, h.Jab.Selections[^1].Index);
            Assert.Equal("Gamma", r.Item);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "List", null), "Delta"));
        }
    }

    [Fact]
    public async Task SelectItem_List_ReportsAMismatch_WhenTheSelectionDoesNotTake()
    {
        var list = new FakeNode("list", x: 10, y: 10, w: 100, h: 60).With(hasSelection: true).Add(
            new FakeNode("label", "Alpha", x: 10, y: 10, w: 100, h: 20),
            new FakeNode("label", "Beta", x: 10, y: 30, w: 100, h: 20));
        list.Selected.Add(0);
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(list);
        var (h, tree) = await Up(root);
        using (h)
        {
            // The app keeps Alpha selected whatever is asked (a model that refuses the change).
            h.Jab.OnSelect = (_, _) => { list.Selected.Clear(); list.Selected.Add(0); };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "List", null), "Beta"));
            Assert.Contains("Selected 'Beta'", ex.Message);
            Assert.Contains("'Alpha' as selected", ex.Message);

            h.Jab.OnSelect = (_, _) => list.Selected.Clear();
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "List", null), "Beta"));
            Assert.Contains("not selected", ex.Message);
        }
    }

    [Fact]
    public async Task SelectItem_List_WithoutAccessibleSelection_ReportsUnknown()
    {
        var list = new FakeNode("list", x: 10, y: 10, w: 100, h: 60).Add(
            new FakeNode("label", "Alpha", x: 10, y: 10, w: 100, h: 20),
            new FakeNode("label", "Beta", x: 10, y: 30, w: 100, h: 20));
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(list);
        var (h, tree) = await Up(root);
        using (h)
        {
            var r = await h.Service.SelectItemAsync(Ref(tree, "List", null), "Beta");
            Assert.Equal((SelectItemResult.Unknown, "Beta", null), (r.Outcome, r.Item, r.Selected));
            Assert.Contains("AccessibleSelection", r.Note);
        }
    }

    [Fact]
    public async Task SelectItem_Tab_SelectsByName()
    {
        var (h, tree) = await Up();
        using (h)
        {
            var r = await h.Service.SelectItemAsync(Ref(tree, "Tab", null), "Second");
            Assert.Equal(("page tab list", 1), (h.Jab.Selections[^1].Node.Role, h.Jab.Selections[^1].Index));
            Assert.Equal((SelectItemResult.Done, "Second"), (r.Outcome, r.Selected));
        }
    }

    /// <summary>A combo box whose popup (child of the layered pane, as Swing lays it out) shows on <c>togglePopup</c>.</summary>
    private static (FakeNode Root, FakeNode Combo, FakeNode Popup, FakeNode PopupList) ComboFixture()
    {
        var combo = new FakeNode("combo box", x: 10, y: 10, w: 100, h: 24).With(actions: ["togglePopup"], hasSelection: true);
        var popupList = new FakeNode("list", "", "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(
            new FakeNode("label", "One", "enabled,visible", x: 10, y: 34, w: 100, h: 20),
            new FakeNode("label", "Two", "enabled,visible", x: 10, y: 54, w: 100, h: 20),
            new FakeNode("label", "Three", "enabled,visible", x: 10, y: 74, w: 100, h: 20));
        popupList.Name = null;
        combo.SelectionList = popupList;
        combo.Selected.Add(0);
        var popup = new FakeNode("popup menu", "", "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(popupList);
        popup.Name = null;
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(
            new FakeNode("root pane", x: 0, y: 0, w: 400, h: 300).Add(
                new FakeNode("layered pane", x: 0, y: 0, w: 400, h: 300).Add(combo, popup)));
        return (root, combo, popup, popupList);
    }

    private static void ShowPopupOnToggle(JavaServiceHarness h, FakeNode combo, FakeNode popup, FakeNode popupList)
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
    }

    [Fact]
    public async Task SelectItem_Combo_OpensPopup_ClicksShowingItem_AndReadsTheSelectionBack()
    {
        var (root, combo, popup, popupList) = ComboFixture();
        var (h, tree) = await Up(root);
        using (h)
        {
            ShowPopupOnToggle(h, combo, popup, popupList);
            // The click lands on the popup list: the combo model takes the item, as the app would.
            h.Service.ClickAt = (x, y) => { h.Clicks.Add((x, y)); combo.Selected.Clear(); combo.Selected.Add(1); return Task.CompletedTask; };
            var r = await h.Service.SelectItemAsync(Ref(tree, "ComboBox", null), "Two");
            Assert.Contains(h.Jab.Actions, a => a.Node == combo && a.Action == "togglePopup");
            Assert.Equal((60, 64), Assert.Single(h.Clicks));
            Assert.Empty(h.Pressed);
            Assert.Equal((SelectItemResult.Done, "Two", "Two"), (r.Outcome, r.Item, r.Selected));
        }
    }

    [Fact]
    public async Task SelectItem_Combo_ReportsAMismatch_WhenTheComboStillShowsAnotherItem()
    {
        var (root, combo, popup, popupList) = ComboFixture();
        var (h, tree) = await Up(root);
        using (h)
        {
            ShowPopupOnToggle(h, combo, popup, popupList);
            // The click is delivered but the combo keeps "One" (the popup closed before the click, or the app vetoed it).
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "ComboBox", null), "Two"));
            Assert.Single(h.Clicks);
            Assert.Contains("Selected 'Two' in ComboBox", ex.Message);
            Assert.Contains("'One' as selected", ex.Message);
        }
    }

    [Fact]
    public async Task SelectItem_Combo_ClosesPopupWithToggle_WhenItemMissing()
    {
        var combo = new FakeNode("combo box", x: 10, y: 10, w: 100, h: 24).With(actions: ["togglePopup"], hasSelection: true);
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(combo);
        var (h, tree) = await Up(root);
        using (h)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SelectItemAsync(Ref(tree, "ComboBox", null), "Nope"));
            // Closing goes through the same action so it does not depend on which window has the keyboard focus.
            Assert.Equal(2, h.Jab.Actions.Count(a => a.Node == combo && a.Action == "togglePopup"));
            Assert.Empty(h.Pressed);
            Assert.Empty(h.Clicks);
        }
    }

    [Fact]
    public async Task GetListItems_Combo_OpensCollectsAndClosesWithToggle()
    {
        var (root, combo, popup) = ComboWithPopup();
        var (h, tree) = await Up(root);
        using (h)
        {
            h.Window = h.Window with { Rect = new WinUse.Core.Windows.Rect(0, 0, 400, 300) };
            h.Jab.OnAction = TogglePopup(combo, popup);
            Assert.Equal(["One", "Two", "Three"], await h.Service.GetListItemsAsync(Ref(tree, "ComboBox", null)));
            Assert.Equal(2, h.Jab.Actions.Count(a => a.Node == combo && a.Action == "togglePopup"));
            Assert.False(popup.Showing, "the popup should be closed again");
            Assert.Empty(h.Pressed);
        }
    }

    /// <summary>A frame with a combo box and its (closed) popup list as a sibling under the layered pane, like Swing lays them out.</summary>
    private static (FakeNode Root, FakeNode Combo, FakeNode Popup) ComboWithPopup()
    {
        var combo = new FakeNode("combo box", x: 10, y: 10, w: 100, h: 24).With(actions: ["togglePopup"], hasSelection: true);
        var popupList = new FakeNode("list", null, "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(
            new FakeNode("label", "One", "enabled,visible", x: 10, y: 34, w: 100, h: 20),
            new FakeNode("label", "Two", "enabled,visible", x: 10, y: 54, w: 100, h: 20),
            new FakeNode("label", "Three", "enabled,visible", x: 10, y: 74, w: 100, h: 20));
        var popup = new FakeNode("popup menu", null, "enabled,visible", x: 10, y: 34, w: 100, h: 60).Add(popupList);
        var root = new FakeNode("frame", "F", x: 0, y: 0, w: 400, h: 300).Add(
            new FakeNode("root pane", x: 0, y: 0, w: 400, h: 300).Add(
                new FakeNode("layered pane", x: 0, y: 0, w: 400, h: 300).Add(combo, popup)));
        return (root, combo, popup);
    }

    private static Func<FakeNode, string, bool> TogglePopup(FakeNode combo, FakeNode popup) => (n, a) =>
    {
        if (n == combo && a == "togglePopup")
        {
            var show = popup.States.Contains("showing") ? "enabled,visible" : "enabled,visible,showing";
            void Set(FakeNode x) { x.States = show; foreach (var c in x.Children) Set(c); }
            Set(popup);
        }
        return true;
    };

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
}
