using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public class JabDllLocatorTests
{
    private const string Exe = @"C:\app";
    private static readonly string ExeDll = Path.Combine(Exe, JabDllLocator.FileName);
    private static readonly string JavaHomeDll = Path.Combine(@"C:\jdk", "bin", JabDllLocator.FileName);
    private static readonly string RegistryDll = Path.Combine(@"C:\reg-jre", "bin", JabDllLocator.FileName);

    private static string? Env(string name) => name == "JAVA_HOME" ? @"C:\jdk" : null;

    private static string? Locate(string? configured, Func<string, bool> exists, out IReadOnlyList<string> searched) =>
        JabDllLocator.Locate(configured, Exe, Env, exists, () => [@"C:\reg-jre"], out searched);

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
}
