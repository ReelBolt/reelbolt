using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Segmentation;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// Every refusal the analyze path raises for a text-prompted segmentation target. Kept beside the code
/// that raises them (and mapped in <see cref="FailureClassifier"/>) so a message, a test and a billing
/// class cannot drift apart.
/// </summary>
public static class SegmentationFailureCodes
{
    /// <summary>No enabled <c>VideoSegmentation</c> provider resolves (the step's own id, else the default).</summary>
    public const string NoProvider = "SEGMENTATION_NO_PROVIDER";

    /// <summary>The target never consented to the source leaving this host (<c>AllowSourceMediaEgress</c> is false).</summary>
    public const string EgressRefused = "EGRESS_REFUSED";

    /// <summary>Consent was given but this deployment cannot mint a short-TTL GET (local-disk storage, no presigner).</summary>
    public const string EgressUnavailable = "SEGMENTATION_EGRESS_UNAVAILABLE";

    /// <summary>The source is longer than <c>MaxSegmentationSeconds</c>; never truncated silently.</summary>
    public const string SourceTooLong = "SEGMENTATION_SOURCE_TOO_LONG";

    /// <summary>
    /// The source's own probed frame count is more than the credit hold priced
    /// (<see cref="ReelBolt.Shared.Metering.SegmentationCreditPricing.PricedFramesFor"/>). The vendor bills
    /// every frame at the clip's real rate, so running it would spend past the reservation and settle the
    /// organization negative.
    /// </summary>
    public const string FrameBudgetExceeded = "SEGMENTATION_FRAME_BUDGET_EXCEEDED";

    /// <summary>The vendor call failed (transport, HTTP status, timeout, queue state).</summary>
    public const string ProviderError = "SEGMENTATION_PROVIDER_ERROR";

    /// <summary>The vendor answered, but not in the shape its own schema documents (RLE, frames, sizes), or the mask video could not be written.</summary>
    public const string BadResponse = "SEGMENTATION_BAD_RESPONSE";

    /// <summary>A tapped target found no object under the tap on the tapped frame. Never billed to the customer.</summary>
    public const string ObjectNotFoundAtPoint = "OBJECT_NOT_FOUND_AT_POINT";

    /// <summary>A target with a tap point or no-words flag in a workflow that is not the hidden object-analysis run. Not refunded.</summary>
    public const string SeededTargetNotAllowed = "SEEDED_TARGET_NOT_ALLOWED";
}

/// <summary>
/// The seam that builds a <see cref="IVideoSegmentationClient"/> for a resolved provider.
/// <para>
/// <b>Why it carries the frame geometry.</b> T-003's client asks its caller for the source clip's
/// width and height (<see cref="IVideoSegmentationFrameSizeProbe"/>) because
/// <c>fal-ai/sam-3-1/video-rle</c>'s answer states neither: the RLE strings are a linear index over a
/// frame the document never sizes. The analyze path already probed the clip, so the factory takes that
/// measurement and hands the client a probe answering exactly it — no second ffprobe of a URL we do not
/// own, and a client cache keyed on the geometry it decodes against.
/// </para>
/// <para>
/// <b>Implemented by <see cref="FalAiVideoSegmentationClientFactory"/>,</b> registered in the engine's
/// composition root beside the mask store and the tracking service. It lives in the engine rather than
/// in <c>ReelBolt.Shared.Inference</c> because the metering decorator it wraps each client in
/// (<c>MeteringVideoSegmentationClient</c>) is applied per resolved provider, exactly as
/// <c>ImageAnalysisClientFactory</c> does for its siblings.
/// </para>
/// </summary>
public interface IVideoSegmentationClientFactory
{
    /// <summary>The client for <paramref name="provider"/>, or a throw when its kind cannot serve this capability.</summary>
    /// <param name="provider">The resolved provider row (kind, endpoint, key, model, timeout).</param>
    /// <param name="frameSize">The source clip's own probe: the width and height the RLE is indexed over.</param>
    IVideoSegmentationClient Get(ResolvedImageAnalysisProvider provider, VideoSegmentationFrameSize frameSize);
}

/// <summary>Which source clip one segmentation pass runs over, and what the mask must be shaped like.</summary>
/// <param name="SourceIndex">0-based index of this source in the analyze step's source list.</param>
/// <param name="ProjectId">The project every object this pass writes must sit under.</param>
/// <param name="SourceStorageKey">The source's own object key — what the presigned GET is minted for, and what a refusal names.</param>
/// <param name="LocalVideoPath">The source on THIS host (the mask is written here, so this pass needs a local media workspace like the feature tracker).</param>
/// <param name="DurationSec">The probed duration, checked against the step's segmentation cap.</param>
/// <param name="Fps">The probed frame rate: the mask video is encoded at exactly this rate.</param>
/// <param name="Width">The probed width: the mask frames are exactly this wide.</param>
/// <param name="Height">The probed height.</param>
/// <param name="OrganizationId">The organization whose plan gate is consulted; <see cref="Guid.Empty"/> skips it (no plan system).</param>
public sealed record SegmentationSourceInput(
    int SourceIndex,
    Guid ProjectId,
    string SourceStorageKey,
    string LocalVideoPath,
    double DurationSec,
    double Fps,
    int Width,
    int Height,
    Guid OrganizationId,
    bool AllowSeededTargets = false);

/// <summary>
/// What one source's segmentation pass produced, and — when it did not finish — exactly why.
/// <para>
/// A failure here is a STEP FAILURE, never a silent degrade: segmentation is a paid, explicitly
/// requested operation (plan §6), so a target that asked for it and did not get it must not publish a
/// picture whose effect is missing. <see cref="Tracks"/> still carries whatever finished, so the report
/// can say which targets succeeded.
/// </para>
/// </summary>
/// <param name="Tracks">One track per target that produced a mask. Their <c>Id</c> is EMPTY: the executor numbers them <c>g{n}</c> in artifact order, exactly like <c>r{n}</c>.</param>
/// <param name="PromptsSent">The sanitized prompts this source actually sent, in target order (provenance).</param>
/// <param name="FailureCode">One of <see cref="SegmentationFailureCodes"/>, or null when every target finished.</param>
/// <param name="FailureMessage">The specific sentence for that code: it names the clip, the prompt or the cap it broke.</param>
/// <param name="FailureClass">Who pays for the work already done; null means "let the table decide".</param>
/// <param name="ProviderCalls">How many vendor calls were made (a content-cache hit is zero).</param>
/// <param name="CacheHits">How many targets were served from the content cache.</param>
/// <param name="FailureLogMessage">
/// The sentence written to the step LOG when the customer-facing <paramref name="FailureMessage"/> names
/// the prompt. A prompt is the customer's own description of the private subject of their footage and
/// never reaches a log line (security finding F-1); this sentence names the source key and the prompt's
/// length instead. Null logs <paramref name="FailureMessage"/>.
/// </param>
public sealed record SegmentationSourceOutcome(
    IReadOnlyList<VideoSegmentationTrack> Tracks,
    IReadOnlyList<string> PromptsSent,
    string? FailureCode = null,
    string? FailureMessage = null,
    FailureClass? FailureClass = null,
    int ProviderCalls = 0,
    int CacheHits = 0,
    VideoSegmentationDiagnostics? Diagnostics = null,
    string? FailureLogMessage = null);

/// <summary>
/// The vendor slots that describe the prompted object (T-020, D-21): the <paramref name="Anchor"/> the
/// old selection would have used alone, and every re-acquisition merged into it, ascending by id.
/// <see cref="Anchor"/> is -1 when the answer named no object at all, and its scores are the track's
/// confidence.
/// </summary>
/// <param name="Anchor">The slot the object starts from (-1 when there is none).</param>
/// <param name="Slots">Every slot that contributes to the track, ascending and deterministic.</param>
internal sealed record SlotSelection(int Anchor, IReadOnlySet<int> Slots);

/// <summary>Runs the <see cref="ObjectTrackMethod.Segmentation"/> targets of one source.</summary>
public interface ISegmentationTrackingService
{
    /// <summary>
    /// Segments every segmentation target of <paramref name="config"/> over <paramref name="source"/>,
    /// caching masks by content and writing them under the project's own prefix. Never throws for a
    /// refusal: a refusal is an outcome with a code, a sentence and a billing class.
    /// </summary>
    Task<SegmentationSourceOutcome> TrackAsync(
        SegmentationSourceInput source,
        VideoAnalyzeStepConfig config,
        IMediaWorkspace workspace,
        Func<string, Task> reportProgress,
        CancellationToken ct);
}

