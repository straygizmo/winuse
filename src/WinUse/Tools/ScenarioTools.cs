using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Scenarios;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ScenarioTools
{
    private const double ReportScreenshotScale = 0.5;

    [McpServerTool(Name = "scenario_save")]
    [Description("Save the given journaled steps (step_id values returned by earlier tool calls), in order, as a replayable scenario JSON file.")]
    public static Task<CallToolResult> ScenarioSave(
        ToolContext ctx,
        [Description("Scenario name.")] string name,
        [Description("Step ids to include, in order (e.g. [\"s3\",\"s5\"]).")] string[] step_ids,
        [Description("Output file path (under the allowed roots).")] string path,
        [Description("Optional description.")] string? description = null) =>
        ctx.RunStepAsync("scenario_save", ToolContext.Args(("name", name), ("step_ids", step_ids), ("path", path), ("description", description)), _ =>
        {
            var s = Scenario.FromJournal(name, ctx.Journal, step_ids, description);
            ctx.Files.WriteText(path, s.ToJson());
            return Task.FromResult<object>(new { path, steps = s.Steps.Count });
        });

    [McpServerTool(Name = "scenario_list", ReadOnly = true)]
    [Description("List the scenario JSON files in a directory with their name, description and step count.")]
    public static Task<CallToolResult> ScenarioList(
        ToolContext ctx,
        [Description("Directory to scan (under the allowed roots).")] string dir) =>
        ctx.RunStepAsync("scenario_list", ToolContext.Args(("dir", dir)), _ =>
        {
            var list = new List<object>();
            foreach (var f in ctx.Files.ListDir(dir).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                try
                {
                    var s = Scenario.Parse(ctx.Files.ReadText(f));
                    list.Add(new { path = f, name = s.Name, description = s.Description, steps = s.Steps.Count });
                }
                catch (Exception ex)
                {
                    list.Add(new { path = f, error = ex.Message });
                }
            }
            return Task.FromResult<object>(new { scenarios = list });
        });

    [McpServerTool(Name = "scenario_run")]
    [Description("Replay a scenario file. Steps with a recorded locator are resolved deterministically; heal=true re-grounds failed act steps with the decision model (Jev or Clef) and writes <name>.healed.json. Returns per-step status and exit_code (0 pass, 1 fail, 2 error).")]
    public static Task<CallToolResult> ScenarioRun(
        ToolContext ctx,
        [Description("Scenario file path (under the allowed roots).")] string path,
        [Description("Re-resolve failed locators with the decision model and save a healed copy.")] bool heal = false,
        [Description("Optional directory for before/after screenshots of each step (under the allowed roots).")] string? report_dir = null,
        CancellationToken cancellationToken = default) =>
        ctx.RunStepAsync("scenario_run", ToolContext.Args(("path", path), ("heal", heal), ("report_dir", report_dir)), async step =>
        {
            var scenario = Scenario.Parse(ctx.Files.ReadText(path));
            if (report_dir is not null) ctx.Files.Exists(report_dir); // throws when outside the allowed roots
            var runner = new ScenarioRunner(
                new ToolDispatcher(ctx) { Heal = heal },
                report_dir is null ? null : async () => (await ctx.Screen.CaptureScreenAsync(ReportScreenshotScale)).Png,
                report_dir);
            var report = await runner.RunAsync(scenario, heal, cancellationToken);
            string? healedPath = null;
            if (report.HealedScenario is { } healed)
            {
                healedPath = Scenario.HealedPath(path);
                ctx.Files.WriteText(healedPath, healed.ToJson());
            }
            step.Passed = report.ExitCode == 0;
            return new
            {
                passed = report.ExitCode == 0,
                scenario = report.Scenario,
                exit_code = report.ExitCode,
                steps = report.Steps.Select(s => new
                {
                    index = s.Index,
                    tool = s.Tool,
                    status = s.Status.ToString().ToLowerInvariant(),
                    message = s.Message,
                    elapsed_ms = (long)s.Elapsed.TotalMilliseconds,
                    screenshot_before = s.ScreenshotBefore,
                    screenshot_after = s.ScreenshotAfter,
                }).ToList(),
                healed_path = healedPath,
            };
        });
}
