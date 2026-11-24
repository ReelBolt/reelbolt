namespace ReelBolt.Shared.Inference;

/// <summary>
/// The hard bounds of one rerank call (docs/design/workflow-apps.md 7.6). They are enforced by the client itself,
/// so no caller can send more text to a vendor than this, whatever it passes in.
/// </summary>
public static class RerankLimits
{
    /// <summary>Most candidate texts one call scores. One query plus up to this many texts is ONE billed unit.</summary>
    public const int MaxCandidates = 20;

    /// <summary>Most characters of one candidate text that leave the host; the rest is cut off.</summary>
    public const int MaxCandidateChars = 600;

    /// <summary>Most characters of the query that leave the host (privacy truncation of the user's message).</summary>
    public const int MaxQueryChars = 500;

    /// <summary>Default wall-clock bound of one rerank call.</summary>
    public const int DefaultTimeoutSeconds = 3;
}

/// <summary>One scored candidate: its position in the list the caller sent, and the relevance score (higher is better).</summary>
public readonly record struct RerankScore(int Index, double Score);

/// <summary>
/// Scores candidate texts against a query. The contract is fail-soft: the answer is <c>null</c> for every
/// failure (timeout, transport error, non-success status, malformed body, scores that do not cover every
/// candidate), because the caller always has a free deterministic order to fall back to. The one exception is a
/// cancellation requested by the CALLER, which is re-thrown.
/// </summary>
public interface IRerankClient
{
    /// <summary>
    /// Scores <paramref name="texts"/> (at most <see cref="RerankLimits.MaxCandidates"/>, each cut to
    /// <see cref="RerankLimits.MaxCandidateChars"/>) against <paramref name="query"/> (cut to
    /// <see cref="RerankLimits.MaxQueryChars"/>). Returns exactly one score per sent text, best first, or
    /// <c>null</c>.
    /// </summary>
    Task<IReadOnlyList<RerankScore>?> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken ct);

    /// <summary>
    /// True when the last call received a 2xx response body (the vendor was reached and may have billed), even if the
    /// scores were unusable. Timeouts, transport errors and non-success statuses leave it false.
    /// </summary>
    bool ReachedVendor => false;
}

/// <summary>Builds a rerank client for a resolved provider, so a test supplies a fake without an HTTP stack.</summary>
public interface IRerankClientFactory
{
    IRerankClient Create(ResolvedRerankProvider provider, int timeoutSeconds);
}
