using WinUse.Core.Java;
using WinUse.Core.Threading;
using WinUse.Core.Windows;
using WinUse.Core.Uia;
using WinUse.Tools;

namespace WinUse.Core.Tests.Java;

[Collection("Desktop")]
public class JabAccessIntegrationTests
{
    internal static string BridgeDll => Path.Combine(Jvms.RepoRoot, "third_party", "jab", JabDllLocator.FileName);

    internal static bool Ready(Jvm jvm) => Jvms.Available(jvm) && File.Exists(BridgeDll);

    [Theory]
    [InlineData(Jvm.Jdk21x64)]
    [InlineData(Jvm.Jre8x86)]
    public async Task Bridge_SeesFixtureWindow_AndReadsRootInfo(Jvm jvm)
    {
        if (!Ready(jvm)) return; // no JVM under tools/: skip
        var cfg = TestConfig.Default with { JabDll = BridgeDll };
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var f = await SwingFixture.LaunchAsync(windows, jvm);
        using var bridge = new JabBridge(cfg, AppContext.BaseDirectory);
        Assert.True(bridge.IsAvailable, bridge.UnavailableReason);

        var isJava = false;
        for (var i = 0; i < 20 && !isJava; i++)
        {
            isJava = await bridge.RunAsync(j => j.IsJavaWindow(f.Window.Hwnd));
            if (!isJava) await Task.Delay(100);
        }
        Assert.True(isJava, "isJavaWindow stayed false (accessibility not enabled in the fixture JVM?)");

        var (info, version, firstChild) = await bridge.RunAsync(j =>
        {
            var root = j.GetContextFromHwnd(f.Window.Hwnd) ?? throw new InvalidOperationException("no root context");
            try
            {
                var child = j.GetChild(root, 0);
                JabContextInfo? childInfo = null;
                if (child is { } c)
                {
                    childInfo = j.GetInfo(c);
                    j.Release(c);
                }
                return (j.GetInfo(root), j.GetVersion(root.VmId), childInfo);
            }
            finally
            {
                j.Release(root);
            }
        });
        Assert.NotNull(info);
        Assert.Equal("frame", info!.Role);
        Assert.Equal(SwingFixture.Title, info.Name);
        Assert.True(info.ChildrenCount > 0);
        Assert.Contains("showing", info.States);
        Assert.NotNull(version);
        Assert.NotNull(firstChild);
        Assert.Equal("root pane", firstChild!.Role);
    }

    /// <summary>
    /// A 32-bit JRE that loads the legacy JavaAccessBridge.dll (what a Java 7/8 x86 without the -32 files does) answers
    /// isJavaWindow but gives the 64-bit client no accessible context; winuse must say so instead of a bare failure.
    /// </summary>
    [Fact]
    public async Task LegacyJavaBridge_AnswersIsJavaWindow_ButNoContext_AndTheReasonSaysSo()
    {
        if (!Ready(Jvm.Jre8x86Legacy)) return; // no 32-bit JRE 8 under tools/: skip
        var cfg = TestConfig.Default with { JabDll = BridgeDll };
        using var ctx = ToolContext.Create(cfg, null, null);
        using var f = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jre8x86Legacy);

        var info = JavaProcessInfo.Inspect(f.Pid);
        Assert.Equal(JavaProcessInfo.LegacyBridgeModule, info.LoadedBridgeModule);
        Assert.True(info.IsLegacyBridge);
        Assert.Contains("legacy bridge", info.Describe());

        var isJava = false;
        for (var i = 0; i < 20 && !isJava; i++)
        {
            isJava = await ctx.Java.IsJavaWindowAsync(f.Window);
            if (!isJava) await Task.Delay(100);
        }
        Assert.True(isJava, "isJavaWindow stayed false (accessibility not enabled in the fixture JVM?)");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Java.GetTreeAsync(f.Window.Hwnd, new TreeOptions()));
        Assert.Contains("legacy Java-side bridge", ex.Message);
        Assert.Contains(JavaProcessInfo.LegacyBridgeModule, ex.Message);
    }
}
