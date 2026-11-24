namespace ReelBolt.Shared.Inference;

/// <summary>
/// Request for video generation from an external provider.
/// </summary>
/// <param name="Model">
/// The provider's model identity. For MiniMax this is the model name (e.g. <c>MiniMax-H3</c>).
/// For <see cref="ReelBolt.Shared.Data.Models.InferenceProviderKind.Higgsfield"/> it is the
/// vendor's documented <b>Endpoint ID</b> — a path-addressed workflow identifier such as
/// <c>kling-video/v3.0/std/text-to-video</c>, which the submit call POSTs to
/// (https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md).
/// </param>
/// <param name="Images">
/// Optional input images for image-to-video workflows, in a FIXED ORDER: <c>Images[0]</c> is the
/// FIRST frame and <c>Images[1]</c> — only where the resolved model accepts one — is the LAST frame.
/// The order is the entire image contract; there is no per-image role member on the request, and each
/// vendor's own role or field name (MiniMax's <c>role=first_frame</c>/<c>last_frame</c>, Higgsfield's
/// <c>image_url</c>/<c>last_image_url</c>, ComfyUI's single <c>LoadImage</c> node) is derived from the
/// index by the client that documents it. What a model accepts, and whether it accepts a last frame
/// at all, is declared by <see cref="VideoImageInputSupport"/>. The trailing <c>= null</c> is
/// load-bearing: every pre-existing positional construction keeps compiling unchanged.
/// </param>
/// <param name="IdempotencyKey">
/// Optional caller-chosen idempotency token for providers that document one. The
/// <c>VideoGenerate</c> step passes its ledger row id, so a retry that re-submits the same reserved
/// take after a crash lands on the vendor as the same request. Only
/// <see cref="ReelBolt.Shared.Data.Models.InferenceProviderKind.Higgsfield"/> sends it today
/// (<c>Idempotency-Key</c> header, documented on the vendor's own model-page cURL —
/// https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md); MiniMax's create
/// endpoint documents none and ComfyUI's <c>POST /prompt</c> accepts a client <c>prompt_id</c>
/// instead. Appended and defaulted so every existing construction keeps compiling.
/// </param>
/// <param name="References">
/// Optional person reference material (an avatar's pictures, short videos and speech recordings,
/// docs/design/avatars.md, AV-11), in the avatar's order. Not frames: what a model accepts is declared by
/// <see cref="IVideoReferenceInputSupport"/>, and a model that declares reference mode exclusive with frames refuses
/// a request carrying both <see cref="Images"/> and references. Null or empty keeps every existing request
/// byte-identical. Appended and defaulted so every existing construction keeps compiling.
/// </param>
public record VideoGenerationRequest(
    string Model,
    string PromptText,
    int DurationSeconds,
    string Resolution,
    string Ratio,
    IReadOnlyList<VideoGenerationImageInput>? Images = null,
    string? IdempotencyKey = null,
    IReadOnlyList<VideoGenerationReference>? References = null)
{
    /// <summary>
    /// The request carries a person's media (an avatar's still or speech, docs/design/avatars.md phase 3): a client
    /// whose vendor documents a "do not keep the inputs" switch sends it (fal: <c>X-Fal-Store-IO: 0</c>). A request that
    /// carries <see cref="References"/> is treated the same way whatever this says. Default false keeps every existing
    /// request byte-identical. Additive.
    /// </summary>
    public bool NoVendorRetention { get; init; }
}

/// <summary>
/// An input image for a video generation request, carried as raw bytes plus its media type.
/// <para>
/// Transport is per provider: Higgsfield requires a public URL and uploads the bytes itself through
/// the vendor's presigned upload, MiniMax accepts an inline <c>data:image/…;base64,…</c> data URI
/// (which is how its client sends them, after checking the vendor's own size, pixel and aspect
/// limits), and a self-hosted ComfyUI server takes a multipart upload against its own image-input API
/// and is then referenced by file name inside the vetted graph.
/// </para>
/// <para>
/// The image's POSITION in <see cref="VideoGenerationRequest.Images"/> is its role: index 0 is the
/// first frame and index 1 the last frame.
/// </para>
/// </summary>
public record VideoGenerationImageInput(string ContentType, byte[] Bytes);

/// <summary>
/// Handle to an external video generation job.
/// </summary>
public record ExternalJobHandle(string RemoteJobId);

/// <summary>
/// State of a video generation job.
/// </summary>
public enum ExternalJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// Status update for an external video generation job.
/// </summary>
/// <param name="State">Current state of the remote job.</param>
/// <param name="DownloadUrl">Download URL of the generated video; set only when succeeded.</param>
/// <param name="FailureReason">Provider-reported failure message; set only when failed.</param>
/// <param name="UsageTotalSeconds">
/// The vendor's metered usage for the job, or <c>null</c> when the provider did not report it.
/// For MiniMax this is <c>task.usage.total_seconds</c> from the v2 query endpoint
/// (https://platform.minimax.io/docs/api-reference/video-generation-v2-query.md, OpenAPI schema
/// <c>VideoTaskUsage</c>): "Total metered seconds = input seconds + output seconds". The usage
/// object is returned only when succeeded, so this is <c>null</c> for every other state, and also
/// whenever the field is absent, null, or not a JSON integer.
/// </param>
public record ExternalJobStatus(
    ExternalJobState State,
    string? DownloadUrl,
    string? FailureReason,
    int? UsageTotalSeconds = null);

/// <summary>
/// Capabilities offered by a video generation provider.
/// </summary>
public record VideoGenerationCapabilities(
    IReadOnlyDictionary<string, decimal> PricePerSecondByResolution,
    int MinDurationSeconds,
    int MaxDurationSeconds,
    IReadOnlyList<string> AllowedRatios);

/// <summary>
/// Outcome of <see cref="IVideoGenerationClient.ProbeAsync"/>, the no-cost provider test.
/// </summary>
/// <param name="Ok">
/// <c>true</c> only when the provider returned a response that proves the endpoint is the vendor's
/// query API and the key was accepted.
/// </param>
/// <param name="AuthRejected"><c>true</c> when the provider rejected the credentials.</param>
/// <param name="Reason">
/// What was seen, for display to an admin: a status code, a failure class, or a bounded vendor status
/// message. Never contains the API key or the raw response body.
/// </param>
public record VideoGenerationProbeResult(bool Ok, bool AuthRejected, string? Reason);
