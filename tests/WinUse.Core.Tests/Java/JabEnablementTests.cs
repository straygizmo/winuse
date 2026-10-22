using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public sealed class JabEnablementTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("winuse-jab").FullName;
    private string Props => Path.Combine(_dir, ".accessibility.properties");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ComposeToolOptions_AddsTheFlag_OnceOnly()
    {
        Assert.Equal(JabEnablement.ToolOption, JabEnablement.ComposeToolOptions(null));
        Assert.Equal(JabEnablement.ToolOption, JabEnablement.ComposeToolOptions("   "));
        Assert.Equal("-Xmx1g " + JabEnablement.ToolOption, JabEnablement.ComposeToolOptions("-Xmx1g"));
        var already = "-Djavax.accessibility.assistive_technologies=com.example.Other -Xmx1g";
        Assert.Equal(already, JabEnablement.ComposeToolOptions(already));
    }

    [Fact]
    public void Enable_CreatesFile_AndIsEnabled()
    {
        Assert.False(JabEnablement.IsEnabled(Props));
        JabEnablement.Enable(Props);
        Assert.True(JabEnablement.IsEnabled(Props));
        var lines = File.ReadAllLines(Props);
        Assert.Contains("assistive_technologies=com.sun.java.accessibility.AccessBridge", lines);
        Assert.Contains("screen_magnifier_present=true", lines);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Enable_KeepsOtherLinesAndOtherTechnologies_AndIsIdempotent()
    {
        File.WriteAllLines(Props, ["# my settings", "assistive_technologies=foo.Bar", "x=1"]);
        JabEnablement.Enable(Props);
        JabEnablement.Enable(Props);
        var lines = File.ReadAllLines(Props);
        Assert.Equal("# my settings", lines[0]);
        Assert.Equal("assistive_technologies=foo.Bar,com.sun.java.accessibility.AccessBridge", lines[1]);
        Assert.Contains("x=1", lines);
        Assert.Single(lines, l => l.StartsWith("screen_magnifier_present="));
        Assert.True(JabEnablement.IsEnabled(Props));
    }

    [Fact]
    public void Disable_RemovesOurSettings_KeepsTheRest()
    {
        File.WriteAllLines(Props, ["x=1", "assistive_technologies=foo.Bar,com.sun.java.accessibility.AccessBridge", "screen_magnifier_present=true"]);
        JabEnablement.Disable(Props);
        Assert.Equal(["x=1", "assistive_technologies=foo.Bar"], File.ReadAllLines(Props));
        Assert.False(JabEnablement.IsEnabled(Props));
    }

    [Fact]
    public void Disable_DeletesFile_WhenNothingElseRemains()
    {
        JabEnablement.Enable(Props);
        JabEnablement.Disable(Props);
        Assert.False(File.Exists(Props));
        JabEnablement.Disable(Props); // missing file is fine
    }

    [Fact]
    public void DefaultPropertiesPath_IsInTheUserProfile()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".accessibility.properties"),
            JabEnablement.DefaultPropertiesPath);
    }
}
