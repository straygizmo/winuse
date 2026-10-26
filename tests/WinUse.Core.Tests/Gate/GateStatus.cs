namespace WinUse.Core.Tests.Gate;

/// <summary>Reads gate-lib.ps1 results through <see cref="GateFixture.RunLib"/>.</summary>
public static class GateStatus
{
    public const string TsxConfig = "{\"ui\":[\"src/**/*.tsx\"]}";

    private const string Setup = "$root = Get-RepoRoot (Get-Location).Path; $c = Get-GateConfig $root; ";

    /// <summary>Dirty UI files as sorted "path|hash" lines.</summary>
    public static string[] Dirty(GateFixture f) =>
        Lines(f.RunLib(Setup + "$d = Get-DirtyUiFiles $root $c; $d.Keys | Sort-Object | ForEach-Object { \"$_|$($d[$_])\" }"));

    /// <summary>Sorted unverified paths for the session.</summary>
    public static string[] Unverified(GateFixture f, string session = "s1") =>
        Lines(f.RunLib(Setup + $"$s = Get-GateStatus $root $c (Get-StateDir $DataDir $root) '{session}'; $s.Unverified.Keys | Sort-Object"));

    /// <summary>Sorted target paths for the session.</summary>
    public static string[] Targets(GateFixture f, string session = "s1") =>
        Lines(f.RunLib(Setup + $"$s = Get-GateStatus $root $c (Get-StateDir $DataDir $root) '{session}'; $s.Targets.Keys | Sort-Object"));

    public static string Fingerprint(GateFixture f, string session = "s1") =>
        f.RunLib(Setup + $"(Get-GateStatus $root $c (Get-StateDir $DataDir $root) '{session}').Fingerprint");

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
