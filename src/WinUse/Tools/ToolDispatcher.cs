using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;
using WinUse.Core.Scenarios;

namespace WinUse.Tools;

/// <summary>
/// Invokes the MCP tool methods by tool name (used by scenario replay and the CLI), so recorded and live
/// calls run the same code. Arguments bind by C# parameter name, which is also the MCP wire name.
/// </summary>
public sealed class ToolDispatcher(ToolContext ctx) : IToolDispatcher
{
    private static readonly Lazy<IReadOnlyDictionary<string, MethodInfo>> Tools = new(Discover);

    /// <summary>When set, an <c>act</c> step whose recorded locator no longer resolves is re-grounded with Jev instead of failing.</summary>
    public bool Heal { get; init; }

    public static IReadOnlyList<string> DiscoverToolNames() => Tools.Value.Keys.Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyDictionary<string, MethodInfo> Discover()
    {
        var map = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var type in typeof(ToolDispatcher).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (m.GetCustomAttribute<McpServerToolAttribute>() is { } attr)
                    map.Add(attr.Name ?? m.Name, m);
        return map;
    }

    public async Task<ToolOutcome> InvokeAsync(ToolInvocation inv, CancellationToken ct)
    {
        if (!Tools.Value.TryGetValue(inv.Tool, out var method))
            return Fail($"Unknown tool: {inv.Tool}", null);
        try
        {
            var args = (JsonObject)inv.Args.DeepClone();
            var recordedRefs = args[ToolContext.ResolvedRefsKey] as JsonObject;
            args.Remove(ToolContext.ResolvedRefsKey);
            var paramNames = method.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
            var injected = new HashSet<string>(StringComparer.Ordinal);
            var isAct = inv.Tool == "act";
            string? actTarget = null;
            var regroundAct = false;

            string? primary = isAct ? null : new[] { "ref", "from_ref" }.FirstOrDefault(paramNames.Contains);
            string? healCause = null;

            // 1. The primary locator (the element the step acted on).
            if (inv.Resolved is not null)
            {
                var refId = await ResolveAsync(inv.Tool, inv.Resolved).ConfigureAwait(false);
                if (refId is null)
                {
                    healCause = $"locator not found: {primary ?? "resolved"}";
                    // A target that is gone is a FAIL of the scenario, not an execution error.
                    if (!(Heal && isAct)) return NotFound(healCause, inv.Resolved);
                    regroundAct = true; // re-ground the intent with Jev
                }
                else if (isAct)
                {
                    actTarget = refId;
                }
                else if (primary is not null)
                {
                    args[primary] = refId;
                    injected.Add(primary);
                }
            }
            else if (isAct && inv.IsReplay)
            {
                // Replay must not depend on Jev: an act without a recorded locator is only re-grounded when healing.
                healCause = "act step has no resolved locator";
                if (!Heal) return NotFound(healCause, null);
                regroundAct = true;
            }

            // 2. Every other ref argument recorded with its locator (to_ref, window, scope, ...).
            if (recordedRefs is not null)
            {
                foreach (var (name, node) in recordedRefs.ToList())
                {
                    if (injected.Contains(name) || !paramNames.Contains(name) || node is null) continue;
                    var loc = node.Deserialize<Locator>() ?? throw new JsonException($"Invalid locator for '{name}'.");
                    var id = await ResolveAsync(inv.Tool, loc).ConfigureAwait(false);
                    if (id is null) return NotFound($"locator not found: {name}", inv.Resolved);
                    args[name] = id;
                    injected.Add(name);
                }
            }

            // 3. Never pass a recorded ref id without its locator: in this session it may name a different element.
            foreach (var name in ToolContext.RefArgNames)
                if (!injected.Contains(name) && args[name] is JsonValue v && v.TryGetValue<string>(out var s) && ToolContext.LooksLikeRef(s))
                    return Fail($"stale ref: {name}", inv.Resolved);

            var call = isAct && !regroundAct && actTarget is not null
                ? IntentTools.ActOnRefAsync(ctx, Str(args, "intent") ?? "", Str(args, "window"), Str(args, "kind") ?? "auto",
                    Str(args, "text"), args["min_confidence"]?.GetValue<double>(), ctx.GetRef(actTarget))
                : Call(method, args, ct);
            var outcome = ToOutcome(inv.Tool, await call.ConfigureAwait(false));
            if (regroundAct && outcome.Passed == false)
                outcome = outcome with { Error = $"{healCause}; heal failed: {outcome.Error}" };
            return outcome;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            return Fail(inner.Message, inv.Resolved);
        }
    }

