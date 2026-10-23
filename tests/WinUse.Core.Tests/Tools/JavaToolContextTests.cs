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
        var check = new FakeNode("check box", "Enabled").With(actions: ["click"]);
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
}
