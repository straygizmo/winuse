namespace WinUse.Core.Jev;

public abstract record JevQuestion(string Instructions);

public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string?> Criteria) : JevQuestion(Instructions);

public sealed record NoulQuestion(string Instructions) : JevQuestion(Instructions);

/// <summary>Jev 1.14 only: locate one target on an image named in the instructions (e.g. "the OK button in `screenshot`").</summary>
public sealed record BoxQuestion(string Instructions) : JevQuestion(Instructions);

/// <summary>A PNG sent with a request. <see cref="Id"/> is how Jev's <c>state</c> / instructions refer to it (Clef ignores it).</summary>
public sealed record ImageAttachment(string Id, byte[] Png);

public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);

public sealed record NoulAnswer(double Probability);

/// <summary>Edges as fractions (0..1) of the image's width / height from its top-left corner.</summary>
public sealed record BoxAnswer(double Top, double Bottom, double Left, double Right, double Confidence);

/// <summary>Answers values are <see cref="ChoiceAnswer"/>, <see cref="NoulAnswer"/> or <see cref="BoxAnswer"/>.</summary>
public sealed record JevResponse(string Model, IReadOnlyDictionary<string, object> Answers, int InputTokens);

public interface IJevClient
{
    bool IsConfigured { get; }
    Task<JevResponse> AskAsync(object state, IReadOnlyDictionary<string, JevQuestion> questions,
        IReadOnlyList<ImageAttachment>? images = null, CancellationToken ct = default);
}

public sealed class JevUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
