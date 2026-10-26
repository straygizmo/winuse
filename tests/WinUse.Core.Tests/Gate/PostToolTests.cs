using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

public class PostToolTests
{
    private static string Passed(GateFixture f, string responseJson) =>
        f.RunLib($"$x = ('{{\"r\": {responseJson}}}' | ConvertFrom-Json).r; Get-ToolPassed $x");

    internal static HookResult Tool(GateFixture f, string toolName, string responseJson) =>
        f.RunHook("post-tool.ps1", new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = toolName,
            ["tool_input"] = new JsonObject(),
            ["tool_response"] = JsonNode.Parse(responseJson),
        });

    internal static void PassAssert(GateFixture f) => Tool(f, "mcp__winuse__assert_text", "{\"step_id\":\"s1\",\"passed\":true}");

    private static GateFixture Started()
    {
        var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        return f;
    }

    [Fact]
    public void Passed_Shapes()
    {
        using var f = new GateFixture();
        Assert.Equal("True", Passed(f, "\"{\\\"step_id\\\":\\\"s1\\\",\\\"passed\\\":true}\""));
        Assert.Equal("True", Passed(f, "{\"passed\":true}"));
        Assert.Equal("True", Passed(f, "[{\"type\":\"text\",\"text\":\"{\\\"passed\\\":true}\"}]"));
        Assert.Equal("True", Passed(f, "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"passed\\\":true}\"}]}"));
        Assert.Equal("True", Passed(f, "{\"structuredContent\":{\"passed\":true}}"));
        Assert.Equal("False", Passed(f, "{\"passed\":false}"));
        Assert.Equal("False", Passed(f, "{\"passed\":\"true\"}"));
        Assert.Equal("False", Passed(f, "{}"));
        Assert.Equal("False", Passed(f, "\"not json\""));
    }

    [Fact]
    public void AssertPass_MarksTargetsVerified()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        var r = Tool(f, "mcp__winuse__assert_text", "{\"step_id\":\"s1\",\"passed\":true}");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout.Trim());
        Assert.Empty(GateStatus.Unverified(f));
        f.Write("src/a.tsx", "two");
        Assert.Equal(["src/a.tsx"], GateStatus.Unverified(f));
    }

    [Fact]
    public void PluginPrefixedToolName_Counts()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        Tool(f, "mcp__plugin_x_winuse__assert_exists", "{\"passed\":true}");
        Assert.Empty(GateStatus.Unverified(f));
    }

    [Fact]
    public void FailedAssert_Or_Click_DoesNotRecord()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        Tool(f, "mcp__winuse__assert_text", "{\"passed\":false}");
        Tool(f, "mcp__winuse__click", "{\"passed\":true}");
        Assert.Equal(["src/a.tsx"], GateStatus.Unverified(f));
    }

    [Fact]
    public void ScenarioRunPass_Records()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        Tool(f, "mcp__winuse__scenario_run", "{\"passed\":true,\"exit_code\":0}");
        Assert.Empty(GateStatus.Unverified(f));
    }

    [Fact]
    public async Task ConcurrentRecords_BothKept()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Write("src/b.tsx", "one");
        var input = () => new JsonObject
        {
            ["tool_name"] = "mcp__winuse__assert_text",
            ["tool_input"] = new JsonObject(),
            ["tool_response"] = new JsonObject { ["passed"] = true },
        };
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.RunHookAsync("post-tool.ps1", input())));
        Assert.Empty(GateStatus.Unverified(f));
        var stateDirs = Directory.GetDirectories(Path.Combine(f.DataDir, "state"));
        var verified = JsonNode.Parse(File.ReadAllText(Path.Combine(stateDirs.Single(), "verified.json")))!;
        Assert.Equal(2, verified["entries"]!.AsArray().Count);
    }
}
