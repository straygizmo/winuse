using WinUse.Core.Java;

namespace WinUse.Core.Tests.Java;

public class JavaStatesTests
{
    [Fact]
    public void Parse_ReadsFlagsFromCommaList()
    {
        var s = JavaStates.Parse("enabled,focusable,visible,showing,checked");
        Assert.True(s.Enabled);
        Assert.True(s.Showing);
        Assert.True(s.Checked);
        Assert.False(s.Focused);
    }

    [Fact]
    public void Parse_Empty_IsAllFalse()
    {
        var s = JavaStates.Parse("");
        Assert.False(s.Enabled);
        Assert.False(s.Showing);
        Assert.False(s.Focused);
        Assert.False(s.Checked);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var s = JavaStates.Parse("enabled, showing, focused");
        Assert.True(s.Enabled);
        Assert.True(s.Showing);
        Assert.True(s.Focused);
    }

    [Theory]
    [InlineData("CheckBox", "check box", true, true)]
    [InlineData("CheckBox", "check box", false, false)]
    [InlineData("RadioButton", "radio button", false, false)]
    [InlineData("Button", "toggle button", true, true)]
    [InlineData("Button", "push button", true, null)]
    [InlineData("Edit", "text", false, null)]
    public void CheckedOf_ThreeValued(string controlType, string role, bool checkedState, bool? expected)
    {
        var flags = JavaStates.Parse(checkedState ? "enabled,checked" : "enabled");
        Assert.Equal(expected, JavaStates.CheckedOf(flags, controlType, role));
    }
}
