using WinUse.Cli;
using WinUse.Core.Config;
using WinUse.Core.Tests.Grounding;
using WinUse.Tools;

namespace WinUse.Core.Tests.Cli;

public class DoctorCommandTests
{
    private static async Task<string> Run(WinUseConfig cfg, FakeJev jev)
    {
        using var ctx = ToolContext.Create(cfg, null, jev);
        var output = new StringWriter();
        await DoctorCommand.ExecuteAsync(ctx, output);
        return output.ToString();
    }

    [Fact]
    public async Task Doctor_WithClefCredentials_ReportsClefProviderEndpointAndProbe()
    {
        var text = await Run(TestConfig.WithClef("acct"), new FakeJev(noul: 0.99));
        Assert.Contains("Decision model: Clef", text);
        Assert.Contains("CLOUDFLARE_ACCOUNT_ID: set", text);
        Assert.Contains("CLOUDFLARE_API_TOKEN: set", text);
        Assert.Contains("JEV_API_KEY: not set", text);
        Assert.Contains("Endpoint: https://api.cloudflare.com/client/v4/accounts/acct/ai/run/@cf/cloudflare/clef", text);
        Assert.Contains("Clef: probe ok, probability 0.990", text);
    }

    [Fact]
    public async Task Doctor_WithJevKey_ReportsJevProvider()
    {
        var text = await Run(TestConfig.WithJev("k"), new FakeJev(noul: 0.9));
        Assert.Contains("Decision model: Jev", text);
        Assert.Contains("Endpoint: https://api.typesafe.ai/v1/systemone", text);
        Assert.Contains("Jev: probe ok", text);
    }

    [Fact]
    public async Task Doctor_WithoutCredentials_SaysHowToConfigure()
    {
        var text = await Run(TestConfig.Default, new FakeJev(configured: false));
        Assert.Contains("Decision model: none", text);
        Assert.Contains("CLOUDFLARE_API_TOKEN", text);
        Assert.DoesNotContain("probe", text);
    }

    [Fact]
    public async Task Doctor_ClefForcedWithoutCredentials_ReportsMissingValues()
    {
        var cfg = TestConfig.WithJev("k") with { ProviderOverride = DecisionProvider.Clef };
        var text = await Run(cfg, new FakeJev(configured: false));
        Assert.Contains("Decision model: Clef (WINUSE_DECISION_PROVIDER)", text);
        Assert.Contains("CLOUDFLARE_ACCOUNT_ID: not set", text);
        Assert.Contains("not configured", text);
    }
}
