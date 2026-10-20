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

    static readonly Dictionary<string, string?> Clef = new()
    {
        ["CLOUDFLARE_ACCOUNT_ID"] = "acct",
        ["CLOUDFLARE_API_TOKEN"] = "cf-token",
    };

    static WinUseConfig LoadEnv(IDictionary<string, string?> env)
    {
        var dir = Directory.CreateTempSubdirectory();
        return WinUseConfig.Load(dir.FullName, dir.FullName, env);
    }

    [Fact]
    public void Load_NoCredentials_ProviderIsNone()
    {
        var cfg = LoadEnv(new Dictionary<string, string?>());
        Assert.Equal(DecisionProvider.None, cfg.Provider);
    }

    [Fact]
    public void Load_JevKeyOnly_ProviderIsJev()
    {
        var cfg = LoadEnv(new Dictionary<string, string?> { ["JEV_API_KEY"] = "k" });
        Assert.Equal(DecisionProvider.Jev, cfg.Provider);
    }

    [Fact]
    public void Load_CloudflareCredentialsOnly_ProviderIsClef()
    {
        var cfg = LoadEnv(Clef);
        Assert.Equal(DecisionProvider.Clef, cfg.Provider);
        Assert.Equal("acct", cfg.CloudflareAccountId);
        Assert.Equal("cf-token", cfg.CloudflareApiToken);
        Assert.Equal("clef", cfg.ClefModel);
        Assert.Equal("https://api.cloudflare.com", cfg.ClefBaseUrl);
    }

    [Fact]
    public void Load_AccountIdWithoutToken_ProviderIsNone()
    {
        var cfg = LoadEnv(new Dictionary<string, string?> { ["CLOUDFLARE_ACCOUNT_ID"] = "acct" });
        Assert.Equal(DecisionProvider.None, cfg.Provider);
    }

    [Fact]
    public void Load_BothCredentials_JevWins()
    {
        var env = new Dictionary<string, string?>(Clef) { ["JEV_API_KEY"] = "k" };
        Assert.Equal(DecisionProvider.Jev, LoadEnv(env).Provider);
    }

    [Fact]
    public void Load_ProviderOverride_SelectsClefOverJev()
    {
        var env = new Dictionary<string, string?>(Clef) { ["JEV_API_KEY"] = "k", ["WINUSE_DECISION_PROVIDER"] = "clef" };
        Assert.Equal(DecisionProvider.Clef, LoadEnv(env).Provider);
    }

    [Fact]
    public void Load_ProviderOverride_IsCaseInsensitive_AndAppliesWithoutCredentials()
    {
        var cfg = LoadEnv(new Dictionary<string, string?> { ["WINUSE_DECISION_PROVIDER"] = "Clef" });
        Assert.Equal(DecisionProvider.Clef, cfg.Provider);
    }

    [Fact]
    public void Load_UnknownProviderOverride_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadEnv(new Dictionary<string, string?> { ["WINUSE_DECISION_PROVIDER"] = "gpt" }));
        Assert.Contains("WINUSE_DECISION_PROVIDER", ex.Message);
    }

    [Fact]
    public void Load_ClefModelAndBaseUrl_FromEnvironment()
    {
        var env = new Dictionary<string, string?>(Clef) { ["CLEF_MODEL"] = "clef-flash", ["CLEF_BASE_URL"] = "https://gw.example/" };
        var cfg = LoadEnv(env);
        Assert.Equal("clef-flash", cfg.ClefModel);
        Assert.Equal("https://gw.example/", cfg.ClefBaseUrl);
    }
}
