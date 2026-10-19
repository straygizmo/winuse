using WinUse.Core.Grounding;
using WinUse.Core.Jev;
using static WinUse.Core.Tests.Grounding.G;

namespace WinUse.Core.Tests.Grounding;

public class GrounderTests
{
    static Grounder Make(FakeJev jev, FakeExecutor exec, params Candidate[] c) =>
        new(jev, new FakeObservation(c), exec, TestConfig.WithJev("k"));

    [Fact]
    public async Task ActAsync_ExecutesWhenAboveThreshold_AndVerifies()
    {
        var jev = new FakeJev(choice: ("u2", 0.9, 0.8), noul: 0.95);
        var exec = new FakeExecutor();
        var g = new Grounder(jev, new FakeObservation(Cand("u1", "Cancel"), Cand("u2", "Save")), exec, TestConfig.WithJev("k"));
        var r = await g.ActAsync("click save", null, ActKind.Click, null, null);
        Assert.True(r.Executed); Assert.Equal("u2", exec.Last!.Id); Assert.True(r.Verified);
        Assert.Equal(ActKind.Click, exec.LastKind);
    }

    [Fact]
    public async Task ActAsync_ReturnsAlternativesWhenBelowThreshold()
    {
        var jev = new FakeJev(choice: ("u2", 0.4, 0.3), noul: 0.5);
        var g = new Grounder(jev, new FakeObservation(Cand("u1", "Cancel"), Cand("u2", "Save")), new FakeExecutor(), TestConfig.WithJev("k"));
        var r = await g.ActAsync("click save", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Equal("low confidence", r.Reason); Assert.NotEmpty(r.Alternatives);
    }

    [Fact]
    public async Task ActAsync_NoneChosen_IsFailure()
    {
        var exec = new FakeExecutor();
        var r = await Make(new FakeJev(choice: ("none", 0.99, 0.99)), exec, Cand("u1")).ActAsync("x", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Null(exec.Last); Assert.Equal("low confidence", r.Reason);
    }

    [Fact]
    public async Task ActAsync_MinConfidenceOverride()
    {
        var jev = new FakeJev(choice: ("u1", 0.9, 0.5));
        var r = await Make(jev, new FakeExecutor(), Cand("u1")).ActAsync("x", null, ActKind.Click, null, 0.9);
        Assert.False(r.Executed);
    }

    [Fact]
    public async Task ActAsync_NotConfigured()
    {
        var g = new Grounder(new FakeJev(configured: false), new FakeObservation(Cand("u1"), Cand("u2")), new FakeExecutor(), TestConfig.Default);
        var r = await g.ActAsync("x", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Equal("jev not configured", r.Reason); Assert.Equal(2, r.Alternatives.Count);
    }

    [Fact]
    public async Task ActAsync_JevUnavailable()
    {
        var r = await Make(new FakeJev(throwUnavailable: true), new FakeExecutor(), Cand("u1")).ActAsync("x", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Equal("jev unavailable", r.Reason);
    }

    [Fact]
    public async Task ActAsync_AutoKind_UsesKindAnswer()
    {
        var jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9, kind: "type");
        var exec = new FakeExecutor();
        var r = await Make(jev, exec, Cand("u1")).ActAsync("enter hi", null, ActKind.Auto, "hi", null);
        Assert.True(r.Executed);
        Assert.Equal(ActKind.Type, exec.LastKind); Assert.Equal("hi", exec.LastText);
        Assert.True(jev.Calls[0].ContainsKey("kind"));
    }

    [Fact]
    public async Task ActAsync_TwoStage_WhenOver254Candidates()
    {
        var cands = Enumerable.Range(0, 300).Select(i => Cand($"u{i}", $"L{i}", container: i < 150 ? "A" : "B")).ToArray();
        var jev = new FakeJev(choice: ("u3", 0.9, 0.9), noul: 0.9);
        var exec = new FakeExecutor();
        var r = await Make(jev, exec, cands).ActAsync("go", null, ActKind.Click, null, null);
        Assert.True(r.Executed); Assert.Equal("u3", exec.Last!.Id);
        Assert.True(jev.Calls[0].ContainsKey("area"));
        Assert.True(jev.Calls[1].ContainsKey("target"));
        Assert.All(jev.Calls.Where(c => c.ContainsKey("target")), c => Assert.True(((ChoiceQuestion)c["target"]).Criteria.Count <= 255));
    }

    [Fact]
    public async Task FindAsync_WithoutJev_ReturnsUngroundedCandidates()
    {
        var g = new Grounder(new FakeJev(configured: false), new FakeObservation(Cand("u1", "Cancel")), new FakeExecutor(), TestConfig.Default);
        var r = await g.FindAsync("anything", null);
        Assert.False(r.Grounded); Assert.Single(r.Ranked);
    }

    [Fact]
    public async Task FindAsync_ExcludesNone_AndRespectsTopK()
    {
        var jev = new FakeJev(choice: ("u1", 0.7, 0.8));
        var r = await Make(jev, new FakeExecutor(), Cand("u1"), Cand("u2"), Cand("u3"), Cand("u4")).FindAsync("x", null, 2);
        Assert.True(r.Grounded);
        Assert.Equal(2, r.Ranked.Count);
        Assert.Equal("u1", r.Ranked[0].Candidate.Id);
        Assert.Equal(0.8, r.Confidence);
    }

    [Fact]
    public async Task FindAsync_StateShape()
    {
        var jev = new FakeJev(choice: ("u1", 0.7, 0.8));
        await Make(jev, new FakeExecutor(), Cand("u1")).FindAsync("goal!", null);
        var json = System.Text.Json.JsonSerializer.Serialize(jev.LastState);
        Assert.Contains("\"goal\":\"goal!\"", json);
        Assert.Contains("\"recent_actions\":[\"did something\"]", json);
        Assert.Contains("\"window\":{\"title\":\"T\",\"process\":\"p\"}", json);
        Assert.Contains("\"checked\":null", json);
    }

    [Fact]
    public async Task CheckAsync_WhenJevUnavailable_ReportsNotAvailable()
    {
        var g = new Grounder(new FakeJev(throwUnavailable: true), new FakeObservation(), new FakeExecutor(), TestConfig.WithJev("k"));
        Assert.False((await g.CheckAsync("done?", null)).Available);
    }

    [Fact]
    public async Task CheckAsync_ReturnsProbability()
    {
        var g = new Grounder(new FakeJev(noul: 0.8), new FakeObservation(), new FakeExecutor(), TestConfig.WithJev("k"));
        var r = await g.CheckAsync("done?", null);
        Assert.True(r.Available); Assert.Equal(0.8, r.Probability);
    }
}
