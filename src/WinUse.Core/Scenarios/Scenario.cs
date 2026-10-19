using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinUse.Core.Refs;

namespace WinUse.Core.Scenarios;

public sealed record ScenarioStep(string Tool, JsonObject Args, Locator? Resolved = null, bool Soft = false, double? MinProbability = null)
{
    public bool Equals(ScenarioStep? other) =>
        other is not null
        && Tool == other.Tool
        && Soft == other.Soft
        && MinProbability == other.MinProbability
        && JsonNode.DeepEquals(Args, other.Args)
        && LocatorEquals(Resolved, other.Resolved);

    public override int GetHashCode() => HashCode.Combine(Tool, Soft, MinProbability);

    internal static bool LocatorEquals(Locator? a, Locator? b) =>
        a is null || b is null
            ? a is null && b is null
            : JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}

public sealed record Scenario(string Name, int Version, IReadOnlyDictionary<string, string> Env, IReadOnlyList<ScenarioStep> Steps, string? Description = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Scenario FromJournal(string name, Journal j, IEnumerable<string> stepIds, string? description)
    {
        var steps = new List<ScenarioStep>();
        foreach (var id in stepIds)
        {
            if (!j.TryGet(id, out var e)) throw new ArgumentException($"Unknown step id: {id}", nameof(stepIds));
            // Replay never grounds live, so an act step is only replayable with the locator it actually acted on.
            if (e.Tool == "act" && e.Executed == false)
                throw new ArgumentException(
                    $"Step {id} is an act that was not executed (nothing was clicked or typed), so it cannot be replayed. " +
                    "Save the step that actually performed the action (a later act, or click/set_text on a ref) instead.", nameof(stepIds));
            if (e.Tool == "act" && e.Resolved is null)
                throw new ArgumentException($"Step {id} is an act without a resolved locator, so it cannot be replayed.", nameof(stepIds));
            var isCheck = e.Tool == "check";
            steps.Add(new ScenarioStep(e.Tool, (JsonObject)e.Args.DeepClone(), e.Resolved, isCheck, isCheck ? 0.8 : null));
        }
        return new Scenario(name, 1, new Dictionary<string, string>(), steps, description);
    }

    public string ToJson()
    {
        var dto = new ScenarioDto
        {
            Name = Name,
            Version = Version,
            Env = new Dictionary<string, string>(Env),
            Description = Description,
            Steps = Steps.Select(s => new StepDto
            {
                Tool = s.Tool,
                Args = (JsonObject)s.Args.DeepClone(),
                Resolved = s.Resolved,
                Soft = s.Soft ? true : null,
                MinProbability = s.MinProbability,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static Scenario Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ScenarioDto>(json, Options)
                  ?? throw new JsonException("Empty scenario");
        return new Scenario(
            dto.Name,
            dto.Version,
            dto.Env ?? new Dictionary<string, string>(),
            (dto.Steps ?? []).Select(s => new ScenarioStep(s.Tool, s.Args ?? new JsonObject(), s.Resolved, s.Soft ?? false, s.MinProbability)).ToList(),
            dto.Description);
    }

    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Path of the healed copy of a scenario: <c>x.json</c> becomes <c>x.healed.json</c>.</summary>
    public static string HealedPath(string scenarioPath) =>
        (scenarioPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? scenarioPath[..^5] : scenarioPath) + ".healed.json";

    public static Scenario Load(string path) => Parse(File.ReadAllText(path));

    public bool Equals(Scenario? other) =>
        other is not null
        && Name == other.Name
        && Version == other.Version
        && Description == other.Description
        && Env.Count == other.Env.Count
        && Env.All(kv => other.Env.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(Name, Version, Description, Steps.Count);

    private sealed class ScenarioDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("steps")] public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
        [JsonPropertyName("args")] public JsonObject? Args { get; set; }
        [JsonPropertyName("resolved")] public Locator? Resolved { get; set; }
        [JsonPropertyName("soft")] public bool? Soft { get; set; }
        [JsonPropertyName("min_probability")] public double? MinProbability { get; set; }
    }
}
