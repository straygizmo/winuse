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
            Task<CallToolResult> call;
            if (inv.Resolved is not null)
            {
                var refId = await ResolveAsync(inv.Tool, inv.Resolved).ConfigureAwait(false);
                if (refId is null)
                {
                    if (!(Heal && inv.Tool == "act")) return Fail("locator not found", inv.Resolved);
                    call = Call(method, args, ct); // re-ground the intent with Jev
                }
                else if (inv.Tool == "act")
                {
                    call = IntentTools.ActOnRefAsync(ctx, Str(args, "intent") ?? "", Str(args, "window"), Str(args, "kind") ?? "auto",
                        Str(args, "text"), args["min_confidence"]?.GetValue<double>(), ctx.GetRef(refId));
                }
                else
                {
                    var refParam = method.GetParameters().Select(p => p.Name).FirstOrDefault(n => n is "ref" or "from_ref");
                    if (refParam is not null) args[refParam] = refId;
                    call = Call(method, args, ct);
                }
            }
            else
            {
                call = Call(method, args, ct);
            }
            return ToOutcome(inv.Tool, await call.ConfigureAwait(false));
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
                var info = await ctx.Windows.FindAsync(w).ConfigureAwait(false);
                return info is null && !lazy ? null : ctx.Refs.Register(RefKind.Window, w, info?.Hwnd).Id;
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
        if (tool == "act" && result["executed"] is JsonValue ev && ev.TryGetValue<bool>(out var executed) && !executed)
            return new ToolOutcome(false, passed, resolved, result, $"act not executed: {reason ?? "unknown reason"}", jevUnavailable);
        return new ToolOutcome(true, passed, resolved, result, null, jevUnavailable);
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ToolOutcome Fail(string error, Locator? resolved) =>
        new(false, null, resolved, new JsonObject(), error);
}
