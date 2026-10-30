using System.Text.Json.Nodes;
using WinUse.Core.Grounding;
using WinUse.Core.Java;
using WinUse.Core.Refs;
using WinUse.Core.Tests.Java;
using WinUse.Core.Uia;
using WinUse.Core.Windows;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

/// <summary>ToolContext over a fake Access Bridge: the Java branches of act execution and coordinate lookup.</summary>
public sealed class JavaToolContextTests : IDisposable
{
    private const nint Hwnd = 0x1234;
    private readonly FakeJabAccess _jab = new(FakeTrees.Fixture(), Hwnd);
    private readonly ToolContext _ctx;
    private readonly WindowInfo _window = new(Hwnd, "WinUse Swing Fixture", "SunAwtFrame", "java", 1, new Rect(100, 100, 640, 480), WindowState.Normal);

    public JavaToolContextTests()
    {
        _ctx = ToolContext.Create(TestConfig.Default, null, null, jab: new FakeJabBridge(_jab));
        _ctx.Java.WindowLookup = h => Task.FromResult(h == Hwnd ? _window : null);
        _ctx.Java.WindowFind = _ => Task.FromResult<WindowInfo?>(_window);
        _ctx.Java.DpiScale = _ => 1.0;
    }

    public void Dispose() => _ctx.Dispose();

    private async Task<string> RefOf(string type, string name)
    {
        var tree = await _ctx.Java.GetTreeAsync(Hwnd, new TreeOptions());
        return JavaServiceHarness.Find(tree, type, name)!.Ref;
    }

    [Fact]
    public async Task ExecuteAsync_JavaRef_Click_CallsInvoke()
    {
        var id = await RefOf("Button", "Increment");
        Assert.True(ToolContext.IsJavaRef(id));
        await _ctx.ExecuteAsync(new Candidate(id, "Button", "Increment", null, true, false, false, null, "top-left", null), ActKind.Click, null);
        Assert.Equal(("Increment", "click"), (_jab.Actions.Single().Node.Name, _jab.Actions.Single().Action));
    }

    [Fact]
    public async Task ExecuteAsync_JavaRef_Type_CallsSetText()
    {
        var id = await RefOf("Edit", "Name");
        await _ctx.ExecuteAsync(new Candidate(id, "Edit", "Name", "hello", true, false, true, null, "top-left", null), ActKind.Type, "world");
        Assert.Equal("world", _jab.Root.Find("text", "Name")!.Text);
    }

    [Fact]
    public async Task ExecuteAsync_JavaRef_Toggle_FlipsCheckedState()
    {
        var check = new FakeNode("check box", "Enabled", x: 120, y: 200, w: 80, h: 20).With(actions: ["click"]); // inside the window
        _jab.Root.Add(check);
        _jab.OnAction = (n, _) => { if (n == check) n.States = "enabled,visible,showing,checked"; return true; };
        var id = await RefOf("CheckBox", "Enabled");
        await _ctx.ExecuteAsync(new Candidate(id, "CheckBox", "Enabled", null, true, false, false, false, "top-left", null), ActKind.Toggle, null);
        Assert.Contains(_jab.Actions, a => a.Node == check && a.Action == "click");
    }

    [Fact]
    public async Task CenterOfAsync_JavaRef_UsesJavaRect()
    {
        var id = await RefOf("Button", "Increment");
        Assert.Equal((160, 172), await _ctx.CenterOfAsync(_ctx.GetRef(id)));
    }

    [Fact]
    public async Task ObserveAsync_JavaWindow_YieldsJavaCandidates()
    {
        // ObserveAsync resolves the window through WindowService, which cannot see the fake window; the observation
        // path itself is covered by the integration tests. Here the candidate shape of a Java tree is checked.
        var tree = await _ctx.Java.GetTreeAsync(Hwnd, new TreeOptions());
        var candidates = CandidateBuilder.Build(tree, [], _window.Rect, null);
        var save = candidates.Single(c => c.Label == "Save");
        Assert.Equal("File", save.Container);
        Assert.StartsWith("j", save.Id);
        Assert.Contains(candidates, c => c.Label == "Increment" && c.Role == "Button");
    }

    /// <summary>Calls a tool the way an MCP client or a replay does (ref arguments travel with their locator).</summary>
    private async Task<JsonNode> Call(string tool, JsonObject args)
    {
        var id = args["ref"]?.GetValue<string>();
        var o = await new ToolDispatcher(_ctx).InvokeAsync(new(tool, args, id is null ? null : _ctx.GetRef(id).Locator), default);
        Assert.True(o.Ok, $"{tool}: {o.Error}");
        return o.Result;
    }

