using System.Text.Json.Nodes;
using WinUse.Core.Grounding;
using WinUse.Core.Refs;
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
        // A missing target is a FAIL (exit 1), not an execution error (exit 2).
        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
        Assert.Equal("locator not found: ref", o.Error);
        Assert.Empty(ctx.Journal.Entries);
    }

    private static WinUse.Core.Refs.WindowLocator GoneWindow() =>
        new("^no-such-window-" + Guid.NewGuid().ToString("N") + "$", null, null);

    [Fact]
    public async Task ScenarioReplay_StaleLocator_ExitsOneThroughRealDispatcher()
    {
        var s = new Scenario("t", 1, new Dictionary<string, string>(),
            [new ScenarioStep("window_activate", Args(("ref", "win1")), GoneWindow())]);
        var r = await new ScenarioRunner(new ToolDispatcher(ctx), null, null).RunAsync(s, false, default);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(StepStatus.Fail, Assert.Single(r.Steps).Status);
        Assert.StartsWith("locator not found", r.Steps[0].Message);
    }

    private ToolContext GroundingContext(FakeJev jev, FakeExecutor exec) =>
        ToolContext.Create(TestConfig.WithRoots(tmp), null, jev, new FakeObservation(G.Cand("u1", "Save")), exec);

    [Fact]
    public async Task Replay_ActWithoutResolved_FailsWithoutCallingJev()
    {
        var jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9);
        var exec = new FakeExecutor();
        using var c = GroundingContext(jev, exec);

        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "save"), ("kind", "click")), null, IsReplay: true), default);

        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
        Assert.Equal("act step has no resolved locator", o.Error);
        Assert.Empty(jev.Calls);
        Assert.Null(exec.Last);
    }

    [Fact]
    public async Task Interactive_ActWithoutResolved_StillGrounds()
    {
        var jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9);
        var exec = new FakeExecutor();
        using var c = GroundingContext(jev, exec);
        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "save"), ("kind", "click")), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.Equal("u1", exec.Last!.Id);
    }

    [Fact]
    public async Task Replay_ActLocatorGone_FailsUnlessHealIsOn()
    {
        var args = Args(("intent", "save"), ("kind", "click"));

        var jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9);
        var exec = new FakeExecutor();
        using (var c = GroundingContext(jev, exec))
        {
            var o = await WinUse.Cli.RunCommand.CreateDispatcher(c, heal: false)
                .InvokeAsync(new("act", args, GoneWindow(), IsReplay: true), default);
            Assert.True(o.Ok, o.Error);
            Assert.False(o.Passed);
            Assert.Equal("locator not found: resolved", o.Error);
            Assert.Null(exec.Last);
            Assert.Empty(jev.Calls);
        }

        jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9);
        exec = new FakeExecutor();
        using (var c = GroundingContext(jev, exec))
        {
            var d = WinUse.Cli.RunCommand.CreateDispatcher(c, heal: true);
            Assert.True(d.Heal);
            var o = await d.InvokeAsync(new("act", args, GoneWindow(), IsReplay: true), default);
            Assert.True(o.Ok, o.Error);
            Assert.NotEqual(false, o.Passed);
            Assert.Equal("u1", exec.Last!.Id); // re-grounded with Jev and executed
        }
    }

    [Fact]
    public async Task ScenarioSave_RefusesActThatWasNotExecuted()
    {
        using var c = GroundingContext(new FakeJev(choice: ("u1", 0.2, 0.1), noul: 0.9), new FakeExecutor());
        c.ActScreenshot = () => Task.FromResult<WinUse.Core.Screen.Capture?>(null);
        var d = new ToolDispatcher(c);
        var act = await d.InvokeAsync(new("act", Args(("intent", "save")), null), default);
        var stepId = act.Result["step_id"]!.GetValue<string>();
        Assert.False(c.Journal.Entries.Single(e => e.StepId == stepId).Executed);

        var save = await d.InvokeAsync(new("scenario_save", Args(("name", "t"), ("step_ids", new JsonArray(stepId)),
            ("path", Path.Combine(tmp, "t.json"))), null), default);

        Assert.False(save.Ok);
        Assert.Contains("not executed", save.Error);
        Assert.False(File.Exists(Path.Combine(tmp, "t.json")));
    }

    [Fact]
    public async Task Act_ModelNotConfigured_IsFlaggedAsUnavailable()
    {
        using var c = GroundingContext(new FakeJev(configured: false), new FakeExecutor());
        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "save"), ("kind", "click")), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
        Assert.True(o.JevUnavailable);
        Assert.Equal("act not executed: model not configured", o.Error);
    }

    [Fact]
    public async Task Act_ModelUnavailable_IsFlaggedAsUnavailable()
    {
        using var c = GroundingContext(new FakeJev(throwUnavailable: true), new FakeExecutor());
        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "save"), ("kind", "click")), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.True(o.JevUnavailable);
        Assert.Equal("act not executed: model unavailable", o.Error);
    }

    [Fact]
    public async Task Replay_HealWithJevUnavailable_IsFailNotError()
    {
        var exec = new FakeExecutor();
        using var c = GroundingContext(new FakeJev(configured: false), exec);
        var s = new Scenario("t", 1, new Dictionary<string, string>(),
            [new ScenarioStep("act", Args(("intent", "save"), ("kind", "click")), GoneWindow())]);

        var r = await new ScenarioRunner(new ToolDispatcher(c) { Heal = true }, null, null).RunAsync(s, true, default);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("heal failed", r.Steps[0].Message);
        Assert.Null(exec.Last);
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
        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
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
    public async Task Act_LowConfidence_AttachesWindowScreenshot()
    {
        using var c = GroundingContext(new FakeJev(choice: ("u1", 0.2, 0.1), noul: 0.9), new FakeExecutor());
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        c.ActScreenshot = () => Task.FromResult<WinUse.Core.Screen.Capture?>(new(png, 10, 10, 0.5));

        var r = await IntentTools.Act(c, "click save");

        Assert.NotEqual(true, r.IsError);
        Assert.Equal(2, r.Content.Count);
        var img = Assert.Single(r.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>());
        Assert.Equal("image/png", img.MimeType);
        var json = JsonNode.Parse(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text)!;
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.Equal("low confidence", json["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Act_Executed_HasNoScreenshot()
    {
        using var c = GroundingContext(new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9), new FakeExecutor());
        var called = false;
        c.ActScreenshot = () => { called = true; return Task.FromResult<WinUse.Core.Screen.Capture?>(null); };
        var r = await IntentTools.Act(c, "click save", kind: "click");
        Assert.Single(r.Content);
        Assert.False(called);
    }

    [Fact]
    public async Task Dispatcher_UnknownArgument_IsRejected_AndNotJournaled()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(
            new("read_text", Args(("path", Path.Combine(tmp, "a.txt")), ("pth", "x")), null), default);
        Assert.False(o.Ok);
        Assert.Equal("unknown argument: pth", o.Error);
        Assert.Empty(ctx.Journal.Entries);
    }

    [Fact]
    public async Task Dispatcher_ResolvedRefsArgument_IsNotUnknown()
    {
        var f = Path.Combine(tmp, "b.txt");
        var args = Args(("path", f), ("text", "x"));
        args[ToolContext.ResolvedRefsKey] = new JsonObject();
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("write_text", args, null), default);
        Assert.True(o.Ok, o.Error);
    }

    [Fact]
    public async Task Screenshot_WindowArgument_ResolvesLikeOtherTools()
    {
        // The window argument is used (not silently dropped in favour of a screen capture): an unknown title fails
        // with the same message as every other tool's window resolution.
        var title = "^no-such-window-" + Guid.NewGuid().ToString("N") + "$";
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("screenshot", Args(("window", title)), null), default);
        Assert.False(o.Ok);
        Assert.Equal($"No window matches '{title}'.", o.Error);
        Assert.Equal(title, ctx.Journal.Entries.Single().Args["window"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dispatcher_MissingRequiredArgument_ReturnsNotOk()
    {
        var o = await new ToolDispatcher(ctx).InvokeAsync(new("read_text", new JsonObject(), null), default);
        Assert.False(o.Ok);
        Assert.Contains("path", o.Error);
    }

    // ---- visual grounding (windows without candidates) ----------------------------------------

    private ToolContext VisualContext(FakeJev jev, FakeExecutor exec) =>
        ToolContext.Create(TestConfig.WithRoots(tmp), null, jev, new FakeObservation(), exec, new FakeCapture(800, 600));

    [Fact]
    public async Task Act_Visual_JournalsAPointLocator_AndReturnsAPointRef()
    {
        var exec = new FakeExecutor();
        using var c = VisualContext(new FakeJev(cell: "A1", noul: 0.9), exec);
        var o = await new ToolDispatcher(c).InvokeAsync(new("act", Args(("intent", "tap OK"), ("kind", "click")), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.NotEqual(false, o.Passed);
        var loc = Assert.IsType<PointLocator>(o.Resolved);
        var (x, y) = exec.LastPoint!.Value;
        Assert.Equal(x / 900.0, loc.Fx, 3); Assert.Equal(y / 900.0, loc.Fy, 3); // FakeObservation window rect is 900x900 at (0,0)
        Assert.Equal("^T$", loc.Window.TitleRegex);
        Assert.Equal(x, o.Result["point"]!["x"]!.GetValue<int>());
        Assert.Equal(y, o.Result["point"]!["y"]!.GetValue<int>());
        var refId = o.Result["target"]!["ref"]!.GetValue<string>();
        Assert.StartsWith("pt", refId);
        Assert.True(c.Refs.TryGet(refId, out var r));
        Assert.Equal(RefKind.Point, r.Kind);
        Assert.Equal((x, y), await c.CenterOfAsync(r));
    }

    [Fact]
    public async Task Find_Visual_ReturnsAPointRef()
    {
        using var c = VisualContext(new FakeJev(cell: "A1"), new FakeExecutor());
        var o = await new ToolDispatcher(c).InvokeAsync(new("find", Args(("intent", "the OK button")), null), default);
        Assert.True(o.Ok, o.Error);
        Assert.True(o.Result["grounded"]!.GetValue<bool>());
        Assert.NotNull(o.Result["point"]);
        var refId = o.Result["candidates"]![0]!["ref"]!.GetValue<string>();
        Assert.StartsWith("pt", refId);
        Assert.True(c.Refs.TryGet(refId, out _));
    }

    [Fact]
    public async Task Replay_PointLocator_ActsAtTheWindowPoint_WithoutTheModel()
    {
        var jev = new FakeJev(cell: "A1");
        var exec = new FakeExecutor();
        using var c = VisualContext(jev, exec);
        var windows = await c.Windows.ListAsync();
        var w = windows.GroupBy(x => x.Title).Where(g => g.Count() == 1).Select(g => g.First()).First(); // a live window with a unique title
        var loc = new PointLocator(ToolContext.LocatorOf(w), 0.5, 0.25);

        var o = await WinUse.Cli.RunCommand.CreateDispatcher(c, heal: false)
            .InvokeAsync(new("act", Args(("intent", "tap OK"), ("kind", "click")), loc, IsReplay: true), default);

        Assert.True(o.Ok, o.Error);
        Assert.NotEqual(false, o.Passed);
        Assert.Empty(jev.Calls);
        Assert.Equal(loc.ToScreen(w.Rect), exec.LastPoint);
        Assert.Equal(ActKind.Click, exec.LastKind);
        Assert.IsType<PointLocator>(o.Resolved);
        Assert.True(o.Result["replayed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Replay_PointLocator_WindowGone_FailsNotErrors()
    {
        var exec = new FakeExecutor();
        using var c = VisualContext(new FakeJev(cell: "A1"), exec);
        var loc = new PointLocator(new WindowLocator("^no such window 9f3e$", "nope", null), 0.5, 0.5);
        var o = await WinUse.Cli.RunCommand.CreateDispatcher(c, heal: false)
            .InvokeAsync(new("act", Args(("intent", "tap OK"), ("kind", "click")), loc, IsReplay: true), default);
        Assert.True(o.Ok, o.Error);
        Assert.False(o.Passed);
        Assert.Equal("locator not found: resolved", o.Error);
        Assert.Null(exec.LastPoint);
    }
}
