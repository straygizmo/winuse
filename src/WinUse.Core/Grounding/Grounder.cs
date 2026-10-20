using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Config;
using WinUse.Core.Grounding.Visual;
using WinUse.Core.Jev;

namespace WinUse.Core.Grounding;

public sealed class Grounder(IJevClient jev, IObservationSource obs, IActionExecutor exec, WinUseConfig cfg,
    IWindowCapture? capture = null, ILogger? log = null)
{
    const string None = "none";
    /// <summary>The id the state and instructions use for the attached window screenshot.</summary>
    public const string ScreenshotId = "screenshot";
    readonly ILogger _log = log ?? NullLogger.Instance;

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
            if (o.Candidates.Count == 0)
            {
                var frame = await ScreenshotAsync(o).ConfigureAwait(false);
                if (frame is null) return Ungrounded(o, topK);
                var p = await LocateAsync(intent, o, frame, cfg.MinConfidence).ConfigureAwait(false);
                if (p is not { } vp) return new FindResult([], 0, true);
                return new FindResult([(PointCandidate(vp.X, vp.Y, o), vp.Probability)], vp.Confidence, true, (vp.X, vp.Y), o.Window);
            }
            var g = await GroundAsync(intent, o, false, await ScreenshotAsync(o).ConfigureAwait(false)).ConfigureAwait(false);
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
            return await ActVisualAsync(intent, o, kind, text, minConfidence ?? cfg.MinConfidence).ConfigureAwait(false);

        Grounding g;
        try
        {
            g = await GroundAsync(intent, o, kind == ActKind.Auto, await ScreenshotAsync(o).ConfigureAwait(false)).ConfigureAwait(false);
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

        var action = new ExecutedAction(resolved.Value.ToString().ToLowerInvariant(), target.Label, text);
        var verified = await VerifyAsync(intent, window, action).ConfigureAwait(false);
        return new ActResult(true, target, g.ChoiceProbability, g.Confidence, verified, alternatives, null);
    }

    /// <summary>
    /// <c>act</c> on a window without candidates (VirtualBox, emulators, VNC ...): locate the target on a screenshot,
    /// act at that screen point, then verify with a fresh screenshot. <c>auto</c> is a type when text is given, else a click.
    /// </summary>
    async Task<ActResult> ActVisualAsync(string intent, Observation o, ActKind kind, string? text, double minConfidence)
    {
        if (cfg.Screenshots == ScreenshotPolicy.Off) return Fail([], "no elements (screenshots off)");
        var frame = await ScreenshotAsync(o).ConfigureAwait(false);
        if (frame is null) return Fail([], "no elements");

        VisualPoint? located;
        try
        {
            located = await LocateAsync(intent, o, frame, minConfidence).ConfigureAwait(false);
        }
        catch (JevUnavailableException)
        {
            return Fail([], "model unavailable");
        }
        if (located is not { } p) return Fail([], "low confidence");

        var resolved = kind == ActKind.Auto ? (text is null ? ActKind.Click : ActKind.Type) : kind;
        await exec.ExecuteAtAsync(p.X, p.Y, resolved, text).ConfigureAwait(false);

        var target = PointCandidate(p.X, p.Y, o);
        var action = new ExecutedAction(resolved.ToString().ToLowerInvariant(), target.Label, text);
        var verified = await VerifyAsync(intent, null, action).ConfigureAwait(false);
        return new ActResult(true, target, p.Probability, p.Confidence, verified, [], null, (p.X, p.Y), o.Window);
    }

    /// <summary>Re-observes after the settle wait and asks whether the goal is now accomplished; null when the model is unavailable.</summary>
    async Task<bool?> VerifyAsync(string intent, string? window, ExecutedAction action)
    {
        try
        {
            // Give the UI time to react before re-observing, otherwise the verification sees the old state.
            if (cfg.SettleMs > 0) await Task.Delay(cfg.SettleMs).ConfigureAwait(false);
            var after = await obs.ObserveAsync(window).ConfigureAwait(false);
            var p = await AskSatisfiedAsync(intent, after, action, await ScreenshotAsync(after).ConfigureAwait(false)).ConfigureAwait(false);
            return p >= cfg.SatisfiedThreshold;
        }
        catch (JevUnavailableException)
        {
            return null;
        }
    }

    public async Task<CheckResult> CheckAsync(string question, string? window)
    {
        if (!jev.IsConfigured) return new CheckResult(0, false);
        var o = await obs.ObserveAsync(window).ConfigureAwait(false);
        try
        {
            var frame = await ScreenshotAsync(o).ConfigureAwait(false);
            return new CheckResult(await AskSatisfiedAsync(question, o, null, frame).ConfigureAwait(false), true);
        }
        catch (JevUnavailableException)
        {
            return new CheckResult(0, false);
        }
    }

    // ---- screenshots ----------------------------------------------------------------------------

    /// <summary>The window screenshot to attach under <see cref="WinUseConfig.Screenshots"/>, or null.</summary>
    async Task<VisualFrame?> ScreenshotAsync(Observation o)
    {
        var wanted = cfg.Screenshots switch
        {
            ScreenshotPolicy.Always => true,
            ScreenshotPolicy.Auto => o.Candidates.Count == 0,
            _ => false,
        };
        if (!wanted || capture is null) return null;
        try
        {
            var f = await capture.CaptureAsync(o.Window).ConfigureAwait(false);
            return f is null ? null : GridLocator.Fit(f);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "window capture for visual grounding failed");
            return null;
        }
    }

    static ImageAttachment[] Attach(VisualFrame f) => [new ImageAttachment(ScreenshotId, f.Png)];

    /// <summary>Locates the target of <paramref name="intent"/> on the screenshot with the configured visual locator.</summary>
    async Task<VisualPoint?> LocateAsync(string intent, Observation o, VisualFrame frame, double minConfidence)
    {
        var useBox = cfg.VisualLocator == VisualLocatorKind.Box;
        if (useBox && jev.Provider != DecisionProvider.Jev)
        {
            _log.LogWarning("WINUSE_VISUAL_LOCATOR=box needs Jev; using the grid locator with {Provider}", jev.Provider);
            useBox = false;
        }
        if (useBox)
        {
            var qs = new Dictionary<string, JevQuestion>
            {
                ["target"] = new BoxQuestion($"The element that must be acted on to accomplish `goal`, in `{ScreenshotId}`."),
            };
            var r = await jev.AskAsync(BuildState(intent, o, [], null, ScreenshotId), qs, Attach(frame)).ConfigureAwait(false);
            var box = (BoxAnswer)r.Answers["target"];
            if (box.Confidence < minConfidence) return null;
            var (x, y) = GridLocator.BoxCentre(frame, box);
            return new VisualPoint(x, y, box.Confidence, box.Confidence);
        }
        return await GridLocator.LocateAsync(frame, async (stage, grid) =>
        {
            var qs = new Dictionary<string, JevQuestion>
            {
                ["cell"] = new ChoiceQuestion(
                    $"Which grid cell of `{ScreenshotId}` contains the element that must be acted on to accomplish `goal`? " +
                    "Cell labels are drawn in the top-left corner of each cell. Choose none if no cell fits.",
                    GridLocator.Criteria(grid)),
            };
            var r = await jev.AskAsync(BuildState(intent, o, [], null, ScreenshotId), qs, Attach(stage)).ConfigureAwait(false);
            return (ChoiceAnswer)r.Answers["cell"];
        }, cfg.MinProbability, minConfidence).ConfigureAwait(false);
    }

    static Candidate PointCandidate(int x, int y, Observation o) =>
        new("point", "point", $"{x},{y}", null, true, false, false, null, CandidateBuilder.RegionOfPoint(x, y, o.Window.Rect), null);

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

    static Dictionary<string, object?> BuildState(string goal, Observation o, IReadOnlyList<Candidate> elements,
        ExecutedAction? executed = null, string? screenshotId = null)
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
        // Jev resolves the asset reference; Clef sees a plain string next to the implicitly attached image.
        if (screenshotId is not null) state[ScreenshotId] = screenshotId;
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

    async Task<double> AskSatisfiedAsync(string goal, Observation o, ExecutedAction? executed = null, VisualFrame? image = null)
    {
        var evidence = image is null ? "the current `elements`" : $"the current `elements` and `{ScreenshotId}`";
        var qs = new Dictionary<string, JevQuestion>
        {
            ["satisfied"] = new NoulQuestion(executed is null
                ? $"Given `goal` and {evidence}, has the goal been accomplished?"
                : $"Given `goal`, `executed_action` and {evidence}, is the goal now accomplished?"),
        };
        var r = await jev.AskAsync(BuildState(goal, o, o.Candidates, executed, image is null ? null : ScreenshotId), qs,
            image is null ? null : Attach(image)).ConfigureAwait(false);
        return ((NoulAnswer)r.Answers["satisfied"]).Probability;
    }

    async Task<Grounding> GroundAsync(string intent, Observation o, bool askKind, VisualFrame? image = null)
    {
        var images = image is null ? null : Attach(image);
        var shot = image is null ? null : ScreenshotId;
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
            var ar = await jev.AskAsync(BuildState(intent, o, [], null, shot), area, images).ConfigureAwait(false);
            var chosen = ((ChoiceAnswer)ar.Answers["area"]).Choice;
            var idx = keys.IndexOf(chosen);
            pool = groups[idx < 0 ? 0 : idx];
        }

        var questions = new Dictionary<string, JevQuestion> { ["target"] = TargetQuestion(pool) };
        if (askKind) questions["kind"] = KindQuestion();
        var resp = await jev.AskAsync(BuildState(intent, o, pool, null, shot), questions, images).ConfigureAwait(false);
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
