using WinUse.Core.Config;

namespace WinUse.Core.Tests;

static class TestConfig
{
    public static WinUseConfig Default => new(
        null, "https://api.typesafe.ai", "jev-latest", 0.55, 0.35, 0.7,
        [Environment.CurrentDirectory, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)],
        [],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winuse", "logs"),
        SettleMs: 0); // keep fake act tests fast; the settle wait has its own test

    public static WinUseConfig WithRoots(params string[] roots) => Default with { AllowedRoots = roots };

    public static WinUseConfig WithJev(string apiKey) => Default with { JevApiKey = apiKey };

    public static WinUseConfig WithClef(string accountId = "acct", string token = "cf-token") =>
        Default with { CloudflareAccountId = accountId, CloudflareApiToken = token };
}
