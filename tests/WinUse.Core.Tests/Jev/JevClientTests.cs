using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Jev;

namespace WinUse.Core.Tests.Jev;

public class JevClientTests
{
    static HttpResponseMessage Json(int status, string json)
        => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static readonly TimeSpan[] NoDelay = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    static Dictionary<string, JevQuestion> OneNoul() => new() { ["q"] = new NoulQuestion("?") };

    [Fact]
    public async Task AskAsync_SendsExpectedBodyAndParsesChoiceAndNoul()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", req.RequestUri!.ToString());
            Assert.Equal("Bearer k", req.Headers.Authorization!.ToString());
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            Assert.Equal("jev-latest", body.GetProperty("model").GetString());
            Assert.Equal("choice", body.GetProperty("questions").GetProperty("target").GetProperty("type").GetString());
            Assert.Equal("Which element?", body.GetProperty("questions").GetProperty("target").GetProperty("instructions").GetString());
            Assert.Equal("Save button", body.GetProperty("questions").GetProperty("target").GetProperty("criteria").GetProperty("u3").GetString());
            Assert.Equal("x", body.GetProperty("state").GetProperty("goal").GetString());
            return Json(200, """{"model":"jev-1.13.0","answers":{"target":{"type":"choice","choice":"u3","probabilities":{"u3":0.9,"none":0.1},"confidence":0.8},"done":{"type":"noul","noul":0.2}},"usage":{"input_tokens":120,"output_tokens":0}}""");
        });
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance);
        var r = await client.AskAsync(new { goal = "x" }, new Dictionary<string, JevQuestion> {
            ["target"] = new ChoiceQuestion("Which element?", new Dictionary<string,string?> { ["u3"] = "Save button", ["none"] = null }),
            ["done"] = new NoulQuestion("Is it done?") });
        var c = Assert.IsType<ChoiceAnswer>(r.Answers["target"]);
        Assert.Equal("u3", c.Choice); Assert.Equal(0.8, c.Confidence);
        Assert.Equal(0.9, c.Probabilities["u3"]);
        Assert.Equal(0.2, Assert.IsType<NoulAnswer>(r.Answers["done"]).Probability);
        Assert.Equal("jev-1.13.0", r.Model);
        Assert.Equal(120, r.InputTokens);
    }

    [Fact]
    public async Task AskAsync_RetriesOn429_ThenThrowsJevUnavailable()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance, NoDelay);
        await Assert.ThrowsAsync<JevUnavailableException>(() => client.AskAsync(new { }, OneNoul()));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public void IsConfigured_FalseWithoutKey()
        => Assert.False(new JevClient(new HttpClient(), TestConfig.Default, NullLogger<JevClient>.Instance).IsConfigured);

    [Fact]
    public async Task AskAsync_NotConfigured_Throws()
    {
        var client = new JevClient(new HttpClient(), TestConfig.Default, NullLogger<JevClient>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskAsync(new { }, OneNoul()));
        Assert.Contains("JEV_API_KEY", ex.Message);
        Assert.Contains("CLOUDFLARE_API_TOKEN", ex.Message);
    }

    [Fact]
    public async Task AskAsync_401_ThrowsWithoutRetry()
    {
        var handler = new FakeHttpHandler(_ => Json(401, """{"error":"bad key"}"""));
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance, NoDelay);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskAsync(new { }, OneNoul()));
        Assert.Contains("401", ex.Message);
        Assert.Contains("bad key", ex.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_TooManyChoices_Throws()
    {
        var handler = new FakeHttpHandler(_ => Json(200, "{}"));
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance, NoDelay);
        var criteria = Enumerable.Range(0, 256).ToDictionary(i => $"o{i}", _ => (string?)null);
        await Assert.ThrowsAsync<ArgumentException>(() => client.AskAsync(new { },
            new Dictionary<string, JevQuestion> { ["q"] = new ChoiceQuestion("?", criteria) }));
        Assert.Equal(0, handler.CallCount);
    }

    const string ClefEnvelope = """{"result":{"model":"clef","answers":{"target":{"type":"choice","choice":"u3","probabilities":{"u3":0.9,"none":0.1},"confidence":0.8},"done":{"type":"noul","noul":0.2}},"usage":{"input_tokens":154,"output_tokens":0}},"success":true,"errors":[],"messages":[]}""";

    [Fact]
    public async Task AskAsync_Clef_SendsCloudflareRequestAndUnwrapsEnvelope()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct/ai/run/@cf/cloudflare/clef", req.RequestUri!.ToString());
            Assert.Equal("Bearer cf-token", req.Headers.Authorization!.ToString());
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            Assert.Equal("clef", body.GetProperty("model").GetString());
            Assert.Equal("choice", body.GetProperty("questions").GetProperty("target").GetProperty("type").GetString());
            Assert.Equal("x", body.GetProperty("state").GetProperty("goal").GetString());
            return Json(200, ClefEnvelope);
        });
        var client = new JevClient(new HttpClient(handler), TestConfig.WithClef(), NullLogger<JevClient>.Instance);
        var r = await client.AskAsync(new { goal = "x" }, new Dictionary<string, JevQuestion> {
            ["target"] = new ChoiceQuestion("Which element?", new Dictionary<string,string?> { ["u3"] = "Save button", ["none"] = null }),
            ["done"] = new NoulQuestion("Is it done?") });
        var c = Assert.IsType<ChoiceAnswer>(r.Answers["target"]);
        Assert.Equal("u3", c.Choice); Assert.Equal(0.8, c.Confidence); Assert.Equal(0.9, c.Probabilities["u3"]);
        Assert.Equal(0.2, Assert.IsType<NoulAnswer>(r.Answers["done"]).Probability);
        Assert.Equal("clef", r.Model);
        Assert.Equal(154, r.InputTokens);
    }

    [Fact]
    public async Task AskAsync_Clef_UsesConfiguredModelAndBaseUrl()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.Equal("https://gw.example/client/v4/accounts/a1/ai/run/@cf/cloudflare/clef-flash", req.RequestUri!.ToString());
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            Assert.Equal("clef-flash", body.GetProperty("model").GetString());
            return Json(200, ClefEnvelope);
        });
        var cfg = TestConfig.WithClef("a1") with { ClefModel = "clef-flash", ClefBaseUrl = "https://gw.example/" };
        await new JevClient(new HttpClient(handler), cfg, NullLogger<JevClient>.Instance).AskAsync(new { }, OneNoul());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_Clef_SuccessFalse_ThrowsWithCloudflareError()
    {
        var handler = new FakeHttpHandler(_ => Json(200, """{"result":null,"success":false,"errors":[{"code":7000,"message":"No route for that URI"}],"messages":[]}"""));
        var client = new JevClient(new HttpClient(handler), TestConfig.WithClef(), NullLogger<JevClient>.Instance, NoDelay);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AskAsync(new { }, OneNoul()));
        Assert.Contains("No route for that URI", ex.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_Clef_RetriesOn429_ThenThrowsUnavailable()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
        var client = new JevClient(new HttpClient(handler), TestConfig.WithClef(), NullLogger<JevClient>.Instance, NoDelay);
        await Assert.ThrowsAsync<JevUnavailableException>(() => client.AskAsync(new { }, OneNoul()));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public void IsConfigured_TrueWithClefCredentials()
        => Assert.True(new JevClient(new HttpClient(), TestConfig.WithClef(), NullLogger<JevClient>.Instance).IsConfigured);

    [Fact]
    public void IsConfigured_FalseWhenClefForcedWithoutCredentials()
    {
        var cfg = TestConfig.WithJev("k") with { ProviderOverride = WinUse.Core.Config.DecisionProvider.Clef };
        Assert.False(new JevClient(new HttpClient(), cfg, NullLogger<JevClient>.Instance).IsConfigured);
    }

    [Fact]
    public void IsConfigured_FalseWhenJevForcedWithoutKey()
    {
        var cfg = TestConfig.WithClef() with { ProviderOverride = WinUse.Core.Config.DecisionProvider.Jev };
        Assert.False(new JevClient(new HttpClient(), cfg, NullLogger<JevClient>.Instance).IsConfigured);
    }

    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task AskAsync_Jev_WithImage_SendsAssetAndDangerZoneHeader()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.Equal("true", Assert.Single(req.Headers.GetValues("TypeSafe-Danger-Zone-Images")));
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            var asset = body.GetProperty("assets").GetProperty("shot");
            Assert.Equal("image", asset.GetProperty("type").GetString());
            Assert.Equal("png", asset.GetProperty("format").GetString());
            Assert.Equal(Convert.ToBase64String(Png), asset.GetProperty("data_base64").GetString());
            Assert.False(body.TryGetProperty("images", out _));
            return Json(200, """{"model":"jev-1.14.0","answers":{"q":{"type":"noul","noul":0.5}}}""");
        });
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance);
        await client.AskAsync(new { screenshot = "shot" }, OneNoul(), [new ImageAttachment("shot", Png)]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_Jev_WithoutImage_SendsNoAssetsAndNoHeader()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.False(req.Headers.Contains("TypeSafe-Danger-Zone-Images"));
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            Assert.False(body.TryGetProperty("assets", out _));
            return Json(200, """{"model":"jev-1.13.0","answers":{"q":{"type":"noul","noul":0.5}}}""");
        });
        await new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance).AskAsync(new { }, OneNoul());
    }

    [Fact]
    public async Task AskAsync_Clef_WithImage_SendsDataUrlInImages()
    {
        var handler = new FakeHttpHandler(req => {
            Assert.False(req.Headers.Contains("TypeSafe-Danger-Zone-Images"));
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            var images = body.GetProperty("images");
            Assert.Equal(1, images.GetArrayLength());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), images[0].GetString());
            Assert.False(body.TryGetProperty("assets", out _));
            return Json(200, ClefEnvelope);
        });
        var client = new JevClient(new HttpClient(handler), TestConfig.WithClef(), NullLogger<JevClient>.Instance);
        await client.AskAsync(new { screenshot = "shot" }, OneNoul(), [new ImageAttachment("shot", Png)]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AskAsync_BoxQuestion_IsSentAndParsed()
    {
        var handler = new FakeHttpHandler(req => {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            var q = body.GetProperty("questions").GetProperty("where");
            Assert.Equal("box", q.GetProperty("type").GetString());
            Assert.Equal("the OK button in `shot`", q.GetProperty("instructions").GetString());
            Assert.False(q.TryGetProperty("criteria", out _));
            return Json(200, """{"model":"jev-1.14.0","answers":{"where":{"type":"box","box":{"top":0.25,"bottom":0.82,"left":0.44,"right":0.84},"confidence":0.92}}}""");
        });
        var client = new JevClient(new HttpClient(handler), TestConfig.WithJev("k"), NullLogger<JevClient>.Instance);
        var r = await client.AskAsync(new { shot = "shot" }, new Dictionary<string, JevQuestion> { ["where"] = new BoxQuestion("the OK button in `shot`") },
            [new ImageAttachment("shot", Png)]);
        var b = Assert.IsType<BoxAnswer>(r.Answers["where"]);
        Assert.Equal(0.25, b.Top); Assert.Equal(0.82, b.Bottom); Assert.Equal(0.44, b.Left); Assert.Equal(0.84, b.Right);
        Assert.Equal(0.92, b.Confidence);
    }
}
