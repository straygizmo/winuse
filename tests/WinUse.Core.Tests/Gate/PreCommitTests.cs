using System.Text.Json.Nodes;

namespace WinUse.Core.Tests.Gate;

public class PreCommitTests
{
    private static string Plan(GateFixture f, string command) =>
        f.RunLib($"$p = Get-CommitPlan '{command.Replace("'", "''")}' 'C:\\work'; if ($null -eq $p) {{ 'null' }} else {{ \"$($p.Mode)|$($p.Skip)|$($p.Dir)\" }}");

    private static HookResult PreCommit(GateFixture f, string command, string tool = "Bash") =>
        f.RunHook("pre-commit.ps1", new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["command"] = command },
        });

    private static GateFixture Started()
    {
        var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.Commit("config");
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        return f;
    }

    private static void AssertAllowed(HookResult r)
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Stdout.Trim());
    }

    private static string AssertDenied(HookResult r)
    {
        Assert.Equal(0, r.ExitCode);
        var output = r.Json!["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", (string?)output["hookEventName"]);
        Assert.Equal("deny", (string?)output["permissionDecision"]);
        return (string)output["permissionDecisionReason"]!;
    }

    [Fact]
    public void CommitPlan_Parsing()
    {
        using var f = new GateFixture();
        Assert.Equal("staged|False|C:\\work", Plan(f, "git commit -m \"x\""));
        Assert.Equal("worktree|False|C:\\work\\sub", Plan(f, "git -C sub commit -am \"x\""));
        Assert.Equal("worktree|False|C:\\work\\a", Plan(f, "cd a && git commit --all -m x"));
        Assert.Equal("staged|False|C:\\work\\sub", Plan(f, "Set-Location sub; git commit -m x"));
        Assert.Equal("staged|False|D:\\other", Plan(f, "cd \"D:\\other\" && git commit -m x"));
        Assert.Equal("staged|False|D:\\other\\sub", Plan(f, "cd D:\\other && git -C sub commit -m x"));
        Assert.Equal("worktree|False|C:\\work", Plan(f, "git add -A && git commit -m x"));
        Assert.Equal("worktree|False|C:\\work", Plan(f, "git add src/a.tsx; git commit -m x"));
        Assert.Equal("worktree|False|C:\\work", Plan(f, "git commit -m x -- src/a.tsx"));
        Assert.Equal("staged|False|C:\\work", Plan(f, "git -c core.editor=true commit --amend --no-edit"));
        Assert.Equal("null", Plan(f, "git status"));
        Assert.Equal("null", Plan(f, "git log --grep commit"));
        Assert.Equal("staged|True|C:\\work", Plan(f, "git commit -m \"fix\" -m \"winuse-gate: skip no display\""));
    }

    [Fact]
    public void StagedUnverifiedUi_Denied()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Git("add", "src/a.tsx");
        Assert.Contains("src/a.tsx", AssertDenied(PreCommit(f, "git commit -m x")));
    }

    [Fact]
    public void PowerShellTool_Denied()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Git("add", "src/a.tsx");
        AssertDenied(PreCommit(f, "git commit -m x", tool: "PowerShell"));
    }

    [Fact]
    public void NonUiOnlyCommit_Allowed()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Write("docs/x.md", "doc");
        f.Git("add", "docs/x.md");
        AssertAllowed(PreCommit(f, "git commit -m x"));
    }

    [Fact]
    public void CommitAll_IncludesUnstagedUi_Denied()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Commit("a");
        f.Write("src/a.tsx", "two");
        AssertDenied(PreCommit(f, "git commit -am x"));
    }

    [Fact]
    public void GitAddThenCommit_Denied()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        AssertDenied(PreCommit(f, "git add -A && git commit -m x"));
    }

    [Fact]
    public void CdIntoOtherRepo_UsesThatRepo()
    {
        using var main = Started();
        using var other = new GateFixture(main.DataDir);
        other.WriteConfig(GateStatus.TsxConfig);
        other.Commit("config");
        other.Write("src/a.tsx", "one");
        other.Git("add", "src/a.tsx");
        Assert.Contains("src/a.tsx", AssertDenied(PreCommit(main, $"cd \"{other.Repo}\" && git commit -m x")));
    }

    [Fact]
    public void VerifiedStaged_Allowed()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Git("add", "src/a.tsx");
        PostToolTests.PassAssert(f);
        AssertAllowed(PreCommit(f, "git commit -m x"));
    }

    [Fact]
    public void StagedContentDiffersFromVerified_Denied()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        PostToolTests.PassAssert(f);
        f.Write("src/a.tsx", "two");
        f.Git("add", "src/a.tsx");
        AssertDenied(PreCommit(f, "git commit -m x"));
    }

    [Fact]
    public void UserBaselineFile_Allowed()
    {
        using var f = new GateFixture();
        f.WriteConfig(GateStatus.TsxConfig);
        f.Commit("config");
        f.Write("src/a.tsx", "user edit");
        f.RunHook("session-start.ps1", new JsonObject { ["source"] = "startup" });
        f.Git("add", "src/a.tsx");
        AssertAllowed(PreCommit(f, "git commit -m x"));
    }

    [Fact]
    public void Skip_Allowed()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Git("add", "src/a.tsx");
        AssertAllowed(PreCommit(f, "git commit -m \"x\" -m \"winuse-gate: skip no desktop session\""));
    }

    [Fact]
    public void NotACommit_Allowed()
    {
        using var f = Started();
        f.Write("src/a.tsx", "one");
        f.Git("add", "src/a.tsx");
        AssertAllowed(PreCommit(f, "git status"));
    }
}
