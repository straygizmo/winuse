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

public sealed record Observation(WindowInfo Window, IReadOnlyList<Candidate> Candidates, IReadOnlyList<string> RecentActions);

public sealed record FindResult(IReadOnlyList<(Candidate Candidate, double Probability)> Ranked, double Confidence, bool Grounded);

public sealed record ActResult(
    bool Executed,
    Candidate? Target,
    double Probability,
    double Confidence,
    bool? Verified,
    IReadOnlyList<(Candidate, double)> Alternatives,
    string? Reason);

public sealed record CheckResult(double Probability, bool Available);

public interface IObservationSource
{
    Task<Observation> ObserveAsync(string? window);
}

public interface IActionExecutor
{
    Task ExecuteAsync(Candidate target, ActKind kind, string? text);
}
