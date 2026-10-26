using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

public class StopTests
{
    private static HookResult Stop(GateFixture f, bool active = false, string message = "Done.", string? cwd = null) =>
        f.RunHook("stop.ps1", new JsonObject
        {
            ["hook_event_name"] = "Stop",
            ["stop_hook_active"] = active,
            ["last_assistant_message"] = message,
        }, cwd);

    private static GateFixture Started()
    {
        var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        return f;
    }

    private static void AssertPasses(HookResult r)
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout.Trim());
    }

    [Fact]
    public void Unverified_BlocksOnce_ThenPasses()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        var first = Stop(f);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("block", (string?)first.Json!["decision"]);
        var reason = (string)first.Json!["reason"]!;
        Assert.Contains("src/a.tsx", reason);
        Assert.Contains("assert_", reason);
        Assert.Contains("winuse-e2e", reason);
        AssertPasses(Stop(f));
    }

    [Fact]
    public void NewContent_BlocksAgain()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        Stop(f);
        f.Write("src/a.tsx", "two");
        Assert.Equal("block", (string?)Stop(f).Json!["decision"]);
    }

    [Fact]
    public void StopHookActive_Passes()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        AssertPasses(Stop(f, active: true));
    }

    [Fact]
    public void Verified_Passes()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        PostToolTests.PassAssert(f);
        AssertPasses(Stop(f));
    }

    [Fact]
    public void NonUiChange_Passes()
    {
        using var f = Started();
        f.Write("docs/x.md", "doc");
        AssertPasses(Stop(f));
    }

    [Fact]
    public void ReasonListsAtMostFivePaths()
    {
        using var f = Started();
        for (var i = 0; i < 7; i++) f.Write($"src/f{i}.tsx", "x");
        var reason = (string)Stop(f).Json!["reason"]!;
        Assert.Contains("7 UI file", reason);
        Assert.Equal(5, Enumerable.Range(0, 7).Count(i => reason.Contains($"src/f{i}.tsx")));
    }

    [Fact]
    public void NonAsciiStdin_Ok()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        var r = Stop(f, message: "実装しました。スモークをお願いします");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("block", (string?)r.Json!["decision"]);
    }

    [Fact]
    public void NotARepo_Passes()
    {
        using var f = new GateFixture();
        var outside = Path.Combine(Path.GetTempPath(), "winuse-gate-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try { AssertPasses(Stop(f, cwd: outside)); }
        finally { Directory.Delete(outside); }
    }
}
