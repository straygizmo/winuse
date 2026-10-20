using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WinUse.Core.Config;

namespace WinUse.Core.Jev;

/// <summary>
/// HTTP client for the System One question API, served by TypeSafe Jev and by Cloudflare Clef (Workers AI).
/// Which one is called follows <see cref="WinUseConfig.Provider"/>.
/// </summary>
public sealed class JevClient : IJevClient
{
    public const string NotConfiguredMessage =
        "No decision model is configured: set JEV_API_KEY, or CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN for Clef.";

    const int MaxChoices = 255;
    static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    readonly HttpClient _http;
    readonly WinUseConfig _cfg;
    readonly ILogger<JevClient> _log;
    readonly TimeSpan[] _backoff;

    public JevClient(HttpClient http, WinUseConfig cfg, ILogger<JevClient> log, TimeSpan[]? backoff = null)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
        _backoff = backoff ?? DefaultBackoff;
    }

    public DecisionProvider Provider => _cfg.Provider;

    public bool IsConfigured => _cfg.Provider switch
    {
        DecisionProvider.Jev => _cfg.JevApiKey is not null,
        DecisionProvider.Clef => _cfg.CloudflareAccountId is not null && _cfg.CloudflareApiToken is not null,
        _ => false,
    };

    /// <summary>The endpoint requests go to, or null when the provider is not configured.</summary>
    public string? Endpoint => EndpointOf(_cfg);

    /// <summary>The System One endpoint for <paramref name="cfg"/>'s provider, or null when it cannot be built.</summary>
    public static string? EndpointOf(WinUseConfig cfg) => cfg.Provider switch
    {
        DecisionProvider.Jev => $"{cfg.JevBaseUrl.TrimEnd('/')}/v1/systemone",
        DecisionProvider.Clef when cfg.CloudflareAccountId is not null =>
            $"{cfg.ClefBaseUrl.TrimEnd('/')}/client/v4/accounts/{cfg.CloudflareAccountId}/ai/run/@cf/cloudflare/{cfg.ClefModel}",
        _ => null,
    };

    string Model => _cfg.Provider == DecisionProvider.Clef ? _cfg.ClefModel : _cfg.JevModel;

    string? Token => _cfg.Provider == DecisionProvider.Clef ? _cfg.CloudflareApiToken : _cfg.JevApiKey;

    public async Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct = default)
    {
        if (!IsConfigured || Endpoint is not { } url || Token is not { } token)
            throw new InvalidOperationException(NotConfiguredMessage);

        var body = BuildBody(state, questions);
        _log.LogDebug("{Provider} request to {Url} (Authorization: Bearer ***): {Body}", Provider, url, body);

        Exception? last = null;
        for (var attempt = 0; attempt <= _backoff.Length; attempt++)
        {
            if (attempt > 0)
            {
                var delay = _backoff[attempt - 1];
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AttemptTimeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                _log.LogDebug("{Provider} response {Status}: {Body}", Provider, (int)resp.StatusCode, text);

                var code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode) return Parse(text);
                if (code is 429 or 529)
                {
                    last = new HttpRequestException($"{Provider} returned {code}");
                    _log.LogWarning("{Provider} returned {Status} (attempt {Attempt})", Provider, code, attempt + 1);
                    continue;
                }
                if (code is >= 400 and < 500)
                    throw new InvalidOperationException($"{Provider} request failed with {code} {resp.ReasonPhrase}: {text}");
                // other 5xx: treat as transient
                last = new HttpRequestException($"{Provider} returned {code}: {text}");
                _log.LogWarning("{Provider} returned {Status} (attempt {Attempt})", Provider, code, attempt + 1);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                _log.LogWarning(ex, "{Provider} request failed (attempt {Attempt})", Provider, attempt + 1);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                _log.LogWarning("{Provider} request timed out (attempt {Attempt})", Provider, attempt + 1);
            }
        }
        throw new JevUnavailableException($"{Provider} unavailable after {_backoff.Length + 1} attempts: {last?.Message}", last);
    }

    string BuildBody(object state, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        var qs = new JsonObject();
        foreach (var (id, q) in questions)
        {
            switch (q)
            {
                case ChoiceQuestion c:
                    if (c.Criteria.Count > MaxChoices)
                        throw new ArgumentException($"Choice question '{id}' has {c.Criteria.Count} options; the maximum is {MaxChoices}", nameof(questions));
                    var criteria = new JsonObject();
                    foreach (var (k, v) in c.Criteria) criteria[k] = v is null ? null : JsonValue.Create(v);
                    qs[id] = new JsonObject { ["type"] = "choice", ["instructions"] = c.Instructions, ["criteria"] = criteria };
                    break;
                case NoulQuestion n:
                    qs[id] = new JsonObject { ["type"] = "noul", ["instructions"] = n.Instructions };
                    break;
                default:
                    throw new ArgumentException($"Unsupported question type {q.GetType().Name}", nameof(questions));
            }
        }
        var root = new JsonObject
        {
            ["model"] = Model,
            ["state"] = JsonSerializer.SerializeToNode(state),
            ["questions"] = qs,
        };
        return root.ToJsonString();
    }

    JevResponse Parse(string text)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement; }
        catch (JsonException ex) { throw new InvalidOperationException($"{Provider} returned invalid JSON: {text}", ex); }
        root = Unwrap(root, text);

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var answers = new Dictionary<string, object>();
        if (root.TryGetProperty("answers", out var ans))
        {
            foreach (var p in ans.EnumerateObject())
            {
                var type = p.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "choice":
                        var probs = new Dictionary<string, double>();
                        if (p.Value.TryGetProperty("probabilities", out var pr))
                            foreach (var kv in pr.EnumerateObject()) probs[kv.Name] = kv.Value.GetDouble();
                        answers[p.Name] = new ChoiceAnswer(
                            p.Value.GetProperty("choice").GetString() ?? "",
                            probs,
                            p.Value.TryGetProperty("confidence", out var cf) ? cf.GetDouble() : 0);
                        break;
                    case "noul":
                        answers[p.Name] = new NoulAnswer(p.Value.GetProperty("noul").GetDouble());
                        break;
                    default:
                        throw new InvalidOperationException($"{Provider} answer '{p.Name}' has unknown type '{type}'");
                }
            }
        }
        var tokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
        return new JevResponse(model, answers, tokens);
    }

    /// <summary>
    /// Cloudflare's REST API wraps the model output as <c>{ result, success, errors, messages }</c>; Jev returns the
    /// output at the root. A body without <c>success</c> is returned as is.
    /// </summary>
    JsonElement Unwrap(JsonElement root, string text)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var ok)) return root;
        if (ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            return result;
        var errors = root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
            ? string.Join("; ", errs.EnumerateArray().Select(e =>
                e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()))
            : "";
        throw new InvalidOperationException($"{Provider} request failed: {(errors.Length > 0 ? errors : text)}");
    }
}
