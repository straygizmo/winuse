using System.Text.Json.Nodes;
using WinUse.Core.Refs;

namespace WinUse.Core.Scenarios;

/// <summary>
/// One tool call. <paramref name="IsReplay"/> is set by the scenario runner: a replayed call must not depend on the decision model,
/// so e.g. an <c>act</c> step without a recorded locator fails instead of grounding live (unless healing).
/// </summary>
public sealed record ToolInvocation(string Tool, JsonObject Args, Locator? Resolved, bool IsReplay = false);

public sealed record ToolOutcome(bool Ok, bool? Passed, Locator? Resolved, JsonObject Result, string? Error, bool JevUnavailable = false);

public interface IToolDispatcher
{
    Task<ToolOutcome> InvokeAsync(ToolInvocation inv, CancellationToken ct);
}
