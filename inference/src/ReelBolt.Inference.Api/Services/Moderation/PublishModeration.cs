using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>The answer the publish/egress gate gives one route: allowed, blocked, pending, or unavailable (refuse).</summary>
/// <param name="Final">
/// True only for a refusal that is certain for these bytes (<see cref="UnavailableFinal"/>): trying again will not help.
/// Sent to clients as the stable field <c>final</c> of the refusal body, so nobody tells it from an outage by its wording.
/// </param>
public sealed record PublishModerationOutcome(bool Allowed, string? Code, string Message, bool Final = false, bool TooLong = false)
{
    public static readonly PublishModerationOutcome AllowedResult = new(true, null, string.Empty);

    public static PublishModerationOutcome Blocked(string message) =>
        new(false, "CONTENT_BLOCKED", string.IsNullOrWhiteSpace(message)
            ? "This output may violate the content policy."
            : message);

    /// <summary>A project under the output hold: neutral wording, the same as the output gate's, no hint of why.</summary>
    public static PublishModerationOutcome Held() =>
        new(false, "CONTENT_MODERATION_UNAVAILABLE", ReelBolt.Shared.Storage.OutputHoldResponse.Message);

    public static PublishModerationOutcome Unavailable() =>
        new(false, "CONTENT_MODERATION_UNAVAILABLE", "Publication is temporarily unavailable.");

    /// <summary>
    /// The refusal of a file that could not be checked and never will be as it stands (two audio tracks, a false length, not a
    /// playable video). Same code as <see cref="Unavailable"/>, but it does not promise that trying again helps.
    /// </summary>
    public static PublishModerationOutcome UnavailableFinal(bool tooLong = false) =>
        tooLong
            ? new(false, "CONTENT_MODERATION_UNAVAILABLE",
                "This file cannot be used because it is longer than 12 hours, which is the most that can be checked. "
                + "Check that no audio track runs much longer than the video, shorten it and use that instead.", Final: true, TooLong: true)
            : new(false, "CONTENT_MODERATION_UNAVAILABLE",
                "This file cannot be used because it could not be checked as it is. Re-export it in a common format and use that instead.", Final: true);

    /// <summary>
    /// The same refusal for one clip of a hand-off package: it names the clip and says what to do (re-export it in a common format, or
    /// take the package without media).
    /// </summary>
    public static PublishModerationOutcome UnavailableFinalClip(string clipName, bool tooLong = false) =>
        tooLong
            ? new(false, "CONTENT_MODERATION_UNAVAILABLE",
                $"The clip \"{clipName}\" cannot be put in a package with media because it is longer than 12 hours, which is the most that can be checked. "
                + "Check that no audio track runs much longer than the video, shorten it and use that instead, or take the package without media.",
                Final: true, TooLong: true)
            : new(false, "CONTENT_MODERATION_UNAVAILABLE",
            $"The clip \"{clipName}\" cannot be put in a package with media because it could not be checked as it is. "
            + "Re-export it in a common format and use that instead, or take the package without media.",
            Final: true);

    /// <summary>The output has no verdict for its current bytes yet. Refused whatever FailClosedAtPublish says.</summary>
    public static PublishModerationOutcome Pending() =>
        new(false, PendingCode, "This output is still being checked. Try again in a few seconds.");

    public const string PendingCode = "CONTENT_MODERATION_PENDING";
}

/// <summary>The smallest seam over <see cref="PublishModeration"/> for callers that only ask whether stored bytes may go to another use.</summary>
public interface IStoredCopyGate
{
    /// <summary>Null when the stored copy may be used; otherwise the refusal.</summary>
    Task<PublishModerationOutcome?> GateStoredCopyAsync(Guid projectId, string storageKey, CancellationToken ct);
}

/// <summary>
/// The publish/egress gate. This is the one fail-closed surface: a Block, an unavailable call, an
/// unclassified answer, an unrecorded Block/Review, or an output with no verdict yet refuses
/// publication. A Review allows it (the review queue blocks nobody) and the gate has already written
/// its pending row.
/// </summary>
/// <remarks>
/// <see cref="EvaluateObjectAsync"/> only reads: one metadata HEAD for the object's version and one
/// row from <c>output_moderation_verdicts</c>. It never downloads, samples, transcribes or calls the
/// vendor. A verdict is judged once, in the background (<see cref="OutputModerationProcessor"/>),
/// bound to the bytes it judged (ETag + size) and never expires; changed bytes read as pending until
/// they are judged. <see cref="JudgeInlineAsync"/> is the explicit-action path for a staging object.
/// Media is sampled as ingest does: derived downscaled frames and the ASR transcript, never the
/// original object.
/// </remarks>
public sealed class PublishModeration : IStoredCopyGate
{
    /// <summary>
    /// How long the inline path reuses a content-hash audit row or an in-process entry. The stored
    /// verdict of an output has no expiry; this only bounds the explicit-action path.
    /// </summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Fallback for direct constructions that do not pass the host's cache; the DI registration always
    /// injects the singleton <see cref="IMemoryCache"/>, so this is only used outside the web host.
    /// </summary>
    private static readonly IMemoryCache DefaultMemoryCache = new MemoryCache(new MemoryCacheOptions());