    /// <summary>Resolves a recorded locator to a fresh ref id, or null when it cannot be found.</summary>
    private async Task<string?> ResolveAsync(string tool, Locator loc)
    {
        // These tools decide themselves what a missing element means, so give them a ref that re-resolves lazily.
        var lazy = tool is "wait_for" or "assert_exists";
        switch (loc)
        {
            case UiaLocator u:
            {
                var el = await ctx.Uia.ResolveAsync(u).ConfigureAwait(false);
                return el is null && !lazy ? null : ctx.Refs.Register(RefKind.Uia, u, el).Id;
            }
            case DomLocator d:
            {
                var sel = await ctx.Web.ResolveAsync(d).ConfigureAwait(false);
                return sel is null && !lazy ? null : ctx.Refs.Register(RefKind.Dom, d, sel ?? d.Selector).Id;
            }
            case WindowLocator w:
            {
                // Titles change (e.g. an unsaved marker): fall back to the single window of the same process/class.
                var info = await ctx.Windows.FindWithFallbackAsync(w).ConfigureAwait(false);
                if (info is null) return lazy ? ctx.Refs.Register(RefKind.Window, w, null).Id : null;
                return ctx.Refs.Register(RefKind.Window, ToolContext.LocatorOf(info), info.Hwnd).Id;
            }
            default:
                return null;
        }
    }

    private Task<CallToolResult> Call(MethodInfo method, JsonObject args, CancellationToken ct)
    {
        var ps = method.GetParameters();
        var values = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
            values[i] = Bind(ps[i], args, ct);
        var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
        return (Task<CallToolResult>)method.Invoke(target, values)!;
    }

    private object? Bind(ParameterInfo p, JsonObject args, CancellationToken ct)
    {
        if (p.ParameterType == typeof(ToolContext)) return ctx;
        if (p.ParameterType == typeof(CancellationToken)) return ct;
        var name = p.Name!;
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            if (p.HasDefaultValue) return p.DefaultValue;
            if (IsNullable(p)) return null;
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        try
        {
            var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            // Scenario variables expand to strings, so accept "true"/"false" for booleans.
            if (type == typeof(bool) && node is JsonValue v && v.TryGetValue<string>(out var s)) return bool.Parse(s);
            return node.Deserialize(p.ParameterType, ToolResult.Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new ArgumentException($"Invalid value for argument '{name}': {ex.Message}", ex);
        }
    }

    private static bool IsNullable(ParameterInfo p) =>
        Nullable.GetUnderlyingType(p.ParameterType) is not null
        || (!p.ParameterType.IsValueType && new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable);

    private ToolOutcome ToOutcome(string tool, CallToolResult r)
    {
        var text = r.Content.OfType<TextContentBlock>().LastOrDefault()?.Text;
        var result = text is null ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        Locator? resolved = null;
        if (result["step_id"]?.GetValue<string>() is { } stepId && ctx.Journal.TryGet(stepId, out var entry)) resolved = entry.Resolved;
        if (r.IsError == true)
            return new ToolOutcome(false, null, resolved, result, Str(result, "error") ?? "tool error");

        bool? passed = result["passed"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) ? pb : null;
        var reason = Str(result, "reason");
        var jevUnavailable = tool switch
        {
            "check" => result["available"] is JsonValue av && av.TryGetValue<bool>(out var a) && !a,
            "act" => reason is "jev unavailable" or "jev not configured",
            _ => false,
        };
        // Nothing to act on (low confidence / Jev unavailable) is a FAIL, not an execution error.
        if (tool == "act" && result["executed"] is JsonValue ev && ev.TryGetValue<bool>(out var executed) && !executed)
            return new ToolOutcome(true, false, resolved, result, $"act not executed: {reason ?? "unknown reason"}", jevUnavailable);
        return new ToolOutcome(true, passed, resolved, result, null, jevUnavailable);
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ToolOutcome Fail(string error, Locator? resolved) =>
        new(false, null, resolved, new JsonObject(), error);

    /// <summary>A replayed target that cannot be found: the step ran but failed (exit 1), see spec ﾂｧ7.</summary>
    private static ToolOutcome NotFound(string error, Locator? resolved) =>
        new(true, false, resolved, new JsonObject(), error);
}
