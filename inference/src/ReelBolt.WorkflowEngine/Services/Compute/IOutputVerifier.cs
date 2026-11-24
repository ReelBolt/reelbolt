using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D12: the PLATFORM-SIDE job spec a runner output is verified against. Every expected value here
/// comes from what the platform asked for — the resolved render/compile/compose spec — and never
/// from anything the runner reported, because the runner's whole result is the untrusted input.
/// A null member means "the platform has no opinion about this", and the corresponding check is
/// skipped rather than guessed (excluding <see cref="MaxBytes"/>, which is always required: size is
/// the one cap the protocol makes mandatory, since R2 and Garage cannot sign a max length into a
/// presigned PUT — see docs/runner-protocol.md "Presigned URLs and object storage").
/// </summary>
/// <param name="MaxBytes">
/// The protocol's own cap for this job type (render 2 GiB, compile 4 GiB, extract artefacts
/// 256 MiB, WAV 512 MiB — docs/runner-protocol.md). Enforced by a post-upload HEAD, because a
/// presigned PUT cannot carry a length.
/// </param>
/// <param name="DurationSeconds">Expected duration. Compared to within one frame, or 0.25 s, whichever is larger.</param>
/// <param name="Width">Expected video width in pixels.</param>
/// <param name="Height">Expected video height in pixels.</param>
/// <param name="VideoCodec">Expected ffprobe <c>codec_name</c> (e.g. <c>h264</c>), or null to accept any.</param>
/// <param name="FpsNum">Expected frame rate numerator; must be given with <paramref name="FpsDen"/>.</param>
/// <param name="FpsDen">Expected frame rate denominator.</param>
/// <param name="Sha256">
/// The SHA-256 the runner CLAIMS for the object. A hint, and treated as one: the engine hashes the
/// staged object itself and compares, so a runner cannot satisfy this check by returning a value
/// (docs/runner-protocol.md "the engine treats that value as a hint and verifies independently").
/// Null when the job type does not publish a digest.
/// </param>
/// <param name="IsMedia">
/// False for an object with no video stream to probe (an extract artefact, a WAV). Such an output is
/// still size- and checksum-verified; the ffprobe and spot-check stages are skipped.
/// </param>
/// <param name="IsJson">
/// True for a JSON document (an analysis extract, a manifest, a report). Implies not media: the
/// ffprobe and spot-check stages are skipped, and instead the engine downloads the staged object
/// (bounded) and requires it to parse as one JSON object or array, and the final key to sit under
/// <c>projects/{projectId}/</c>.
/// </param>
public sealed record RunnerOutputSpec(
    long MaxBytes,
    double? DurationSeconds = null,
    int? Width = null,
    int? Height = null,
    string? VideoCodec = null,
    int? FpsNum = null,
    int? FpsDen = null,
    string? Sha256 = null,
    bool IsMedia = true,
    bool IsJson = false,
    bool IsText = false);

/// <summary>
/// D12: what the engine asked a runner to produce, addressed by the object it lands in.
/// <paramref name="StagingIndex"/> is the <c>n</c> of <c>runner-staging/{jobId}/{n}</c> — the only
/// key shape a presigned PUT may target.
/// </summary>
public sealed record RunnerOutputRequest(
    Guid ProjectId,
    Guid JobId,
    int StagingIndex,
    string FinalKey,
    RunnerJobTarget Target,
    RunnerOutputSpec Spec);

/// <summary>
/// D12: the outcome of verifying (and, on success, promoting) one runner output. A failure is
/// returned, never thrown: the caller turns it into a failed step and, for the codes that mean the
/// runner misbehaved, a <see cref="FailureClass.Runner"/> (not refunded).
/// </summary>
/// <param name="Verified">True only when every applicable check passed and the object was promoted.</param>
/// <param name="FailureCode">One of the <c>RUNNER_OUTPUT_*</c> codes, or null on success.</param>
/// <param name="FailureClass">
/// Who bears the cost when <paramref name="Verified"/> is false: <see cref="FailureClass.Runner"/>
/// for output the runner produced and the platform rejected, <see cref="FailureClass.PlatformInternal"/>
/// when the platform itself could not complete the verification or the promotion.
/// <see cref="FailureClass.None"/> on success.
/// </param>
/// <param name="Message">Human-readable detail for the step's error and the logs.</param>
/// <param name="SizeBytes">The staged object's size, when it could be read.</param>
/// <param name="Sha256">The digest the engine computed, when it computed one.</param>
/// <param name="FinalKey">The promoted key, set only on success.</param>
public sealed record OutputVerificationResult(
    bool Verified,
    string? FailureCode,
    FailureClass FailureClass,
    string? Message,
    long SizeBytes,
    string? Sha256,
    string? FinalKey);

