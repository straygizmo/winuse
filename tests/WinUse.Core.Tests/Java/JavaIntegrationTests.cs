using System.Text.Json.Nodes;
using WinUse.Cli;
using WinUse.Core.Config;
using WinUse.Core.Java;
using WinUse.Core.Jev;
using WinUse.Core.Tests.Grounding;
using WinUse.Tools;

namespace WinUse.Core.Tests.Java;

/// <summary>A decision model that picks the candidate whose description names the wanted label.</summary>
sealed class LabelPickingJev(string label) : IJevClient
{
    public bool IsConfigured => true;
    public DecisionProvider Provider => DecisionProvider.Jev;

    public Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        IReadOnlyList<ImageAttachment>? images = null, CancellationToken ct = default)
    {
        var answers = new Dictionary<string, object>();
        foreach (var (key, q) in questions)
        {
            switch (q)
            {
                case NoulQuestion: answers[key] = new NoulAnswer(0.9); break;
                case ChoiceQuestion cq when key == "kind":
                    answers[key] = new ChoiceAnswer("click", new Dictionary<string, double> { ["click"] = 0.9 }, 0.9); break;
                case ChoiceQuestion cq:
                {
                    var hit = cq.Criteria.FirstOrDefault(kv => kv.Value is { } v && v.Contains($"'{label}'")).Key
                              ?? cq.Criteria.Keys.First();
                    answers[key] = new ChoiceAnswer(hit, new Dictionary<string, double> { [hit] = 0.9 }, 0.9);
                    break;
                }
            }
        }
        return Task.FromResult(new JevResponse("label-picker", answers, 0));
    }
}

/// <summary>End-to-end: the real Swing fixture on a real JVM, through ToolContext / ToolDispatcher like an MCP client.</summary>
[Collection("Desktop")]
public sealed class JavaIntegrationTests : IDisposable
{
    private const string Window = "^WinUse Swing Fixture$";
    private readonly string _tmp = Directory.CreateTempSubdirectory("winuse-java-it").FullName;

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private WinUseConfig Cfg => TestConfig.Default with { AllowedRoots = [_tmp] };