    private readonly IContentModerationGate _gate;
    private readonly IFileStorageService _storage;
    private readonly IModerationMediaSampler _sampler;
    private readonly IInferenceProviderResolver _providers;
    private readonly ITranscriptionClientFactory _transcription;
    private readonly InferenceApiDbContext _db;
    private readonly ILogger<PublishModeration> _logger;
    private readonly ContentModerationOptions _options;
    private readonly IMemoryCache _memoryCache;
    private readonly IOutputModerationEnqueuer? _enqueuer;
    private readonly int _maxTextChars;
    private readonly ITenantContext? _tenant;
    private readonly bool _csamEnabled;

    /// <summary>A text output larger than this is refused rather than read whole (it cannot be fully checked).</summary>
    private const long MaxTextObjectBytes = 4L * 1024 * 1024;

    /// <summary>
    /// The extraction stops at the listening limit, so a track at the limit may go on past it. Anything within this many
    /// seconds of the limit counts as reaching it (the cut lands a few samples short).
    /// </summary>
    private const double AudioLimitMargin = 1.0;

    public PublishModeration(
        IContentModerationGate gate,
        IFileStorageService storage,
        IModerationMediaSampler sampler,
        IInferenceProviderResolver providers,
        ITranscriptionClientFactory transcription,
        InferenceApiDbContext db,
        IOptions<ContentModerationOptions> options,
        ILogger<PublishModeration> logger,
        IMemoryCache? memoryCache = null,
        IOutputModerationEnqueuer? enqueuer = null,
        ITenantContext? tenant = null,
        IOptions<CsamOptions>? csamOptions = null)
    {
        _tenant = tenant;
        _csamEnabled = csamOptions?.Value.Arachnid.Enabled ?? true;
        _gate = gate;
        _storage = storage;
        _sampler = sampler;
        _providers = providers;
        _transcription = transcription;
        _db = db;
        _options = options.Value;
        _memoryCache = memoryCache ?? DefaultMemoryCache;
        _enqueuer = enqueuer;
        // Never round a configured cap UP (the client truncates at it); a tiny cap simply chunks more.
        _maxTextChars = Math.Max(1, options.Value.MaxTextChars);
        _logger = logger;
    }

    /// <summary>
    /// Moderates every bounded chunk of the text. A payload too long for the chunk bound is refused
    /// rather than partly classified; any Block or Unavailable refuses the whole publication.
    /// </summary>
    public async Task<PublishModerationOutcome> EvaluateTextAsync(
        string text, ModerationAttribution attribution, CancellationToken ct)
    {
        // Moderation is opt-in. When the operator has not enabled it, the feature is off, not an
        // outage: publication must keep working (the gate's own ScreenPublishAsync does the same).
        if (!_options.Enabled) return PublishModerationOutcome.AllowedResult;

        // Text of a project under a CSAM hold (a text export, hand-off text) never reaches the vendor.
        Func<CancellationToken, Task<bool>>? guard = null;
        if (attribution.ProjectId is Guid heldProject)
        {
            guard = async token => !await IsProjectHeldAsync(heldProject, token);
            if (!await guard(ct)) return PublishModerationOutcome.Unavailable();
        }

        Evaluated result = await EvaluateTextCoreAsync(text, attribution, guard, ct);
        return result.Held ? PublishModerationOutcome.Unavailable() : result.Outcome;
    }

    /// <summary>
    /// Reads the stored verdict of an output. One HEAD for the object's version and one row read: no
    /// body, no ffmpeg, no transcription, no vendor call, no lock. No row, or a row for other bytes,
    /// offers the output to the background lane and answers pending (a refusal, whatever
    /// <c>FailClosedAtPublish</c> says). The arguments other than the key are kept for the callers'
    /// sake; the verdict is bound to the object's version, not to a step result.
    /// </summary>
    public Task<PublishModerationOutcome> EvaluateObjectAsync(
        Guid projectId,
        string storageKey,
        string mimeType,
        ModerationAttribution attribution,
        CancellationToken ct) =>
        ReadVerdictAsync(projectId, storageKey, serveNothingToClassify: false, ct);

    /// <summary>
    /// Gates a route that serves a project file's bytes. A project file can share the storage key of a
    /// step output, so for a key under the output prefixes the stored verdict decides exactly as for the
    /// download route: pending, block and an outage refuse. Any other key is untouched (no HEAD, null).
    /// An output with nothing to classify (wordless audio) is still served here, as it always was.
    /// Returns the refusal, or null when the file may be served.
    /// </summary>
    public async Task<PublishModerationOutcome?> GateStoredFileAsync(
        Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!_options.Enabled || !ProjectOutputKeys.IsAddressableOutput(projectId, storageKey)) return null;