/// <summary>
/// D12: the object-store operations verification needs, in the terms this feature talks about.
/// Kept as its own seam — rather than taking <c>IAmazonS3</c> directly — so the verifier is testable
/// against a fake with no object store, no container and no network, and so the one implementation
/// (<see cref="S3RunnerOutputStore"/>) is the single place that knows a staging key's shape.
/// </summary>
public interface IRunnerOutputStore
{
    /// <summary>The staged object's size, or null when it does not exist (the runner never uploaded, or the upload was aborted).</summary>
    Task<long?> TryGetSizeAsync(string key, CancellationToken ct);

    /// <summary>
    /// A presigned GET for a STAGING object, for ffprobe to read over HTTP range requests. Note this
    /// is deliberately not <c>IPresignedUrlService.CreateGetAsync</c>, which only signs keys under
    /// <c>projects/{projectId}/</c>; a staging key is a different key space with a different
    /// lifetime, and the key is derived from the job here rather than taken from a caller.
    /// </summary>
    Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct);

    /// <summary>HEAD-checks the size cap, copies the staging object to <paramref name="finalKey"/> and deletes the staging object. Returns the copied size.</summary>
    Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct);

    /// <summary>Deletes an object — used to throw away output that failed verification.</summary>
    Task DiscardAsync(string key, CancellationToken ct);

    /// <summary>
    /// The object's SHA-256, computed by streaming it, or null when the object is over
    /// <paramref name="maxBytes"/> and the platform declined to download it (see
    /// <see cref="OutputVerificationOptions.MaxHashBytes"/>).
    /// </summary>
    Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct);

    /// <summary>
    /// Streams the object to a local file, refusing more than <paramref name="maxBytes"/>, and returns
    /// the first bytes it read (up to 16, for diagnosing a non-media object) or null when it could not
    /// be downloaded. The verifier's second opinion when probing the presigned URL fails: ffprobe over
    /// a local file needs no network protocol at all.
    /// </summary>
    Task<byte[]?> TryDownloadAsync(string key, string destinationPath, long maxBytes, CancellationToken ct)
        => Task.FromResult<byte[]?>(null);
}

/// <summary>
/// D12: verifies runner-produced output before it becomes a result, and promotes it only when it
/// passes. See <see cref="OutputVerifier"/> for the checks and <see cref="RunnerOutputSpec"/> for
/// what they compare against.
/// </summary>
public interface IOutputVerifier
{
    Task<OutputVerificationResult> VerifyAndPromoteAsync(RunnerOutputRequest request, CancellationToken ct = default);
}

/// <summary>
/// D12 configuration, bound from the <c>Compute:OutputVerification</c> section
/// (<c>Compute__OutputVerification__*</c> in compose).
/// </summary>
public sealed class OutputVerificationOptions
{
    public const string SectionName = "Compute:OutputVerification";

    /// <summary>
    /// Percentage of ORG RUNNER outputs that additionally get the sampled spot check (D12 step 3).
    /// 0 disables it; 100 runs it on every OrgRunner output. CloudPool and Local outputs are never
    /// spot-checked — the check exists to catch a hostile or broken customer machine, and it costs a
    /// real decode, so paying for it on our own hardware would be waste. Default 10.
    /// </summary>
    public int SpotCheckPercent { get; set; } = 10;

    /// <summary>Length of each sampled spot-check window, in seconds.</summary>
    public double SpotCheckWindowSeconds { get; set; } = 1.0;

    /// <summary>
    /// How many windows the spot check samples. A window is reported blank when a black or freeze
    /// segment covers at least half of it; the output fails only when EVERY sampled window is blank,
    /// because a single blank window is ordinary content (a fade, a still title card).
    /// </summary>
    public int SpotCheckWindows { get; set; } = 3;

    /// <summary>Timeout for one ffprobe against the staging URL.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>Timeout for one spot-check ffmpeg pass.</summary>
    public int SpotCheckTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Largest object the engine will download to hash. The digest check is integrity, not
    /// authenticity, and downloading a multi-gigabyte render to compare a hash the runner supplied
    /// is not worth the egress: over this size the comparison is skipped and logged, and the size,
    /// probe and spot-check stages still apply. Default 512 MiB.
    /// </summary>
    public long MaxHashBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>TTL of the presigned GET the probe uses. Short by design: it is minted and consumed inside one verification.</summary>
    public int ProbeUrlTtlSeconds { get; set; } = 10 * 60;

    /// <summary>
    /// Duration tolerance when the expected frame rate is unknown, in seconds. With a frame rate the
    /// tolerance is one frame, whichever of the two is larger.
    /// </summary>
    public double DurationToleranceSeconds { get; set; } = 0.25;
}
