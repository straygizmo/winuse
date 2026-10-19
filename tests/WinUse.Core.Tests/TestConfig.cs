using WinUse.Core.Config;

namespace WinUse.Core.Tests;

static class TestConfig
{
    public static WinUseConfig Default => new(
        null, "https://api.typesafe.ai", "jev-latest", 0.55, 0.35, 0.7,
        [Environment.CurrentDirectory, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)],
        [],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winuse", "logs"));

    public static WinUseConfig WithRoots(params string[] roots) => Default with { AllowedRoots = roots };

    public static WinUseConfig WithJev(string apiKey) => Default with { JevApiKey = apiKey };
}
