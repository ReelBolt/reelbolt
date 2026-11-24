using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One hosted video upscale. <see cref="SourceUrl"/> is a presigned GET URL of the source clip (https, no credentials);
/// its lifetime is the CALLER's job. It is sent to the vendor and never logged or put in an error message.
/// </summary>
/// <param name="SourceUrl">The presigned GET URL of the source clip.</param>
/// <param name="SourceWidth">The measured source width in pixels.</param>
/// <param name="SourceHeight">The measured source height in pixels.</param>
/// <param name="SourceFps">The measured source frame rate (also what bytedance is asked to keep, rounded and clamped to 24..120).</param>
/// <param name="SourceDurationSec">The measured source duration in seconds (capped per request).</param>
/// <param name="SourceFrames">The source's frame count.</param>
/// <param name="Factor">The scale factor, computed by the caller; within the endpoint's range (Topaz 1..4, bytedance 1.1..4).</param>
/// <param name="Model">The fal endpoint id; blank = the catalogue default.</param>
/// <param name="CorrelationId">The caller's idempotency/correlation id. fal's queue has no idempotency key, so it is carried for the caller's ledger and logs only; it is never sent.</param>
public sealed record VideoUpscaleRequest(
    string SourceUrl,
    int SourceWidth,
    int SourceHeight,
    double SourceFps,
    double SourceDurationSec,
    long SourceFrames,
    double Factor,
    string Model = "",
    string CorrelationId = "")
{
    /// <summary>Never prints the presigned URL (the generated record ToString would).</summary>
    public override string ToString() =>
        $"upscale {SourceWidth}x{SourceHeight} {SourceDurationSec:0.###}s x{Factor:0.##} with '{(string.IsNullOrWhiteSpace(Model) ? "default" : Model)}'";
}

/// <summary>One hosted frame interpolation (same source fields as <see cref="VideoUpscaleRequest"/>).</summary>
/// <param name="Multiplier">The frame multiplier: 2 or 4 on the hosted path (8x is local only).</param>
public sealed record FrameInterpolationRequest(
    string SourceUrl,
    int SourceWidth,
    int SourceHeight,
    double SourceFps,
    double SourceDurationSec,
    long SourceFrames,
    int Multiplier,
    string Model = "",
    string CorrelationId = "")
{
    /// <summary>Never prints the presigned URL (the generated record ToString would).</summary>
    public override string ToString() =>
        $"interpolate {SourceWidth}x{SourceHeight} {SourceDurationSec:0.###}s x{Multiplier} with '{(string.IsNullOrWhiteSpace(Model) ? "default" : Model)}'";
}

/// <summary>
/// One hosted outpaint (canvas extension). Exactly one of <see cref="TargetAspect"/> and the four margins is set; the
/// caller has already validated that and the prompt. Same source fields as <see cref="VideoUpscaleRequest"/>.
/// </summary>
/// <param name="TargetAspect">A supported aspect ("16:9", "9:16", "1:1", "4:5"), or null when margins are used.</param>
/// <param name="MarginLeft">Fraction of the source width added on the left (0..0.5).</param>
/// <param name="MarginRight">Fraction of the source width added on the right (0..0.5).</param>
/// <param name="MarginTop">Fraction of the source height added on the top (0..0.5).</param>
/// <param name="MarginBottom">Fraction of the source height added on the bottom (0..0.5).</param>
/// <param name="Prompt">The sanitized, moderated prompt (never blank: the model requires one and the caller supplies a default).</param>
public sealed record VideoOutpaintRequest(
    string SourceUrl,
    int SourceWidth,
    int SourceHeight,
    double SourceFps,
    double SourceDurationSec,
    long SourceFrames,
    string? TargetAspect,
    double MarginLeft,
    double MarginRight,
    double MarginTop,
    double MarginBottom,
    string Prompt,
    string Model = "",
    string CorrelationId = "")
{
    /// <summary>Never prints the presigned URL or the prompt.</summary>
    public override string ToString() =>
        $"outpaint {SourceWidth}x{SourceHeight} {SourceDurationSec:0.###}s " +
        $"{(string.IsNullOrWhiteSpace(TargetAspect) ? "margins" : TargetAspect)} with " +
        $"'{(!string.IsNullOrWhiteSpace(Model) ? Model : "default")}'";
}

