using WinUse.Core.Refs;
using WinUse.Core.Scenarios;
using static WinUse.Core.Tests.Scenarios.ScenarioTestHelpers;

namespace WinUse.Core.Tests.Scenarios;

public class JournalTests
{
    [Fact]
    public void Journal_NextId_IsSequential_AndTryGet()
    {
        var j = new Journal();
        Assert.Equal("s1", j.NextId());
        Assert.Equal("s2", j.NextId());
        j.Record(Entry("s1", "click", Args()));
        Assert.True(j.TryGet("s1", out var e));
        Assert.Equal("click", e.Tool);
        Assert.False(j.TryGet("s9", out _));
        Assert.Single(j.Entries);
    }

    [Fact]
    public void FromJournal_PicksOnlyGivenStepsInOrder_AndMarksCheckSoft()
    {
        var j = new Journal();
        j.Record(Entry(j.NextId(), "click", Args(("ref", "u1"))));
        j.Record(Entry(j.NextId(), "act", Args(("intent", "save")), new DomLocator(W, "#save")));
        j.Record(Entry(j.NextId(), "check", Args(("question", "saved?"))));
        var s = Scenario.FromJournal("t", j, ["s2", "s3"], null);
        Assert.Equal(["act", "check"], s.Steps.Select(x => x.Tool));
        Assert.True(s.Steps[1].Soft); Assert.Equal(0.8, s.Steps[1].MinProbability);
        Assert.Equal(s, Scenario.Parse(s.ToJson()));
    }

    [Fact]
    public void FromJournal_UnknownStep_Throws()
    {
        Assert.Throws<ArgumentException>(() => Scenario.FromJournal("t", new Journal(), ["s1"], null));
    }

    [Fact]
    public void ToJson_OmitsDefaults_AndUsesWireNames()
    {
        var j = new Journal();
        j.Record(Entry(j.NextId(), "check", Args(("question", "q"))));
        var json = Scenario.FromJournal("t", j, ["s1"], null).ToJson();
        Assert.Contains("\"min_probability\"", json);
        Assert.DoesNotContain("\"resolved\"", json);
        Assert.DoesNotContain("\"description\"", json);
    }

    [Fact]
    public void Load_Save_RoundTrip()
    {
        var j = new Journal();
        j.Record(Entry(j.NextId(), "act", Args(("intent", "x")), new DomLocator(W, "#a")));
        var s = Scenario.FromJournal("t", j, ["s1"], "desc") with { Env = new Dictionary<string, string> { ["A"] = "b" } };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { s.Save(path); Assert.Equal(s, Scenario.Load(path)); }
        finally { File.Delete(path); }
    }
}
