using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Windows;

public class WindowFilterTests
{
    [Theory]
    [InlineData("Untitled - Notepad", "^Untitled", true)]
    [InlineData("C++ (1)", "C++ (1)", true)]       // invalid regex -> literal match
    [InlineData("Other", "^Untitled", false)]
    [InlineData("untitled - notepad", "UNTITLED", true)]
    public void Matches_HandlesRegexAndInvalidPatterns(string title, string pattern, bool expected)
    {
        var w = new WindowInfo(1, title, "Notepad", "notepad", 1, new Rect(0, 0, 1, 1), WindowState.Normal);
        Assert.Equal(expected, WindowService.Matches(w, new WindowLocator(pattern, null, null)));
    }

    [Theory]
    [InlineData("notepad", "Notepad.exe", true)]
    [InlineData("notepad", "NOTEPAD", true)]
    [InlineData("notepad", "calc", false)]
    public void Matches_ProcessNameIgnoresCaseAndExe(string actual, string wanted, bool expected)
    {
        var w = new WindowInfo(1, "t", "Cls", actual, 1, new Rect(0, 0, 1, 1), WindowState.Normal);
        Assert.Equal(expected, WindowService.Matches(w, new WindowLocator(".", wanted, null)));
    }

    [Theory]
    [InlineData("Notepad", "notepad", true)]
    [InlineData("Notepad", "Edit", false)]
    public void Matches_ClassNameIgnoresCase(string actual, string wanted, bool expected)
    {
        var w = new WindowInfo(1, "t", actual, "p", 1, new Rect(0, 0, 1, 1), WindowState.Normal);
        Assert.Equal(expected, WindowService.Matches(w, new WindowLocator(".", null, wanted)));
    }

    private static WindowInfo Win(string title, string process, string cls = "C", nint hwnd = 1) =>
        new(hwnd, title, cls, process, 1, new Rect(0, 0, 10, 10), WindowState.Normal);

    [Fact]
    public void PickWithFallback_PrefersExactTitle()
    {
        var a = Win("Doc - App", "app", hwnd: 1);
        var b = Win("*Doc - App", "app", hwnd: 2);
        Assert.Same(a, WindowService.PickWithFallback([b, a], new WindowLocator("^Doc - App$", "app", "C")));
    }

    [Fact]
    public void PickWithFallback_UsesSingleWindowOfSameProcessAndClass_WhenTitleChanged()
    {
        var changed = Win("*Doc - App", "App", hwnd: 2);
        var other = Win("Other", "notepad", hwnd: 3);
        Assert.Same(changed, WindowService.PickWithFallback([other, changed], new WindowLocator("^Doc - App$", "app.exe", "c")));
    }

    [Fact]
    public void PickWithFallback_ReturnsNull_WhenAmbiguousOrNoProcessRecorded()
    {
        var w1 = Win("A", "app", hwnd: 1);
        var w2 = Win("B", "app", hwnd: 2);
        Assert.Null(WindowService.PickWithFallback([w1, w2], new WindowLocator("^Gone$", "app", "C")));
        Assert.Null(WindowService.PickWithFallback([w1], new WindowLocator("^Gone$", null, null)));
        Assert.Null(WindowService.PickWithFallback([w1], new WindowLocator("^Gone$", "app", "OtherClass")));
    }
}
