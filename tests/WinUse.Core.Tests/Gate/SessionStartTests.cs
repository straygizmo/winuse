using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

public class SessionStartTests
{
    private static HookResult Start(GateFixture f, string source = "startup") =>
        f.RunHook("session-start.ps1", new JsonObject { ["hook_event_name"] = "SessionStart", ["source"] = source });

    [Fact]
    public void SessionStart_RecordsBaseline_UserEditsNotTargets()
    {
        using var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.Write("src/a.tsx", "a1");
        f.Commit("base");
        f.Write("src/a.tsx", "user edit");
        var r = Start(f);
        Assert.Equal(0, r.ExitCode);
        Assert.Empty(GateStatus.Targets(f));
        f.Write("src/a.tsx", "claude edit");
        Assert.Equal(["src/a.tsx"], GateStatus.Unverified(f));
    }

    [Fact]
    public void SessionStart_ResumeKeepsBaseline()
    {
        using var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        Start(f);
        f.Write("src/a.tsx", "claude edit");
        Start(f, "resume");
        Assert.Equal(["src/a.tsx"], GateStatus.Unverified(f));
    }

    [Fact]
    public void SessionStart_MissingConfig_EmitsNotice()
    {
        using var f = new GateFixture();
        var r = Start(f);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("SessionStart", (string?)r.Json!["hookSpecificOutput"]!["hookEventName"]);
        Assert.Contains("no .claude/winuse-gate.json", (string?)r.Json!["hookSpecificOutput"]!["additionalContext"]);
    }

    [Fact]
    public void SessionStart_BrokenConfig_EmitsNotice()
    {
        using var f = new GateFixture();
        f.WriteConfig("{\"ui\":\"src/**\"}");
        var r = Start(f);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("is invalid", (string?)r.Json!["hookSpecificOutput"]!["additionalContext"]);
    }

    [Fact]
    public void SessionStart_NotARepo_Silent()
    {
        using var f = new GateFixture();
        var outside = Path.Combine(Path.GetTempPath(), "winuse-gate-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var r = f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" }, outside);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal("", r.Stdout.Trim());
        }
        finally { Directory.Delete(outside); }
    }
}
