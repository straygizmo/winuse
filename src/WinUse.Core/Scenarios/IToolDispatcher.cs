using System.Text.Json.Nodes;
using WinUse.Core.Refs;

namespace WinUse.Core.Scenarios;

public sealed record ToolInvocation(string Tool, JsonObject Args, Locator? Resolved);

public sealed record ToolOutcome(bool Ok, bool? Passed, Locator? Resolved, JsonObject Result, string? Error, bool JevUnavailable = false);

public interface IToolDispatcher
{
    Task<ToolOutcome> InvokeAsync(ToolInvocation inv, CancellationToken ct);
}
