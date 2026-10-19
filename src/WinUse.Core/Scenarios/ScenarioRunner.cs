using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace WinUse.Core.Scenarios;

public enum StepStatus { Pass, Fail, Warn, Error }

public sealed record StepReport(int Index, string Tool, StepStatus Status, string? Message, TimeSpan Elapsed, string? ScreenshotBefore, string? ScreenshotAfter);

public sealed record RunReport(string Scenario, IReadOnlyList<StepReport> Steps, int ExitCode, Scenario? HealedScenario = null);

public sealed class ScenarioRunner(IToolDispatcher dispatcher, Func<Task<byte[]>>? screenshot, string? reportDir)
{
    private static readonly Regex VarPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private sealed class UndefinedVariableException(string name) : Exception($"Undefined variable: {name}");

    public async Task<RunReport> RunAsync(Scenario s, bool heal, CancellationToken ct)
    {
        var reports = new List<StepReport>();
        var healedSteps = s.Steps.ToList();
        var healed = false;
        var exit = 0;

        for (var i = 0; i < s.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = s.Steps[i];
            var sw = Stopwatch.StartNew();

            JsonObject args;
            try { args = (JsonObject)Expand(step.Args, s.Env)!; }
            catch (UndefinedVariableException ex)
            {
                reports.Add(new StepReport(i, step.Tool, StepStatus.Error, ex.Message, sw.Elapsed, null, null));
                exit = 2;
                break;
            }

            var before = await CaptureAsync(i, "before");
            var outcome = await dispatcher.InvokeAsync(new ToolInvocation(step.Tool, args, step.Resolved), ct);
            var after = await CaptureAsync(i, "after");
            sw.Stop();

            if (heal && outcome.Resolved is not null && step.Resolved is not null
                && !ScenarioStep.LocatorEquals(outcome.Resolved, step.Resolved))
            {
                healedSteps[i] = step with { Resolved = outcome.Resolved };
                healed = true;
            }

            StepStatus status;
            string? message = null;
            var stop = false;
            if (step.Soft)
            {
                if (!outcome.Ok || outcome.JevUnavailable || outcome.Passed == false)
                {
                    status = StepStatus.Warn;
                    message = outcome.Error ?? "soft check failed";
                }
                else if (step.MinProbability is { } min
                         && outcome.Result["probability"] is JsonValue pv && pv.TryGetValue<double>(out var p) && p < min)
                {
                    status = StepStatus.Warn;
                    message = $"probability {p} below {min}";
                }
                else status = StepStatus.Pass;
            }
            else if (!outcome.Ok)
            {
                status = StepStatus.Error; message = outcome.Error; exit = 2; stop = true;
            }
            else if (outcome.Passed == false)
            {
                status = StepStatus.Fail; message = outcome.Error; exit = 1; stop = true;
            }
            else status = StepStatus.Pass;

            reports.Add(new StepReport(i, step.Tool, status, message, sw.Elapsed, before, after));
            if (stop) break;
        }

        var healedScenario = healed ? s with { Steps = healedSteps } : null;
        return new RunReport(s.Name, reports, exit, healedScenario);
    }

    private async Task<string?> CaptureAsync(int index, string phase)
    {
        if (screenshot is null || reportDir is null) return null;
        Directory.CreateDirectory(reportDir);
        var path = Path.Combine(reportDir, $"{index:D3}-{phase}.png");
        await File.WriteAllBytesAsync(path, await screenshot());
        return path;
    }

    private static JsonNode? Expand(JsonNode? node, IReadOnlyDictionary<string, string> env)
    {
        switch (node)
        {
            case JsonObject o:
                var no = new JsonObject();
                foreach (var kv in o) no[kv.Key] = Expand(kv.Value, env);
                return no;
            case JsonArray a:
                var na = new JsonArray();
                foreach (var item in a) na.Add(Expand(item, env));
                return na;
            case JsonValue v when v.TryGetValue<string>(out var str):
                return JsonValue.Create(VarPattern.Replace(str, m =>
                {
                    var name = m.Groups[1].Value;
                    return env.TryGetValue(name, out var val) ? val
                        : Environment.GetEnvironmentVariable(name) ?? throw new UndefinedVariableException(name);
                }));
            default:
                return node?.DeepClone();
        }
    }
}
