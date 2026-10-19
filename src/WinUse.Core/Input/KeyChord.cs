namespace WinUse.Core.Input;

public enum MouseButton { Left, Right, Middle }

public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key)
{
    private static readonly Dictionary<string, ushort> ModifierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28,
        ["left"] = 0x25, ["right"] = 0x27, ["insert"] = 0x2D,
    };

    public static KeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Unknown key: (empty chord)", nameof(text));

        var mods = new List<ushort>();
        ushort? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
                throw new ArgumentException($"Unknown key: (empty token) in '{text}'", nameof(text));
            if (ModifierMap.TryGetValue(token, out var m))
            {
                mods.Add(m);
                continue;
            }
            if (key is not null)
                throw new ArgumentException($"Unknown key: '{token}' (chord '{text}' has more than one non-modifier key)", nameof(text));
            key = ParseKey(token, text);
        }
        if (key is null)
            throw new ArgumentException($"Unknown key: no non-modifier key in '{text}'", nameof(text));
        return new KeyChord(mods, key.Value);
    }

    private static ushort ParseKey(string token, string text)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        else if (NamedKeys.TryGetValue(token, out var vk)) return vk;
        else if ((token[0] == 'f' || token[0] == 'F') && int.TryParse(token.AsSpan(1), System.Globalization.NumberStyles.None, null, out var n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + n - 1);
        throw new ArgumentException($"Unknown key: '{token}' in '{text}'", nameof(text));
    }
}
