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
    DecisionProvider? ProviderOverride = null)
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
            ProviderOverride: ParseProvider(Get("WINUSE_DECISION_PROVIDER")));
    }

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
