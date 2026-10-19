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
}