    private static JsonObject Args(params (string k, JsonNode? v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    private sealed class Session : IAsyncDisposable
    {
        public ToolContext Ctx = null!;
        public ToolDispatcher D = null!;
        public SwingFixture Fixture = null!;

        public static async Task<Session?> OpenAsync(Jvm jvm, WinUseConfig cfg, IJevClient? jev = null)
        {
            if (!JabAccessIntegrationTests.Ready(jvm)) return null;
            var ctx = ToolContext.Create(cfg, null, jev);
            var fixture = await SwingFixture.LaunchAsync(ctx.Windows, jvm);
            return new Session { Ctx = ctx, D = new ToolDispatcher(ctx), Fixture = fixture };
        }

        /// <summary>Like a replayed step, a ref argument travels with its locator (the dispatcher refuses bare ref ids).</summary>
        public async Task<JsonNode> Call(string tool, JsonObject args)
        {
            var resolved = args["ref"] is JsonValue v && v.TryGetValue<string>(out var id) && ToolContext.LooksLikeRef(id)
                ? Ctx.GetRef(id).Locator : null;
            var o = await D.InvokeAsync(new(tool, args, resolved), default);
            Assert.True(o.Ok, $"{tool}: {o.Error}");
            return o.Result;
        }

        public async Task<JsonNode> Tree(JsonObject? extra = null)
        {
            var a = Args(("window", Window));
            if (extra is not null) foreach (var kv in extra) a[kv.Key] = kv.Value?.DeepClone();
            return await Call("get_ui_tree", a);
        }

        public async Task<string> Status()
        {
            var tree = await Tree();
            var node = Find(tree["tree"]!, "Text", n => n.StartsWith("status: ")) ?? throw new Exception("no status label");
            return node["name"]!.GetValue<string>();
        }

        public async Task<string> RefOf(string type, string name) =>
            (Find((await Tree())["tree"]!, type, n => n == name) ?? throw new Exception($"{type} '{name}' not found"))["ref"]!.GetValue<string>();

        public async Task<string> WaitStatus(string expected)
        {
            string last = "";
            for (var i = 0; i < 20; i++)
            {
                last = await Status();
                if (last == expected) return last;
                await Task.Delay(150);
            }
            return last;
        }

        public ValueTask DisposeAsync()
        {
            Fixture.Dispose();
            Ctx.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal static JsonNode? Find(JsonNode node, string type, Func<string, bool> name)
    {
        if (node["controlType"]?.GetValue<string>() == type && node["name"]?.GetValue<string>() is { } n && name(n)) return node;
        foreach (var c in node["children"]?.AsArray() ?? [])
            if (c is not null && Find(c, type, name) is { } f) return f;
        return null;
    }

    public static IEnumerable<object[]> Jvms_() => [[Jvm.Jdk21x64], [Jvm.Jre8x86], [Jvm.Jre8x86Legacy]];

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Tree_ListsButtonsMenusAndDeepButton(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var r = await s.Tree();
        Assert.Null(r["java_access_bridge"]?.GetValue<string>());
        var tree = r["tree"]!;
        Assert.NotNull(Find(tree, "Button", n => n == "Increment"));
        Assert.NotNull(Find(tree, "Button", n => n == "Deep"));
        var save = Find(tree, "MenuItem", n => n == "Save");
        Assert.NotNull(save);
        Assert.True(save!["offscreen"]!.GetValue<bool>());
        Assert.StartsWith("j", save["ref"]!.GetValue<string>());
        var edit = Find(tree, "Edit", n => n == "Name");
        Assert.Equal("hello", edit!["value"]!.GetValue<string>());
        Assert.NotNull(Find(tree, "List", _ => true) ?? FindType(tree, "List"));
        Assert.NotNull(FindType(tree, "Tab"));
    }

    private static JsonNode? FindType(JsonNode node, string type)
    {
        if (node["controlType"]?.GetValue<string>() == type) return node;
        foreach (var c in node["children"]?.AsArray() ?? [])
            if (c is not null && FindType(c, type) is { } f) return f;
        return null;
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Invoke_Button_UpdatesStatus(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        await s.Call("invoke", Args(("ref", await s.RefOf("Button", "Increment"))));
        Assert.Equal("status: increment 1", await s.WaitStatus("status: increment 1"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Invoke_ClosedMenuItem_RunsWithoutOpening(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        await s.Call("invoke", Args(("ref", await s.RefOf("MenuItem", "Save"))));
        Assert.Equal("status: save", await s.WaitStatus("status: save"));
        var save = Find((await s.Tree())["tree"]!, "MenuItem", n => n == "Save")!;
        Assert.True(save["offscreen"]!.GetValue<bool>(), "the menu should still be closed");
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task SetChecked_MenuCheckItem(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        await s.Call("set_checked", Args(("ref", await s.RefOf("CheckBox", "Word Wrap")), ("checked", true)));
        Assert.Equal("status: wordwrap on", await s.WaitStatus("status: wordwrap on"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task SetText_Edit(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var id = await s.RefOf("Edit", "Name");
        await s.Call("set_text", Args(("ref", id), ("text", "world")));
        var text = await s.Call("get_text", Args(("ref", id)));
        Assert.Equal("world", text["text"]!.GetValue<string>());
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task SelectItem_List_Tab_Combo(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var tree = (await s.Tree())["tree"]!;
        await s.Call("select_item", Args(("ref", FindType(tree, "List")!["ref"]!.GetValue<string>()), ("item", "Beta")));
        Assert.Equal("status: list Beta", await s.WaitStatus("status: list Beta"));
        await s.Call("select_item", Args(("ref", FindType(tree, "Tab")!["ref"]!.GetValue<string>()), ("item", "Second")));
        Assert.Equal("status: tab Second", await s.WaitStatus("status: tab Second"));
        await s.Call("select_item", Args(("ref", FindType(tree, "ComboBox")!["ref"]!.GetValue<string>()), ("item", "Two")));
        Assert.Equal("status: combo Two", await s.WaitStatus("status: combo Two"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Click_ByRef_UsesCalibratedCenter(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        await s.Call("click", Args(("ref", await s.RefOf("Button", "Increment"))));
        Assert.Equal("status: increment 1", await s.WaitStatus("status: increment 1"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Act_WithLabelPickingModel_PressesTheButton(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg with { JevApiKey = "k" }, new LabelPickingJev("Increment"));
        if (s is null) return;
        var r = await s.Call("act", Args(("intent", "press Increment"), ("window", Window)));
        Assert.True(r["executed"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal("Increment", r["target"]!["label"]!.GetValue<string>());
        Assert.Equal("status: increment 1", await s.WaitStatus("status: increment 1"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task StaleRef_AfterThreeObservations_StillInvokes(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var id = await s.RefOf("Button", "Increment");
        await s.Tree(); await s.Tree(); await s.Tree();
        // Straight through the tool method (as the MCP server does): the old ref's handle is released by now.
        var r = await ElementTools.Invoke(s.Ctx, id);
        Assert.NotEqual(true, r.IsError);
        Assert.Equal("status: increment 1", await s.WaitStatus("status: increment 1"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Scenario_SaveAndReplay_InvokesByLocator(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var inv = await s.Call("invoke", Args(("ref", await s.RefOf("MenuItem", "New"))));
        Assert.Equal("status: new", await s.WaitStatus("status: new"));
        var path = Path.Combine(_tmp, "swing.json");
        await s.Call("scenario_save", Args(("name", "swing"), ("step_ids", new JsonArray(inv["step_id"]!.GetValue<string>())), ("path", path)));

        // Press Save so the replay has something observable to change back.
        await s.Call("invoke", Args(("ref", await s.RefOf("MenuItem", "Save"))));
        Assert.Equal("status: save", await s.WaitStatus("status: save"));

        using var fresh = ToolContext.Create(Cfg);
        var code = await RunCommand.ExecuteAsync(path, false, null, fresh);
        Assert.Equal(0, code);
        Assert.Equal("status: new", await s.WaitStatus("status: new"));
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task GetUiTree_WithJabOff_ReturnsRootAndReason(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg with { Jab = JabMode.Off });
        if (s is null) return;
        var r = await s.Tree();
        // UIA still synthesises the title bar controls, but none of the Swing content is visible.
        Assert.Null(Find(r["tree"]!, "Button", n => n == "Increment"));
        Assert.Equal("WINUSE_JAB=off", r["java_access_bridge"]!.GetValue<string>());
    }

    [Theory] [MemberData(nameof(Jvms_))]
    public async Task Doctor_ReportsFixtureAsResponding(Jvm jvm)
    {
        await using var s = await Session.OpenAsync(jvm, Cfg);
        if (s is null) return;
        var w = new StringWriter();
        await DoctorCommand.ExecuteAsync(s.Ctx, w);
        var text = w.ToString();
        Assert.Contains("Java Access Bridge: built-in client (window 0x", text);
        Assert.Contains($"1 JVMs: pid {s.Fixture.Pid} {(jvm == Jvm.Jre8x86Legacy ? "legacy" : "wide")})", text);
        Assert.Contains("\"WinUse Swing Fixture\"", text);
        Assert.Contains(jvm == Jvm.Jre8x86Legacy ? "bridge responding (legacy wire format" : "bridge responding (wide wire format", text);
        Assert.Contains(jvm == Jvm.Jdk21x64 ? ", Java 21" : ", Java 1.8.0", text);
        Assert.Contains(", bridge ", text);
        Assert.Contains(jvm == Jvm.Jdk21x64 ? "64-bit" : "32-bit", text);
        Assert.Contains(jvm switch
        {
            Jvm.Jdk21x64 => "javaaccessbridge.dll loaded (wide wire format)",
            Jvm.Jre8x86 => "JavaAccessBridge-32.dll loaded (wide wire format)",
            _ => "JavaAccessBridge.dll loaded (legacy wire format)",
        }, text);
    }
}
