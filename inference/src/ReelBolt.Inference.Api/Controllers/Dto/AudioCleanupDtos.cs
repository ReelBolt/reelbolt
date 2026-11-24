namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Body of <c>POST .../media/{mediaRef}/cleanup</c> and <c>.../cleanup/estimate</c>. <c>Target</c> is an
/// <c>AudioCleanupTarget</c> name (VoiceBasic, Hum, Wind, Hiss, Echo, IsolateVoice, RemoveMusic). A hosted target needs
/// <c>AllowSourceMediaEgress: true</c> (the consent dialog sets it); a local target ignores it.
/// </summary>
public sealed record AudioCleanupRequestBody(string? Target, bool AllowSourceMediaEgress = false, string? OutputKind = null);

/// <summary>202 answer of the cleanup start: the run to poll. Never a storage key.</summary>
public sealed record AudioCleanupStartedResponse(Guid ExecutionId, string Target, bool Hosted, string State, double? Credits);

/// <summary>What a cleanup would cost. Local targets are free (<c>Credits: 0</c>); billing off answers <c>BillingEnabled: false</c>.</summary>
public sealed record AudioCleanupEstimateResponse(
    bool BillingEnabled,
    double? Credits,
    double? Seconds,
    double? BalanceCredits,
    bool Affordable,
    bool Cached,
    string? UnavailableReason,
    bool Hosted);

/// <summary>The poll: state is queued, running, ready, failed or cancelled. <c>OutputFileId</c> is the registered project file.</summary>
public sealed record AudioCleanupStatusResponse(
    Guid ExecutionId,
    string State,
    string? Target,
    string? Method,
    bool? Applied,
    double? DurationSec,
    Guid? OutputFileId,
    string? Message,
    string? FailureCode);
