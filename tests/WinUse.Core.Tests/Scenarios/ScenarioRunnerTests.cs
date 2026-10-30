using System.Text.Json.Nodes;
using WinUse.Core.Refs;
using WinUse.Core.Scenarios;
using static WinUse.Core.Tests.Scenarios.ScenarioTestHelpers;

namespace WinUse.Core.Tests.Scenarios;

public class ScenarioRunnerTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public async Task RunAsync_SoftStepWithJevUnavailable_IsWarnAndExitZero()
    {
        var d = new FakeDispatcher(("check", new ToolOutcome(false, null, null, new(), "jev", JevUnavailable: true)));
        var s = new Scenario("t", 1, NoEnv, [new("check", Args(("question", "?")), Soft: true)]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(StepStatus.Warn, r.Steps[0].Status); Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public async Task RunAsync_SoftCheckBelowMinProbability_IsWarnWithProbabilityInMessage()
    {
        var result = new JsonObject { ["probability"] = 0.7, ["available"] = true };
        var d = new FakeDispatcher(("check", new ToolOutcome(true, null, null, result, null)));
        var s = new Scenario("t", 1, NoEnv, [new("check", Args(("question", "?")), Soft: true, MinProbability: 0.8)]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(StepStatus.Warn, r.Steps[0].Status);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("p=0.7", r.Steps[0].Message);
    }

    [Fact]
    public async Task RunAsync_SoftCheckAtOrAboveMinProbability_IsPassWithProbabilityInMessage()
    {
        var result = new JsonObject { ["probability"] = 0.85, ["available"] = true };
        var d = new FakeDispatcher(("check", new ToolOutcome(true, null, null, result, null)));
        var s = new Scenario("t", 1, NoEnv, [new("check", Args(("question", "?")), Soft: true, MinProbability: 0.8)]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(StepStatus.Pass, r.Steps[0].Status);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("p=0.85", r.Steps[0].Message);
    }

    [Fact]
    public async Task RunAsync_MarksInvocationsAsReplay()
    {
        var d = new FakeDispatcher();
        await new ScenarioRunner(d, null, null).RunAsync(new Scenario("t", 1, NoEnv, [new("click", Args())]), false, default);
        Assert.True(d.Last!.IsReplay);
    }

    [Fact]
    public async Task RunAsync_ThrowingScreenshot_DoesNotAbort_AndIsNoted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winuse-shot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = new FakeDispatcher();
            var s = new Scenario("t", 1, NoEnv, [new("click", Args()), new("click", Args())]);
            var r = await new ScenarioRunner(d, () => throw new InvalidOperationException("no desktop"), dir).RunAsync(s, false, default);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(2, r.Steps.Count);
            Assert.All(r.Steps, x =>
            {
                Assert.Equal(StepStatus.Pass, x.Status);
                Assert.Null(x.ScreenshotBefore);
                Assert.Null(x.ScreenshotAfter);
                Assert.Contains("screenshot failed: no desktop", x.Message);
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RunAsync_FailedAssert_StopsWithExitOne()
    {
        var d = new FakeDispatcher(("assert_text", new ToolOutcome(true, false, null, new(), null)), ("click", new ToolOutcome(true, null, null, new(), null)));
        var s = new Scenario("t", 1, NoEnv, [new("assert_text", Args()), new("click", Args())]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(1, r.ExitCode); Assert.Single(r.Steps);
    }

    [Fact]
    public async Task RunAsync_NonSoftError_ExitTwo_AndStops()
    {
        var d = new FakeDispatcher(("click", new ToolOutcome(false, null, null, new(), "boom")));
        var s = new Scenario("t", 1, NoEnv, [new("click", Args()), new("click", Args())]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(2, r.ExitCode); Assert.Single(r.Steps);
        Assert.Equal(StepStatus.Error, r.Steps[0].Status); Assert.Equal("boom", r.Steps[0].Message);
    }

    [Fact]
    public async Task RunAsync_ExpandsEnvVariables_AndErrorsOnUndefined()
    {
        var d = new FakeDispatcher(("process_run", new ToolOutcome(true, null, null, new(), null)));
        var ok = new Scenario("t", 1, new Dictionary<string, string> { ["EXE"] = "x.exe" }, [new("process_run", Args(("path", "${EXE}")))]);
        await new ScenarioRunner(d, null, null).RunAsync(ok, false, default);
        Assert.Equal("x.exe", d.Last!.Args["path"]!.GetValue<string>());
        var bad = new Scenario("t", 1, NoEnv, [new("process_run", Args(("path", "${NOPE_UNDEFINED_VAR}")))]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(bad, false, default);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("Undefined variable: NOPE_UNDEFINED_VAR", r.Steps[0].Message);
    }

    [Fact]
    public async Task RunAsync_Heal_ReturnsHealedScenario_WhenLocatorChanged()
    {
        var oldL = new DomLocator(W, "#old"); var newL = new DomLocator(W, "#new");
        var d = new FakeDispatcher(("click", new ToolOutcome(true, null, newL, new(), null)));
        var s = new Scenario("t", 1, NoEnv, [new("click", Args(), oldL)]);
        var healed = await new ScenarioRunner(d, null, null).RunAsync(s, true, default);
        Assert.Equal(newL, healed.HealedScenario!.Steps[0].Resolved);
        var notHealed = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Null(notHealed.HealedScenario);
    }

    [Fact]
    public async Task RunAsync_WritesScreenshots_WhenConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var d = new FakeDispatcher();
            var s = new Scenario("t", 1, NoEnv, [new("click", Args())]);
            var r = await new ScenarioRunner(d, () => Task.FromResult(new byte[] { 1, 2, 3, 4 }), dir).RunAsync(s, false, default);
            Assert.Equal(Path.Combine(dir, "000-before.png"), r.Steps[0].ScreenshotBefore);
            Assert.Equal(Path.Combine(dir, "000-after.png"), r.Steps[0].ScreenshotAfter);
            Assert.Equal(4, new FileInfo(r.Steps[0].ScreenshotBefore!).Length);
            Assert.True(File.Exists(r.Steps[0].ScreenshotAfter));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RunAsync_SecretStepStillHoldingThePlaceholder_IsErrorAndNothingIsDispatched()
    {
        var d = new FakeDispatcher();
        var args = Args(("ref", "u1"), ("text", ScenarioRunner.SecretPlaceholder));
        args["secret"] = true;
        var s = new Scenario("t", 1, NoEnv, [new("set_text", args), new("click", Args())]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(StepStatus.Error, r.Steps[0].Status);
        Assert.Contains("secret text was not recorded", r.Steps[0].Message);
        Assert.Contains("${NAME}", r.Steps[0].Message);
        Assert.Equal(2, r.ExitCode);
        Assert.Single(r.Steps);
        Assert.Empty(d.All);
    }

    [Fact]
    public async Task RunAsync_SecretStepWithAVariable_IsExpandedAndDispatched()
    {
        var d = new FakeDispatcher();
        var args = Args(("ref", "u1"), ("text", "${PW}"));
        args["secret"] = true;
        var s = new Scenario("t", 1, new Dictionary<string, string> { ["PW"] = "hunter2" }, [new("set_text", args)]);
        var r = await new ScenarioRunner(d, null, null).RunAsync(s, false, default);
        Assert.Equal(StepStatus.Pass, r.Steps[0].Status);
        Assert.Equal("hunter2", d.Last!.Args["text"]!.GetValue<string>());
        Assert.True(d.Last.Args["secret"]!.GetValue<bool>());
    }
}
