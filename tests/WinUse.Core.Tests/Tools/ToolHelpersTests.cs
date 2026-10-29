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
    [InlineData("Virtual", null, null, null, "virtual", null, null)]
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
        Assert.Equal("screen", json["method"]!.GetValue<string>());
        Assert.Null(json["monitors"]);
    }

    [Fact]
    public void ScreenshotPayload_ListsMonitors()
    {
        var monitors = new[] { new WinUse.Core.Screen.MonitorInfo(0, new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true, @"\.\DISPLAY1") };
        var json = System.Text.Json.Nodes.JsonNode.Parse(ToolResult.Ok(
            ObserveTools.ScreenshotPayload(new WinUse.Core.Screen.Capture([1], 1920, 1080, 1.0, 0, 0, "print"), monitors), "s1"))!;
        Assert.Equal("print", json["method"]!.GetValue<string>());
        var m = json["monitors"]!.AsArray().Single()!;
        Assert.Equal(0, m["index"]!.GetValue<int>());
        Assert.True(m["primary"]!.GetValue<bool>());
        Assert.Equal(1920, m["rect"]!["width"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, WinUse.Core.Screen.CaptureMethod.Auto)]
    [InlineData("auto", WinUse.Core.Screen.CaptureMethod.Auto)]
    [InlineData("Screen", WinUse.Core.Screen.CaptureMethod.Screen)]
    [InlineData("print", WinUse.Core.Screen.CaptureMethod.Print)]
    public void ParseMethod_AcceptsTheThreeMethods(string? input, WinUse.Core.Screen.CaptureMethod expected) =>
        Assert.Equal(expected, ObserveTools.ParseMethod(input));

    [Fact]
    public void ParseMethod_RejectsOthers() => Assert.Throws<ArgumentException>(() => ObserveTools.ParseMethod("gdi"));

    [Fact]
    public void ToScreen_AddsTheWindowOrigin()
    {
        var w = new InputTools.TargetWindow(new WindowInfo(1, "t", "c", "p", 1, new Rect(100, 200, 300, 400), WindowState.Normal), "win1");
        Assert.Equal((110, 220), InputTools.ToScreen(w, (10, 20)));
        Assert.Equal((10, 20), InputTools.ToScreen(null, (10, 20)));
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
