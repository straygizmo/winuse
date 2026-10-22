using System.Diagnostics;
using WinUse.Core.Java;
using WinUse.Core.Threading;
using WinUse.Core.Uia;
using WinUse.Core.Windows;
using WinUse.Tools;

namespace WinUse.Core.Tests.Java;

/// <summary>The built-in client against real JVMs: Temurin 21 x64, Temurin 8 x86 and Temurin 8 x86 forced onto its legacy bridge.</summary>
[Collection("Desktop")]
public class JabAccessIntegrationTests
{
    internal static bool Ready(Jvm jvm) => Jvms.Available(jvm);

    [Theory]
    [InlineData(Jvm.Jdk21x64)]
    [InlineData(Jvm.Jre8x86)]
    [InlineData(Jvm.Jre8x86Legacy)]
    public async Task Bridge_SeesFixtureWindow_AndReadsRootInfo(Jvm jvm)
    {
        if (!Ready(jvm)) return; // no JVM under tools/: skip
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var f = await SwingFixture.LaunchAsync(windows, jvm);
        using var bridge = new JabBridge(TestConfig.Default);
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
        Assert.StartsWith("winuse ", version!.WindowsDllVersion);
        Assert.NotNull(firstChild);
        Assert.Equal("root pane", firstChild!.Role);
        Assert.Contains(jvm == Jvm.Jre8x86Legacy ? "legacy" : "wide", bridge.Status);
    }

    /// <summary>Two JVMs of different wire formats at once: each handle goes to its own JVM in that JVM's layout.</summary>
    [Fact]
    public async Task TwoJvms_BothReadable()
    {
        if (!Ready(Jvm.Jdk21x64) || !Ready(Jvm.Jre8x86Legacy)) return;
        using var ctx = ToolContext.Create(TestConfig.Default, null, null);
        using var a = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jdk21x64);
        using var b = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jre8x86Legacy);
        foreach (var f in new[] { a, b })
        {
            Assert.True(await WaitJava(ctx, f.Window), $"isJavaWindow stayed false for {f.Pid}");
            var tree = await ctx.Java.GetTreeAsync(f.Window.Hwnd, new TreeOptions());
            Assert.Contains(Flatten(tree), n => n.ControlType == "Button" && n.Name == "Increment");
        }
        Assert.Contains("2 JVMs", ctx.Java.BridgeStatus);
        Assert.Contains("legacy", ctx.Java.BridgeStatus);
        Assert.Contains("wide", ctx.Java.BridgeStatus);
    }

    /// <summary>A JVM that exits is forgotten at the next call, and that call does not wait for the 4 s message timeout.</summary>
    [Fact]
    public async Task Jvm_Exit_IsJavaWindowFalse_NoHang()
    {
        if (!Ready(Jvm.Jre8x86)) return;
        using var ctx = ToolContext.Create(TestConfig.Default, null, null);
        var f = await SwingFixture.LaunchAsync(ctx.Windows, Jvm.Jre8x86);
        Assert.True(await WaitJava(ctx, f.Window));
        Assert.Contains("1 JVMs", ctx.Java.BridgeStatus);
        f.Dispose(); // kills the JVM
        var sw = Stopwatch.StartNew();
        Assert.False(await ctx.Java.IsJavaWindowAsync(f.Window));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
        Assert.DoesNotContain("1 JVMs", ctx.Java.BridgeStatus);
    }

    internal static async Task<bool> WaitJava(ToolContext ctx, WindowInfo w)
    {
        for (var i = 0; i < 20; i++)
        {
            if (await ctx.Java.IsJavaWindowAsync(w)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    internal static IEnumerable<UiNode> Flatten(UiNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in Flatten(c)) yield return d;
    }
}
