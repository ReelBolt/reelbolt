using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>What an audio generation call produces. Append-only; persisted by name in provenance.</summary>
public enum AudioGenerationKind
{
    /// <summary>A music track (<see cref="InferenceProviderCapability.MusicGeneration"/>).</summary>
    Music,

    /// <summary>A sound effect (<see cref="InferenceProviderCapability.SoundEffectGeneration"/>).</summary>
    SoundEffect,
}

/// <summary>
/// One music or sound-effect generation. Every optional member is <c>null</c> when the caller did not ask for
/// it, and an endpoint that does not declare the matching field REFUSES a non-null value rather than dropping it
/// (a vendor silently ignores an unknown field and returns a plausible track for a request nobody made).
/// </summary>
/// <param name="Kind">Music or sound effect; must match the endpoint <paramref name="Model"/> names.</param>
/// <param name="Prompt">The text prompt. Music: mutually exclusive with <paramref name="CompositionPlanJson"/>.</param>
/// <param name="DurationSec">
/// Requested length. Required for prompt-mode music (the platform caps it at 300 s); null on a sound effect lets
/// the model choose, which the budget gate prices at the endpoint maximum. Must be null with a composition plan
/// (the plan's own section durations are the length).
/// </param>
/// <param name="Model">The endpoint id (fal) or <c>model_id</c> (ElevenLabs); blank = the catalogue default for <paramref name="Kind"/>.</param>
/// <param name="Instrumental">Music, prompt mode only: <c>force_instrumental</c>.</param>
/// <param name="CompositionPlanJson">
/// Music only: the vendor's composition plan JSON, validated against the endpoint's plan schema and passed through
/// unchanged (fal: the <c>sections</c> plan; ElevenLabs <c>music_v2</c>/<c>music_v2_5</c>: the <c>chunks</c> plan).
/// </param>
/// <param name="Loop">Sound effect only, where the endpoint declares <c>loop</c>.</param>
/// <param name="PromptInfluence">Sound effect only, 0-1, where the endpoint declares <c>prompt_influence</c>.</param>
/// <param name="OutputFormat">
/// The vendor's <c>output_format</c> token; null = <see cref="AudioOutputFormats.Default"/> (<c>mp3_44100_128</c>) on
/// an endpoint that has the field. Only container formats (MP3, Opus) are offered, because the engine must be able
/// to probe the file's duration.
/// </param>
public sealed record AudioGenerationRequest(
    AudioGenerationKind Kind,
    string? Prompt,
    double? DurationSec,
    string Model = "",
    bool? Instrumental = null,
    string? CompositionPlanJson = null,
    bool? Loop = null,
    double? PromptInfluence = null,
    string? OutputFormat = null);

