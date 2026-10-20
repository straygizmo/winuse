using WinUse.Core.Config;
using WinUse.Core.Jev;

namespace WinUse.Core.Grounding;

public sealed class Grounder(IJevClient jev, IObservationSource obs, IActionExecutor exec, WinUseConfig cfg)
{
    const string None = "none";

    sealed record Grounding(
        IReadOnlyList<(Candidate Candidate, double Probability)> Ranked,
        double Confidence,
        string Choice,
        double ChoiceProbability,
        string? Kind);

    public async Task<FindResult> FindAsync(string intent, string? window, int topK = 5)
    {
        var o = await obs.ObserveAsync(window).ConfigureAwait(false);
        if (!jev.IsConfigured) return Ungrounded(o, topK);
        try
        {
            var g = await GroundAsync(intent, o, false).ConfigureAwait(false);
            return new FindResult(g.Ranked.Take(topK).ToList(), g.Confidence, true);
        }
        catch (JevUnavailableException)
        {
            return Ungrounded(o, topK);
        }
    }

    public async Task<ActResult> ActAsync(string intent, string? window, ActKind kind, string? text, double? minConfidence)
    {
        var o = await obs.ObserveAsync(window).ConfigureAwait(false);
        if (!jev.IsConfigured)
            return Fail(Ungrounded(o, 5).Ranked, "model not configured");
        if (o.Candidates.Count == 0)
            return Fail([], "low confidence");

        Grounding g;
        try
        {
            g = await GroundAsync(intent, o, kind == ActKind.Auto).ConfigureAwait(false);
        }
        catch (JevUnavailableException)
        {
            return Fail(Ungrounded(o, 5).Ranked, "model unavailable");
        }

        var alternatives = g.Ranked.Take(5).ToList();
        var target = g.Choice == None ? null : o.Candidates.FirstOrDefault(c => c.Id == g.Choice);
        if (target is null || g.ChoiceProbability < cfg.MinProbability || g.Confidence < (minConfidence ?? cfg.MinConfidence))
            return new ActResult(false, target, g.ChoiceProbability, g.Confidence, null, alternatives, "low confidence");

        var resolved = kind == ActKind.Auto ? ParseKind(g.Kind) : kind;
        if (resolved is null)
            return new ActResult(false, target, g.ChoiceProbability, g.Confidence, null, alternatives, "low confidence");

        await exec.ExecuteAsync(target, resolved.Value, text).ConfigureAwait(false);

        bool? verified = null;
        try
        {
            // Give the UI time to react before re-observing, otherwise the verification sees the old state.
            if (cfg.SettleMs > 0) await Task.Delay(cfg.SettleMs).ConfigureAwait(false);
            var after = await obs.ObserveAsync(window).ConfigureAwait(false);
            var action = new ExecutedAction(resolved.Value.ToString().ToLowerInvariant(), target.Label, text);
            var p = await AskSatisfiedAsync(intent, after, action).ConfigureAwait(false);
            verified = p >= cfg.SatisfiedThreshold;
        }
        catch (JevUnavailableException)
        {
        }
        return new ActResult(true, target, g.ChoiceProbability, g.Confidence, verified, alternatives, null);
    }

    public async Task<CheckResult> CheckAsync(string question, string? window)
    {
        if (!jev.IsConfigured) return new CheckResult(0, false);
        var o = await obs.ObserveAsync(window).ConfigureAwait(false);
        try
        {
            return new CheckResult(await AskSatisfiedAsync(question, o).ConfigureAwait(false), true);
        }
        catch (JevUnavailableException)
        {
            return new CheckResult(0, false);
        }
    }

    static ActResult Fail(IReadOnlyList<(Candidate, double)> alternatives, string reason) =>
        new(false, null, 0, 0, null, alternatives, reason);

