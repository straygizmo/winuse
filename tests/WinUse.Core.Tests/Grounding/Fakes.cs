using WinUse.Core.Grounding;
using WinUse.Core.Jev;
using WinUse.Core.Uia;
using WinUse.Core.Web;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Grounding;

sealed class FakeJev(
    bool configured = true,
    (string choice, double p, double conf)? choice = null,
    double noul = 0.5,
    bool throwUnavailable = false,
    string kind = "click") : IJevClient
{
    public bool IsConfigured => configured;
    public object? LastState { get; private set; }
    public List<IReadOnlyDictionary<string, JevQuestion>> Calls { get; } = [];

    public List<IReadOnlyList<ImageAttachment>?> Images { get; } = [];

    public Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        IReadOnlyList<ImageAttachment>? images = null, CancellationToken ct = default)
    {
        if (throwUnavailable) throw new JevUnavailableException("down");
        LastState = state;
        Calls.Add(questions);
        Images.Add(images);
        var answers = new Dictionary<string, object>();
        foreach (var (key, q) in questions)
        {
            switch (q)
            {
                case NoulQuestion:
                    answers[key] = new NoulAnswer(noul);
                    break;
                case ChoiceQuestion when key == "kind":
                    answers[key] = new ChoiceAnswer(kind, new Dictionary<string, double> { [kind] = 0.9 }, 0.9);
                    break;
                case ChoiceQuestion cq when key == "area":
                    var first = cq.Criteria.Keys.First();
                    answers[key] = new ChoiceAnswer(first, new Dictionary<string, double> { [first] = 0.9 }, 0.9);
                    break;
                case ChoiceQuestion cq:
                    var (c, p, conf) = choice ?? ("none", 1.0, 1.0);
                    var probs = new Dictionary<string, double>();
                    var others = cq.Criteria.Keys.Where(k => k != c).ToList();
                    foreach (var o in others) probs[o] = (1 - p) / others.Count;
                    probs[c] = p;
                    answers[key] = new ChoiceAnswer(c, probs, conf);
                    break;
            }
        }
        return Task.FromResult(new JevResponse("fake", answers, 0));
    }
}

sealed class FakeObservation(params Candidate[] cands) : IObservationSource
{
    public Task<Observation> ObserveAsync(string? window) =>
        Task.FromResult(new Observation(
            new WindowInfo(0, "T", "C", "p", 1, new Rect(0, 0, 900, 900), WindowState.Normal),
            cands, ["did something"]));
}

sealed class FakeExecutor : IActionExecutor
{
    public Candidate? Last { get; private set; }
    public ActKind? LastKind { get; private set; }
    public string? LastText { get; private set; }

    public Task ExecuteAsync(Candidate target, ActKind kind, string? text)
    {
        Last = target; LastKind = kind; LastText = text;
        return Task.CompletedTask;
    }
}

static class G
{
    static int _dom;

    public static Candidate Cand(string id, string label = "x", string? container = null, string role = "Button") =>
        new(id, role, label, null, true, false, false, null, "middle-center", container);

    public static UiNode Node(string controlType, string? name, Rect rect, string? automationId = null, string? value = null, params UiNode[] children) =>
        new("u?", controlType, name, automationId, value, true, false, null, false, rect, 1, children);

    public static UiNode Button(string name, Rect rect) => Node("Button", name, rect);

    public static UiNode Tree(params UiNode[] children)
    {
        var n = 0;
        UiNode Number(UiNode x) => x with { Ref = $"u{++n}", Children = x.Children.Select(Number).ToList() };
        return Number(new UiNode("u0", "Window", "Main", null, null, true, false, null, false, new Rect(0, 0, 900, 900), 0, children));
    }

    public static DomNode Dom(string role, string name, string selector, Rect rect) =>
        new($"w{++_dom}", role, name, role, null, true, false, null, rect, selector);
}
