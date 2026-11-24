using WinUse.Core.Grounding.Visual;
using WinUse.Core.Windows;

namespace WinUse.Core.Grounding;

public sealed record Candidate(
    string Id,
    string Role,
    string Label,
    string? Value,
    bool Enabled,
    bool Focused,
    bool Editable,
    bool? Checked,
    string Region,
    string? Container);

public enum ActKind { Auto, Click, Type, Select, Toggle }

/// <summary>
/// <see cref="NoElementsReason"/> says why a window that should have elements (a Java window) has none;
/// <see cref="DepthLimitReached"/> that the walk stopped at the depth limit above elements that have children.
/// </summary>
public sealed record Observation(WindowInfo Window, IReadOnlyList<Candidate> Candidates, IReadOnlyList<string> RecentActions,
    string? NoElementsReason = null, bool DepthLimitReached = false);

/// <summary>
/// What part of the window an observation reads: the subtree of <see cref="ScopeRef"/> (an element ref) instead of the
/// whole window, walked to <see cref="MaxDepth"/> levels (null: <see cref="DefaultMaxDepth"/>).
/// </summary>
public sealed record ObserveScope(string? ScopeRef = null, int? MaxDepth = null)
{
    public const int DefaultMaxDepth = 12;
    public int Depth => MaxDepth ?? DefaultMaxDepth;
}

/// <summary>
/// <see cref="Point"/> is set when the target was located on a screenshot (no candidates). When <see cref="Grounded"/> is
/// false, <see cref="Reason"/> says why no decision model answered and the probabilities are text-match scores
/// (<see cref="LexicalRanker"/>) instead of model probabilities; candidates sharing no text with the intent are left out.
/// <see cref="Considered"/> is the number of elements examined.
/// </summary>
public sealed record FindResult(IReadOnlyList<(Candidate Candidate, double Probability)> Ranked, double Confidence, bool Grounded,
    (int X, int Y)? Point = null, WindowInfo? Window = null, string? Reason = null, int Considered = 0, bool DepthLimitReached = false);

/// <summary><see cref="Detail"/> explains a <see cref="Reason"/> of "model not configured" / "model unavailable".</summary>
public sealed record ActResult(
    bool Executed,
    Candidate? Target,
    double Probability,
    double Confidence,
    bool? Verified,
    IReadOnlyList<(Candidate, double)> Alternatives,
    string? Reason,
    (int X, int Y)? Point = null,
    WindowInfo? Window = null,
    string? Detail = null);

/// <summary><see cref="Reason"/> says why the model could not be asked when <see cref="Available"/> is false.</summary>
public sealed record CheckResult(double Probability, bool Available, string? Reason = null);

public interface IObservationSource
{
    Task<Observation> ObserveAsync(string? window, ObserveScope? scope = null);
}

public interface IActionExecutor
{
    Task ExecuteAsync(Candidate target, ActKind kind, string? text);

    /// <summary>Acts at a screen point: click, or click then type <paramref name="text"/> for <see cref="ActKind.Type"/>.</summary>
    Task ExecuteAtAsync(int x, int y, ActKind kind, string? text);
}

/// <summary>Captures the observed window for visual grounding; null when it cannot be captured.</summary>
public interface IWindowCapture
{
    Task<VisualFrame?> CaptureAsync(WindowInfo window);
}