        PublishModerationOutcome outcome = await ReadVerdictAsync(projectId, storageKey, serveNothingToClassify: true, ct);
        return outcome.Allowed ? null : ForUse(outcome);
    }

    /// <summary>
    /// Gates anything that takes an output's bytes to another key or row (move, rename of a folder,
    /// copy, save as a file, a picture layer). The source must have a current Allow or Review verdict
    /// for its current version: pending, changed bytes, a block and an outage all refuse, whatever
    /// FailClosedAtPublish says (the copy would leave the gate for good). A wordless output keeps
    /// working. Reads one HEAD and one row; samples nothing. Null when the bytes may go.
    /// </summary>
    public async Task<PublishModerationOutcome?> GateStoredCopyAsync(
        Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!_options.Enabled || !ProjectOutputKeys.IsAddressableOutput(projectId, storageKey)) return null;

        PublishModerationOutcome outcome = await ReadVerdictAsync(
            projectId, storageKey, serveNothingToClassify: true, ct, strictOutage: true);
        return outcome.Allowed ? null : ForUse(outcome);
    }

    /// <summary>
    /// Follows a moved object: a released verdict (allow, review, wordless) for the exact bytes that were
    /// judged moves to the new key with the version read after the move, so the unchanged bytes are not
    /// judged again. Otherwise the old row is dropped. Changes the context; the caller saves.
    /// </summary>
    internal async Task MoveVerdictAsync(
        Guid projectId, string oldKey, string newKey, string? versionBefore, CancellationToken ct)
    {
        OutputModerationVerdict? row = await _db.OutputModerationVerdicts
            .FirstOrDefaultAsync(v => v.ProjectId == projectId && v.StorageKey == oldKey, ct);
        if (row is null) return;

        bool released = row.State is nameof(ModerationAction.Allow) or nameof(ModerationAction.Review)
            || (row.State == nameof(ModerationAction.Unavailable) && row.ReasonCode == OutputModerationReasonCodes.NothingToClassify);
        // versionBefore is non-null only when the copy was conditional on the judged ETag, so the bytes at the
        // new key are the judged ones; the size must agree too.
        string? after = released && versionBefore is not null && row.SourceVersion == versionBefore
            ? await ReadObjectVersionAsync(projectId, newKey, ct)
            : null;
        // The copy must be the judged bytes: the ETag and the size read at the new key must both equal what was judged.
        // Any difference drops the row (the file is judged again at its new key) rather than carry a verdict across.
        if (after is not null && !string.Equals(after, versionBefore, StringComparison.Ordinal)) after = null;
        if (after is null)
        {
            _db.OutputModerationVerdicts.Remove(row);
            return;
        }

        _db.OutputModerationVerdicts.RemoveRange(await _db.OutputModerationVerdicts
            .Where(v => v.ProjectId == projectId && v.StorageKey == newKey && v.Id != row.Id).ToListAsync(ct));
        row.StorageKey = newKey;
        row.SourceVersion = after;
    }

    /// <summary>
    /// Whether the uploaded project file at this key was quarantined by ingest moderation. That is the only
    /// thing read from an ingest status: no path turns a "clean" ingest status into an Allow, because ingest
    /// records neither which bytes it judged nor under which rules. Everything else is judged by the output lane.
    /// </summary>
    private async Task<bool> IsQuarantinedUploadAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        using (_tenant.System())
        {
            return await _db.ProjectFiles.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(f => f.ProjectId == projectId && f.StorageKey == storageKey
                               && (f.ModerationStatus == FileModerationStatus.Quarantined
                                   || f.CsamStatus == CsamScanStatus.Quarantined), ct);
        }
    }

    /// <summary>The unavailable answer says "publication"; on a file, move, copy or preview path plainer words fit.</summary>
    private static PublishModerationOutcome ForUse(PublishModerationOutcome outcome) =>
        outcome.Code == "CONTENT_MODERATION_UNAVAILABLE" && outcome.Message == PublishModerationOutcome.Unavailable().Message
            ? outcome with { Message = "This file is not available right now because it has not been cleared. Try again later." }
            : outcome;

    private async Task<PublishModerationOutcome> ReadVerdictAsync(
        Guid projectId, string storageKey, bool serveNothingToClassify, CancellationToken ct, bool strictOutage = false)
    {
        if (!_options.Enabled) return PublishModerationOutcome.AllowedResult;

        // Backstop under every gate path: a project under the output hold (the marker, a flagged file, or an unreadable
        // marker) serves no stored Allow, and the check comes before the HEAD and the row. Only the output hold counts
        // here: a file still waiting for its CSAM scan is the moderation lane's concern, not a reason to hide stored outputs.
        if (await IsOutputHoldActiveAsync(projectId, ct)) return PublishModerationOutcome.Held();

        string? version = await ReadObjectVersionAsync(projectId, storageKey, ct);
        if (version is null) return PublishModerationOutcome.Unavailable();

        OutputModerationVerdict? row = await _db.OutputModerationVerdicts
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ProjectId == projectId && v.StorageKey == storageKey, ct);

        if (row is null || !string.Equals(row.SourceVersion, version, StringComparison.Ordinal))
        {
            // A file ingest moderation quarantined stays refused. Nothing else is read from the ingest status:
            // the key is offered to the output lane like any other object and reads pending until it has a verdict.
            if (row is null && !ProjectOutputKeys.IsAddressableOutput(projectId, storageKey)
                && await IsQuarantinedUploadAsync(projectId, storageKey, ct))
            {
                return PublishModerationOutcome.Blocked(string.Empty);
            }

            await OfferAsync(projectId, storageKey, ct);
            return PublishModerationOutcome.Pending();
        }

        switch (row.State)
        {
            case nameof(ModerationAction.Allow):
            case nameof(ModerationAction.Review):
                return PublishModerationOutcome.AllowedResult;

            case nameof(ModerationAction.Block):
                return PublishModerationOutcome.Blocked(row.ReasonMessage ?? string.Empty);

            default:
                {
                    // A recorded outage. A view may ask for another try once the retry time has come;
                    // after the attempts are used up the gap is the capped one (at most every 6 h).
                    // Nothing to classify (or an unsupported type) is final: the bytes never go to the vendor again.
                    if (row.ReasonCode == OutputModerationReasonCodes.NothingToClassify)
                    {
                        return serveNothingToClassify
                            ? PublishModerationOutcome.AllowedResult
                            : PublishModerationOutcome.Unavailable();
                    }

                    // A refusal that is certain for these bytes is never offered by time, and is not told to try again.
                    if (row.NextAttemptAt == OutputModerationBackoff.Never)
                        return PublishModerationOutcome.UnavailableFinal(row.ReasonCode == ModerationReasonCodes.TooLong);

                    // Only a real outage is offered again by time.
                    if (OutputModerationProcessor.IsRetryDue(row, DateTime.UtcNow))
                        await OfferAsync(projectId, storageKey, ct);

                    return strictOutage ? PublishModerationOutcome.Unavailable() : OutageOutcome(row.ReasonCode);
                }
        }
    }

    /// <summary>
    /// A view or hand-off asks the background lane to judge an output, on behalf of the project's organization: the
    /// per-organization cap applies, so one tenant's views cannot hold the lane or keep the sweep from the others.
    /// </summary>
    private async Task OfferAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (_enqueuer is null) return;

        Guid organizationId;
        using (_tenant.System())
        {
            organizationId = await _db.Projects
                .Where(p => p.Id == projectId)
                .Select(p => p.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }

        if (organizationId != Guid.Empty && !_enqueuer.TryQueue(projectId, storageKey, organizationId))
            _logger.LogDebug(
                "An output view could not offer the output to the moderation lane (capped, full, or already queued); the sweep will pick it up");
    }

    private async Task<bool> IsOutputHoldActiveAsync(Guid projectId, CancellationToken ct)
    {
        using (_tenant.System())
            return (await ReelBolt.Shared.Storage.ProjectOutputHold.ReadAsync(_db, projectId, ct)).IsActive;
    }

    /// <summary>
    /// The one place the suspected-CSAM rule is read for a project: true when a project file is held,
    /// when its CSAM verdict is still pending (control on), or when the check itself failed. Suspected
    /// CSAM never goes to the moderation vendor (docs/csam.md), so every doubt answers "held".
    /// </summary>
    internal async Task<bool> IsProjectHeldAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            using (_tenant.System())
            {
                // The project marker first (unreadable counts as held), then the file predicate: additive.
                if ((await ReelBolt.Shared.Storage.ProjectOutputHold.ReadAsync(_db, projectId, ct)).IsActive) return true;
                return await _db.ProjectFiles
                    .IgnoreQueryFilters()
                    .Where(f => f.ProjectId == projectId)
                    .AnyAsync(OutputModerationHold.FilePredicate(_csamEnabled), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The CSAM hold check failed; the output stays pending");
            return true;
        }
    }

    /// <summary>
    /// The refusal or allowance a recorded outage gives. An unclassified answer (or an unrecorded
    /// Block/Review) is never an outage to fail open on, whatever the operator chose; a plain outage
    /// follows <c>FailClosedAtPublish</c> exactly as the live decision does.
    /// </summary>
    private PublishModerationOutcome OutageOutcome(string? reasonCode)
    {
        if (reasonCode is ModerationReasonCodes.Unclassified or ModerationReasonCodes.AuditUnavailable
            or OutputModerationReasonCodes.NothingToClassify or ModerationReasonCodes.TooLong)
            return PublishModerationOutcome.Unavailable();

        return _options.FailClosedAtPublish
            ? PublishModerationOutcome.Unavailable()
            : PublishModerationOutcome.AllowedResult;
    }

    /// <summary>
    /// Judges an object inside the request: download, sample, transcribe, classify. Used only for an
    /// explicit action on a staging object (the image export); every stored output is judged once, in
    /// the background, and read through <see cref="EvaluateObjectAsync"/>.
    /// </summary>
    public async Task<PublishModerationOutcome> JudgeInlineAsync(
        Guid projectId,
        string storageKey,
        string mimeType,
        ModerationAttribution attribution,
        CancellationToken ct)
    {
        if (!_options.Enabled) return PublishModerationOutcome.AllowedResult;

        // Suspected CSAM never goes to the moderation vendor: a held project (or a failed check) is
        // refused with the neutral unavailable answer, before any byte is read.
        if (await IsProjectHeldAsync(projectId, ct)) return PublishModerationOutcome.Unavailable();

        // A storage key is not an identity: bind the in-process entry to the object's own version
        // (ETag + size, one metadata HEAD). An unreadable version means no cache at all (the
        // pipeline still runs, fail-closed).
        string? objectVersion = await ReadObjectVersionAsync(projectId, storageKey, ct);
        string? memoryKey = objectVersion is null
            ? null
            : MemoryKey(projectId, storageKey, objectVersion);
        PublishModerationOutcome? cached = memoryKey is null ? null : ReadMemoryCache(memoryKey);
        if (cached is not null) return cached;

        // Concurrent requests for one object sample it once.
        string lockKey = memoryKey ?? $"publish-uncached:{projectId:N}:{storageKey}";
        LockEntry entry = RentEntry(lockKey);
        bool acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct);
            acquired = true;

            objectVersion = await ReadObjectVersionAsync(projectId, storageKey, ct);
            memoryKey = objectVersion is null
                ? null
                : MemoryKey(projectId, storageKey, objectVersion);

            cached = memoryKey is null ? null : ReadMemoryCache(memoryKey);
            if (cached is not null) return cached;

            Evaluated result = await EvaluateObjectUncachedAsync(
                projectId, storageKey, mimeType,
                attribution with { SourceVersion = objectVersion },
                async token => !await IsProjectHeldAsync(projectId, token), ct);

            if (result.Held) return PublishModerationOutcome.Unavailable();

            if (memoryKey is not null && result.Kind is CacheKind kind)
            {
                // Store only while the object still is the version that was sampled.
                string? storedVersion = await ReadObjectVersionAsync(projectId, storageKey, ct);
                if (storedVersion is not null && storedVersion == objectVersion)
                    StoreMemoryOutcome(memoryKey, result, kind);
            }

            return result.Outcome;
        }
        finally
        {
            // A cancelled waiter never releases what it did not take, and never cancels the winner.
            if (acquired) entry.Semaphore.Release();
            ReturnEntry(lockKey, entry);
        }
    }

    /// <summary>
    /// The background judgement of one stored output: the same pipeline the inline path runs, with a
    /// send guard checked before the transcription call and before every vendor call. A false guard
    /// answer stops the run with <see cref="Evaluated.Held"/> set and nothing sent.
    /// </summary>
    internal Task<Evaluated> JudgeStoredAsync(
        Guid projectId,
        string storageKey,
        string mimeType,
        ModerationAttribution attribution,
        Func<CancellationToken, Task<bool>> mayProceed,
        CancellationToken ct) =>
        EvaluateObjectUncachedAsync(projectId, storageKey, mimeType, attribution, mayProceed, ct);

    private async Task<Evaluated> EvaluateObjectUncachedAsync(
        Guid projectId,
        string storageKey,
        string mimeType,
        ModerationAttribution attribution,
        Func<CancellationToken, Task<bool>>? mayProceed,
        CancellationToken ct)
    {
        JudgeFlags flags = new();
        Evaluated judged = await EvaluateObjectCoreAsync(projectId, storageKey, mimeType, attribution, mayProceed, flags, ct);

        // An Allow that never heard the speech says so on the stored row.
        return flags.FramesOnly && judged.Outcome.Allowed && judged.State is nameof(ModerationAction.Allow) or nameof(ModerationAction.Review)
            ? judged with { ReasonCode = OutputModerationReasonCodes.FramesOnly }
            : judged;
    }

    private sealed class JudgeFlags
    {
        public bool FramesOnly { get; set; }
    }

    private async Task<Evaluated> EvaluateObjectCoreAsync(
        Guid projectId,
        string storageKey,
        string mimeType,
        ModerationAttribution attribution,
        Func<CancellationToken, Task<bool>>? mayProceed,
        JudgeFlags flags,
        CancellationToken ct)
    {
        // A text output is classified as text (chunked), not run through the media sampler, which
        // would call it unsupported and refuse every text export.
        if (IsTextMime(mimeType))
        {
            string? text = await ReadTextAsync(projectId, storageKey, ct);
            if (text is null)
                return Unavailable(ModerationReasonCodes.Unclassified);

            return await EvaluateTextCoreAsync(
                text,
                attribution with { Surface = ModerationSurface.Publish, Kind = ModerationContentKind.Text },
                mayProceed,
                ct);
        }

        ModerationInput input;
        ModerationContentKind kind;
        List<ModerationImageInput> images = [];
        try
        {
            await using Stream media = await _storage.DownloadAsync(projectId, storageKey, ct);
            ModerationMediaSample sample = await _sampler.SampleAsync(media, mimeType, ct);
            kind = sample.Kind;

            // Any sampling failure on the judging path is unclassified and refuses: a video whose frames
            // were only partly sampled, whose audio track could not be extracted, or that carries
            // several audio tracks is never judged on what is left and stored as an Allow.
            if (sample.Failure is not null)
            {
                _logger.LogWarning(
                    "An output for publish could not be sampled ({Failure}); refusing publication", sample.Failure);
                return Unavailable(sample.TooLong ? ModerationReasonCodes.TooLong : ModerationReasonCodes.Unclassified, noRetry: sample.Deterministic);
            }

            string? transcript = null;
            bool noProvider = false;
            if (kind is ModerationContentKind.Video or ModerationContentKind.Audio
                && sample.Audio is { Length: > 0 })
            {
                // The audio goes to a transcription vendor: the hold is checked again right before.
                if (mayProceed is not null && !await mayProceed(ct))
                    return HeldResult();

                (transcript, noProvider) = await TryTranscribeAsync(sample.Audio, ct);

                // A transcription that was attempted and failed is a real failure, never "frames only".
                if (transcript is null && !noProvider)
                    return Unavailable(ModerationReasonCodes.Unclassified);

                // Two-channel audio whose side signal (left minus right) is audible is transcribed too, so speech the
                // mono downmix cancels is judged with the rest. Its failure is the same outage as the main one, and
                // a wordless take needs both transcripts empty.
                // Further audio of the same file (stereo's side signal, each audible channel of 3 to 8 channels) is
                // transcribed too, so speech the main audio does not carry is judged with the rest. Each failure is
                // the same outage as the main one, and a wordless take needs every transcript empty.
                if (!noProvider)
                {
                    foreach (byte[] extra in ModerationMediaSample.ExtraAudioOf(sample))
                    {
                        if (mayProceed is not null && !await mayProceed(ct))
                            return HeldResult();

                        (string? extraTranscript, bool extraNoProvider) = await TryTranscribeAsync(extra, ct);
                        if (extraTranscript is null && !extraNoProvider)
                            return Unavailable(ModerationReasonCodes.Unclassified);
                        transcript = ModerationMediaSample.JoinTranscripts(transcript, extraTranscript);
                    }
                }

                // No platform transcription provider: the speech cannot be heard. Refused as an outage unless the
                // operator chose, in so many words, to judge such files on their frames alone.
                if (noProvider)
                {
                    if (!_options.AllowFramesOnlyWithoutTranscription)
                    {
                        _logger.LogWarning("A media output has sound but no platform transcription provider is configured; refusing");
                        return Unavailable(ModerationReasonCodes.Unclassified);
                    }

                    flags.FramesOnly = true;
                }
            }

            // The text of the file's subtitle streams is judged with the transcript, as the same words.
            transcript = ModerationMediaSample.WithSubtitles(transcript, sample.SubtitleText);

            images = sample.Images
                .Select(bytes => new ModerationImageInput("image/jpeg", bytes))
                .ToList();

            bool unclassifiable = kind switch
            {
                ModerationContentKind.Image => images.Count == 0,
                ModerationContentKind.Audio => images.Count == 0 && string.IsNullOrWhiteSpace(transcript),
                ModerationContentKind.Video => images.Count == 0 && string.IsNullOrWhiteSpace(transcript),
                _ => true
            };

            if (unclassifiable)
            {
                _logger.LogWarning("An output for publish had nothing to classify; refusing publication");

                // Final only for audio that sampled fine with a real, empty transcript (wordless audio,
                // such as generated music). A video with no frames, or anything else, is unclassified.
                bool wordless = kind == ModerationContentKind.Audio && transcript is not null;

                // The one exception: wordless audio our own Music or Sound step made from a checked prompt, heard in full
                // (one track, shorter than the listening limit, measured from the decoded samples) and still the bytes
                // the step wrote. Cleared with its own reason code; no vendor call. Everything else stays as it was.
                if (wordless && sample.Failure is null
                    && sample.AudioSeconds is double seconds && seconds < _options.MaxAudioSeconds - AudioLimitMargin
                    && await new GeneratedAudioClearance(_db, _storage, _tenant, _logger).IsClearedAsync(projectId, storageKey, ct))
                {
                    return new Evaluated(
                        PublishModerationOutcome.AllowedResult, CacheKind.Allowed, nameof(ModerationAction.Allow),
                        OutputModerationReasonCodes.GeneratedWordlessAudio);
                }

                return Unavailable(wordless
                    ? OutputModerationReasonCodes.NothingToClassify
                    : ModerationReasonCodes.Unclassified);
            }

            input = new ModerationInput(
                kind == ModerationContentKind.Image ? null : transcript, images, kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An output could not be read/sampled for moderation; refusing publication");
            return Unavailable(ModerationReasonCodes.Unclassified);
        }

        ModerationAttribution publishAttribution = attribution with
        {
            Surface = ModerationSurface.Publish,
            Kind = kind
        };

        // A transcript longer than one call cannot be sent with the frames: the client would
        // truncate it while the compile/transcript used all of it. Classify the frames and every
        // bounded transcript chunk; no content-hash cache, because no single audit row carries the
        // full hash.
        bool cacheable = string.IsNullOrEmpty(input.Text) || input.Text.Length <= _maxTextChars;
        if (!cacheable)
        {
            bool outage = false;
            if (images.Count > 0)
            {
                ModerationDecision? decision = await ModerateGuardedAsync(
                    new ModerationInput(null, images, kind), publishAttribution, mayProceed, ct);
                if (decision is null) return HeldResult();
                Evaluated frames = EvaluateDecision(decision);
                if (!frames.Outcome.Allowed) return frames;
                if (frames.State == nameof(ModerationAction.Unavailable)) outage = true;
            }

            // Text within the sampler's cap but beyond the chunker's bound cannot be judged whole: a final refusal that
            // trying again does not change (no retry time), never a partial pass.
            if (!ModerationTextChunker.TryChunk(input.Text, _maxTextChars, out IReadOnlyList<string> chunks))
                return new Evaluated(PublishModerationOutcome.UnavailableFinal(), CacheKind.Unavailable,
                    nameof(ModerationAction.Unavailable), ModerationReasonCodes.Unclassified, NoRetry: true);

            foreach (string chunk in chunks)
            {
                ModerationDecision? decision = await ModerateGuardedAsync(
                    new ModerationInput(chunk, [], ModerationContentKind.Text), publishAttribution, mayProceed, ct);
                if (decision is null) return HeldResult();
                Evaluated evaluated = EvaluateDecision(decision);
                if (!evaluated.Outcome.Allowed) return evaluated;
                if (evaluated.State == nameof(ModerationAction.Unavailable)) outage = true;
            }

            return outage
                ? new Evaluated(PublishModerationOutcome.AllowedResult, CacheKind.Unavailable, nameof(ModerationAction.Unavailable), null)
                : AllowedEvaluated();
        }

        string hash = ModerationContentHash.Compute(input);
        DateTime now = DateTime.UtcNow;
        ContentModerationDecision? cachedRow = await _db.ContentModerationDecisions
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId
                        && d.Surface == nameof(ModerationSurface.Publish)
                        && d.ContentSha256 == hash
                        && d.CreatedAt >= now - CacheWindow)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cachedRow is not null && MapCachedRow(cachedRow, now) is Evaluated hit)
            return hit;

        ModerationDecision? single = await ModerateGuardedAsync(input, publishAttribution, mayProceed, ct);
        return single is null ? HeldResult() : EvaluateDecision(single);
    }

    /// <summary>One vendor call, preceded by the send guard when there is one. Null means the guard refused.</summary>
    private async Task<ModerationDecision?> ModerateGuardedAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        Func<CancellationToken, Task<bool>>? mayProceed,
        CancellationToken ct)
    {
        if (mayProceed is not null && !await mayProceed(ct)) return null;
        return await _gate.ModerateAsync(input, attribution, ct);
    }

    /// <summary>
    /// Classifies every bounded chunk. The result is not an allow when a fail-open outage means the
    /// object was not fully classified: a transient outage must never become a stored allow.
    /// </summary>
    private async Task<Evaluated> EvaluateTextCoreAsync(
        string text,
        ModerationAttribution attribution,
        Func<CancellationToken, Task<bool>>? mayProceed,
        CancellationToken ct)
    {
        if (!ModerationTextChunker.TryChunk(text, _maxTextChars, out IReadOnlyList<string> chunks))
            return Unavailable(ModerationReasonCodes.Unclassified);

        bool outage = false;
        foreach (string chunk in chunks)
        {
            ModerationDecision? decision = await ModerateGuardedAsync(
                new ModerationInput(chunk, [], ModerationContentKind.Text), attribution, mayProceed, ct);
            if (decision is null) return HeldResult();
            Evaluated evaluated = EvaluateDecision(decision);
            if (!evaluated.Outcome.Allowed) return evaluated;
            if (evaluated.State == nameof(ModerationAction.Unavailable)) outage = true;
        }

        return outage
            ? new Evaluated(PublishModerationOutcome.AllowedResult, CacheKind.Unavailable, nameof(ModerationAction.Unavailable), null)
            : AllowedEvaluated();
    }

    private PublishModerationOutcome? ReadMemoryCache(string memoryKey)
    {
        if (_memoryCache.TryGetValue(memoryKey, out CachedOutcome entry))
        {
            TimeSpan window = entry.Kind == CacheKind.Unavailable
                ? TimeSpan.FromSeconds(_options.EffectiveUnavailableCacheSeconds)
                : CacheWindow;
            if (window > TimeSpan.Zero && DateTime.UtcNow - entry.CreatedAt < window)
                return entry.Outcome;
        }

        return null;
    }

    private void StoreMemoryOutcome(string key, Evaluated result, CacheKind kind)
    {
        TimeSpan window = kind == CacheKind.Unavailable
            ? TimeSpan.FromSeconds(_options.EffectiveUnavailableCacheSeconds)
            : CacheWindow;
        if (window <= TimeSpan.Zero) return;

        _memoryCache.Set(
            key,
            new CachedOutcome(result.Outcome, kind, DateTime.UtcNow),
            window);
    }

    private static string MemoryKey(Guid projectId, string storageKey, string version) =>
        $"publish:{projectId:N}:{storageKey}:{version}";

    /// <summary>
    /// Reads the object's identity from one metadata HEAD: the store's ETag (quotes trimmed) plus
    /// the size, or <c>len:{size}</c> when the store reports no ETag. Returns null when the metadata
    /// cannot be read; an unknown identity is never shared.
    /// </summary>
    internal async Task<string?> ReadObjectVersionAsync(
        Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            StoredObjectMetadata? meta = await _storage.GetMetadataAsync(projectId, storageKey, ct);
            if (meta is null) return null;

            string? etag = meta.ETag?.Trim().Trim('"');
            return string.IsNullOrEmpty(etag)
                ? $"len:{meta.SizeBytes}"
                : $"{etag}:{meta.SizeBytes}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An output's object version could not be read; its verdict cannot be matched");
            return null;
        }
    }

    /// <summary>Maps a stored audit row back to its publish outcome, or null when it has expired.</summary>
    private Evaluated? MapCachedRow(ContentModerationDecision row, DateTime now)
    {
        switch (row.Action)
        {
            case nameof(ModerationAction.Block):
                return new Evaluated(
                    PublishModerationOutcome.Blocked(row.ReasonMessage ?? string.Empty),
                    CacheKind.Blocked, nameof(ModerationAction.Block), row.ReasonCode);

            case nameof(ModerationAction.Review):
                return new Evaluated(
                    PublishModerationOutcome.AllowedResult, CacheKind.Allowed, nameof(ModerationAction.Review), null);

            case nameof(ModerationAction.Allow):
                return AllowedEvaluated();

            case nameof(ModerationAction.Unavailable):
                {
                    TimeSpan window = TimeSpan.FromSeconds(_options.EffectiveUnavailableCacheSeconds);
                    if (window <= TimeSpan.Zero || row.CreatedAt < now - window) return null;

                    return new Evaluated(
                        OutageOutcome(row.ReasonCode), CacheKind.Unavailable,
                        nameof(ModerationAction.Unavailable), row.ReasonCode);
                }

            default:
                return null;
        }
    }

    private Evaluated EvaluateDecision(ModerationDecision decision)
    {
        if (decision.Action == ModerationAction.Block)
        {
            return new Evaluated(
                PublishModerationOutcome.Blocked(decision.UserMessage), CacheKind.Blocked,
                nameof(ModerationAction.Block), decision.ReasonCode);
        }

        // An unclassified answer or an unrecorded Block/Review is never an outage to fail open on:
        // refuse it regardless of the operator's FailClosedAtPublish choice.
        if (decision.Action == ModerationAction.Unavailable
            || decision.ReasonCode == ModerationReasonCodes.AuditUnavailable)
        {
            return Unavailable(decision.ReasonCode ?? ModerationReasonCodes.Unclassified);
        }

        // A genuine moderation outage (provider unreachable, timeout): the operator may have chosen
        // to keep publishing (FailClosedAtPublish=false). The shipped default refuses.
        if (!decision.Available)
        {
            return new Evaluated(
                OutageOutcome(null), CacheKind.Unavailable, nameof(ModerationAction.Unavailable), null);
        }

        return decision.Action == ModerationAction.Review
            ? new Evaluated(PublishModerationOutcome.AllowedResult, CacheKind.Allowed, nameof(ModerationAction.Review), null)
            : AllowedEvaluated();
    }

    private static Evaluated AllowedEvaluated() =>
        new(PublishModerationOutcome.AllowedResult, CacheKind.Allowed, nameof(ModerationAction.Allow), null);

    private static Evaluated Unavailable(string? reasonCode, bool noRetry = false) =>
        new(PublishModerationOutcome.Unavailable(), CacheKind.Unavailable, nameof(ModerationAction.Unavailable), reasonCode, NoRetry: noRetry);

    private static Evaluated HeldResult() =>
        new(PublishModerationOutcome.Unavailable(), null, nameof(ModerationAction.Unavailable), null, Held: true);

    /// <summary>Whether a result can be cached by object identity, and for which window.</summary>
    internal enum CacheKind
    {
        Allowed,
        Blocked,
        Unavailable
    }

    /// <summary>
    /// One judgement: the outcome a caller sees plus what a stored verdict needs. <see cref="Held"/>
    /// means the send guard refused and nothing was sent.
    /// </summary>
    internal readonly record struct Evaluated(
        PublishModerationOutcome Outcome, CacheKind? Kind, string State, string? ReasonCode, bool Held = false, bool NoRetry = false);

    private readonly record struct CachedOutcome(
        PublishModerationOutcome Outcome, CacheKind Kind, DateTime CreatedAt);

    /// <summary>
    /// One in-process single-flight gate per object identity. The static map is shared by the scoped
    /// service instances (a per-request map would not coordinate anything); a reference count evicts
    /// an identity when its last caller leaves, so the map cannot grow without bound.
    /// </summary>
    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References;
    }

    private static readonly object LocksGate = new();
    private static readonly Dictionary<string, LockEntry> Locks = new();

    private static LockEntry RentEntry(string key)
    {
        lock (LocksGate)
        {
            if (!Locks.TryGetValue(key, out LockEntry? entry))
            {
                entry = new LockEntry();
                Locks[key] = entry;
            }

            entry.References++;
            return entry;
        }
    }

    private static void ReturnEntry(string key, LockEntry entry)
    {
        lock (LocksGate)
        {
            if (--entry.References == 0) Locks.Remove(key);
        }
    }

    private static bool IsTextMime(string mimeType)
    {
        string mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return mime.StartsWith("text/", StringComparison.Ordinal)
            || mime is "application/json" or "application/xml" or "application/x-ndjson"
                or "application/yaml" or "application/x-yaml";
    }

    private async Task<string?> ReadTextAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            StoredObjectMetadata? meta = await _storage.GetMetadataAsync(projectId, storageKey, ct);
            if (meta is null || meta.SizeBytes > MaxTextObjectBytes)
            {
                _logger.LogWarning(
                    "A published text output is missing or larger than {MaxBytes} bytes; refusing publication",
                    MaxTextObjectBytes);
                return null;
            }

            await using Stream stream = await _storage.DownloadAsync(projectId, storageKey, ct);
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, ct);
            byte[] bytes = buffer.ToArray();
            if (bytes.Length > MaxTextObjectBytes) return null;

            // Bytes that are not text are not classified as text: a NUL byte or invalid UTF-8 refuses.
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                _logger.LogWarning("A published text output holds binary data; refusing publication");
                return null;
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).TrimStart('\uFEFF');
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A published text output could not be read; refusing publication");
            return null;
        }
    }

    private async Task<(string? Text, bool NoProvider)> TryTranscribeAsync(byte[] audio, CancellationToken ct)
    {
        ResolvedTranscriptionProvider? provider = await _providers.ResolvePlatformTranscriptionAsync(ct);
        if (provider is null)
        {
            _logger.LogInformation("No transcription provider is configured; a published media output has no transcript");
            return (null, true);
        }

        try
        {
            ITranscriptionClient client = _transcription.Get(provider);
            TranscriptResult result = await client.TranscribeAsync(
                new MemoryStream(audio, writable: false), "audio.wav", language: null, wordTimestamps: false, ct);
            return (result.Text ?? string.Empty, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription failed while moderating a published media output");
            return (null, false);
        }
    }
}
