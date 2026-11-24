using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One hosted voice isolation. Every optional member is <c>null</c> when the caller did not ask for it, and an
/// endpoint that does not declare the matching feature REFUSES a non-null value rather than dropping it.
/// </summary>
/// <param name="SourceUrl">
/// A presigned GET URL of the source audio (https, no credentials). Its 10-minute lifetime is the CALLER's job.
/// It is sent to the vendor and never logged.
/// </param>
/// <param name="SourceDurationSec">The measured length of the source in seconds; it is what is metered (rounded up to minutes).</param>
/// <param name="Model">The fal endpoint id; blank = the catalogue default.</param>
/// <param name="WantBackground">True to also download a background stem; refused when the endpoint returns none.</param>
public sealed record AudioIsolationRequest(
    string SourceUrl,
    double SourceDurationSec,
    string Model = "",
    bool? WantBackground = null)
{
    /// <summary>Never prints the presigned URL (the generated record ToString would).</summary>
    public override string ToString() => $"isolate {SourceDurationSec:0.###}s with '{(string.IsNullOrWhiteSpace(Model) ? "default" : Model)}'";
}

/// <summary>A finished isolation: the downloaded stems and what was billed.</summary>
/// <param name="VoiceStem">The isolated voice, already downloaded through the SSRF-guarded client.</param>
/// <param name="MimeType">The voice stem's media type, e.g. <c>audio/mpeg</c>.</param>
/// <param name="Extension">Its file extension including the dot.</param>
/// <param name="BackgroundStem">The background stem, or null when none was asked for or the endpoint has none.</param>
/// <param name="BilledSeconds">The audio seconds metered (the request's measured source duration); the price rounds them up to minutes.</param>
/// <param name="Model">The endpoint that ran.</param>
/// <param name="RawMetadata">A bounded JSON of the vendor's output WITHOUT its URLs, for provenance; null when empty.</param>
/// <param name="VendorRequestId">fal's <c>request_id</c>, or null.</param>
public sealed record AudioIsolationResult(
    byte[] VoiceStem,
    string MimeType,
    string Extension,
    byte[]? BackgroundStem,
    double BilledSeconds,
    string Model,
    string? RawMetadata,
    string? VendorRequestId)
{
    /// <summary>The generated ToString would print every byte; this one does not.</summary>
    public override string ToString() =>
        $"{MimeType} {VoiceStem.Length} bytes{(BackgroundStem is null ? "" : " +background")} ({VendorRequestId ?? "no id"})";
}

/// <summary>How an isolation failure should be treated by the caller.</summary>
public enum AudioIsolationErrorKind
{
    /// <summary>The row is misconfigured: key rejected (401/403) or no such endpoint (404).</summary>
    Configuration,

    /// <summary>The vendor account is out of quota or credit (402).</summary>
    VendorQuota,

    /// <summary>The request was refused, locally against the catalogue or by the vendor (400/422).</summary>
    RequestInvalid,

    /// <summary>Rate limit (429) or server error (5xx): nothing was produced; retrying is safe.</summary>
    Retryable,

    /// <summary>The vendor accepted the job then failed it, timed out, or answered unusably; billing unknown, so not retried.</summary>
    VendorFailed,
}

/// <summary>A classified isolation failure. The message is bounded and never carries the key or any URL.</summary>
public sealed class AudioIsolationException(
    AudioIsolationErrorKind kind, string message, int? vendorStatus = null, Exception? inner = null) : Exception(message, inner)
{
    public AudioIsolationErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status the vendor answered, when the failure was an HTTP answer.</summary>
    public int? VendorStatus { get; } = vendorStatus;

    public bool IsRetryable => Kind == AudioIsolationErrorKind.Retryable;

    /// <summary>A stable failure code for the step that called.</summary>
    public string Code => Kind switch
    {
        AudioIsolationErrorKind.Configuration => "AUDIO_PROVIDER_CONFIG",
        AudioIsolationErrorKind.VendorQuota => "AUDIO_VENDOR_QUOTA",
        AudioIsolationErrorKind.RequestInvalid => "AUDIO_REQUEST_INVALID",
        AudioIsolationErrorKind.Retryable => "AUDIO_VENDOR_RETRYABLE",
        _ => "AUDIO_VENDOR_FAILED",
    };

    /// <summary>The classification applied to an HTTP refusal status.</summary>
    public static AudioIsolationErrorKind Classify(int status) => status switch
    {
        401 or 403 or 404 => AudioIsolationErrorKind.Configuration,
        402 => AudioIsolationErrorKind.VendorQuota,
        400 or 422 => AudioIsolationErrorKind.RequestInvalid,
        408 or 429 => AudioIsolationErrorKind.Retryable,
        >= 500 => AudioIsolationErrorKind.Retryable,
        _ => AudioIsolationErrorKind.VendorFailed,
    };
}

/// <summary>
/// Hosted AI voice isolation (<see cref="InferenceProviderCapability.AudioIsolation"/>), served by fal only.
/// Requests are validated against the endpoint's read schema BEFORE anything is sent.
/// </summary>
public interface IAudioIsolationClient
{
    /// <param name="onSubmitted">Invoked with the vendor request id right after the submit answers, before the poll.</param>
    /// <exception cref="AudioIsolationException">Refused locally or by the vendor, or failed (classified).</exception>
    Task<AudioIsolationResult> IsolateAsync(AudioIsolationRequest request, CancellationToken ct,
        Func<string, Task>? onSubmitted = null);

    /// <summary>The catalogued endpoint of <paramref name="model"/> (blank = default). Pure; no network call.</summary>
    /// <exception cref="NotSupportedException">The model has no read schema and no price, so it must not be offered.</exception>
    AudioIsolationEndpoint Describe(string model);
}

/// <summary>Builds (and caches) the client for a resolved audio-isolation row.</summary>
public interface IAudioIsolationClientFactory
{
    IAudioIsolationClient Get(ResolvedAudioGenerationProvider provider);
}
