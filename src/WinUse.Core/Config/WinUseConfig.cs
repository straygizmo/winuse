namespace WinUse.Core.Config;

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
    int SettleMs = WinUseConfig.DefaultSettleMs)
{
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
            SettleMs: int.TryParse(Get("WINUSE_SETTLE_MS"), out var settle) && settle >= 0 ? settle : DefaultSettleMs);
    }

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
