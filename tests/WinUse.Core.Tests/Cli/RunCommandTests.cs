using WinUse.Cli;
using WinUse.Core.Refs;
using WinUse.Core.Scenarios;
using WinUse.Core.Tests.Scenarios;
using static WinUse.Core.Tests.Scenarios.ScenarioTestHelpers;

namespace WinUse.Core.Tests.Cli;

public class RunCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winuse-run-" + Guid.NewGuid().ToString("N"));

    public RunCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Save(Scenario s)
    {
        var p = Path.Combine(_dir, "s.json");
        s.Save(p);
        return p;
    }

    private static Scenario One(Locator? resolved = null) =>
        new("t", 1, new Dictionary<string, string>(), [new("write_text", Args(("text", "x")), resolved)]);

    [Fact]
    public async Task Run_WritesReportJsonAndReturnsExitCode()
    {
        var path = Save(One());
        var report = Path.Combine(_dir, "report");
        var log = new StringWriter();
        var code = await RunCommand.ExecuteAsync(path, false, report, new FakeDispatcher(), null, log);
        Assert.Equal(0, code);
        var json = File.ReadAllText(Path.Combine(report, "report.json"));
        Assert.Contains("\"exitCode\": 0", json);
        Assert.Contains("[PASS] 1 write_text", log.ToString());
    }

    [Fact]
    public async Task Run_FailingStep_ReturnsOne_AndLogsFailLine()
    {
        var path = Save(One());
        var d = new FakeDispatcher(("write_text", new ToolOutcome(true, false, null, new(), "nope")));
        var log = new StringWriter();
        var code = await RunCommand.ExecuteAsync(path, false, null, d, null, log);
        Assert.Equal(1, code);
        Assert.Contains("[FAIL]", log.ToString());
    }

    [Fact]
    public async Task Run_MissingFile_ReturnsTwo()
    {
        var log = new StringWriter();
        var code = await RunCommand.ExecuteAsync(Path.Combine(_dir, "nope.json"), false, null, new FakeDispatcher(), null, log);
        Assert.Equal(2, code);
        Assert.NotEqual("", log.ToString());
    }

    [Fact]
    public async Task Run_Heal_WritesHealedScenario()
    {
        var path = Save(One(new WindowLocator("Old", null, null)));
        var d = new FakeDispatcher(("write_text", new ToolOutcome(true, null, new WindowLocator("New", null, null), new(), null)));
        var log = new StringWriter();
        var code = await RunCommand.ExecuteAsync(path, true, Path.Combine(_dir, "r"), d, null, log);
        Assert.Equal(0, code);
        Assert.True(File.Exists(path + ".healed.json"));
        Assert.Contains(".healed.json", log.ToString());
    }
}
