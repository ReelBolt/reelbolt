namespace ReelBolt.Shared.Inference;

public enum DecisionKind { Choice, Score, Noul }

/// <summary>
/// Represents a decision question with options based on its kind.
/// </summary>
public sealed record DecisionQuestion(
    string Name,
    DecisionKind Kind,
    string Instructions,
    /// <summary>
    /// What the question offers, by kind. <b>The count is not validated</b> — neither this type nor
    /// <c>DecisionClientFactory</c> enforces a range or a maximum, so the following describes what
    /// callers actually pass, not something this code checks.
    /// <list type="bullet">
    /// <item>Choice: the offered ids/words.</item>
    /// <item>Score: an ordered rubric of at least two levels — the ReviewLoop gate's 0..10 (eleven
    /// levels) is the current example.</item>
    /// <item>Noul: ignored.</item>
    /// </list>
    /// </summary>
    IReadOnlyList<string> Options);

/// <summary>
/// Represents the state content for decision-making.
/// The Content is caller-composed and the caller owns the context budget.
/// </summary>
public sealed record DecisionState(string Content);

/// <summary>
/// Represents a decision answer with a flat shape, exactly one of Choice/Score/Noul non-null, matching Kind.
/// Noul has no Confidence.
/// </summary>
public sealed record DecisionAnswer(
    string Name,
    DecisionKind Kind,
    string? Choice,
    double? Score,
    double? Noul,
    double? Confidence,
    IReadOnlyDictionary<string, double> Probabilities);

public sealed record DecisionResult(
    string Model,
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    int InputTokens,
    int OutputTokens);

public interface IDecisionClient
{
    Task<DecisionResult> DecideAsync(
        DecisionState state,
        IReadOnlyList<DecisionQuestion> questions,
        CancellationToken ct);
}
