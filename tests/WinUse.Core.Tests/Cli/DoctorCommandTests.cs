using WinUse.Cli;
using WinUse.Core.Config;
using WinUse.Core.Java;
using WinUse.Core.Tests.Grounding;
using WinUse.Core.Tests.Java;
using WinUse.Tools;

namespace WinUse.Core.Tests.Cli;

public class DoctorCommandTests
{
    private static async Task<string> Run(WinUseConfig cfg, FakeJev jev, IJabBridge? jab = null)
    {
        using var ctx = ToolContext.Create(cfg, null, jev, jab: jab);
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
    public async Task Doctor_WithGeminiKey_ReportsGeminiProviderModelEndpointAndProbe()
    {
        var text = await Run(TestConfig.WithGemini(), new FakeJev(noul: 0.97, provider: DecisionProvider.Gemini));
        Assert.Contains("Decision model: Gemini (auto)", text);
        Assert.Contains("GEMINI_API_KEY: set", text);
        Assert.Contains("Gemini model: gemini-decision-1", text);
        Assert.Contains("Endpoint: https://generativelanguage.googleapis.com/v1beta/decisions", text);
        Assert.Contains("Gemini: probe ok, probability 0.970", text);
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
    /// <summary>No DLL any more: the built-in client creates its hidden window and reports it.</summary>
    [Fact]
    public async Task Doctor_ReportsJavaAccessBridge_BuiltInClient_AndUserEnablement()
    {
        var text = await Run(TestConfig.Default, new FakeJev(configured: false));
        Assert.Contains("Java Access Bridge: built-in client (window 0x", text);
        Assert.Contains("Java accessibility (user):", text);
        Assert.Contains("Java windows:", text);
    }

    [Fact]
    public async Task Doctor_ReportsJavaAccessBridge_UnavailableReason()
    {
        var text = await Run(TestConfig.Default, new FakeJev(configured: false),
            new FakeJabBridge(new FakeJabAccess(new FakeNode("frame", "x")), available: false, reason: "client window could not be created: boom"));
        Assert.Contains("Java Access Bridge: client window could not be created: boom", text);
    }

    [Fact]
    public async Task Doctor_WithJabOff_SaysSo()
    {
        var text = await Run(TestConfig.Default with { Jab = JabMode.Off }, new FakeJev(configured: false));
        Assert.Contains("Java Access Bridge: WINUSE_JAB=off", text);
    }
}
