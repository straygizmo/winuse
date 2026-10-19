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
        Assert.Equal("Jev is not configured", ex.Message);
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
}
