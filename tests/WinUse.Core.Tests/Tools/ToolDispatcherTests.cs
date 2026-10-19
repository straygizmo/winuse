using System.Text.Json.Nodes;
using WinUse.Core.Grounding;
using WinUse.Core.Scenarios;
using WinUse.Core.Tests.Grounding;
using WinUse.Core.Windows;
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

    private sealed class Unserializable
    {
        public int Value => throw new InvalidOperationException("cannot serialize");
    }

    [Fact]
    public async Task RunStep_PayloadSerializationFailure_JournalsOnce()
    {
        var r = await ctx.RunStepAsync("boom", new JsonObject(), _ => Task.FromResult<object>(new Unserializable()));
        Assert.True(r.IsError);
        var e = Assert.Single(ctx.Journal.Entries);
        Assert.Equal("boom", e.Tool);
    }

    [Fact]
    public async Task GetElement_OnWindowRef_Succeeds_AndJournalsOnce()
    {
        var d = new ToolDispatcher(ctx);
        var list = await d.InvokeAsync(new("list_windows", new JsonObject(), null), default);
        Assert.True(list.Ok, list.Error);
        var windows = list.Result["windows"]!.AsArray();
        if (windows.Count == 0) return; // no desktop windows (e.g. a service session)
        var winRef = windows[0]!["ref"]!.GetValue<string>();

        var o = await d.InvokeAsync(new("get_element", Args(("ref", winRef)), ctx.GetRef(winRef).Locator), default);

        Assert.True(o.Ok, o.Error);
        Assert.Equal(windows[0]!["title"]!.GetValue<string>(), o.Result["element"]!["title"]!.GetValue<string>());
        var stepId = o.Result["step_id"]!.GetValue<string>();
        Assert.Single(ctx.Journal.Entries, e => e.StepId == stepId);
    }

    [Fact]
    public async Task Replay_ReResolvesRefArgsFromResolvedRefs_ThroughScenarioJson()
    {
        var fg = NativeMethods.GetForegroundWindow();
        var d = new ToolDispatcher(ctx);
        var list = await d.InvokeAsync(new("list_windows", new JsonObject(), null), default);
        var match = list.Result["windows"]!.AsArray().FirstOrDefault(w => ctx.GetRef(w!["ref"]!.GetValue<string>()).Handle is nint h && h == fg);
        if (match is null) return; // the foreground window is not a listed top-level window
        var recordedRef = match["ref"]!.GetValue<string>();

        // Live call records the locator of every ref argument.
        var live = await WindowTools.WindowActivate(ctx, recordedRef);
        Assert.NotEqual(true, live.IsError);
        var entry = ctx.Journal.Entries[^1];
        Assert.Equal("window_activate", entry.Tool);
        Assert.NotNull(entry.Args[ToolContext.ResolvedRefsKey]?["ref"]);

        // resolved_refs survives the scenario file round trip unchanged (it lives inside args).
        var scenario = Scenario.Parse(Scenario.FromJournal("t", ctx.Journal, [entry.StepId], null).ToJson());
        var step = Assert.Single(scenario.Steps);
        Assert.True(JsonNode.DeepEquals(entry.Args, step.Args));

        // Replay without the primary locator: the ref must be re-resolved from resolved_refs.
        var o = await d.InvokeAsync(new(step.Tool, step.Args, null), default);

        Assert.True(o.Ok, o.Error);
        var replayed = ctx.Journal.Entries[^1];
        var injected = replayed.Args["ref"]!.GetValue<string>();
        Assert.NotEqual(recordedRef, injected);
        Assert.Equal(fg, (nint)ctx.GetRef(injected).Handle!);
    }

    [Fact]
    public async Task Replay_RefArgWithoutLocator_FailsAsStale()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("drag", Args(("from_ref", "u1"), ("to_ref", "u2")), null), default);
        Assert.False(o.Ok);
        Assert.Equal("stale ref: from_ref", o.Error);
        Assert.Empty(ctx.Journal.Entries);
    }

    [Fact]
    public async Task Replay_SecondaryLocatorNotFound_NamesTheArgument()
    {
        var gone = new WinUse.Core.Refs.WindowLocator("^no-such-window-" + Guid.NewGuid().ToString("N") + "$", null, null);
        var args = Args(("from_x", 1), ("from_y", 1), ("to_ref", "win9"));
        args[ToolContext.ResolvedRefsKey] = new JsonObject
        {
            ["to_ref"] = System.Text.Json.JsonSerializer.SerializeToNode<WinUse.Core.Refs.Locator>(gone),
        };
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("drag", args, null), default);
        Assert.False(o.Ok);
        Assert.Equal("locator not found: to_ref", o.Error);
    }

    [Fact]
    public async Task Act_JournalsExecutedKind_InsteadOfAuto()
    {
        var exec = new FakeExecutor();
        using var c = ToolContext.Create(TestConfig.WithRoots(tmp), null,
            new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9, kind: "type"),
            new FakeObservation(G.Cand("u1", "Name box", role: "Edit")), exec);

        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "enter the name"), ("text", "abc")), null), default);

        Assert.True(o.Ok, o.Error);
        Assert.Equal(ActKind.Type, exec.LastKind);
        Assert.Equal("type", Assert.Single(c.Journal.Entries).Args["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dispatcher_MissingRequiredArgument_ReturnsNotOk()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("read_text", new JsonObject(), null), default);
        Assert.False(o.Ok);
        Assert.Contains("path", o.Error);
    }
}
