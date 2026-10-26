using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

/// <summary>Edits in another repository or worktree than the session's cwd are gated too.</summary>
public class SessionReposTests
{
    private static HookResult Stop(GateFixture f) =>
        f.RunHook("stop.ps1", new JsonObject { ["hook_event_name"] = "Stop", ["stop_hook_active"] = false });

    private static HookResult TrackEdit(GateFixture f, string filePath, string tool = "Edit") =>
        f.RunHook("track-edit.ps1", new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["file_path"] = filePath },
        });

    private static (GateFixture Main, GateFixture Other) TwoRepos()
    {
        var main = new GateFixture();
        main.WriteConfig(GateStatus.TsxConfig);
        main.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        var other = new GateFixture(main.DataDir);
        other.WriteConfig(GateStatus.TsxConfig);
        other.Commit("config");
        return (main, other);
    }

    [Fact]
    public void EditInOtherRepo_BlocksStop_UntilAsserted()
    {
        var (main, other) = TwoRepos();
        using (main)
        using (other)
        {
            other.Write("src/a.tsx", "one");
            TrackEdit(main, Path.Combine(other.Repo, "src", "a.tsx"));
            var r = Stop(main);
            Assert.Equal("block", (string?)r.Json!["decision"]);
            Assert.Contains("src/a.tsx", (string)r.Json!["reason"]!);
            other.Write("src/a.tsx", "two");
            PostToolTests.PassAssert(main);
            Assert.Equal("", Stop(main).Stdout.Trim());
        }
    }

    [Fact]
    public void UntrackedOtherRepo_IsIgnored()
    {
        var (main, other) = TwoRepos();
        using (main)
        using (other)
        {
            other.Write("src/a.tsx", "one");
            Assert.Equal("", Stop(main).Stdout.Trim());
        }
    }

    [Fact]
    public void TrackEdit_OutsideAnyRepo_Silent()
    {
        using var f = new GateFixture();
        var r = TrackEdit(f, Path.Combine(Path.GetTempPath(), "no-repo-here", "x.tsx"), tool: "Write");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout.Trim());
    }

    [Fact]
    public void BrokenConfigMidSession_BlocksOnceWithNotice()
    {
        using var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        f.WriteConfig("{");
        var r = Stop(f);
        Assert.Equal("block", (string?)r.Json!["decision"]);
        Assert.Contains("is invalid", (string)r.Json!["reason"]!);
        Assert.Equal("", Stop(f).Stdout.Trim());
    }

    [Fact]
    public void MoreThan500Targets_AllVerified()
    {
        using var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        for (var i = 0; i < 520; i++) f.Write($"src/gen/file{i:D4}.tsx", i.ToString());
        PostToolTests.PassAssert(f);
        Assert.Empty(GateStatus.Unverified(f));
    }
}
