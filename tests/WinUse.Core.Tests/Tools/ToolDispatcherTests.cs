using System.Text.Json.Nodes;
using WinUse.Core.Scenarios;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

public sealed class ToolDispatcherTests : IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "winuse-tools-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext ctx;

    public ToolDispatcherTests()
    {
        Directory.CreateDirectory(tmp);
        ctx = ToolContext.Create(TestConfig.WithRoots(tmp));   // real context; UIA / Playwright stay uninitialised
    }

    public void Dispose()
    {
        ctx.Dispose();
        try { Directory.Delete(tmp, true); } catch { }
    }

    private static JsonObject Args(params (string k, JsonNode? v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    [Fact]
    public async Task Dispatcher_WriteThenReadText_RoundTripsAndJournals()
    {
        var f = Path.Combine(tmp, "a.txt");
        var d = new ToolDispatcher(ctx);
        var w = await d.InvokeAsync(new("write_text", Args(("path", f), ("text", "hi")), null), default);
        Assert.True(w.Ok, w.Error);
        var r = await d.InvokeAsync(new("read_text", Args(("path", f)), null), default);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("hi", r.Result["text"]!.GetValue<string>());
        Assert.Equal(2, ctx.Journal.Entries.Count);
        Assert.Equal(["write_text", "read_text"], ctx.Journal.Entries.Select(e => e.Tool));
        Assert.Equal(f, ctx.Journal.Entries[0].Args["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dispatcher_UnknownTool_ReturnsNotOk()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("no_such_tool", new JsonObject(), null), default);
        Assert.False(o.Ok);
        Assert.Contains("no_such_tool", o.Error);
    }

    [Fact]
    public async Task Dispatcher_AssertFile_SetsPassed()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(
            new("assert_file", Args(("path", Path.Combine(tmp, "missing.txt")), ("exists", true)), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
        Assert.False(ctx.Journal.Entries.Single().Passed);
    }

    [Fact]
    public async Task Dispatcher_ToolError_ReturnsNotOkWithMessage()
    {
        // Path outside the allowed roots: the tool reports an error instead of throwing.
        var o = await new ToolDispatcher(ctx).InvokeAsync(
            new("read_text", Args(("path", @"C:\Windows\win.ini")), null), default);
        Assert.False(o.Ok);
        Assert.Contains("outside allowed roots", o.Error);
    }

    [Fact]
    public async Task Dispatcher_UnresolvableLocator_ReturnsLocatorNotFound()
    {
        var loc = new WinUse.Core.Refs.UiaLocator(new("^no-such-window-" + Guid.NewGuid().ToString("N") + "$", null, null), []);
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("get_text", Args(("ref", "u1")), loc), default);
        Assert.False(o.Ok);
        Assert.Equal("locator not found", o.Error);
        Assert.Empty(ctx.Journal.Entries);
    }

    [Fact]
    public async Task Dispatcher_MissingRequiredArgument_ReturnsNotOk()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("read_text", new JsonObject(), null), default);
        Assert.False(o.Ok);
        Assert.Contains("path", o.Error);
    }
}
