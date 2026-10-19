using WinUse.Core.Uia;

namespace WinUse.Core.Tests.Uia;

public class InteractiveFilterTests
{
    [Theory]
    [InlineData("Button", true)] [InlineData("Edit", true)] [InlineData("ComboBox", true)] [InlineData("Document", true)]
    [InlineData("Window", true)] [InlineData("MenuBar", true)] [InlineData("Tab", true)]
    [InlineData("Text", false)] [InlineData("Image", false)] [InlineData("Pane", false)]
    [InlineData("Group", false)] [InlineData("Custom", false)] [InlineData("", false)]
    public void IsInteractive(string type, bool expected) => Assert.Equal(expected, InteractiveFilter.IsInteractive(type));
}