/// <summary>
/// A finished generation: the audio bytes, already downloaded (fal) or read from the response (ElevenLabs).
/// </summary>
/// <param name="Bytes">The audio file.</param>
/// <param name="MimeType">Its media type, e.g. <c>audio/mpeg</c>.</param>
/// <param name="Extension">Its file extension including the dot, e.g. <c>.mp3</c>.</param>
/// <param name="DurationSec">
/// The duration the vendor stated, or <c>null</c>: no endpoint in the catalogue returns one today, so the engine
/// probes the file (<c>AudioGenerationDurationProbe</c>) before metering.
/// </param>
/// <param name="VendorRequestId">fal's <c>request_id</c>, ElevenLabs' <c>song-id</c>/<c>request-id</c> header, or null.</param>
public sealed record AudioGenerationResult(
    byte[] Bytes,
    string MimeType,
    string Extension,
    double? DurationSec,
    string? VendorRequestId)
{
    /// <summary>The record's generated ToString would print every byte; this one does not.</summary>
    public override string ToString() =>
        $"{MimeType} {Bytes.Length} bytes{(DurationSec is { } d ? $" {d:0.###}s" : string.Empty)} ({VendorRequestId ?? "no id"})";
}

/// <summary>Outcome of a no-cost connectivity probe.</summary>
public sealed record AudioGenerationProbeResult(bool Ok, bool AuthRejected, string? Reason);

/// <summary>How a vendor refusal or failure should be treated by the caller.</summary>
public enum AudioGenerationErrorKind
{
    /// <summary>The row is misconfigured: the key was rejected (401/403) or the endpoint does not exist.</summary>
    Configuration,

    /// <summary>The vendor account is out of quota or credit (ElevenLabs 402 <c>payment_required</c>).</summary>
    VendorQuota,

    /// <summary>The request itself was refused (400/422, <c>bad_prompt</c>, <c>bad_composition_plan</c>).</summary>
    UserInput,

    /// <summary>Rate or concurrency limit (429) or a server error (5xx): nothing was generated; retrying is safe.</summary>
    Retryable,

    /// <summary>
    /// The vendor accepted the job and then failed it, timed out, or answered something that is not audio. Whether
    /// it was billed is not known, so it is NOT retried automatically.
    /// </summary>
    VendorFailed,
}

/// <summary>A classified vendor failure. The message is bounded and never carries the key.</summary>
public sealed class AudioGenerationException(
    AudioGenerationErrorKind kind, string message, int? vendorStatus = null, string? suggestion = null,
    Exception? inner = null) : Exception(message, inner)
{
    public AudioGenerationErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status the vendor answered, when the failure was an HTTP answer.</summary>
    public int? VendorStatus { get; } = vendorStatus;

    /// <summary>
    /// The vendor's own rewrite of a refused input: ElevenLabs' <c>data.prompt_suggestion</c> (text) or
    /// <c>data.composition_plan_suggestion</c> (its JSON). Null when the vendor offered none.
    /// </summary>
    public string? Suggestion { get; } = suggestion;

    public bool IsRetryable => Kind == AudioGenerationErrorKind.Retryable;

    /// <summary>A stable failure code for the step that called (the engine maps it to a failure class).</summary>
    public string Code => Kind switch
    {
        AudioGenerationErrorKind.Configuration => "AUDIO_PROVIDER_CONFIG",
        AudioGenerationErrorKind.VendorQuota => "AUDIO_VENDOR_QUOTA",
        AudioGenerationErrorKind.UserInput => "AUDIO_BAD_REQUEST",
        AudioGenerationErrorKind.Retryable => "AUDIO_VENDOR_RETRYABLE",
        _ => "AUDIO_VENDOR_FAILED",
    };

    /// <summary>The classification every audio client applies to an HTTP refusal status.</summary>
    public static AudioGenerationErrorKind Classify(int status) => status switch
    {
        401 or 403 or 404 => AudioGenerationErrorKind.Configuration,
        402 => AudioGenerationErrorKind.VendorQuota,
        400 or 422 => AudioGenerationErrorKind.UserInput,
        408 or 429 => AudioGenerationErrorKind.Retryable,
        >= 500 => AudioGenerationErrorKind.Retryable,
        _ => AudioGenerationErrorKind.VendorFailed,
    };
}

/// <summary>
/// Music and sound-effect generation (Product decision: fal's hosted ElevenLabs/MMAudio endpoints and a direct
/// ElevenLabs kind). Requests are validated against the endpoint's read schema BEFORE anything is sent.
/// </summary>
public interface IAudioGenerationClient
{
    /// <summary>Generates one track or effect and returns its bytes.</summary>
    /// <param name="onSubmitted">
    /// QA-P1-17: invoked with the vendor's request id the moment the submit answers — for a client with a submit/poll
    /// split (fal) that is BEFORE the poll and the result download, so a crash in that window still leaves the id
    /// recorded. A client whose one call is also its result (ElevenLabs) never invokes it. Null when the caller does
    /// not need the id early.
    /// </param>
    /// <exception cref="ArgumentException">The request breaks the endpoint's schema; nothing was sent.</exception>
    /// <exception cref="NotSupportedException">The model is not one this client has a read schema for.</exception>
    /// <exception cref="AudioGenerationException">The vendor refused or failed the job (classified).</exception>
    Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken ct,
        Func<string, Task>? onSubmitted = null);

    /// <summary>No-cost provider test: creates nothing billable.</summary>
    Task<AudioGenerationProbeResult> ProbeAsync(CancellationToken ct);

    /// <summary>The price and duration range of <paramref name="model"/>. Pure; no network call.</summary>
    /// <exception cref="NotSupportedException">The model cannot be priced, so it must not be offered.</exception>
    AudioGenerationCapabilities Describe(string model);
}

/// <summary>Builds (and caches) the client for a resolved audio row.</summary>
public interface IAudioGenerationClientFactory
{
    IAudioGenerationClient Get(ResolvedAudioGenerationProvider provider);
}