/// <summary>
/// The vendor's tracking handle of a submitted job: what the caller persists (the <c>external_generation_jobs</c> ledger)
/// so it can resume after a restart instead of submitting, and paying for, the same clip again.
/// </summary>
public sealed record VideoEnhanceJobHandle(string RequestId, string StatusUrl, string ResponseUrl)
{
    /// <summary>The two URLs are vendor-scoped tracking URLs; they are not printed.</summary>
    public override string ToString() => $"fal job {(string.IsNullOrEmpty(RequestId) ? "(no id)" : RequestId)}";
}

/// <summary>A finished enhancement: the downloaded clip.</summary>
/// <param name="Video">The enhanced clip, downloaded through the SSRF-guarded client (video only; the caller remuxes the source audio).</param>
/// <param name="MimeType">Its media type, e.g. <c>video/mp4</c>.</param>
/// <param name="Extension">Its file extension including the dot.</param>
/// <param name="VendorDurationSec">The duration the vendor reports (bytedance's <c>duration</c>, "as used for billing"), or null when it reports none.</param>
/// <param name="Model">The endpoint that ran.</param>
/// <param name="RawMetadata">A bounded JSON of the vendor's output WITHOUT its URLs, for provenance; null when empty.</param>
/// <param name="VendorRequestId">fal's <c>request_id</c>, or null.</param>
public sealed record VideoEnhanceResult(
    byte[] Video,
    string MimeType,
    string Extension,
    double? VendorDurationSec,
    string Model,
    string? RawMetadata,
    string? VendorRequestId)
{
    /// <summary>The generated ToString would print every byte; this one does not.</summary>
    public override string ToString() => $"{MimeType} {Video.Length} bytes ({VendorRequestId ?? "no id"})";
}

/// <summary>How a video-enhance failure should be treated by the caller.</summary>
public enum VideoEnhanceErrorKind
{
    /// <summary>The row is misconfigured: key rejected (401/403) or no such endpoint (404).</summary>
    Configuration,

    /// <summary>The vendor account is out of quota or credit (402).</summary>
    VendorQuota,

    /// <summary>The request was refused, locally against the catalogue or by the vendor (400/422).</summary>
    RequestInvalid,

    /// <summary>Rate limit (429) on the submit: the vendor refused before queueing; retrying is safe.</summary>
    Retryable,

    /// <summary>
    /// A 5xx, 408 or transport failure on the SUBMIT: the vendor may have queued (and will bill) the job. Never
    /// resubmitted automatically (mirrors VideoGenerate's SUBMIT_OUTCOME_UNKNOWN).
    /// </summary>
    SubmitOutcomeUnknown,

    /// <summary>The vendor accepted the job then failed it, or answered unusably; billing unknown, so not retried.</summary>
    VendorFailed,

    /// <summary>The vendor's safety checker or content policy refused the clip.</summary>
    Refused,

    /// <summary>
    /// The poll budget ran out while the job was still queued or running. Not a failure: the caller keeps its
    /// <see cref="VideoEnhanceJobHandle"/> and polls again (or resumes after a restart).
    /// </summary>
    StillRunning,
}

/// <summary>A classified video-enhance failure. The message is bounded and never carries the key or any URL.</summary>
public sealed class VideoEnhanceException(
    VideoEnhanceErrorKind kind, string message, int? vendorStatus = null, Exception? inner = null) : Exception(message, inner)
{
    public VideoEnhanceErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status the vendor answered, when the failure was an HTTP answer.</summary>
    public int? VendorStatus { get; } = vendorStatus;

    public bool IsRetryable => Kind == VideoEnhanceErrorKind.Retryable;

    /// <summary>A stable failure code for the step that called.</summary>
    public string Code => Kind switch
    {
        VideoEnhanceErrorKind.Configuration => "ENHANCE_PROVIDER_CONFIG",
        VideoEnhanceErrorKind.VendorQuota => "ENHANCE_VENDOR_QUOTA",
        VideoEnhanceErrorKind.RequestInvalid => "ENHANCE_REQUEST_INVALID",
        VideoEnhanceErrorKind.Retryable => "ENHANCE_VENDOR_RETRYABLE",
        VideoEnhanceErrorKind.SubmitOutcomeUnknown => "ENHANCE_SUBMIT_OUTCOME_UNKNOWN",
        VideoEnhanceErrorKind.Refused => "ENHANCE_VENDOR_REFUSED",
        VideoEnhanceErrorKind.StillRunning => "ENHANCE_STILL_RUNNING",
        _ => "ENHANCE_VENDOR_FAILED",
    };

    /// <summary>The classification applied to an HTTP refusal status.</summary>
    public static VideoEnhanceErrorKind Classify(int status) => status switch
    {
        401 or 403 or 404 => VideoEnhanceErrorKind.Configuration,
        402 => VideoEnhanceErrorKind.VendorQuota,
        400 or 422 => VideoEnhanceErrorKind.RequestInvalid,
        429 => VideoEnhanceErrorKind.Retryable,
        408 or >= 500 => VideoEnhanceErrorKind.SubmitOutcomeUnknown,
        _ => VideoEnhanceErrorKind.VendorFailed,
    };
}

