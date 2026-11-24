namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// The answer of <c>objects/estimate</c>: the earlier DTO's fields plus <see cref="UpperBound"/>, true when the video's length is
/// unknown and the figures price the longest video allowed (so the real charge is at most this).
/// </summary>
public sealed record ObjectAnalysisEstimate(
    bool BillingEnabled,
    double Credits,
    int Frames,
    int Seconds,
    double BalanceCredits,
    bool Affordable,
    bool Cached,
    bool Consented,
    string? UnavailableReason,
    bool UpperBound);
