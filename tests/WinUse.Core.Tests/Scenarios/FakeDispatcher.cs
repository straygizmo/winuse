using System.Text.Json.Nodes;
using WinUse.Core.Refs;
using WinUse.Core.Scenarios;

namespace WinUse.Core.Tests.Scenarios;

internal sealed class FakeDispatcher(params (string tool, ToolOutcome outcome)[] map) : IToolDispatcher
{
    private readonly Dictionary<string, ToolOutcome> _map = map.ToDictionary(m => m.tool, m => m.outcome);
    public List<ToolInvocation> All { get; } = [];
    public ToolInvocation? Last => All.Count == 0 ? null : All[^1];

    public Task<ToolOutcome> InvokeAsync(ToolInvocation inv, CancellationToken ct)
    {
        All.Add(inv);
        return Task.FromResult(_map.TryGetValue(inv.Tool, out var o) ? o : new ToolOutcome(true, null, null, new JsonObject(), null));
    }
}

internal static class ScenarioTestHelpers
{
    public static readonly WindowLocator W = new("T", null, null);

    public static JsonObject Args(params (string k, string v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    public static JournalEntry Entry(string id, string tool, JsonObject args, Locator? resolved = null)
        => new(id, tool, args, resolved, null, TimeSpan.Zero, DateTimeOffset.UnixEpoch);
}
