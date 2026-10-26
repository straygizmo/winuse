namespace WinUse.Core.Tests.Gate;

public class GateLibTests
{
    private static string Matches(GateFixture f, string[] ui, string[] exclude, string path)
    {
        static string List(string[] xs) => "@(" + string.Join(",", xs.Select(x => $"(Convert-GlobToRegex '{x}')")) + ")";
        return f.RunLib($"$c = @{{ Status = 'ok'; Ui = {List(ui)}; Exclude = {List(exclude)} }}; Test-UiPath '{path}' $c");
    }

    [Fact]
    public void Glob_DoubleStar_MatchesAnyDepth()
    {
        using var f = new GateFixture();
        string[] ui = ["src/**/*.tsx"];
        Assert.Equal("True", Matches(f, ui, [], "src/a.tsx"));
        Assert.Equal("True", Matches(f, ui, [], "src/x/y/a.tsx"));
        Assert.Equal("False", Matches(f, ui, [], "lib/a.tsx"));
        Assert.Equal("False", Matches(f, ui, [], "src/a.ts"));
    }

    [Fact]
    public void Glob_Star_DoesNotCrossSlash()
    {
        using var f = new GateFixture();
        Assert.Equal("True", Matches(f, ["src/*.ts"], [], "src/a.ts"));
        Assert.Equal("False", Matches(f, ["src/*.ts"], [], "src/x/a.ts"));
    }

    [Fact]
    public void Glob_IsCaseInsensitive()
    {
        using var f = new GateFixture();
        Assert.Equal("True", Matches(f, ["src/**/*.TSX"], [], "src/A.tsx"));
    }

    [Fact]
    public void UiPath_ExcludeWins()
    {
        using var f = new GateFixture();
        Assert.Equal("False", Matches(f, ["src/**/*.tsx"], ["**/*.test.tsx"], "src/a.test.tsx"));
        Assert.Equal("True", Matches(f, ["src/**/*.tsx"], ["**/*.test.tsx"], "src/a.tsx"));
    }

    [Fact]
    public void Config_Missing_Broken_Ok()
    {
        using var f = new GateFixture();
        var status = "(Get-GateConfig (Get-RepoRoot (Get-Location).Path)).Status";
        Assert.Equal("missing", f.RunLib(status));
        f.WriteConfig("{");
        Assert.Equal("broken", f.RunLib(status));
        f.WriteConfig("{\"ui\":[]}");
        Assert.Equal("broken", f.RunLib(status));
        f.WriteConfig("{\"ui\":[\"src/**\"]}");
        Assert.Equal("ok", f.RunLib(status));
    }

    [Fact]
    public void RepoRoot_FromSubdirectory_ReturnsToplevel()
    {
        using var f = new GateFixture();
        f.Write("src/deep/x.txt", "x");
        var sub = Path.Combine(f.Repo, "src", "deep");
        var root = f.RunLib($"Get-RepoRoot '{sub}'");
        Assert.Equal(Path.GetFullPath(f.Repo).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public void RepoRoot_OutsideRepo_ReturnsNull()
    {
        using var f = new GateFixture();
        var outside = Path.Combine(Path.GetTempPath(), "winuse-gate-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Equal("null", f.RunLib($"$r = Get-RepoRoot '{outside}'; if ($null -eq $r) {{ 'null' }} else {{ $r }}"));
        }
        finally { Directory.Delete(outside); }
    }
}