    [Fact]
    public async Task SetText_ReportsThePathTaken_AndNeverTheText()
    {
        var id = await RefOf("Edit", "Name");
        var r = await Call("set_text", new JsonObject { ["ref"] = id, ["text"] = "world" });
        Assert.Equal("accessible_text", r["method"]!.GetValue<string>());
        Assert.StartsWith("j", r["ref"]!.GetValue<string>()); // the dispatcher re-resolves the locator to a fresh ref
        Assert.DoesNotContain("world", r.ToJsonString());
        _jab.Root.Find("text", "Name")!.RejectSetText = true;
        _ctx.Java.TypeText = _ => Task.CompletedTask;
        _ctx.Java.Press = _ => Task.CompletedTask;
        _ctx.Java.Activate = _ => Task.FromResult(true);
        var k = await Call("set_text", new JsonObject { ["ref"] = id, ["text"] = "world" });
        Assert.Equal("keyboard", k["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_ReportsMethodAndOutcome()
    {
        _ctx.Java.ClickAt = (_, _) => Task.CompletedTask;
        _ctx.Java.Activate = _ => Task.FromResult(true);
        _ctx.Java.WindowAt = (_, _) => Task.FromResult<WindowInfo?>(null);
        var button = await RefOf("Button", "Increment");
        var r = await Call("invoke", new JsonObject { ["ref"] = button });
        Assert.Equal(("mouse", "done"), (r["method"]!.GetValue<string>(), r["outcome"]!.GetValue<string>()));

        var save = await RefOf("MenuItem", "Save");
        var a = await Call("invoke", new JsonObject { ["ref"] = save });
        Assert.Equal(("action", "click", "done"), (a["method"]!.GetValue<string>(), a["action"]!.GetValue<string>(), a["outcome"]!.GetValue<string>()));

        _jab.ActionOutcome = (_, _) => JabActionOutcome.Timeout;
        var u = await Call("invoke", new JsonObject { ["ref"] = save });
        Assert.Equal("unknown", u["outcome"]!.GetValue<string>());
        Assert.Contains("modal dialog", u["note"]!.GetValue<string>());
        Assert.NotNull(u["new_windows"]);
    }

    [Fact]
    public async Task Click_RefusesAJavaRef_ThatIsScrolledOutOfView()
    {
        _jab.Root.Add(new FakeNode("viewport", x: 100, y: 500, w: 200, h: 50).Add(
            new FakeNode("panel", x: 100, y: 500, w: 200, h: 400).Add(
                new FakeNode("push button", "Below", x: 110, y: 600, w: 80, h: 20).With(actions: ["click"]))));
        var tree = await _ctx.Java.GetTreeAsync(Hwnd, new TreeOptions(IncludeOffscreen: true));
        var id = JavaServiceHarness.Find(tree, "Button", "Below")!.Ref;
        var o = await new ToolDispatcher(_ctx).InvokeAsync(new("click", new JsonObject { ["ref"] = id }, _ctx.GetRef(id).Locator), default);
        Assert.False(o.Ok);
        Assert.Contains("outside the on-screen area", o.Error);
        Assert.Contains("Nothing was clicked", o.Error);
    }

    [Fact]
    public async Task SetText_Secret_SetsTheText_ButJournalsThePlaceholder()
    {
        var id = await RefOf("Edit", "Name");
        var args = new JsonObject { ["ref"] = id, ["text"] = "hunter2", ["secret"] = true };
        await Call("set_text", args);
        Assert.Equal("hunter2", _jab.Root.Find("text", "Name")!.Text);
        var entry = _ctx.Journal.Entries.Last(e => e.Tool == "set_text");
        Assert.Equal("***", entry.Args["text"]!.GetValue<string>());
        Assert.True(entry.Args["secret"]!.GetValue<bool>());
        Assert.DoesNotContain("hunter2", entry.Args.ToJsonString());
    }

    [Fact]
    public async Task SetText_WithoutSecret_JournalsTheText()
    {
        var id = await RefOf("Edit", "Name");
        await Call("set_text", new JsonObject { ["ref"] = id, ["text"] = "plain" });
        var entry = _ctx.Journal.Entries.Last(e => e.Tool == "set_text");
        Assert.Equal("plain", entry.Args["text"]!.GetValue<string>());
        Assert.Null(entry.Args["secret"]);
    }

    [Fact]
    public async Task GetTable_JavaRef_ReturnsRowsRowCountAndOmittedRows()
    {
        _jab.Root.Add(FakeTrees.Table());
        var id = await RefOf("Table", "Connections");
        var r = await Call("get_table", new JsonObject { ["ref"] = id, ["max_rows"] = 1 });
        Assert.Equal(["Name", "Port", "Active"], r["rows"]![0]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(["alpha", "8080", "true"], r["rows"]![1]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(2, r["row_count"]!.GetValue<int>());
        Assert.Equal(1, r["omitted_rows"]!.GetValue<int>());
        var all = await Call("get_table", new JsonObject { ["ref"] = id });
        Assert.Equal(3, all["rows"]!.AsArray().Count);
        Assert.Equal(3, all["column_count"]!.GetValue<int>());
        Assert.Null(all["omitted_rows"]);
        Assert.Null(all["unreadable_cells"]);
    }

    [Fact]
    public async Task GetTable_JavaRef_CountsOnly_WhenHeaderIsLeftOut_AndMaxRowsIsZero()
    {
        _jab.Root.Add(FakeTrees.Table());
        var id = await RefOf("Table", "Connections");
        var r = await Call("get_table", new JsonObject { ["ref"] = id, ["max_rows"] = 0, ["include_header"] = false });
        Assert.Empty(r["rows"]!.AsArray());
        Assert.Equal(2, r["row_count"]!.GetValue<int>());
        Assert.Equal(3, r["column_count"]!.GetValue<int>());
        Assert.Equal(0, _jab.ColumnHeaderCalls);
        Assert.DoesNotContain("Port", r.ToJsonString());
    }

    [Fact]
    public async Task GetTable_JavaRef_UnreadableCell_IsNull_AndCounted()
    {
        _jab.Root.Add(new FakeNode("table", "Connections", x: 100, y: 300, w: 300, h: 60) { Columns = 2 }.Add(
            new FakeNode("label", "alpha"), new FakeNode("label", "8080") { NoHandle = true }));
        var id = await RefOf("Table", "Connections");
        var r = await Call("get_table", new JsonObject { ["ref"] = id });
        var row = r["rows"]![0]!.AsArray();
        Assert.Equal("alpha", row[0]!.GetValue<string>());
        Assert.Null(row[1]);
        Assert.Equal(1, r["unreadable_cells"]!.GetValue<int>());
    }
}
