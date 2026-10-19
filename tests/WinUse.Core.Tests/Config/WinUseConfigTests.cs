using WinUse.Core.Config;

namespace WinUse.Core.Tests.Config;

public class WinUseConfigTests
{
    [Fact]
    public void Load_ReadsDotEnvAndEnvironmentOverrides()
    {
        var dir = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(dir.FullName, ".env"), "JEV_API_KEY=from-dotenv\nJEV_MODEL=jev-1.13.0\n");
        var env = new Dictionary<string, string?> { ["JEV_MODEL"] = "jev-latest" };
        var cfg = WinUseConfig.Load(dir.FullName, dir.FullName, env);
        Assert.Equal("from-dotenv", cfg.JevApiKey);
        Assert.Equal("jev-latest", cfg.JevModel);
        Assert.Equal("https://api.typesafe.ai", cfg.JevBaseUrl);
        Assert.Equal(0.55, cfg.MinProbability);
        Assert.Contains(dir.FullName, cfg.AllowedRoots);
    }

    [Fact]
    public void Load_WithoutDotEnv_HasNullApiKey()
    {
        var dir = Directory.CreateTempSubdirectory();
        var cfg = WinUseConfig.Load(dir.FullName, dir.FullName, new Dictionary<string, string?>());
        Assert.Null(cfg.JevApiKey);
        Assert.Equal(400, cfg.SettleMs);
    }

    [Fact]
    public void Load_SettleMs_FromEnvironment()
    {
        var dir = Directory.CreateTempSubdirectory();
        var cfg = WinUseConfig.Load(dir.FullName, dir.FullName, new Dictionary<string, string?> { ["WINUSE_SETTLE_MS"] = "150" });
        Assert.Equal(150, cfg.SettleMs);
    }
}
