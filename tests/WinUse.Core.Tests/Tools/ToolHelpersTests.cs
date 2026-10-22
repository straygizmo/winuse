using WinUse.Core.Windows;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

public class ToolHelpersTests
{
    private static readonly Rect Host = new(10, 10, 400, 300);

    [Theory]
    [InlineData(false, 1, 1, null, "app")]           // no render host: never, even when the pid matches
    [InlineData(true, 1, 2, "chrome", "app")]        // host of another Chromium app: no pid / name match
    [InlineData(true, 1, null, null, null)]          // not connected through process_run
    public void DomBelongsToWindow_RejectsForeignOrHostlessWindows(bool hasHost, int windowPid, int? ownerPid, string? hostProcess, string? ownerProcess) =>
        Assert.False(ToolContext.DomBelongsToWindow(hasHost ? Host : null, windowPid, hostProcess, ownerPid, ownerProcess));

    [Fact]
    public void DomBelongsToWindow_AcceptsOwnerPid() =>
        Assert.True(ToolContext.DomBelongsToWindow(Host, 42, "msedgewebview2", 42, "mdium"));

    [Fact]
    public void DomBelongsToWindow_AcceptsHostProcessNamedLikeOwner() =>
        Assert.True(ToolContext.DomBelongsToWindow(Host, 7, "msedge", 99, "msedge.exe"));

    [Theory]
    [InlineData(null, null, null, null, "screen", null, null)]
    [InlineData(null, null, null, "win3", "window", null, "win3")]       // window ref goes through the title slot
    [InlineData(null, null, null, "^Notepad", "window", null, "^Notepad")]
    [InlineData(null, "win1", "^X", "^Y", "window", null, "^Y")]          // window wins over ref/title
    [InlineData("window", "win1", null, null, "window", "win1", null)]
    [InlineData(null, null, "^X", null, "window", null, "^X")]
    [InlineData("Element", "u4", null, null, "element", null, null)]
    [InlineData("screen", null, null, "^Y", "screen", null, "^Y")]       // explicit target is kept
    public void NormalizeScreenshotArgs_WindowDefaultsTargetToWindow(string? target, string? @ref, string? title, string? window,
        string expectedTarget, string? expectedRef, string? expectedTitle) =>
        Assert.Equal((expectedTarget, expectedRef, expectedTitle), ObserveTools.NormalizeScreenshotArgs(target, @ref, title, window));

    [Fact]
    public void ScreenshotPayload_CarriesOrigin()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ToolResult.Ok(
            ObserveTools.ScreenshotPayload(new WinUse.Core.Screen.Capture([1], 960, 540, 0.5, -1920, 50)), "s1"))!;
        Assert.Equal(960, json["width"]!.GetValue<int>());
        Assert.Equal(540, json["height"]!.GetValue<int>());
        Assert.Equal(0.5, json["scale"]!.GetValue<double>());
        Assert.Equal(-1920, json["origin"]!["x"]!.GetValue<int>());
        Assert.Equal(50, json["origin"]!["y"]!.GetValue<int>());
    }

    private static WindowInfo Win(int pid, string process, string title) =>
        new(pid * 10, title, "C", process, pid, new Rect(0, 0, 100, 100), WindowState.Normal);

    [Fact]
    public void PickStartedWindow_PrefersStartedPid_ThenProcessName()
    {
        var other = Win(1, "notepad", "Untitled - Notepad");
        var sameName = Win(2, "notepad", "Other - Notepad");
        var mine = Win(3, "notepad", "Mine - Notepad");
        Assert.Equal((mine, "pid"), ProcessTools.PickStartedWindow([other, sameName, mine], "Notepad", 3, "notepad.exe", false));
        Assert.Equal((other, "process"), ProcessTools.PickStartedWindow([Win(5, "explorer", "Notepad folder"), other], "Notepad", 3, "notepad", false));
    }

    [Fact]
    public void PickStartedWindow_TitleOnlyMatch_OnlyAfterTimeout()
    {
        var foreign = Win(1, "explorer", "MDium docs");
        Assert.Equal((null, null), ProcessTools.PickStartedWindow([foreign], "MDium", 9, "mdium", allowTitleOnly: false));
        Assert.Equal((foreign, "title-only"), ProcessTools.PickStartedWindow([foreign], "MDium", 9, "mdium", allowTitleOnly: true));
        Assert.Equal((null, null), ProcessTools.PickStartedWindow([Win(1, "x", "nothing")], "MDium", 9, "mdium", allowTitleOnly: true));
    }
    [Fact]
    public void JavaRefs_AreRecognised()
    {
        Assert.True(ToolContext.IsJavaRef("j3"));
        Assert.True(ToolContext.LooksLikeRef("j3"));
        Assert.False(ToolContext.IsUiaRef("j3"));
        Assert.False(ToolContext.IsJavaRef("u3"));
    }
}