/// <summary>
/// Hosted AI video upscaling (<see cref="InferenceProviderCapability.VideoUpscale"/>), served by fal only. Requests are
/// validated against the endpoint's read schema BEFORE anything is sent. A job outlives one poll: submit returns a
/// handle the caller persists, poll and resume read it, and an engine restart never re-submits.
/// </summary>
public interface IVideoUpscaleClient
{
    /// <summary>Validates, then submits one job (one POST, no polling). A submission is what the vendor bills.</summary>
    /// <exception cref="VideoEnhanceException">Refused locally or by the vendor (classified).</exception>
    Task<VideoEnhanceJobHandle> SubmitAsync(VideoUpscaleRequest request, CancellationToken ct);

    /// <summary>
    /// Polls a submitted job to completion (bounded by the poll budget), then downloads the clip.
    /// <see cref="VideoEnhanceErrorKind.StillRunning"/> means the budget ran out and the job may still finish.
    /// </summary>
    Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <summary>The same read as <see cref="PollAsync"/>, for a handle loaded from the ledger after a restart. Never submits.</summary>
    Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <summary>The catalogued endpoint of <paramref name="model"/> (blank = default). Pure; no network call.</summary>
    /// <exception cref="NotSupportedException">The model has no read schema and no price, so it must not be offered.</exception>
    VideoEnhanceEndpoint Describe(string model);
}

/// <summary>Hosted AI frame interpolation (<see cref="InferenceProviderCapability.FrameInterpolation"/>), served by fal only.</summary>
public interface IFrameInterpolationClient
{
    /// <inheritdoc cref="IVideoUpscaleClient.SubmitAsync"/>
    Task<VideoEnhanceJobHandle> SubmitAsync(FrameInterpolationRequest request, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.PollAsync"/>
    Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.ResumeAsync"/>
    Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.Describe"/>
    VideoEnhanceEndpoint Describe(string model);
}

/// <summary>
/// Hosted AI video outpainting (<see cref="InferenceProviderCapability.VideoOutpaint"/>), served by fal only: Wan VACE
/// (default) or Luma Ray 2 reframe. The canvas is computed locally and validated against the endpoint BEFORE anything is
/// sent; a job outlives one poll exactly as the other hosted video jobs do.
/// </summary>
public interface IVideoOutpaintClient
{
    /// <inheritdoc cref="IVideoUpscaleClient.SubmitAsync"/>
    Task<VideoEnhanceJobHandle> SubmitAsync(VideoOutpaintRequest request, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.PollAsync"/>
    Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.ResumeAsync"/>
    Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct);

    /// <inheritdoc cref="IVideoUpscaleClient.Describe"/>
    VideoEnhanceEndpoint Describe(string model);
}

/// <summary>Builds (and caches) the upscale client for a resolved row.</summary>
public interface IVideoUpscaleClientFactory
{
    IVideoUpscaleClient Get(ResolvedAudioGenerationProvider provider);
}

/// <summary>Builds (and caches) the interpolation client for a resolved row.</summary>
public interface IFrameInterpolationClientFactory
{
    IFrameInterpolationClient Get(ResolvedAudioGenerationProvider provider);
}

/// <summary>Builds (and caches) the outpainting client for a resolved row.</summary>
public interface IVideoOutpaintClientFactory
{
    IVideoOutpaintClient Get(ResolvedAudioGenerationProvider provider);
}