    static FindResult Ungrounded(Observation o, int topK) =>
        new(o.Candidates.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).Take(topK).Select(c => (c, 0.0)).ToList(), 0, false);

    static ActKind? ParseKind(string? k) => k switch
    {
        "click" => ActKind.Click,
        "type" => ActKind.Type,
        "select" => ActKind.Select,
        "toggle" => ActKind.Toggle,
        _ => null,
    };

    /// <summary>The action <c>act</c> just performed; it is not journaled yet when the result is verified.</summary>
    sealed record ExecutedAction(string Kind, string TargetLabel, string? Text)
    {
        public string Summary => Text is null ? $"act {Kind} '{TargetLabel}'" : $"act {Kind} '{TargetLabel}' text={Text}";
    }

    static Dictionary<string, object?> BuildState(string goal, Observation o, IReadOnlyList<Candidate> elements, ExecutedAction? executed = null)
    {
        var state = new Dictionary<string, object?>
        {
            ["goal"] = goal,
            ["window"] = new { title = o.Window.Title, process = o.Window.ProcessName },
            ["elements"] = elements.Select(c => new
            {
                id = c.Id,
                role = c.Role,
                label = c.Label,
                value = c.Value,
                enabled = c.Enabled,
                focused = c.Focused,
                editable = c.Editable,
                @checked = c.Checked,
                region = c.Region,
            }).ToList(),
            ["recent_actions"] = executed is null ? o.RecentActions : [.. o.RecentActions, executed.Summary],
        };
        if (executed is not null)
            state["executed_action"] = new { kind = executed.Kind, target_label = executed.TargetLabel, text = executed.Text };
        return state;
    }

    static ChoiceQuestion TargetQuestion(IReadOnlyList<Candidate> cands)
    {
        var criteria = new Dictionary<string, string?>();
        foreach (var c in cands) criteria[c.Id] = $"{c.Role} '{c.Label}' ({c.Region})";
        criteria[None] = null;
        return new ChoiceQuestion("Which element should be acted on to accomplish `goal`? Choose none if no element fits.", criteria);
    }

    static ChoiceQuestion KindQuestion() => new(
        "Which kind of action does `goal` require on the chosen element?",
        new Dictionary<string, string?> { ["click"] = null, ["type"] = null, ["select"] = null, ["toggle"] = null, [None] = null });

    async Task<double> AskSatisfiedAsync(string goal, Observation o, ExecutedAction? executed = null)
    {
        var qs = new Dictionary<string, JevQuestion>
        {
            ["satisfied"] = new NoulQuestion(executed is null
                ? "Given `goal` and the current `elements`, has the goal been accomplished?"
                : "Given `goal`, `executed_action` and the current `elements`, is the goal now accomplished?"),
        };
        var r = await jev.AskAsync(BuildState(goal, o, o.Candidates, executed), qs).ConfigureAwait(false);
        return ((NoulAnswer)r.Answers["satisfied"]).Probability;
    }

    async Task<Grounding> GroundAsync(string intent, Observation o, bool askKind)
    {
        var all = o.Candidates;
        var pool = all;
        var groups = CandidateBuilder.SplitForChoice(all);
        if (groups.Count > 1)
        {
            var keys = new List<string>();
            var criteria = new Dictionary<string, string?>();
            for (var i = 0; i < groups.Count; i++)
            {
                var name = groups[i][0].Container ?? "window";
                var key = criteria.ContainsKey(name) ? $"{name} ({i + 1})" : name;
                keys.Add(key);
                criteria[key] = string.Join(", ", groups[i].Take(5).Select(c => c.Label));
            }
            var area = new Dictionary<string, JevQuestion>
            {
                ["area"] = new ChoiceQuestion("Which area of the window contains the element needed for `goal`?", criteria),
            };
            var ar = await jev.AskAsync(BuildState(intent, o, []), area).ConfigureAwait(false);
            var chosen = ((ChoiceAnswer)ar.Answers["area"]).Choice;
            var idx = keys.IndexOf(chosen);
            pool = groups[idx < 0 ? 0 : idx];
        }

        var questions = new Dictionary<string, JevQuestion> { ["target"] = TargetQuestion(pool) };
        if (askKind) questions["kind"] = KindQuestion();
        var resp = await jev.AskAsync(BuildState(intent, o, pool), questions).ConfigureAwait(false);
        var ans = (ChoiceAnswer)resp.Answers["target"];
        var byId = pool.ToDictionary(c => c.Id);
        var ranked = ans.Probabilities
            .Where(kv => kv.Key != None && byId.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (byId[kv.Key], kv.Value))
            .ToList();
        var kind = askKind && resp.Answers.TryGetValue("kind", out var k) ? ((ChoiceAnswer)k).Choice : null;
        var p = ans.Probabilities.TryGetValue(ans.Choice, out var pv) ? pv : 0;
        return new Grounding(ranked, ans.Confidence, ans.Choice, p, kind);
    }
}
