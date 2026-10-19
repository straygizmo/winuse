using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace WinUse.Tools;

public static class ToolResult
{
    /// <summary>camelCase, numbers readable from strings, non-ASCII text kept readable.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON object <c>{"step_id": stepId, ...payload}</c>; a non-object payload goes under <c>value</c>.</summary>
    public static string Ok(object payload, string stepId)
    {
        var o = new JsonObject { ["step_id"] = stepId };
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), Json);
        if (node is JsonObject po)
        {
            foreach (var key in po.Select(kv => kv.Key).ToList())
            {
                if (key == "step_id") continue;
                var value = po[key];
                po.Remove(key);
                o[key] = value;
            }
        }
        else
        {
            o["value"] = node;
        }
        return o.ToJsonString(Json);
    }

    public static CallToolResult Error(string message, string stepId) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = new JsonObject { ["step_id"] = stepId, ["error"] = message }.ToJsonString(Json) }],
    };

    internal static CallToolResult Text(string json, IEnumerable<ContentBlock>? before = null) => new()
    {
        Content = [.. before ?? [], new TextContentBlock { Text = json }],
    };
}
