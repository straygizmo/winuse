using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public class JabDllLocatorTests
{
    private const string Exe = @"C:\app";
    private static readonly string ExeDll = Path.Combine(Exe, JabDllLocator.FileName);
    private static readonly string JavaHomeDll = Path.Combine(@"C:\jdk", "bin", JabDllLocator.FileName);
    private static readonly string RegistryDll = Path.Combine(@"C:\reg-jre", "bin", JabDllLocator.FileName);

    private static string? Env(string name) => name switch
    {
        "JAVA_HOME" => @"C:\jdk",
        "SystemRoot" => @"C:\Windows",
        _ => null,
    };

    private static string? Locate(string? configured, Func<string, bool> exists, out IReadOnlyList<string> searched) =>
        JabDllLocator.Locate(configured, Exe, Env, exists, () => [@"C:\reg-jre"], out searched, out _);

    private static string? Locate(Func<string, bool> exists, out IReadOnlyList<string> thirtyTwoBit) =>
        JabDllLocator.Locate(null, Exe, Env, exists, () => [@"C:\reg-jre"], out _, out thirtyTwoBit);

    [Fact]
    public void Locate_PrefersConfiguredPath()
    {
        var p = Locate(@"C:\custom\bridge.dll", f => true, out _);
        Assert.Equal(@"C:\custom\bridge.dll", p);
    }

    [Fact]
    public void Locate_ThenExeDir_ThenJavaHome_ThenRegistry()
    {
        Assert.Equal(ExeDll, Locate(null, f => true, out _));
        Assert.Equal(JavaHomeDll, Locate(null, f => f != ExeDll, out _));
        Assert.Equal(RegistryDll, Locate(null, f => f == RegistryDll, out _));
    }

    [Fact]
    public void Locate_ConfiguredButMissing_FallsThroughToExeDir()
    {
        Assert.Equal(ExeDll, Locate(@"C:\custom\bridge.dll", f => f == ExeDll, out _));
    }

    [Fact]
    public void Locate_ReturnsNullAndListsSearched_WhenMissing()
    {
        var p = Locate(null, f => false, out var searched);
        Assert.Null(p);
        Assert.Equal(5, searched.Count);
        Assert.Contains("WINUSE_JAB_DLL", searched[0]);
        Assert.Contains(Exe, searched[1]);
        Assert.Contains(@"C:\jdk", searched[2]);
        Assert.Contains(@"C:\reg-jre", searched[3]);
        Assert.Equal("PATH", searched[4]);
    }

    /// <summary>A 32-bit JRE 7/8 ships only WindowsAccessBridge-32.dll and the legacy WindowsAccessBridge.dll; neither loads into the 64-bit winuse.</summary>
    [Fact]
    public void Locate_ListsThirtyTwoBitClientDlls_WhenThe64BitOneIsMissing()
    {
        var reg32 = Path.Combine(@"C:\reg-jre", "bin", JabDllLocator.FileName32);
        var regLegacy = Path.Combine(@"C:\reg-jre", "bin", JabDllLocator.FileNameLegacy);
        var sysWow = Path.Combine(@"C:\Windows", "SysWOW64", JabDllLocator.FileName32);
        var p = Locate(f => f == reg32 || f == regLegacy || f == sysWow, out var thirtyTwoBit);
        Assert.Null(p);
        Assert.Equal([reg32, regLegacy, sysWow], thirtyTwoBit);
    }

    [Fact]
    public void Locate_ThirtyTwoBitList_IsEmpty_WhenNoneExist()
    {
        Assert.Null(Locate(f => false, out var thirtyTwoBit));
        Assert.Empty(thirtyTwoBit);
    }

    [Fact]
    public void Locate_ThirtyTwoBitList_IgnoresTheFoundSixtyFourBitDll()
    {
        Assert.Equal(ExeDll, Locate(f => f == ExeDll, out var thirtyTwoBit));
        Assert.Empty(thirtyTwoBit);
    }

    [Fact]
    public void NotFoundReason_NamesThirtyTwoBitDlls_AndTheFix()
    {
        var reg32 = Path.Combine(@"C:\reg-jre", "bin", JabDllLocator.FileName32);
        var reason = JabDllLocator.NotFoundReason(["WINUSE_JAB_DLL (not set)", Exe], [reg32]);
        Assert.StartsWith("not found (searched: WINUSE_JAB_DLL (not set); " + Exe + ")", reason);
        Assert.Contains(reg32, reason);
        Assert.Contains("64-bit winuse cannot load them", reason);
        Assert.Contains("fetch-jab.ps1", reason);
        Assert.Contains("WINUSE_JAB_DLL", reason);
    }

    [Fact]
    public void NotFoundReason_WithoutThirtyTwoBitDlls_OnlySaysWhereItLooked()
    {
        var reason = JabDllLocator.NotFoundReason([Exe], []);
        Assert.DoesNotContain("32-bit", reason);
        Assert.Contains(JabDllLocator.FileName, reason);
    }
}
