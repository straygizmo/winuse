namespace WinUse.Core.Jev;

public abstract record JevQuestion(string Instructions);

public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string?> Criteria) : JevQuestion(Instructions);

public sealed record NoulQuestion(string Instructions) : JevQuestion(Instructions);

public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);

public sealed record NoulAnswer(double Probability);

/// <summary>Answers values are either <see cref="ChoiceAnswer"/> or <see cref="NoulAnswer"/>.</summary>
public sealed record JevResponse(string Model, IReadOnlyDictionary<string, object> Answers, int InputTokens);

public interface IJevClient
{
    bool IsConfigured { get; }
    Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken ct = default);
}

public sealed class JevUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
