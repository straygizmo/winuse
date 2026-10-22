using WinUse.Cli;
using WinUse.Core.Java;

namespace WinUse.Core.Tests.Cli;

public sealed class JabCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winuse-jabcmd").FullName;
    private string Props => Path.Combine(_dir, ".accessibility.properties");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (int Code, string Out) Run(params string[] args)
    {
        var w = new StringWriter();
        var code = JabCommand.Execute(["jab", .. args], w, Props);
        return (code, w.ToString());
    }

    [Fact]
    public void Status_WhenNotEnabled_SaysHow()
    {
        var (code, text) = Run("status");
        Assert.Equal(0, code);
        Assert.Contains("not enabled", text);
        Assert.Contains("winuse jab enable", text);
        Assert.Contains(Props, text);
    }

    [Fact]
    public void Enable_WritesFile_AndAsksForRestart()
    {
        var (code, text) = Run("enable");
        Assert.Equal(0, code);
        Assert.True(JabEnablement.IsEnabled(Props));
        Assert.Contains("enabled", text);
        Assert.Contains("restart", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enabled", Run("status").Out);
    }

    [Fact]
    public void Disable_RemovesIt()
    {
        Run("enable");
        var (code, text) = Run("disable");
        Assert.Equal(0, code);
        Assert.Contains("disabled", text);
        Assert.False(JabEnablement.IsEnabled(Props));
    }

    [Fact]
    public void UnknownSubcommand_PrintsUsage_ReturnsTwo()
    {
        var (code, text) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("usage", text);
        Assert.Equal(2, Run().Code);
    }
}
