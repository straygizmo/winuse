using System.Text.Json;
using System.Text.Json.Serialization;
using WinUse.Core.Scenarios;
using WinUse.Tools;

namespace WinUse.Cli;

/// <summary>Replays a saved scenario without AI. Exit code: 0 PASS, 1 FAIL, 2 error.</summary>
public static class RunCommand
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> ExecuteAsync(string scenarioPath, bool heal, string? reportDir, ToolContext ctx)
    {
        if (!Environment.UserInteractive)
        {
            Console.Error.WriteLine("winuse run requires an interactive desktop session");
            return 2;
        }
        return await ExecuteAsync(scenarioPath, heal, reportDir, new ToolDispatcher(ctx),
            async () => (await ctx.Screen.CaptureScreenAsync(0.5).ConfigureAwait(false)).Png, Console.Error).ConfigureAwait(false);
    }

    public static async Task<int> ExecuteAsync(string scenarioPath, bool heal, string? reportDir,
        IToolDispatcher dispatcher, Func<Task<byte[]>>? screenshot, TextWriter log)
    {
        Scenario scenario;
        try
        {
            scenario = Scenario.Load(scenarioPath);
        }
        catch (Exception ex)
        {
            log.WriteLine($"cannot load scenario '{scenarioPath}': {ex.Message}");
            return 2;
        }

        RunReport report;
        try
        {
            report = await new ScenarioRunner(dispatcher, screenshot, reportDir).RunAsync(scenario, heal, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.WriteLine($"run failed: {ex.Message}");
            return 2;
        }

        foreach (var s in report.Steps)
        {
            var tag = s.Status switch
            {
                StepStatus.Pass => "PASS",
                StepStatus.Fail => "FAIL",
                StepStatus.Warn => "WARN",
                _ => "ERROR",
            };
            var line = $"[{tag}] {s.Index + 1} {s.Tool} ({(long)s.Elapsed.TotalMilliseconds}ms)";
            if (s.Status != StepStatus.Pass && s.Message is not null) line += $": {s.Message}";
            log.WriteLine(line);
        }

        if (reportDir is not null)
        {
            try
            {
                Directory.CreateDirectory(reportDir);
                await File.WriteAllTextAsync(Path.Combine(reportDir, "report.json"),
                    JsonSerializer.Serialize(report, ReportJson)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.WriteLine($"cannot write report: {ex.Message}");
                return 2;
            }
        }

        if (heal && report.HealedScenario is { } healed)
        {
            var healedPath = scenarioPath + ".healed.json";
            healed.Save(healedPath);
            log.WriteLine($"healed scenario saved: {healedPath}");
        }

        return report.ExitCode;
    }
}