/// <summary>
/// The analyze-side implementation: consent, presign, provider call, RLE decode, mask video, artifact
/// track (initiative "prompted region effects", plan §3 and §6).
///
/// <para>
/// <b>The mask is the artifact.</b> This is the only place in the system that calls a segmentation
/// model. It writes ONE full-frame grey8 mask video per track (255 = object), content-addressed, and
/// records its key and SHA-256 in the track; the compile step downloads exactly that object, re-hashes
/// it, and conforms it with the picture's own geometry. Nothing downstream re-interprets the vendor's
/// answer, and the vendor's answer is never stored.
/// </para>
///
/// <para>
/// <b>Order of refusals, and why.</b> Plan entitlement first (a plan that cannot use the feature must
/// not cause the source to leave the host), then egress consent, then provider resolution, then the
/// duration cap, and only then the presign — so the URL that grants access to the customer's footage is
/// minted at the last possible moment, and only for work that will actually happen.
/// </para>
/// </summary>
public sealed class SegmentationTrackingService(
    IInferenceProviderResolver resolver,
    ISegmentationMaskStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<SegmentationTrackingService> logger,
    IVideoSegmentationClientFactory? clients = null,
    IPresignedUrlService? presignedUrls = null) : ISegmentationTrackingService
{
    /// <summary>How long the source URL the vendor fetches stays valid. The house cap is 15 minutes; this is deliberately shorter.</summary>
    public static readonly TimeSpan SourceUrlTtl = TimeSpan.FromMinutes(10);

    /// <summary>Most per-frame boxes a track carries into the artifact (sampled evenly across the span).</summary>
    public const int MaxBoxKeyframes = 120;

    /// <summary>Longest prompt the model is sent, in characters, after sanitizing.</summary>
    public const int MaxPromptChars = 160;

    /// <summary>
    /// How far apart, in seconds, two vendor id segments may be and still be the same object re-acquired
    /// (T-020, D-21). Measured on the clip the rule exists for: the vendor renumbered one speedometer
    /// three times, the id 0 → id 1 join is 0 frames and the id 1 → id 2 gap is 11 frames (0.18 s at
    /// 60 fps), so half a second covers the real renumbering and stays far short of "a different moment
    /// of the film".
    /// </summary>
    public const double ReacquireGapSeconds = 0.5;

    /// <summary>
    /// Hard ceiling on the slots merged into ONE object (T-020). The merge is driven by the vendor's own
    /// id count, so an answer naming thousands of ids must not turn into a long fixed-point scan: past
    /// this ceiling the answer is refused as <c>SEGMENTATION_BAD_RESPONSE</c>. Twice
    /// <see cref="Sam31VideoRleResponseReader.MaxObjectSlots"/> — a real renumbering is a handful of ids
    /// (three on the clip this rule exists for), never dozens.
    /// </summary>
    public const int MaxMergedSlots = 64;

    /// <summary>
    /// A slot that overlaps an instance in time and covers under this fraction of the instance's area is a
    /// PART of that object, not another instance of it (D-24). Grounded in the live F2/FACE answer, which
    /// emits 17 slots for one prompt: the dominant one covers 27 % of the frame and the rest 0.2–1 %. Two
    /// comparable instances (roughly equal area) stay two instances.
    /// </summary>
    public const double InstanceAbsorbMaxFraction = 0.25;

    /// <summary>
    /// Hard ceiling on the distinct instances one answer may describe (T-021). Like
    /// <see cref="MaxMergedSlots"/>, the vendor's answer drives the work, so it is bounded: past this
    /// the answer is refused as <c>SEGMENTATION_BAD_RESPONSE</c> rather than grouped.
    /// </summary>
    public const int MaxInstances = 16;

    /// <summary>Reported frames sampled to choose WHICH vendor slot is "the object" when the answer carries several.</summary>
    internal const int SlotSampleFrames = 6;

    /// <summary>
    /// How much of the content key a log line names. The key is a SHA-256 over the source hash, the prompt
    /// and the request knobs, and it is also the mask's storage name, so its prefix is a stable correlation
    /// handle that carries none of the customer's words.
    /// </summary>
    public const int ContentKeyLogPrefixChars = 12;

    /// <summary>Marks a content-key source hash whose vendor upload was the upright mezzanine.</summary>
    internal const string UprightHashPrefix = "upright1:";

    internal const int UprightTranscodeTimeoutSeconds = 900;

    /// <summary>
    /// Whether ffmpeg changes the source pixels on decode (rotation of any angle, or a mirror). FAILS CLOSED: a failed, timed-out or unreadable
    /// re-probe throws, because guessing "not rotated" would send the sideways original to the vendor.
    /// </summary>
    private static async Task<bool> SourceNeedsUprightAsync(
        SegmentationSourceInput source, IMediaWorkspace workspace, CancellationToken ct)
    {
        VideoToolResult probed = await workspace.RunFfprobeAsync(
            FfmpegArgvBuilder.BuildProbeArgs(source.LocalVideoPath), TimeSpan.FromSeconds(60), ct);
        if (!probed.Succeeded || !FfprobeOutputParser.HasVideoStream(probed.StdOut))
            throw new InvalidOperationException("the source could not be re-probed");
        return FfprobeOutputParser.AltersPixelsOnDecode(probed.StdOut);
    }

    /// <summary>Share of <see cref="SegmentationLimits.VendorSourceMaxBytes"/> the VBV ceiling aims at, leaving room for the container and the buffer.</summary>
    internal const double VendorCopyBudgetShare = 0.85;

    /// <summary>Floor and ceiling of the vendor copy's VBV ceiling, in kbit/s.</summary>
    internal const int VendorCopyMinKbps = 300;
    internal const int VendorCopyMaxKbps = 50_000;

    /// <summary>
    /// The VBV ceiling that keeps a <paramref name="durationSec"/>-second copy inside the vendor budget:
    /// <c>VendorSourceMaxBytes × 8 × 0.85 / duration</c>, clamped to 300..50,000 kbit/s. A 60 s clip gets
    /// ~11 Mbps; the 600 s cap gets ~1.1 Mbps.
    /// </summary>
    internal static int VendorCopyMaxrateKbps(double durationSec)
    {
        double seconds = double.IsFinite(durationSec) && durationSec > 1 ? durationSec : 1;
        double kbps = SegmentationLimits.VendorSourceMaxBytes * 8.0 * VendorCopyBudgetShare / 1000.0 / seconds;
        return (int)Math.Clamp(Math.Floor(kbps), VendorCopyMinKbps, VendorCopyMaxKbps);
    }

    /// <summary>Encode deadline for the vendor copy: six times the clip's length, 900 s..3600 s.</summary>
    internal static TimeSpan VendorCopyTimeout(double durationSec) =>
        TimeSpan.FromSeconds(Math.Clamp(
            double.IsFinite(durationSec) ? Math.Ceiling(durationSec * 6) : UprightTranscodeTimeoutSeconds,
            UprightTranscodeTimeoutSeconds,
            SegmentationLimits.MaxProviderTimeoutSeconds));

    /// <summary>
    /// Encodes, checks and stores the bounded vendor copy. Fails closed: a copy whose geometry differs
    /// from the source, or whose size cannot be read or is still over the budget, is never stored, so
    /// <see cref="ISegmentationMaskStore.VendorCopyExistsAsync"/> only ever finds a checked copy.
    /// </summary>
    private async Task MakeVendorCopyAsync(
        SegmentationSourceInput source, IMediaWorkspace workspace, string sourceHash, CancellationToken ct)
    {
        string copyPath = await workspace.MaterializeAsync(SegmentationMaskArgvBuilder.VendorCopyVirtualPath, ct);
        VideoToolResult made = await workspace.RunFfmpegAsync(
            SegmentationMaskArgvBuilder.BuildVendorCopyArgs(source.LocalVideoPath, copyPath, VendorCopyMaxrateKbps(source.DurationSec)),
            VendorCopyTimeout(source.DurationSec), ct);
        if (!made.Succeeded)
        {
            throw new InvalidOperationException($"ffmpeg exited {made.ExitCode}: {made.StdErr}");
        }

        VideoToolResult probed = await workspace.RunFfprobeAsync(
            FfmpegArgvBuilder.BuildProbeArgs(copyPath), TimeSpan.FromSeconds(60), ct);
        if (!probed.Succeeded)
        {
            throw new InvalidOperationException("the vendor copy could not be probed");
        }

        MediaProbeResult copy = FfprobeOutputParser.Parse(probed.StdOut);
        if (FfprobeOutputParser.AltersPixelsOnDecode(probed.StdOut) ||
            copy.Width != source.Width || copy.Height != source.Height)
        {
            throw new InvalidOperationException(
                $"the vendor copy probes {copy.Width}x{copy.Height} but the source decodes {source.Width}x{source.Height}");
        }

        long copyBytes = ProbedFormatSizeBytes(probed.StdOut)
            ?? throw new InvalidOperationException("the vendor copy's size could not be read");
        if (copyBytes > SegmentationLimits.VendorSourceMaxBytes)
        {
            throw new VendorCopyTooLargeException(copyBytes);
        }

        await store.PutVendorCopyAsync(source.ProjectId, workspace, SegmentationMaskArgvBuilder.VendorCopyVirtualPath, sourceHash, ct);
    }

    /// <summary><c>format.size</c> of an ffprobe <c>-show_format</c> JSON answer, or null when absent or unreadable.</summary>
    internal static long? ProbedFormatSizeBytes(string? ffprobeJson)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return null;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(ffprobeJson);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out System.Text.Json.JsonElement format)
                && format.ValueKind == System.Text.Json.JsonValueKind.Object
                && format.TryGetProperty("size", out System.Text.Json.JsonElement size))
            {
                string? text = size.ValueKind == System.Text.Json.JsonValueKind.String ? size.GetString() : size.GetRawText();
                if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) && bytes > 0)
                    return bytes;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    /// <summary>The bounded vendor copy is still over <see cref="SegmentationLimits.VendorSourceMaxBytes"/>.</summary>
    internal sealed class VendorCopyTooLargeException(long actualBytes)
        : Exception($"the bounded vendor copy is {actualBytes} bytes, over {SegmentationLimits.VendorSourceMaxBytes}")
    {
        public long ActualBytes { get; } = actualBytes;
    }

    /// <summary>The mask video's session path (one per source pass; each track uploads its own copy).</summary>
    internal const string MaskVirtualPath = SegmentationMaskArgvBuilder.Directory + "/mask.mkv";

    public async Task<SegmentationSourceOutcome> TrackAsync(
        SegmentationSourceInput source,
        VideoAnalyzeStepConfig config,
        IMediaWorkspace workspace,
        Func<string, Task> reportProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(workspace);

        List<ObjectTrackTarget> targets = [.. (config.TrackObjects ?? [])
            .Where(t => t.Method == ObjectTrackMethod.Segmentation && !string.IsNullOrWhiteSpace(t.Label))];

        if (targets.Count == 0)
        {
            return new SegmentationSourceOutcome([], []);
        }

        // A tapped target is refunded when it finds nothing, so only the hidden object-analysis run (which the API caps) may carry
        // one. Anywhere else it is the author's own input error, and it is not refunded.
        if (!source.AllowSeededTargets && targets.Any(t => t.SeedPoint is not null || t.NoWords))
        {
            return new SegmentationSourceOutcome([], [],
                SegmentationFailureCodes.SeededTargetNotAllowed,
                "A target that names an object by a tap can only be used by the editor's Objects panel, not in a workflow you build.",
                FailureClass.UserInput);
        }

        List<VideoSegmentationTrack> tracks = [];
        List<string> prompts = [];
        int calls = 0;
        int hits = 0;

        // Facts about the vendor's SHAPE, aggregated over this source's calls: frame-count tolerance
        // (padding/truncation) and the worst box agreement. Recorded, never refused.
        int observedMaxObjectSlots = 0;
        long totalObjectSlots = 0;
        int framesPadded = 0;
        int framesTruncated = 0;
        double? boxAgreementMin = null;
        int? boxAgreementFrame = null;
        int? boxAgreementObject = null;

        SegmentationSourceOutcome Outcome(
            string code, string message, FailureClass failureClass, string? logMessage = null) =>
            new(tracks, prompts, code, message, failureClass, calls, hits, FailureLogMessage: logMessage);

        // ---- 1. Plan entitlement: before consent, before any URL, before any call. ----
        if (await EntitlementViolationAsync(source.OrganizationId, ct) is { } violation)
        {
            return Outcome(PlanGate.NotEntitledCode, violation, FailureClass.EntitlementDenied);
        }

        // ---- 2. Egress consent: the customer's footage leaves this host, or nothing runs. ----
        if (!config.AllowSourceMediaEgress)
        {
            string label = targets[0].Label.Trim();
            return Outcome(SegmentationFailureCodes.EgressRefused,
                $"Text-prompted segmentation sends the source clip to a hosted model, so it needs consent. " +
                $"\"{label}\" was configured for \"{source.SourceStorageKey}\" but allowSourceMediaEgress is false. " +
                "Turn it on for this step to accept that the clip leaves this host.",
                FailureClass.UserInput,
                // The customer's message names the target; the LOG names the clip and the target's length.
                $"A segmentation target ({label.Length} characters) was configured for \"{source.SourceStorageKey}\" " +
                "but allowSourceMediaEgress is false, so nothing left this host.");
        }

        // ---- 3. The provider: an explicit id, else the deployment's default; null fails closed. ----
        ResolvedImageAnalysisProvider? provider = await resolver.ResolveVideoSegmentationAsync(
            config.SegmentationProviderId, ct);
        if (provider is null)
        {
            return Outcome(SegmentationFailureCodes.NoProvider,
                config.SegmentationProviderId is { } explicitId
                    ? $"No enabled provider with the VideoSegmentation capability matches the id {explicitId:D} this step names, and no default VideoSegmentation provider is configured. Add one under Inference providers."
                    : "No default VideoSegmentation provider is configured, so this step's segmentation targets cannot run. Add a fal provider for video segmentation and mark it the default.",
                FailureClass.UserInput);
        }

        if (provider.Capability != InferenceProviderCapability.VideoSegmentation)
        {
            return Outcome(SegmentationFailureCodes.NoProvider,
                $"Provider \"{provider.Name}\" resolved for video segmentation but carries the {provider.Capability} capability.",
                FailureClass.UserInput);
        }

        if (clients is null)
        {
            // No client factory in this deployment: the capability ticket (T-003) supplies it. Refusing
            // here names the real cause rather than blaming the provider configuration.
            return Outcome(SegmentationFailureCodes.NoProvider,
                $"Provider \"{provider.Name}\" is configured for video segmentation but this engine build carries no video-segmentation client, so the target cannot run.",
                FailureClass.PlatformInternal);
        }

        // ---- 4. The cost cap: a longer source refuses, naming both numbers. Never truncates. ----
        int capSeconds = SegmentationLimits.ClampSeconds(config.MaxSegmentationSeconds);
        if (source.DurationSec > capSeconds + 1e-6)
        {
            return Outcome(SegmentationFailureCodes.SourceTooLong,
                string.Create(CultureInfo.InvariantCulture,
                    $"This clip is {source.DurationSec:0.##}s long but one segmentation pass is capped at {capSeconds}s (maxSegmentationSeconds). " +
                    $"Raise the cap — the vendor bills per 16 frames of input — or analyse a shorter clip. The clip was not truncated."),
                FailureClass.UserInput);
        }

        // ---- 4b. The FRAME budget: the hold prices 30 fps, the vendor bills the clip's real rate. ----
        // Refused before the presign and before any provider call, because a 60/120 fps source would spend
        // two to four times what the reservation bought and settle the organization negative.
        int frames = Math.Max(1, (int)Math.Ceiling(source.DurationSec * source.Fps));
        int pricedFrames = SegmentationCreditPricing.PricedFramesFor(capSeconds);
        if (frames > pricedFrames)
        {
            return Outcome(SegmentationFailureCodes.FrameBudgetExceeded,
                string.Create(CultureInfo.InvariantCulture,
                    $"This clip is {frames} frames at {source.Fps:0.##} fps, but one segmentation pass holds credit for at most {pricedFrames} frames: " +
                    $"the {capSeconds}s cap prices {SegmentationCreditPricing.AssumedMaxFps} fps, and the vendor bills every frame at the clip's own rate. " +
                    $"Trim the clip or use a lower frame rate: lowering MaxSegmentationSeconds shrinks the reservation but cannot make this clip fit; the clip was not truncated."),
                FailureClass.UserInput);
        }

        // ---- 4c. A rotated or over-size source goes to the vendor as a BOUNDED, UPRIGHT copy. ----
        // ffmpeg autorotates every engine decode, so masks must be in display space; the vendor does not
        // autorotate, so the rotation is baked into a copy first. And the vendor downloads the URL itself
        // and refuses a big file (fal: 422 file_download_error on a 563 MB original, prod 2026-11-02), so
        // that copy is size-bounded: every frame at the source's own display size (the RLE still indexes
        // the W x H grid every decode below uses), only the bitrate drops, and its size is checked before
        // it is stored. A rotated source never goes out unbounded either: the old near-lossless upright
        // mezzanine measured 4.5x its source. An unrotated source within the budget skips all of this:
        // same key, same URL, same hash (no transcode, no extra upload).
        string sourceHash = await SegmentationMaskStore.HashFileAsync(source.LocalVideoPath, ct);
        string vendorStorageKey = source.SourceStorageKey;
        if (presignedUrls is not null)
        {
            bool needsUpright = false;
            bool buildingVendorCopy = false;
            try
            {
                needsUpright = await SourceNeedsUprightAsync(source, workspace, ct);
                long sourceBytes = new FileInfo(source.LocalVideoPath).Length;
                if (needsUpright || sourceBytes > SegmentationLimits.VendorSourceMaxBytes)
                {
                    buildingVendorCopy = true;
                    if (!await store.VendorCopyExistsAsync(source.ProjectId, sourceHash, ct))
                    {
                        await reportProgress(string.Create(CultureInfo.InvariantCulture,
                            $"Preparing a copy of this {Math.Max(1, sourceBytes / 1_000_000)} MB clip for the segmentation provider"));
                        await MakeVendorCopyAsync(source, workspace, sourceHash, ct);
                    }

                    vendorStorageKey = SegmentationMaskStore.VendorCopyStorageKeyFor(source.ProjectId, sourceHash);
                    // Same frames, same size: masks cached from an unrotated original stay valid. A rotated
                    // source's copy is upright, so its masks live in the upright key space (shared with masks
                    // made from the earlier upright mezzanine, which is the same display-space picture).
                    if (needsUpright) sourceHash = UprightHashPrefix + sourceHash;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (VendorCopyTooLargeException ex)
            {
                logger.LogWarning("VideoAnalyze source {SourceIndex}: the bounded vendor copy is still too large: {Detail}", source.SourceIndex, ex.Message);
                return Outcome(SegmentationFailureCodes.EgressUnavailable,
                    string.Create(CultureInfo.InvariantCulture,
                        $"This clip is too large to send to the object-tracking provider even after compressing it (it came to {ex.ActualBytes / 1_000_000} MB; the limit is {SegmentationLimits.VendorSourceMaxBytes / 1_000_000} MB). Try a shorter clip. You have not been charged for this step."),
                    FailureClass.PlatformInternal,
                    $"The bounded vendor copy of \"{source.SourceStorageKey}\" for segmentation is {ex.ActualBytes} bytes, over {SegmentationLimits.VendorSourceMaxBytes}.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VideoAnalyze source {SourceIndex}: could not prepare the {Copy} for segmentation",
                    source.SourceIndex, buildingVendorCopy ? "vendor copy" : "orientation probe");
                return Outcome(SegmentationFailureCodes.EgressUnavailable,
                    !buildingVendorCopy || needsUpright
                        ? "This clip's orientation could not be read or straightened for object tracking, so tracking was skipped for it. You have not been charged for this step."
                        : "A copy of this clip sized for the object-tracking provider could not be prepared, so tracking was skipped for it. You have not been charged for this step.",
                    FailureClass.PlatformInternal,
                    // Operator detail only: the key and the tool's stderr/exit code never reach the customer.
                    $"The {(buildingVendorCopy ? "bounded vendor copy" : "orientation re-probe")} of \"{source.SourceStorageKey}\" for segmentation failed: {ex.Message}");
            }
        }

        // ---- 5. The short-TTL URL, minted last and only for work that will happen. ----
        Uri sourceUrl;
        try
        {
            if (presignedUrls is null)
            {
                return Outcome(SegmentationFailureCodes.EgressUnavailable,
                    "This deployment cannot mint a temporary download URL for the source clip (no presigner is configured, which is the case for local-disk storage), so a hosted model cannot read it. Configure S3-compatible object storage with presigning, or run the step without segmentation targets.",
                    FailureClass.PlatformInternal);
            }

            sourceUrl = new Uri(await presignedUrls.CreateGetAsync(source.ProjectId, vendorStorageKey, SourceUrlTtl));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VideoAnalyze source {SourceIndex}: could not presign {Key} for segmentation",
                source.SourceIndex, source.SourceStorageKey);
            return Outcome(SegmentationFailureCodes.EgressUnavailable,
                $"The temporary download URL for the source clip \"{source.SourceStorageKey}\" could not be created ({ex.Message}).",
                FailureClass.PlatformInternal);
        }

        VideoSegmentationFrameSize frameSize = new(source.Width, source.Height, frames);
        double threshold = SegmentationLimits.ClampDetectionThreshold(config.SegmentationDetectionThreshold);
        int maxObjects = SegmentationLimits.ClampObjects(config.MaxSegmentationObjects);
        TimeSpan timeout = TimeSpan.FromSeconds(SegmentationLimits.ClampProviderTimeout(config.SegmentationTimeoutSeconds));

        foreach (ObjectTrackTarget target in targets)
        {
            ct.ThrowIfCancellationRequested();

            string prompt = SanitizePrompt(target.Label);
            if (prompt.Length == 0) continue;

            prompts.Add(prompt);
            int seedFrame = SeedFrame(target, source.Fps);
            (VideoSegmentationBoxPrompt? box, VideoSegmentationPointPrompt? point) = BuildSeedPrompts(target, seedFrame);
            bool tapped = target.SeedPoint is not null;
            string seedText = DescribeSeed(box, point, seedFrame, tapped);

            // A tap names ONE object by where it is: the words (if the target sends none) are not part of the request or the key,
            // and the scope is always SingleTrack.
            string sentPrompt = target.NoWords ? string.Empty : prompt;
            ObjectMatchScope matchScope = tapped ? ObjectMatchScope.SingleTrack : target.MatchScope;

            SegmentationMaskKey key = new(
                sourceHash,
                sentPrompt,
                provider.ModelName,
                threshold,
                maxObjects,
                seedText,
                $"{provider.ProviderId}:{provider.Kind}:{provider.ModelName}",
                MatchScope: matchScope);

            string contentKey = store.ContentKey(key);
            await reportProgress(string.Create(CultureInfo.InvariantCulture,
                $"Segmenting \"{prompt}\" over {frames} frames of \"{source.SourceStorageKey}\""));

            // ---- 6a. The content cache: a hit is zero vendor calls and zero billed frames. ----
            // One answer can describe several distinct instances (D-23), so the cache holds a SET: ordinal
            // 0's entry carries InstanceCount and the rest are read back by ordinal. Any missing ordinal
            // is a miss, so the vendor is called and the set rewritten rather than half-served.
            if (await ReadCachedInstancesAsync(source.ProjectId, key, workspace, ct) is { } cachedSet)
            {
                hits++;
                foreach (SegmentationMaskEntry cached in cachedSet)
                {
                    tracks.Add(Track(cached, prompt, source.SourceIndex, truncated: false));
                }

                await reportProgress(string.Create(CultureInfo.InvariantCulture,
                    $"Reused {cachedSet.Count} stored mask(s) for \"{prompt}\" (no vendor call)"));
                continue;
            }

            // ---- 6b. The one vendor call. A refusal here FAILS the step: no silent degrade. ----
            VideoSegmentationResult result;
            try
            {
                calls++;
                result = await clients.Get(provider, frameSize).SegmentVideoAsync(
                    new VideoSegmentationRequest(
                        provider.ModelName,
                        sourceUrl,
                        sentPrompt,
                        point is null ? null : [point],
                        box is null ? null : [box],
                        maxObjects,
                        threshold,
                        timeout),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (VideoSegmentationBadResponseException ex)
            {
                // A malformed answer is the VENDOR's data, not a provider outage: report the code the
                // contract names (F-10). Same refund class, but the operator can tell them apart.
                return Outcome(SegmentationFailureCodes.BadResponse,
                    $"We could not use the segmentation provider's answer for '{prompt}': it came back in a shape we could not read safely, so we stopped this step rather than guess. This is a problem on our side or the provider's, not something you did wrong, and you have not been charged for it. Please run it again, and contact support if it keeps happening. (Detail: {ex.Message})",
                    FailureClass.PlatformInternal,
                    // The prompt's length and content key are the log-safe handle (F-1).
                    $"The segmentation provider '{provider.Name}' returned an unreadable answer for a {prompt.Length}-char prompt ({KeyPrefix(contentKey, ContentKeyLogPrefixChars)}): {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "VideoAnalyze source {SourceIndex}: the {Provider} segmentation call failed",
                    source.SourceIndex, provider.Name);
                return Outcome(SegmentationFailureCodes.ProviderError,
                    $"The segmentation provider \"{provider.Name}\" did not answer the prompt \"{prompt}\" ({ex.Message}).",
                    // B1: a platform row is refunded, the organization's own key is not.
                    provider.IsPlatformManaged ? FailureClass.PlatformProviderError : FailureClass.ByoProviderError,
                    // The log names the prompt's length and content key, never the prompt (F-1).
                    $"The segmentation provider \"{provider.Name}\" did not answer a {prompt.Length}-char prompt ({KeyPrefix(contentKey, ContentKeyLogPrefixChars)}): {ex.Message}");
            }

            if (result.Diagnostics is { } diagnostics)
            {
                observedMaxObjectSlots = Math.Max(observedMaxObjectSlots, diagnostics.ObservedMaxObjectSlots);
                totalObjectSlots += diagnostics.TotalObjectSlots;
                framesPadded += diagnostics.FramesPadded;
                framesTruncated += diagnostics.FramesTruncated;
                if (diagnostics.BoxAgreementMin is { } agreement
                    && (boxAgreementMin is not { } worst || agreement < worst))
                {
                    boxAgreementMin = agreement;
                    boxAgreementFrame = diagnostics.BoxAgreementFrame;
                    boxAgreementObject = diagnostics.BoxAgreementObject;
                }
            }

            // ---- 7. Decode, measure, write the mask video(s), upload them. ----
            // One answer can describe several distinct instances (D-23): each becomes its own track, its
            // own mask and its own facts, so one bad instance is never averaged away by a good one.
            IReadOnlyList<SegmentationMaskEntry> entries;
            try
            {
                entries = await WriteMasksAsync(
                    source, workspace, result, prompt, contentKey, key, frames, threshold, maxObjects, provider,
                    matchScope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (VideoSegmentationBadResponseException ex)
            {
                return Outcome(SegmentationFailureCodes.BadResponse,
                    $"The segmentation provider \"{provider.Name}\" returned an answer for \"{prompt}\" that does not fit a {source.Width}x{source.Height} frame ({ex.Message}).",
                    FailureClass.PlatformInternal,
                    $"The segmentation provider \"{provider.Name}\" returned an answer for a {prompt.Length}-char prompt ({KeyPrefix(contentKey, ContentKeyLogPrefixChars)}) that does not fit a {source.Width}x{source.Height} frame: {ex.Message}");
            }
            catch (SegmentationMaskWriteException ex)
            {
                return Outcome(SegmentationFailureCodes.BadResponse,
                    $"The masks the provider returned for \"{prompt}\" could not be turned into a mask video ({ex.Message}).",
                    FailureClass.PlatformInternal,
                    $"The masks the provider returned for a {prompt.Length}-char prompt ({KeyPrefix(contentKey, ContentKeyLogPrefixChars)}) could not be turned into a mask video: {ex.Message}");
            }

            if (tapped && entries.Count == 0)
            {
                // Nothing under the tap on the tapped frame. Never another object. The vendor was called, but the customer is not
                // billed for a find that found nothing: a platform-attributed failure is refunded.
                return Outcome(SegmentationFailureCodes.ObjectNotFoundAtPoint,
                    "We could not find an object where you tapped. Tap its centre, or draw a box.",
                    FailureClass.PlatformInternal,
                    $"No segmentation object held the tapped point on frame {seedFrame} ({KeyPrefix(contentKey, ContentKeyLogPrefixChars)}); the step failed as not found.");
            }

            foreach (SegmentationMaskEntry entry in entries)
            {
                tracks.Add(Track(entry, prompt, source.SourceIndex, truncated: entry.DistinctObjects >= maxObjects));
                await reportProgress(string.Create(CultureInfo.InvariantCulture,
                    $"Segmented \"{prompt}\": {entry.FrameCount} frames, {entry.Coverage:P0} of the picture covered, {entry.EmptyFrames} empty"));
            }
        }

        VideoSegmentationDiagnostics? segmentationDiagnostics =
            framesPadded > 0 || framesTruncated > 0 || boxAgreementMin is not null
                ? new VideoSegmentationDiagnostics(
                    observedMaxObjectSlots,
                    totalObjectSlots,
                    boxAgreementMin,
                    boxAgreementFrame,
                    boxAgreementObject,
                    framesPadded,
                    framesTruncated)
                : null;

        return new SegmentationSourceOutcome(
            tracks, prompts, ProviderCalls: calls, CacheHits: hits, Diagnostics: segmentationDiagnostics);
    }

    /// <summary>The plan's <c>segmentation</c> flag, as a sentence, or null when the plan allows it.</summary>
    private async Task<string?> EntitlementViolationAsync(Guid organizationId, CancellationToken ct)
    {
        if (organizationId == Guid.Empty)
        {
            return null;
        }

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService(typeof(IOrganizationEntitlements)) is not IOrganizationEntitlements entitlements)
            {
                return null;
            }

            OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
            return PlanGate.CheckSegmentation(set)?.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The one gate in this path that protects money: a lookup that fails refuses rather than
            // silently granting a paid feature.
            logger.LogWarning(ex, "VideoAnalyze: the plan entitlement lookup for {OrganizationId} failed", organizationId);
            return "The plan that covers this workspace could not be read, so text-prompted segmentation cannot start.";
        }
    }

    /// <summary>
    /// The content cache as a SET: ordinal 0's entry states how many instances the answer produced, and
    /// the rest are read back by ordinal. Any missing ordinal is a MISS — the vendor is called and the
    /// whole set rewritten — so a half-written cache can never be served as a complete answer.
    /// </summary>
    /// <returns>The cached instances, or null when the key is not fully cached.</returns>
    private async Task<IReadOnlyList<SegmentationMaskEntry>?> ReadCachedInstancesAsync(
        Guid projectId, SegmentationMaskKey key, IMediaWorkspace workspace, CancellationToken ct)
    {
        SegmentationMaskEntry? first = await store.TryGetAsync(projectId, store.ContentKey(key), workspace, ct);
        if (first is null)
        {
            return null;
        }

        int count = Math.Clamp(first.InstanceCount ?? 1, 1, MaxInstances);
        List<SegmentationMaskEntry> entries = [first];
        for (int ordinal = 1; ordinal < count; ordinal++)
        {
            SegmentationMaskEntry? next = await store.TryGetAsync(
                projectId, store.ContentKey(key with { InstanceOrdinal = ordinal }), workspace, ct);
            if (next is null)
            {
                return null;
            }

            entries.Add(next);
        }

        return entries;
    }

    /// <summary>
    /// Writes ONE mask video and entry per instance the prompt matched (T-021/D-23), in the deterministic
    /// order <see cref="ChooseInstances"/> returns them. Each instance's content key carries its ordinal;
    /// ordinal 0 also carries <see cref="SegmentationMaskEntry.InstanceCount"/>, which is how a repeat run
    /// reads the whole set back from the cache without calling the vendor.
    /// </summary>
    private async Task<IReadOnlyList<SegmentationMaskEntry>> WriteMasksAsync(
        SegmentationSourceInput source,
        IMediaWorkspace workspace,
        VideoSegmentationResult result,
        string prompt,
        string contentKey,
        SegmentationMaskKey key,
        int expectedFrames,
        double threshold,
        int maxObjects,
        ResolvedImageAnalysisProvider provider,
        ObjectMatchScope matchScope,
        CancellationToken ct)
    {
        if (result.Width <= 0 || result.Height <= 0)
        {
            throw new VideoSegmentationBadResponseException($"the answer reports a {result.Width}x{result.Height} frame");
        }

        // The mask is conformed to the PICTURE's own geometry downstream, so a mask that is not at the
        // source's size can never be aligned: refuse rather than rescale (a rescale would silently move
        // the effect by a subpixel-per-frame drift).
        if (result.Width != source.Width || result.Height != source.Height)
        {
            throw new VideoSegmentationBadResponseException(
                $"the answer reports a {result.Width}x{result.Height} frame but the source probes {source.Width}x{source.Height}");
        }

        Dictionary<int, VideoSegmentationFrame> byIndex = [];
        foreach (VideoSegmentationFrame frame in result.Frames)
        {
            byIndex[frame.FrameIndex] = frame;
        }

        // T-020/T-021: the vendor renumbers an object it re-acquires (always merged), and one prompt can
        // match several DISTINCT instances (MatchScope decides how many become tracks).
        IReadOnlyList<SlotSelection> instances = ChooseInstances(byIndex, key, source, expectedFrames, matchScope);
        int reportedObjects = byIndex.Values.SelectMany(f => f.Objects).Select(o => o.ObjectId).Distinct().Count();

        List<SegmentationMaskEntry> entries = [];
        for (int ordinal = 0; ordinal < instances.Count; ordinal++)
        {
            SegmentationMaskKey instanceKey = key with { InstanceOrdinal = ordinal };
            entries.Add(await WriteOneMaskAsync(
                source, workspace, byIndex, instances[ordinal], prompt,
                store.ContentKey(instanceKey),
                instanceCount: ordinal == 0 ? instances.Count : null,
                reportedObjects, expectedFrames, threshold, provider, result.Model, ordinal, ct));
        }

        return entries;
    }

    /// <summary>One instance's mask video and entry: the per-frame decode, measure, encode and upload.</summary>
    private async Task<SegmentationMaskEntry> WriteOneMaskAsync(
        SegmentationSourceInput source,
        IMediaWorkspace workspace,
        Dictionary<int, VideoSegmentationFrame> byIndex,
        SlotSelection selection,
        string prompt,
        string contentKey,
        int? instanceCount,
        int reportedObjects,
        int expectedFrames,
        double threshold,
        ResolvedImageAnalysisProvider provider,
        string model,
        int ordinal,
        CancellationToken ct)
    {
        List<double> coveragePerFrame = [];
        List<double[]> boxRows = [];
        int empty = 0, longestGap = 0, gap = 0, firstCovered = -1, lastCovered = -1;
        double scoreSum = 0;
        int scoreCount = 0;

        for (int frame = 0; frame < expectedFrames; frame++)
        {
            byte[] mask = DecodeMergedSlots(byIndex, frame, selection.Slots, source.Width, source.Height);
            Sam31VideoRleDecoder.SegmentationMaskMeasurement measured =
                Sam31VideoRleDecoder.Measure(mask, source.Width, source.Height);

            coveragePerFrame.Add(measured.Coverage);
            if (measured.CoveredPixels == 0)
            {
                empty++;
                longestGap = Math.Max(longestGap, ++gap);
            }
            else
            {
                gap = 0;
                if (firstCovered < 0) firstCovered = frame;
                lastCovered = frame;
                boxRows.Add([frame / source.Fps, measured.X, measured.Y, measured.W, measured.H]);
            }

            if (selection.Anchor >= 0
                && byIndex.TryGetValue(frame, out VideoSegmentationFrame? reported)
                && reported.Scores is { } scores)
            {
                for (int i = 0; i < reported.Objects.Count && i < scores.Count; i++)
                {
                    if (reported.Objects[i].ObjectId != selection.Anchor) continue;
                    scoreSum += Math.Clamp(scores[i], 0, 1);
                    scoreCount++;
                    break;
                }
            }

            await workspace.WriteBytesAsync(
                SegmentationMaskArgvBuilder.FrameVirtualPath(frame + 1),
                SegmentationMaskArgvBuilder.EncodePbmFrame(mask, source.Width, source.Height),
                ct);
        }

        // ---- One first-party ffmpeg call turns the frames into the mask video. ----
        string pattern = await workspace.MaterializeAsync(SegmentationMaskArgvBuilder.FramePattern, ct);
        string outputPath = await workspace.MaterializeAsync(MaskVirtualPath, ct);
        VideoToolResult encoded = await workspace.RunFfmpegAsync(
            SegmentationMaskArgvBuilder.BuildEncodeArgs(pattern, source.Fps, outputPath),
            EncodeTimeout(expectedFrames),
            ct);

        if (encoded.ExitCode != 0)
        {
            throw new SegmentationMaskWriteException($"ffmpeg exited {encoded.ExitCode}: {Tail(encoded.StdErr)}");
        }

        double coverage = coveragePerFrame.Count == 0 ? 0 : coveragePerFrame.Average();
        double confidence = scoreCount > 0
            ? Math.Clamp(scoreSum / scoreCount, 0, 1)
            : Math.Min(1, coverage * 4);

        SegmentationMaskEntry entry = new(
            contentKey,
            MaskStorageKey: string.Empty,
            MaskContentHash: string.Empty,
            source.Width,
            source.Height,
            expectedFrames,
            source.Fps,
            coverage,
            confidence,
            firstCovered < 0 ? 0 : firstCovered / source.Fps,
            lastCovered < 0 ? source.DurationSec : lastCovered / source.Fps,
            provider.ModelName,
            $"{provider.ProviderId}:{provider.Kind}:{provider.ModelName}",
            empty,
            longestGap,
            threshold,
            source.Fps,
            firstCovered < 0 ? null : firstCovered,
            lastCovered < 0 ? null : lastCovered,
            InstanceCount: instanceCount)
        {
            Boxes = Sample(boxRows),
            DistinctObjects = reportedObjects
        };

        SegmentationMaskAssessment assessment = SegmentationMaskQualityPolicy.Assess(
            expectedFrames, empty, longestGap, source.Fps,
            firstCovered < 0 ? null : firstCovered,
            lastCovered < 0 ? null : lastCovered);

        // The prompt is the customer's own sentence about the private subject of their footage: it never
        // reaches a log line. Its LENGTH and the content key (a SHA-256 that is also the mask's storage
        // name) are the stable correlation handle that survives after the project is purged. The
        // leading/trailing gaps are added so a head gap is visible in the log without a per-frame dump.
        logger.LogInformation(
            "VideoAnalyze source {SourceIndex}: segmented a {PromptLength}-char prompt ({ContentKey}) instance {Ordinal} into {Frames} mask frames ({Empty} empty, longest gap {Gap}, leading {Leading}, trailing {Trailing}) via {Model}",
            source.SourceIndex, prompt.Length, KeyPrefix(contentKey, ContentKeyLogPrefixChars), ordinal, expectedFrames, empty,
            longestGap, assessment.LeadingGapFrames, assessment.TrailingGapFrames, model);

        if (assessment.Quality != SegmentationMaskQuality.Clean)
        {
            // A gap the customer will see in the step output is also worth a warning here, so an operator
            // reading logs can find it without the raw answer (which is not retained). Prompt-free (F-1);
            // the fact sentence names only the frames and the seconds.
            logger.LogWarning(
                "VideoAnalyze source {SourceIndex}: a {PromptLength}-char prompt ({ContentKey}) produced a {Quality} mask over {Frames} frames for instance {Ordinal}: {Fact}",
                source.SourceIndex, prompt.Length, KeyPrefix(contentKey, ContentKeyLogPrefixChars),
                assessment.Quality, expectedFrames, ordinal, SegmentationMaskQualityPolicy.Describe(assessment));
        }

        return await store.PutAsync(source.ProjectId, workspace, outputPath, MaskVirtualPath, entry, ct);
    }

    /// <summary>
    /// Which vendor slots describe the prompted object: the ANCHOR plus every re-acquisition that joins
    /// it (T-020, D-21; the measured answer is `trials/fal/T020/analysis.md`).
    /// <para>
    /// <b>Why more than one slot.</b> <c>fal-ai/sam-3-1/video-rle</c> assigns a fresh <c>object_id</c>
    /// whenever it re-acquires an object. On the clip this ticket exists for it renumbered the same
    /// speedometer three times in 3.75 s — id 0 on frames 7–17, id 1 on 18–199, id 2 on 211–224 — so a
    /// one-slot reading renders <c>empty</c> everywhere outside that one id's span, which is exactly the
    /// reported "empty for the first 211 frames, effect starting at 3.52 s".
    /// </para>
    /// <para>
    /// <b>The anchor is chosen exactly as before</b> (sampled over up to <see cref="SlotSampleFrames"/>
    /// frames; a seeded target matches its box, an unseeded one takes the most covered pixels), so nothing
    /// about the old selection changes for a clip the vendor does not renumber.
    /// </para>
    /// <para>
    /// <b>The merge is deliberately narrow.</b> A slot joins only when it shares NO frame with the track
    /// (two slots on one frame are two instances of the prompted class at that moment — the live
    /// F2/FACE answer returns 17 heavily overlapping slots for one prompt, and merging those would paint
    /// far more of the picture than the object) AND it joins the track's span within
    /// <see cref="ReacquireGapSeconds"/> at either end. Fixed point over the ids in ascending order, so
    /// the same answer always yields the same set and the content cache stays deterministic.
    /// </para>
    /// </summary>
    /// <returns>The anchor (its scores are the track's confidence) and every merged slot, ascending.</returns>
    internal static IReadOnlyList<SlotSelection> ChooseInstances(
        Dictionary<int, VideoSegmentationFrame> byIndex,
        SegmentationMaskKey key,
        SegmentationSourceInput source,
        int expectedFrames,
        ObjectMatchScope matchScope)
    {
        int? anchor = ChooseAnchor(byIndex, key, source, expectedFrames);
        if (anchor is not { } chosen)
        {
            return [];
        }

        // Presence per id, from the answer itself: no decode, so this costs nothing extra.
        Dictionary<int, (int First, int Last, HashSet<int> Frames)> presence = [];
        foreach ((int frameIndex, VideoSegmentationFrame reported) in byIndex)
        {
            foreach (VideoSegmentationObjectRle candidate in reported.Objects)
            {
                if (!presence.TryGetValue(candidate.ObjectId, out (int First, int Last, HashSet<int> Frames) entry))
                {
                    entry = (frameIndex, frameIndex, []);
                }

                entry.Frames.Add(frameIndex);
                entry.First = Math.Min(entry.First, frameIndex);
                entry.Last = Math.Max(entry.Last, frameIndex);
                presence[candidate.ObjectId] = entry;
            }
        }

        // The vendor's id count drives the work, so it is bounded BEFORE anything else: an answer that
        // names more ids than any real clip needs is refused, not grouped.
        if (presence.Count > MaxInstances * MaxMergedSlots)
        {
            throw new VideoSegmentationBadResponseException(
                $"the answer named {presence.Count} object ids, over this reader's {MaxInstances * MaxMergedSlots}-id grouping ceiling");
        }

        int window = (int)Math.Ceiling(ReacquireGapSeconds * (source.Fps > 0 ? source.Fps : 1));

        // Each id's own size, sampled (bounded decode). It decides the absorb rule and the order the
        // greedy visits ids in, so the dominant object forms the first instance.
        Dictionary<int, long> area = [];
        foreach ((int id, (int First, int Last, HashSet<int> Frames) value) in presence)
        {
            area[id] = MedianCoveredPixels(byIndex, value.Frames, id, source);
        }

        // The anchor's instance is FIRST, so the old primary track keeps artifact position g0.
        List<InstanceBuilder> instances = [new InstanceBuilder(chosen, presence[chosen], area[chosen])];

        foreach (int id in presence.Keys
            .Where(id => id != chosen)
            .OrderByDescending(id => area[id]).ThenBy(id => id))
        {
            (int First, int Last, HashSet<int> Frames) candidate = presence[id];
            bool placed = false;
            foreach (InstanceBuilder instance in instances)
            {
                // Interval bounds first (cheap): a slot whose span cannot meet the instance's cannot
                // join it in time. Only the time-adjacent cases reach the exact frame intersection.
                bool intervalsMeet = candidate.First <= instance.Last && candidate.Last >= instance.First;
                if (!intervalsMeet)
                {
                    if (JoinsWithin(candidate, instance.First, instance.Last, window))
                    {
                        instance.Add(id, candidate, area[id]);
                        placed = true;
                        break;
                    }

                    continue;
                }

                // The spans meet: a slot under a quarter of the instance's area is a PART of it (the live
                // F2/FACE answer emits 17 slots for one prompt), a comparable one is another instance.
                if (area[id] * 1.0 >= InstanceAbsorbMaxFraction * instance.Area)
                {
                    continue;
                }

                if (candidate.Frames.Overlaps(instance.Frames))
                {
                    instance.Add(id, candidate, area[id]);
                    placed = true;
                    break;
                }
            }

            if (placed)
            {
                continue;
            }

            instances.Add(new InstanceBuilder(id, candidate, area[id]));
            if (instances.Count > MaxInstances)
            {
                throw new VideoSegmentationBadResponseException(
                    $"the answer's object slots describe more than {MaxInstances} distinct instances, over this reader's ceiling");
            }
        }

        return matchScope == ObjectMatchScope.SingleTrack
            ? [instances[0].ToSelection()]
            : [.. instances.Select(instance => instance.ToSelection())];
    }

    /// <summary>True when a disjoint slot joins the span at either end within the re-acquisition window.</summary>
    private static bool JoinsWithin((int First, int Last, HashSet<int> Frames) candidate, int first, int last, int window) =>
        (candidate.First > last && candidate.First - last - 1 <= window) ||
        (candidate.Last < first && first - candidate.Last - 1 <= window);

    /// <summary>
    /// One id's own size: the median covered pixels over up to <see cref="SlotSampleFrames"/> of the frames
    /// it appears on. Bounded decode — the absorb rule compares relative sizes, not exact ones.
    /// </summary>
    private static long MedianCoveredPixels(
        Dictionary<int, VideoSegmentationFrame> byIndex,
        HashSet<int> frames,
        int id,
        SegmentationSourceInput source)
    {
        List<int> ordered = [.. frames.Order()];
        int step = Math.Max(1, ordered.Count / SlotSampleFrames);
        List<long> areas = [];
        foreach (int frame in ordered.Where((_, i) => i % step == 0).Take(SlotSampleFrames))
        {
            foreach (VideoSegmentationObjectRle candidate in byIndex[frame].Objects)
            {
                if (candidate.ObjectId != id)
                {
                    continue;
                }

                areas.Add(Sam31VideoRleDecoder.Measure(
                    Sam31VideoRleDecoder.Decode(candidate.Rle, id, source.Width, source.Height).Pixels,
                    source.Width,
                    source.Height).CoveredPixels);
                break;
            }
        }

        if (areas.Count == 0)
        {
            return 0;
        }

        areas.Sort();
        return areas[areas.Count / 2];
    }

    /// <summary>One instance being built: the anchor's slot plus everything that belongs to the same object.</summary>
    private sealed class InstanceBuilder
    {
        public InstanceBuilder(int anchor, (int First, int Last, HashSet<int> Frames) presence, long area)
        {
            Anchor = anchor;
            Ids = [anchor];
            Frames = [.. presence.Frames];
            First = presence.First;
            Last = presence.Last;
            Area = Math.Max(1, area);
        }

        public int Anchor { get; }

        public HashSet<int> Ids { get; }

        public HashSet<int> Frames { get; }

        public int First { get; private set; }

        public int Last { get; private set; }

        public long Area { get; private set; }

        public void Add(int id, (int First, int Last, HashSet<int> Frames) presence, long area)
        {
            Ids.Add(id);
            Frames.UnionWith(presence.Frames);
            First = Math.Min(First, presence.First);
            Last = Math.Max(Last, presence.Last);
            Area = Math.Max(Area, area);
            if (Ids.Count > MaxMergedSlots)
            {
                throw new VideoSegmentationBadResponseException(
                    $"the answer's object slots merge into one object under more than {MaxMergedSlots} ids; " +
                    "that is more re-acquisitions than this reader will follow, so the answer was refused " +
                    "rather than merged");
            }
        }

        public SlotSelection ToSelection() => new(Anchor, Ids);
    }

    /// <summary>
    /// The slot the prompted object STARTS from: sampled over up to <see cref="SlotSampleFrames"/> reported
    /// frames — never the whole clip — so the decision costs a bounded decode. A seeded target matches its
    /// box against each slot's box on those frames; an unseeded one takes the slot covering the most pixels.
    /// </summary>
    private static int? ChooseAnchor(
        Dictionary<int, VideoSegmentationFrame> byIndex,
        SegmentationMaskKey key,
        SegmentationSourceInput source,
        int expectedFrames)
    {
        // A tap is matched on the TAPPED frame, not on the first frames the object appears on.
        if (TapSeedOf(key) is { } tap)
        {
            return ChooseAnchorAtTap(byIndex, tap, source);
        }

        List<int> reported = [.. byIndex.Keys.Where(i => byIndex[i].Objects.Count > 0).Order()];
        if (reported.Count == 0)
        {
            return null;
        }

        int step = Math.Max(1, reported.Count / SlotSampleFrames);
        List<int> samples = [.. reported.Where((_, i) => i % step == 0).Take(SlotSampleFrames)];
        _ = expectedFrames;

        Dictionary<int, long> covered = [];
        Dictionary<int, Sam31VideoRleDecoder.SegmentationBox?> firstBox = [];

        foreach (int frame in samples)
        {
            foreach (VideoSegmentationObjectRle candidate in byIndex[frame].Objects)
            {
                Sam31VideoRleDecoder.SegmentationMaskMeasurement measured = Sam31VideoRleDecoder.Measure(
                    Sam31VideoRleDecoder.Decode(candidate.Rle, candidate.ObjectId, source.Width, source.Height).Pixels,
                    source.Width,
                    source.Height);

                covered[candidate.ObjectId] = covered.GetValueOrDefault(candidate.ObjectId) + measured.CoveredPixels;
                if (!firstBox.ContainsKey(candidate.ObjectId))
                {
                    firstBox[candidate.ObjectId] = measured.CoveredPixels > 0
                        ? new Sam31VideoRleDecoder.SegmentationBox(measured.X, measured.Y, measured.W, measured.H)
                        : null;
                }
            }
        }

        List<Sam31VideoRleDecoder.SegmentationObjectCandidate> candidates = [.. covered.Keys
            .Order()
            .Select(id => new Sam31VideoRleDecoder.SegmentationObjectCandidate(
                id, covered[id], firstBox.GetValueOrDefault(id)))];

        return Sam31VideoRleDecoder.ChooseObjectId(candidates, SeedBoxOf(key));
    }

    /// <summary>A tap parsed back out of the mask key's seed text: the point, the frame and an optional drawn box.</summary>
    internal sealed record TapSeed(double X, double Y, int Frame, Sam31VideoRleDecoder.SegmentationBox? Box);

    internal static TapSeed? TapSeedOf(SegmentationMaskKey key)
    {
        if (!key.Seed.StartsWith(TapRuleVersion + ";p:", StringComparison.Ordinal)) return null;

        string[] sections = key.Seed.Split(';');
        string[] pointAndFrame = sections[1][2..].Split('@');
        string[] xy = pointAndFrame[0].Split(',');
        if (pointAndFrame.Length != 2 || xy.Length != 2
            || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            || !int.TryParse(pointAndFrame[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
        {
            return null;
        }

        Sam31VideoRleDecoder.SegmentationBox? box = null;
        if (sections.Length > 2 && sections[2].StartsWith("b:", StringComparison.Ordinal))
        {
            string[] parts = sections[2][2..].Split(',');
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double bx)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double by)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double bw)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double bh))
            {
                box = new Sam31VideoRleDecoder.SegmentationBox(bx, by, bw, bh);
            }
        }

        return new TapSeed(x, y, frame, box);
    }

    /// <summary>
    /// The vendor object a tap names: on the tapped frame only, the object whose mask holds the point (the smallest when
    /// several do, so a person wins over the room around them); failing that, when a box was drawn, the object whose box
    /// overlaps it best. Nothing there means null — NEVER another object, and never the biggest.
    /// </summary>
    private static int? ChooseAnchorAtTap(
        Dictionary<int, VideoSegmentationFrame> byIndex, TapSeed tap, SegmentationSourceInput source)
    {
        if (!byIndex.TryGetValue(tap.Frame, out VideoSegmentationFrame? frame) || frame.Objects.Count == 0)
        {
            return null;
        }

        int px = (int)Math.Round(Math.Clamp(tap.X, 0, 1) * (source.Width - 1), MidpointRounding.AwayFromZero);
        int py = (int)Math.Round(Math.Clamp(tap.Y, 0, 1) * (source.Height - 1), MidpointRounding.AwayFromZero);

        int? holding = null;
        long holdingArea = long.MaxValue;
        int? overlapping = null;
        double overlapScore = 0;

        foreach (VideoSegmentationObjectRle candidate in frame.Objects.OrderBy(o => o.ObjectId))
        {
            byte[] pixels = Sam31VideoRleDecoder.Decode(candidate.Rle, candidate.ObjectId, source.Width, source.Height).Pixels;
            Sam31VideoRleDecoder.SegmentationMaskMeasurement measured = Sam31VideoRleDecoder.Measure(pixels, source.Width, source.Height);
            if (measured.CoveredPixels == 0) continue;

            if (pixels[(py * source.Width) + px] != 0 && measured.CoveredPixels < holdingArea)
            {
                holding = candidate.ObjectId;
                holdingArea = measured.CoveredPixels;
            }

            if (tap.Box is { } drawn)
            {
                double score = new Sam31VideoRleDecoder.SegmentationBox(measured.X, measured.Y, measured.W, measured.H).MatchScore(drawn);
                if (score > overlapScore)
                {
                    overlapping = candidate.ObjectId;
                    overlapScore = score;
                }
            }
        }

        return holding ?? overlapping;
    }

    /// <summary>
    /// The frame's mask as the union of every merged slot present on it. Because merged slots are
    /// disjoint in time at most one is present in practice; the union is written anyway so the rule is
    /// enforced here rather than assumed by the caller.
    /// </summary>
    internal static byte[] DecodeMergedSlots(
        Dictionary<int, VideoSegmentationFrame> byIndex, int frame, IReadOnlySet<int> slots, int width, int height)
    {
        byte[] mask = new byte[width * height];
        if (slots.Count == 0 || !byIndex.TryGetValue(frame, out VideoSegmentationFrame? reported))
        {
            return mask;
        }

        foreach (VideoSegmentationObjectRle candidate in reported.Objects)
        {
            if (!slots.Contains(candidate.ObjectId))
            {
                continue;
            }

            byte[] pixels = Sam31VideoRleDecoder.Decode(candidate.Rle, candidate.ObjectId, width, height).Pixels;
            for (int i = 0; i < mask.Length; i++)
            {
                if (pixels[i] != 0)
                {
                    mask[i] = pixels[i];
                }
            }
        }

        return mask;
    }

    /// <summary>The first <paramref name="chars"/> characters of <paramref name="key"/>, never more than it has.</summary>
    private static string KeyPrefix(string key, int chars) =>
        key.Length <= chars ? key : key[..chars];

    /// <summary>The artifact track for one stored mask.</summary>
    private static VideoSegmentationTrack Track(
        SegmentationMaskEntry entry, string prompt, int sourceIndex, bool truncated) =>
        new(
            Id: string.Empty,
            SourceIndex: sourceIndex,
            Prompt: prompt,
            StartSec: entry.StartSec,
            EndSec: entry.EndSec,
            FrameCount: entry.FrameCount,
            Fps: entry.Fps,
            Coverage: entry.Coverage,
            Confidence: entry.Confidence,
            MaskStorageKey: entry.MaskStorageKey,
            MaskContentHash: entry.MaskContentHash,
            Model: entry.Model,
            ProviderFingerprint: entry.ProviderFingerprint,
            BoxKeyframes:
            [
                .. (entry.Boxes ?? []).Select(row =>
                    new VideoSegmentationBoxFrame(row[0], row[1], row[2], row[3], row[4]))
            ],
            Facts: new VideoSegmentationFacts(
                entry.EmptyFrames, entry.LongestGapFrames, entry.DetectionThreshold, truncated,
                entry.FirstNonEmptyFrame, entry.LastNonEmptyFrame),
            DistinctObjects: entry.DistinctObjects);

    /// <summary>
    /// The prompt as the model receives it: control characters and whitespace runs collapse to single
    /// spaces, and the result is capped at <see cref="MaxPromptChars"/>. It never reaches an ffmpeg argv
    /// (it is model input only) and never carries a newline, and it is stable, so the content key it
    /// feeds is stable too.
    /// </summary>
    public static string SanitizePrompt(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        StringBuilder cleaned = new(label.Length);
        bool space = false;
        foreach (char c in label.Trim())
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && cleaned.Length > 0)
            {
                cleaned.Append(' ');
            }

            space = false;
            cleaned.Append(c);
            if (cleaned.Length >= MaxPromptChars) break;
        }

        return cleaned.ToString();
    }

    private static int SeedFrame(ObjectTrackTarget target, double fps)
    {
        if (target.AtSec is not { } at || at <= 0 || fps <= 0) return 0;
        return (int)Math.Round(at * fps, MidpointRounding.AwayFromZero);
    }

    private static (VideoSegmentationBoxPrompt? Box, VideoSegmentationPointPrompt? Point) BuildSeedPrompts(
        ObjectTrackTarget target, int frame)
    {
        if (target.SeedPoint is { } tap)
        {
            // A tap: the point is exactly where the person tapped; a box is sent only when one was drawn.
            VideoSegmentationBoxPrompt? drawn = target.Region is { } region ? BoxOf(region, frame) : null;
            return (drawn, new VideoSegmentationPointPrompt(Math.Clamp(tap.X, 0, 1), Math.Clamp(tap.Y, 0, 1), frame));
        }

        if (target.Region is not { } quad) return (null, null);

        if (BoxOf(quad, frame) is not { } regionBox) return (null, null);
        double x = regionBox.X, y = regionBox.Y, width = regionBox.Width, height = regionBox.Height;

        return (
            regionBox,
            new VideoSegmentationPointPrompt(x + (width / 2), y + (height / 2), frame));
    }

    private static VideoSegmentationBoxPrompt? BoxOf(VideoInsertSurfaceQuad quad, int frame)
    {
        double minX = Math.Min(Math.Min(quad.X0, quad.X1), Math.Min(quad.X2, quad.X3));
        double maxX = Math.Max(Math.Max(quad.X0, quad.X1), Math.Max(quad.X2, quad.X3));
        double minY = Math.Min(Math.Min(quad.Y0, quad.Y1), Math.Min(quad.Y2, quad.Y3));
        double maxY = Math.Max(Math.Max(quad.Y0, quad.Y1), Math.Max(quad.Y2, quad.Y3));

        double width = Math.Clamp(maxX - minX, 0, 1);
        double height = Math.Clamp(maxY - minY, 0, 1);
        if (width <= 0 || height <= 0) return null;

        double x = Math.Clamp(minX, 0, 1 - width);
        double y = Math.Clamp(minY, 0, 1 - height);

        return new VideoSegmentationBoxPrompt(x, y, width, height, frame);
    }

    /// <summary>Bumped when the rule that turns a tap into an object changes, so a mask chosen by an older rule is never reused.</summary>
    internal const string TapRuleVersion = "tap2";

    private static string DescribeSeed(
        VideoSegmentationBoxPrompt? box, VideoSegmentationPointPrompt? point, int frame, bool tapped)
    {
        if (tapped && point is not null)
        {
            string boxText = box is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $";b:{box.X:0.####},{box.Y:0.####},{box.Width:0.####},{box.Height:0.####}");
            return string.Create(CultureInfo.InvariantCulture,
                $"{TapRuleVersion};p:{point.X:0.####},{point.Y:0.####}@{frame}{boxText}");
        }

        if (box is null || point is null) return "-";

        return string.Create(CultureInfo.InvariantCulture,
            $"b:{box.X:0.####},{box.Y:0.####},{box.Width:0.####},{box.Height:0.####}@{frame};p:{point.X:0.####},{point.Y:0.####}@{frame}");
    }

    private static Sam31VideoRleDecoder.SegmentationBox? SeedBoxOf(SegmentationMaskKey key)
    {
        // The seed is already canonical text in the key; parsing it back keeps the store's shape free of
        // a second box record. A key with no seed carries "-".
        if (!key.Seed.StartsWith("b:", StringComparison.Ordinal)) return null;

        string[] parts = key.Seed[2..].Split('@')[0].Split(',');
        if (parts.Length != 4) return null;

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            ? new Sam31VideoRleDecoder.SegmentationBox(x, y, w, h)
            : null;
    }

    private static IReadOnlyList<double[]> Sample(List<double[]> rows)
    {
        if (rows.Count <= MaxBoxKeyframes) return rows;

        List<double[]> sampled = new(MaxBoxKeyframes);
        for (int i = 0; i < MaxBoxKeyframes; i++)
        {
            sampled.Add(rows[(int)Math.Round(i * (rows.Count - 1) / (double)(MaxBoxKeyframes - 1))]);
        }

        return sampled;
    }

    /// <summary>The encode budget: one grey8 frame per input frame, comfortably above what writing a mask costs.</summary>
    private static TimeSpan EncodeTimeout(int frames) =>
        TimeSpan.FromSeconds(Math.Clamp(60 + (frames * 0.05), 60, 1800));

    private static string Tail(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(no output)" :
        text.Length <= 400 ? text.Trim() : text[^400..].Trim();
}

/// <summary>Thrown when the mask frames could not be turned into the mask video.</summary>
public sealed class SegmentationMaskWriteException(string message) : Exception(message);
