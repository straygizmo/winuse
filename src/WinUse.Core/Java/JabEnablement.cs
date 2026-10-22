namespace WinUse.Core.Java;

/// <summary>
/// Turns Java Access Bridge on for the JVMs this user starts, without touching any JRE: the user-profile
/// <c>.accessibility.properties</c> (what <c>jabswitch -enable</c> writes) and the <c>JAVA_TOOL_OPTIONS</c> flag that
/// <c>process_run</c> passes to its children. Both take effect only for JVMs started afterwards.
/// </summary>
public static class JabEnablement
{
    public const string AssistiveTechnology = "com.sun.java.accessibility.AccessBridge";
    public const string ToolOptionsVar = "JAVA_TOOL_OPTIONS";
    public const string ToolOptionKey = "-Djavax.accessibility.assistive_technologies=";
    public const string ToolOption = ToolOptionKey + AssistiveTechnology;

    private const string TechnologiesKey = "assistive_technologies";
    private const string MagnifierKey = "screen_magnifier_present";

    /// <summary>%USERPROFILE%\.accessibility.properties, which every Java 8+ runtime reads at start-up.</summary>
    public static string DefaultPropertiesPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".accessibility.properties");

    /// <summary>The value <c>JAVA_TOOL_OPTIONS</c> should have: the flag appended unless the variable already names assistive technologies.</summary>
    public static string ComposeToolOptions(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return ToolOption;
        if (existing.Contains(ToolOptionKey, StringComparison.Ordinal)) return existing;
        return existing.TrimEnd() + " " + ToolOption;
    }

    public static bool IsEnabled(string propertiesPath)
    {
        if (!File.Exists(propertiesPath)) return false;
        foreach (var (key, value) in Entries(File.ReadAllLines(propertiesPath)))
            if (key == TechnologiesKey) return Split(value).Contains(AssistiveTechnology, StringComparer.Ordinal);
        return false;
    }

    /// <summary>Adds the bridge to <c>assistive_technologies</c> (keeping other entries and lines) and sets <c>screen_magnifier_present=true</c>.</summary>
    public static void Enable(string propertiesPath)
    {
        var lines = File.Exists(propertiesPath) ? File.ReadAllLines(propertiesPath).ToList() : [];
        bool sawTech = false, sawMagnifier = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Parse(lines[i]) is not var (key, value)) continue;
            if (key == TechnologiesKey)
            {
                sawTech = true;
                var list = Split(value);
                if (!list.Contains(AssistiveTechnology, StringComparer.Ordinal)) list.Add(AssistiveTechnology);
                lines[i] = $"{TechnologiesKey}={string.Join(",", list)}";
            }
            else if (key == MagnifierKey)
            {
                sawMagnifier = true;
                lines[i] = $"{MagnifierKey}=true";
            }
        }
        if (!sawTech) lines.Add($"{TechnologiesKey}={AssistiveTechnology}");
        if (!sawMagnifier) lines.Add($"{MagnifierKey}=true");
        File.WriteAllLines(propertiesPath, lines);
    }

    /// <summary>Removes the bridge from <c>assistive_technologies</c> and drops <c>screen_magnifier_present</c>; deletes the file when nothing else is left.</summary>
    public static void Disable(string propertiesPath)
    {
        if (!File.Exists(propertiesPath)) return;
        var result = new List<string>();
        foreach (var line in File.ReadAllLines(propertiesPath))
        {
            if (Parse(line) is not var (key, value)) { result.Add(line); continue; }
            if (key == MagnifierKey) continue;
            if (key == TechnologiesKey)
            {
                var rest = Split(value).Where(t => t != AssistiveTechnology).ToList();
                if (rest.Count > 0) result.Add($"{TechnologiesKey}={string.Join(",", rest)}");
                continue;
            }
            result.Add(line);
        }
        if (result.All(string.IsNullOrWhiteSpace)) File.Delete(propertiesPath);
        else File.WriteAllLines(propertiesPath, result);
    }

    private static IEnumerable<(string Key, string Value)> Entries(IEnumerable<string> lines)
    {
        foreach (var l in lines)
            if (Parse(l) is var (k, v) && k.Length > 0) yield return (k, v);
    }

    /// <summary>A <c>key=value</c> line; null for blank lines and comments.</summary>
    private static (string Key, string Value)? Parse(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t[0] is '#' or '!') return null;
        var eq = t.IndexOfAny(['=', ':']);
        if (eq <= 0) return null;
        return (t[..eq].Trim(), t[(eq + 1)..].Trim());
    }

    private static List<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
