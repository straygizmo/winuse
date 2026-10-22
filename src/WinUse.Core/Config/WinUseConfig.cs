namespace WinUse.Core.Config;

/// <summary>Which decision-model API answers grounding and check questions.</summary>
public enum DecisionProvider
{
    None,
    /// <summary>TypeSafe Jev: <c>POST {JevBaseUrl}/v1/systemone</c> with <c>JEV_API_KEY</c>.</summary>
    Jev,
    /// <summary>Cloudflare Clef on Workers AI (same request/response body as Jev, wrapped in the Cloudflare envelope).</summary>
    Clef,
}

/// <summary>When a window screenshot is attached to decision-model requests.</summary>
public enum ScreenshotPolicy
{
    /// <summary>Only when the observed window exposes no UIA / DOM candidates.</summary>
    Auto,
    /// <summary>Always, also next to candidates.</summary>
    Always,
    /// <summary>Never; visual grounding is disabled.</summary>
    Off,
}

/// <summary>How a target is located on a screenshot when there are no candidates.</summary>
public enum VisualLocatorKind
{
    /// <summary>Staged grid-cell choice questions (works with Jev and Clef).</summary>
    Grid,
    /// <summary>One Jev 1.14 <c>box</c> question (Jev only; falls back to Grid on Clef).</summary>
    Box,
}

/// <summary>Whether Java Swing windows are read through Java Access Bridge.</summary>
public enum JabMode
{
    /// <summary>Use the bridge when <c>WindowsAccessBridge-64.dll</c> can be loaded.</summary>
    Auto,
    /// <summary>Never load the bridge; Java windows fall back to visual grounding.</summary>
    Off,
}

public sealed record WinUseConfig(
    string? JevApiKey,
    string JevBaseUrl,
    string JevModel,
    double MinProbability,
    double MinConfidence,
    double SatisfiedThreshold,
    IReadOnlyList<string> AllowedRoots,
    IReadOnlyList<string> KillableProcesses,
    string LogDirectory,
    int SettleMs = WinUseConfig.DefaultSettleMs,
    string? CloudflareAccountId = null,
    string? CloudflareApiToken = null,
    string ClefModel = WinUseConfig.DefaultClefModel,
    string ClefBaseUrl = WinUseConfig.DefaultClefBaseUrl,
    DecisionProvider? ProviderOverride = null,
    ScreenshotPolicy Screenshots = ScreenshotPolicy.Auto,
    VisualLocatorKind VisualLocator = VisualLocatorKind.Grid,
    JabMode Jab = JabMode.Auto,
    string? JabDll = null,
    bool JavaAccessibility = true)
{
    public const string DefaultClefModel = "clef";
    public const string DefaultClefBaseUrl = "https://api.cloudflare.com";

    /// <summary>
    /// The decision model in use: <c>WINUSE_DECISION_PROVIDER</c> when set, else Jev when <c>JEV_API_KEY</c> is present,
    /// else Clef when both Cloudflare values are present, else none.
    /// </summary>
    public DecisionProvider Provider => ProviderOverride ?? (
        JevApiKey is not null ? DecisionProvider.Jev
        : CloudflareAccountId is not null && CloudflareApiToken is not null ? DecisionProvider.Clef
        : DecisionProvider.None);

    /// <summary>Default wait (ms) after an <c>act</c> before re-observing to verify it (<c>WINUSE_SETTLE_MS</c>).</summary>
    public const int DefaultSettleMs = 400;

    /// <summary>
    /// Precedence (low to high): .env in exeDirectory, .env in currentDirectory, environment.
    /// </summary>
    public static WinUseConfig Load(string currentDirectory, string exeDirectory, IDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { exeDirectory, currentDirectory })
            ReadDotEnv(Path.Combine(dir, ".env"), values);
        foreach (var (key, value) in environment)
            if (!string.IsNullOrEmpty(value))
                values[key] = value;

        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        var allowedRoots = SplitList(Get("WINUSE_ALLOWED_ROOTS"));
        if (Get("WINUSE_ALLOWED_ROOTS") is null)
            allowedRoots = [currentDirectory, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)];

        return new WinUseConfig(
            JevApiKey: Get("JEV_API_KEY"),
            JevBaseUrl: Get("JEV_BASE_URL") ?? "https://api.typesafe.ai",
            JevModel: Get("JEV_MODEL") ?? "jev-latest",
            MinProbability: 0.55,
            MinConfidence: 0.35,
            SatisfiedThreshold: 0.7,
            AllowedRoots: allowedRoots,
            KillableProcesses: SplitList(Get("WINUSE_KILLABLE_PROCESSES")),
            LogDirectory: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winuse", "logs"),
            SettleMs: int.TryParse(Get("WINUSE_SETTLE_MS"), out var settle) && settle >= 0 ? settle : DefaultSettleMs,
            CloudflareAccountId: Get("CLOUDFLARE_ACCOUNT_ID"),
            CloudflareApiToken: Get("CLOUDFLARE_API_TOKEN"),
            ClefModel: Get("CLEF_MODEL") ?? DefaultClefModel,
            ClefBaseUrl: Get("CLEF_BASE_URL") ?? DefaultClefBaseUrl,
            ProviderOverride: ParseProvider(Get("WINUSE_DECISION_PROVIDER")),
            Screenshots: ParseEnum<ScreenshotPolicy>("WINUSE_SCREENSHOTS", Get("WINUSE_SCREENSHOTS")) ?? ScreenshotPolicy.Auto,
            VisualLocator: ParseEnum<VisualLocatorKind>("WINUSE_VISUAL_LOCATOR", Get("WINUSE_VISUAL_LOCATOR")) ?? VisualLocatorKind.Grid,
            Jab: ParseEnum<JabMode>("WINUSE_JAB", Get("WINUSE_JAB")) ?? JabMode.Auto,
            JabDll: Get("WINUSE_JAB_DLL"),
            JavaAccessibility: ParseOnOff("WINUSE_JAVA_ACCESSIBILITY", Get("WINUSE_JAVA_ACCESSIBILITY")) ?? true);
    }

    private static T? ParseEnum<T>(string name, string? raw) where T : struct, Enum
    {
        if (raw is null) return null;
        if (Enum.TryParse<T>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v)) return v;
        var allowed = string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
        throw new InvalidOperationException($"{name} must be one of {allowed}, not '{raw}'.");
    }

    private static bool? ParseOnOff(string name, string? raw) => raw?.ToLowerInvariant() switch
    {
        null => null,
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw new InvalidOperationException($"{name} must be 'on' or 'off', not '{raw}'."),
    };

    private static DecisionProvider? ParseProvider(string? raw) => raw?.ToLowerInvariant() switch
    {
        null => null,
        "jev" => DecisionProvider.Jev,
        "clef" => DecisionProvider.Clef,
        _ => throw new InvalidOperationException($"WINUSE_DECISION_PROVIDER must be 'jev' or 'clef', not '{raw}'."),
    };

    private static string[] SplitList(string? raw) =>
        raw is null
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ReadDotEnv(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            into[key] = value;
        }
    }
}
