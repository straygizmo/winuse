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
}
