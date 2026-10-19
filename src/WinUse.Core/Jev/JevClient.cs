using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WinUse.Core.Config;

namespace WinUse.Core.Jev;

public sealed class JevClient : IJevClient
{
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

    public bool IsConfigured => _cfg.JevApiKey is not null;

    public async Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct = default)
    {
        if (_cfg.JevApiKey is null) throw new InvalidOperationException("Jev is not configured");

        var body = BuildBody(state, questions);
        var url = $"{_cfg.JevBaseUrl.TrimEnd('/')}/v1/systemone";
        _log.LogDebug("Jev request to {Url} (Authorization: Bearer ***): {Body}", url, body);

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
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.JevApiKey);

                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                _log.LogDebug("Jev response {Status}: {Body}", (int)resp.StatusCode, text);

                var code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode) return Parse(text);
                if (code is 429 or 529)
                {
                    last = new HttpRequestException($"Jev returned {code}");
                    _log.LogWarning("Jev returned {Status} (attempt {Attempt})", code, attempt + 1);
                    continue;
                }
                if (code is >= 400 and < 500)
                    throw new InvalidOperationException($"Jev request failed with {code} {resp.ReasonPhrase}: {text}");
                // other 5xx: treat as transient
                last = new HttpRequestException($"Jev returned {code}: {text}");
                _log.LogWarning("Jev returned {Status} (attempt {Attempt})", code, attempt + 1);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                _log.LogWarning(ex, "Jev request failed (attempt {Attempt})", attempt + 1);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                _log.LogWarning("Jev request timed out (attempt {Attempt})", attempt + 1);
            }
        }
        throw new JevUnavailableException($"Jev unavailable after {_backoff.Length + 1} attempts: {last?.Message}", last);
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
            ["model"] = _cfg.JevModel,
            ["state"] = JsonSerializer.SerializeToNode(state),
            ["questions"] = qs,
        };
        return root.ToJsonString();
    }

    static JevResponse Parse(string text)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement; }
        catch (JsonException ex) { throw new InvalidOperationException($"Jev returned invalid JSON: {text}", ex); }

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
                        throw new InvalidOperationException($"Jev answer '{p.Name}' has unknown type '{type}'");
                }
            }
        }
        var tokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
        return new JevResponse(model, answers, tokens);
    }
}
