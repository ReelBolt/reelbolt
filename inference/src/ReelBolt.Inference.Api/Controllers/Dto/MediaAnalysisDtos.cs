using System.Text.Json;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>R09.1: <c>POST …/analysis</c> and <c>…/analysis/estimate</c> body.</summary>
public sealed record MediaAnalysisRequestBody(IReadOnlyList<string>? Kinds, string? Language);

/// <summary>
/// One kind's state. <see cref="State"/> is none, queued, running, ready or failed; <see cref="Reason"/> is plain language.
/// <see cref="Outdated"/> is true on a ready <c>events</c> result made by an earlier version of the detector: it is still
/// served, and asking for the kind again makes a fresh one that replaces it. Left out of the JSON otherwise.
/// </summary>
public sealed record MediaAnalysisKindState(
    string State,
    string? Reason,
    Guid? RequestId,
    DateTime? UpdatedAt,
    JsonElement? Summary,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? Outdated = null);

public sealed record MediaAnalysisStartedResponse(Guid RequestId, string MediaRef, IReadOnlyDictionary<string, MediaAnalysisKindState> States);

public sealed record MediaAnalysisStatesResponse(string MediaRef, IReadOnlyDictionary<string, MediaAnalysisKindState> Kinds);

/// <summary>Transcript cost before it is asked for. Free kinds cost 0; billing off answers <c>BillingEnabled: false</c>.</summary>
public sealed record MediaAnalysisEstimateResponse(
    bool BillingEnabled,
    double? Credits,
    double? Seconds,
    double? BalanceCredits,
    bool Affordable,
    bool Cached,
    string? UnavailableReason);
