using WinUse.Core.Input;

namespace WinUse.Core.Tests.Input;

public class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+s", 1, 0x53)]
    [InlineData("Ctrl+Shift+S", 2, 0x53)]
    [InlineData("enter", 0, 0x0D)]
    [InlineData("f5", 0, 0x74)]
    [InlineData("F24", 0, 0x87)]
    [InlineData("alt+Escape", 1, 0x1B)]
    [InlineData("win+5", 1, 0x35)]
    [InlineData("pagedown", 0, 0x22)]
    [InlineData("insert", 0, 0x2D)]
    public void Parse_ValidChords(string text, int modCount, int vk)
    {
        var c = KeyChord.Parse(text);
        Assert.Equal(modCount, c.Modifiers.Count);
        Assert.Equal((ushort)vk, c.Key);
    }

    [Fact]
    public void Parse_ModifierCodes()
    {
        var c = KeyChord.Parse("ctrl+alt+shift+win+a");
        Assert.Equal(new ushort[] { 0x11, 0x12, 0x10, 0x5B }, c.Modifiers);
    }

    [Theory]
    [InlineData("ctrl+")]
    [InlineData("foo+a")]
    [InlineData("")]
    [InlineData("ctrl")]
    [InlineData("a+b")]
    [InlineData("f25")]
    [InlineData("ctrl++a")]
    public void Parse_InvalidChords_Throw(string text)
        => Assert.Throws<ArgumentException>(() => KeyChord.Parse(text));
}
