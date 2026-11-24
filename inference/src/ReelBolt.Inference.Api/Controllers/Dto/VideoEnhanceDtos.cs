namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// The body of <c>POST .../media/{mediaRef}/enhance</c> and <c>.../enhance/estimate</c>: the step config's fields minus the source,
/// the provider and the registration choice (the route decides those). The consent is never in the body: only the recorded
/// project consent turns it on.
/// </summary>
public sealed record VideoEnhanceRequestBody(
    string? Op,
    string? Engine,
    int? UpscaleFactor = null,
    int? TargetHeight = null,
    int? FrameMultiplier = null,
    string? DenoiseStrength = null,
    bool? KeepGrain = null,
    int? ShutterAngle = null,
    string? TargetAspect = null,
    double? MarginLeft = null,
    double? MarginRight = null,
    double? MarginTop = null,
    double? MarginBottom = null,
    string? Prompt = null);

/// <summary>202 answer of a queued enhancement. No storage key, bucket or URL.</summary>
public sealed record VideoEnhanceQueuedResponse(Guid ExecutionId, string Op, string Engine, bool Hosted, string State, double Credits);

/// <summary>
/// The estimate: what the run would hold and what it would produce. <c>UnavailableCode</c> is a machine code (PLAN_NOT_ENTITLED,
/// SOURCE_TOO_LONG, SOURCE_NOT_MEASURED, ENHANCE_UNAVAILABLE, CONSENT_REQUIRED, RESOLUTION_ABOVE_PLAN) so the web never parses the
/// English <c>UnavailableReason</c>. <c>MaxSeconds</c> is the source-length cap that applies to this operation (hosted or local, as
/// configured), so the UI can name it. The size, byte and time figures are estimates.
/// </summary>
public sealed record VideoEnhanceEstimate(
    bool BillingEnabled,
    bool Hosted,
    double Credits,
    int Seconds,
    double BalanceCredits,
    bool Affordable,
    string? UnavailableReason,
    string? UnavailableCode,
    bool Consented,
    int MaxOutputHeight,
    int OutputWidth,
    int OutputHeight,
    double OutputFps,
    long EstimatedBytes,
    int EstimatedProcessingSeconds,
    int MaxSeconds);

/// <summary>The status poll. <c>State</c> is queued, running, ready, failed or cancelled. No storage key.</summary>
public sealed record VideoEnhanceStatusResponse(
    Guid ExecutionId,
    string State,
    string? Op,
    string? Engine,
    string? Method,
    double? DurationSec,
    Guid? OutputFileId,
    string SourceMediaRef,
    int? OutputWidth,
    int? OutputHeight,
    double? OutputFps,
    long? SizeBytes,
    IReadOnlyList<string> Warnings,
    string? Message,
    string? FailureCode,
    int? Progress);

/// <summary>Whether a project may upload a source video to a hosted AI model to enhance it.</summary>
public sealed record VideoEnhanceConsentResponse(bool Consented, DateTime? ConsentedAt);
