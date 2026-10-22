using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

[Collection("Desktop")]
public class SwingFixtureTests
{
    [Theory]
    [InlineData(Jvm.Jdk21x64)]
    [InlineData(Jvm.Jre8x86)]
    public async Task Fixture_Launches_AsSunAwtFrame(Jvm jvm)
    {
        if (!Jvms.Available(jvm)) return; // no JVM in tools/: skip
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var f = await SwingFixture.LaunchAsync(windows, jvm);
        Assert.Equal("WinUse Swing Fixture", f.Window.Title);
        Assert.Equal("SunAwtFrame", f.Window.ClassName);
    }
}
