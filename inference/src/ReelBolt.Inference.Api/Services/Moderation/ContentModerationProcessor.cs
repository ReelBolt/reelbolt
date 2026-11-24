using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// Classifies one ingested project file and records the outcome on its row: a high-confidence
/// <see cref="ModerationAction.Block"/> quarantines it, any other usable answer marks it
/// <see cref="FileModerationStatus.Clean"/>, and anything that could not be classified marks it
/// <see cref="FileModerationStatus.Error"/> — never <c>Clean</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, with no loop of its own, so the whole decision is testable without a background host. The
/// row status is the idempotency key: only <see cref="FileModerationStatus.Unmoderated"/> is claimed.
/// </para>
/// <para>
/// The failure stance is fail OPEN: the bytes stay where they are and the file stays usable. A
/// <see cref="ModerationAction.Review"/> is likewise not a block — the gate has already written the
/// pending audit row — so it settles as <c>Clean</c>. Only egress (T-004) fails closed.
/// </para>
/// <para>
/// A block is saved BEFORE the object is moved, so every read path refuses the file from the moment
/// the decision exists even if the move is slow or fails. A move failure leaves the row Quarantined
/// (the bytes are not served) and is logged for a later sweep.
/// </para>
/// </remarks>
public sealed class ContentModerationProcessor
{
    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IModerationMediaSampler _sampler;
    private readonly IContentModerationGate _gate;
    private readonly IInferenceProviderResolver _providers;
    private readonly ITranscriptionClientFactory _transcription;
    private readonly IPublishEndpoint _publish;
    private readonly ITenantContext? _tenant;
    private readonly IOptions<ContentModerationOptions> _options;
    private readonly ILogger<ContentModerationProcessor> _logger;
    private readonly IContentModerationEnqueuer? _enqueuer;
    private readonly bool _csamEnabled;

    private readonly IProjectHistoryEraser _historyEraser;

    public ContentModerationProcessor(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IModerationMediaSampler sampler,
        IContentModerationGate gate,
        IInferenceProviderResolver providers,
        ITranscriptionClientFactory transcription,
        IPublishEndpoint publish,
        IOptions<ContentModerationOptions> options,
        ILogger<ContentModerationProcessor> logger,
        ITenantContext? tenant = null,
        IContentModerationEnqueuer? enqueuer = null,
        IOptions<CsamOptions>? csamOptions = null,
        IProjectHistoryEraser? historyEraser = null)
    {
        _historyEraser = historyEraser ?? new ProjectHistoryEraser(db, storage, NullLogger<ProjectHistoryEraser>.Instance);
        _enqueuer = enqueuer;
        // Only the master switch decides whether a CSAM verdict is expected. A stopped sweeper is a
        // deployment fault and does not settle Pending/Scanning rows; the disabled-path normalization
        // settles only rows older than the normal grace/claim windows.
        _csamEnabled = csamOptions?.Value.Arachnid.Enabled ?? true;
        _db = db;
        _storage = storage;
        _sampler = sampler;
        _gate = gate;
        _providers = providers;
        _transcription = transcription;
        _publish = publish;
        _options = options;
        _logger = logger;
        _tenant = tenant;
    }

    private ContentModerationOptions Options => _options.Value;

    public async Task ProcessAsync(Guid fileId, CancellationToken ct)
    {
        ProjectFile? file;
        Guid organizationId;
        using (_tenant.System())
        {
            file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
            organizationId = file is null
                ? Guid.Empty
                : await _db.Projects
                    .Where(p => p.Id == file.ProjectId)
                    .Select(p => p.OrganizationId)
                    .FirstOrDefaultAsync(ct);
        }

        if (file is null)
        {
            _logger.LogInformation("Project file {FileId} no longer exists; nothing to moderate", fileId);
            return;
        }

        // Judge the CSAM state before touching the bytes. A quarantined file is suspected illegal
        // material and must never reach a general moderation API; an unresolved verdict waits for
        // the CSAM sweeper, and the ingest sweep will offer the row again once it settles.
        if (CsamScanGate.IsHeld(file))
        {
            _logger.LogInformation(
                "File {FileId} is held by the CSAM control; ingest moderation will not read it", fileId);
            return;
        }

        if (_csamEnabled && file.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning)
        {
            _logger.LogDebug(
                "File {FileId} waits for its CSAM verdict ({Status}); moderation will retry later",
                fileId, file.CsamStatus);
            return;
        }

        if (file.ModerationStatus != FileModerationStatus.Unmoderated)
        {
            _logger.LogDebug(
                "Skipping moderation of file {FileId}: already {Status}", fileId, file.ModerationStatus);
            return;
        }

        // The bytes this scan reads. A re-upload can replace the object (and its key) while the scan
        // runs; every status write below re-reads the row and refuses to record a verdict over bytes
        // it never classified.
        string scannedKey = file.StorageKey;

        // The object's identity, when the store exposes it. A presigned PUT can overwrite the SAME
        // key with different bytes while the scan runs; the storage key alone cannot see that.
        string? scannedEtag = null;
        try
        {
            scannedEtag = await _storage.GetObjectETagAsync(file.ProjectId, scannedKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the object ETag for moderation; the storage-key check remains");
        }

        using IDisposable orgScope = _tenant.Organization(organizationId);

        try
        {
            if (!await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Scanning, ct))
                return;

            if (!Options.Enabled)
            {
                // The feature is off, not a classification gap: the file is usable and unscanned.
                await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Clean, ct);
                return;
            }

            ModerationContentKind? kind = KindOf(file.MimeType);
            if (kind is null)
            {
                // Text (and every other non-media type) is out of ingest scope.
                await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Clean, ct);
                return;
            }

            // Check-then-act: a quarantine (or a replacement) can land after the initial check.
            // Re-read the row and its object identity immediately before reading any bytes.
            if (!await StillOursAndUnquarantinedAsync(file, scannedKey, scannedEtag, ct))
            {
                await AbortStaleScanAsync(file, ct);
                return;
            }

            ModerationMediaSample sample;
            await using (Stream media = await _storage.DownloadAsync(file.ProjectId, scannedKey, ct))
                sample = await _sampler.SampleAsync(media, file.MimeType, ct);

            string? transcript = null;
            bool speechUnheard = false;
            if (kind is ModerationContentKind.Video or ModerationContentKind.Audio
                && sample.Audio is { Length: > 0 })
            {
                (bool transcriptionAborted, string? transcribed, bool noProvider) =
                    await TryTranscribeAsync(file, scannedKey, scannedEtag, sample.Audio, ct);
                if (transcriptionAborted) return;
                transcript = transcribed;

                // Two-channel audio whose side signal (left minus right) is audible: heard too, so speech the mono
                // downmix cancels is judged with the rest. It goes the same way as the main audio.
                if (!noProvider)
                {
                    foreach (byte[] extra in ModerationMediaSample.ExtraAudioOf(sample))
                    {
                        (bool extraAborted, string? extraText, _) =
                            await TryTranscribeAsync(file, scannedKey, scannedEtag, extra, ct);
                        if (extraAborted) return;
                        transcript = ModerationMediaSample.JoinTranscripts(transcript, extraText);
                    }
                }

                // No platform transcription provider: the speech cannot be heard. The frames are still judged (a
                // Block still quarantines), but the file is not called checked unless the operator chose frames-only.
                speechUnheard = noProvider && !Options.AllowFramesOnlyWithoutTranscription;
            }

            // The text of the file's subtitle streams is judged with the transcript, as the same words.
            transcript = ModerationMediaSample.WithSubtitles(transcript, sample.SubtitleText);

            List<ModerationImageInput> images = sample.Images
                .Select(bytes => new ModerationImageInput("image/jpeg", bytes))
                .ToList();

            bool hasImages = images.Count > 0;
            bool hasText = !string.IsNullOrWhiteSpace(transcript);

            bool unclassifiable = kind switch
            {
                ModerationContentKind.Image => !hasImages,
                ModerationContentKind.Audio => !hasText && !hasImages,
                ModerationContentKind.Video => !hasImages && !hasText,
                _ => true
            };

            if (unclassifiable)
            {
                // "No frames and no transcript" is an outage, not a clean answer. An audio-only file
                // with no transcript likewise has nothing the model could have classified.
                _logger.LogWarning(
                    "File {FileId} could not be sampled for moderation ({Failure}); marking Error and keeping it usable",
                    file.Id, sample.Failure ?? "no frames or transcript");
                await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Error, ct);
                return;
            }

            ModerationContentKind inputKind = kind.Value;
            ModerationAttribution attribution = new(
                organizationId,
                file.ProjectId,
                UserId: null,
                ExecutionId: null,
                StepResultId: null,
                ModerationSurface.Ingest,
                inputKind);

            // Apply one call's verdict. True means the step is done (quarantined or Error).
            async Task<bool> ApplyAsync(ModerationDecision decision)
            {
                if (decision.Action == ModerationAction.Block)
                {
                    await QuarantineAsync(file, scannedKey, scannedEtag, ct);
                    return true;
                }

                if (!decision.Available)
                {
                    _logger.LogWarning(
                        "Moderation of file {FileId} was unavailable ({Reason}); keeping it usable with Error",
                        file.Id, decision.ReasonCode ?? "unclassified");
                    await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Error, ct);
                    return true;
                }

                return false;
            }

            int maxChars = Math.Max(1, Options.MaxTextChars);
            bool longTranscript = !string.IsNullOrEmpty(transcript) && transcript.Length > maxChars;

            if (longTranscript)
            {
                // The client truncates one call at MaxTextChars; classify the frames and every
                // transcript chunk so the whole recording was actually checked.
                if (images.Count > 0)
                {
                    ModerationDecision? frames = await ModerateIfCurrentAsync(
                        new ModerationInput(null, images, inputKind), attribution,
                        file, scannedKey, scannedEtag, ct);
                    if (frames is null) return;
                    if (await ApplyAsync(frames)) return;
                }

                // What fits in the bounded number of calls is judged; text beyond it is not, so the file is never Clean
                // (the same fail-open Error as any partly checked file, after a Block in what was judged still quarantines).
                IReadOnlyList<string> chunks = ModerationTextChunker.Chunk(
                    transcript, maxChars, ModerationTextChunker.DefaultMaxChunks, out bool allChunksFit);
                if (!allChunksFit)
                {
                    _logger.LogWarning(
                        "File {FileId} has a transcript too long to check in the bounded number of calls; judging what fits and keeping it usable with Error",
                        file.Id);
                }

                foreach (string chunk in chunks)
                {
                    ModerationDecision? textDecision = await ModerateIfCurrentAsync(
                        new ModerationInput(chunk, [], ModerationContentKind.Text), attribution,
                        file, scannedKey, scannedEtag, ct);
                    if (textDecision is null) return;
                    if (await ApplyAsync(textDecision)) return;
                }

                await SetStatusAsync(file, scannedKey, scannedEtag,
                    sample.Failure is null && allChunksFit ? FileModerationStatus.Clean : FileModerationStatus.Error, ct);
                return;
            }

            ModerationInput input = new(
                inputKind == ModerationContentKind.Image ? null : transcript,
                images,
                inputKind);

            ModerationDecision? decision = await ModerateIfCurrentAsync(
                input, attribution, file, scannedKey, scannedEtag, ct);
            if (decision is null) return;
            if (await ApplyAsync(decision)) return;

            // Allow and Review both keep the file usable. A Review has already written its pending row. A sample
            // that failed part-way, or speech that could not be heard, is not a complete check: it is not called Clean.
            if (sample.Failure is not null || speechUnheard)
            {
                _logger.LogWarning(
                    "File {FileId} was only partly checked ({Failure}); marking Error and keeping it usable",
                    file.Id, sample.Failure ?? "speech not heard");
                await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Error, ct);
                return;
            }

            await SetStatusAsync(file, scannedKey, scannedEtag, FileModerationStatus.Clean, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller stopped waiting; not a moderation outcome.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moderation of file {FileId} failed; keeping it usable with Error", fileId);
            await MarkErrorAfterFailureAsync(fileId, scannedKey, scannedEtag);
        }
    }

    /// <summary>
    /// Hides the file first, then moves its object out of the served prefix and drops its vectors.
    /// Nothing after the first save can turn the row back into a usable one.
    /// </summary>
    private async Task QuarantineAsync(
        ProjectFile file, string scannedKey, string? scannedEtag, CancellationToken ct)
    {
        // A re-upload may have replaced the bytes while this scan ran: never quarantine the
        // replacement on the old scan's verdict. The new scan decides for its own bytes.
        await _db.Entry(file).ReloadAsync(ct);

        // A CSAM match can land while this general scan ran. Its quarantine owns the bytes and the
        // evidence key; a general Block must not move the object or record a second verdict.
        if (CsamScanGate.IsHeld(file))
        {
            _logger.LogInformation(
                "File {FileId} became CSAM-held during moderation; the general Block is not applied", file.Id);
            return;
        }

        if (!string.Equals(file.StorageKey, scannedKey, StringComparison.Ordinal)
            || await BytesChangedInPlaceAsync(file.ProjectId, scannedKey, scannedEtag, ct))
        {
            _logger.LogInformation(
                "File {FileId} was replaced while its scan ran; the stale Block is not applied", file.Id);

            // The bytes this scan read were already copied into the project's history before the
            // replacement. They are blocked, so erase them there too: the stale verdict must never
            // leave them downloadable through an older version (finding 6).
            try
            {
                await _historyEraser.EraseFileAsync(file.ProjectId, file.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "File {FileId} was replaced under a Block, but its history copies could not be erased; the retention GC retries",
                    file.Id);
            }

            await ResetStaleScanAndRequeueAsync(file, ct);
            return;
        }

        file.ModerationStatus = FileModerationStatus.Quarantined;
        await ProjectContentQuarantineMarker.StageAsync(_db, file.ProjectId, DateTime.UtcNow, ct);
        await _db.SaveChangesAsync(ct);

        string storageFileName = string.IsNullOrWhiteSpace(file.StorageFileName)
            ? Path.GetFileName(scannedKey)
            : file.StorageFileName;
        string destination = $"projects/{file.ProjectId}/quarantine/{storageFileName}";

        try
        {
            using (ProjectHistoryProtection.Suppress())
                await _storage.MoveAsync(file.ProjectId, scannedKey, destination, ct);
            file.StorageKey = destination;
            file.StoragePrefix = $"projects/{file.ProjectId}/quarantine/";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The row is already Quarantined, so every read path refuses it even though the bytes
            // remain at the old key. A later sweep must retry the move.
            _logger.LogError(ex,
                "File {FileId} is quarantined but its object could not be moved from {Key}",
                file.Id, file.StorageKey);
        }

        // Quarantined bytes are never retained in the project's history, whether or not the move
        // above succeeded (the row is already hidden).
        try
        {
            await _historyEraser.EraseFileAsync(file.ProjectId, file.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File {FileId} is quarantined but its history copies could not be erased", file.Id);
        }

        file.SummaryStatus = SummaryStatus.Done;
        file.IndexingStatus = FileIndexingStatus.NotIndexed;
        file.IndexingError = "Quarantined";
        await _db.SaveChangesAsync(ct);

        try
        {
            await _publish.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = file.ProjectId,
                FileId = file.Id,
                Operation = "Delete",
                RequestedAt = DateTime.UtcNow
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Vectors are a derived index; the row and the object are already hidden.
            _logger.LogWarning(ex, "Failed to publish the vector delete for quarantined file {FileId}", file.Id);
        }
    }

    /// <summary>
    /// Transcribes sampled audio through the ASR provider — a third-party egress. The row and the
    /// object identity are re-checked by <see cref="EgressIfCurrentAsync"/> immediately before the
    /// call; an aborted scan sends nothing. <c>Aborted</c> is distinct from "no transcript" so the
    /// caller can stop instead of marking a stale scan Error.
    /// </summary>
    private async Task<(bool Aborted, string? Text, bool NoProvider)> TryTranscribeAsync(
        ProjectFile file, string scannedKey, string? scannedEtag, byte[] audio, CancellationToken ct)
    {
        ResolvedTranscriptionProvider? provider = await _providers.ResolvePlatformTranscriptionAsync(ct);
        if (provider is null)
        {
            _logger.LogInformation(
                "No transcription provider is configured; ingested audio cannot be classified");
            return (false, null, true);
        }

        ITranscriptionClient client;
        try
        {
            client = _transcription.Get(provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build the transcription client for ingested media");
            return (false, null, false);
        }

        async Task<TranscriptResult?> EgressAsync()
        {
            try
            {
                return await client.TranscribeAsync(
                    new MemoryStream(audio, writable: false), "audio.wav", language: null, wordTimestamps: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transcription failed while moderating ingested media");
                return null;
            }
        }

        (bool aborted, TranscriptResult? result) = await EgressIfCurrentAsync(
            file, scannedKey, scannedEtag, EgressAsync, ct);
        return aborted ? (true, null, false) : (false, result?.Text, false);
    }

    /// <summary>
    /// Writes a verdict only when the row still describes the bytes this scan read. A re-upload
    /// changes the storage key (or resets the status) while the scan runs; the stale scan returns
    /// false and writes nothing, and the enqueued new scan classifies the replacement.
    /// </summary>
    private async Task<bool> SetStatusAsync(
        ProjectFile file, string scannedKey, string? scannedEtag,
        FileModerationStatus status, CancellationToken ct)
    {
        await _db.Entry(file).ReloadAsync(ct);

        if (!string.Equals(file.StorageKey, scannedKey, StringComparison.Ordinal)
            || await BytesChangedInPlaceAsync(file.ProjectId, scannedKey, scannedEtag, ct))
        {
            _logger.LogInformation(
                "File {FileId} was replaced while its scan ran; this stale scan writes no verdict", file.Id);

            // A final verdict must never leave the row Scanning with no queued scan. The claim on
            // Scanning is ours; reset it (guarded against a newer verdict) and offer a new scan.
            if (status != FileModerationStatus.Scanning)
                await ResetStaleScanAndRequeueAsync(file, ct);

            return false;
        }

        // Another worker claimed the same scan first; leave it to that worker.
        if (status == FileModerationStatus.Scanning
            && file.ModerationStatus != FileModerationStatus.Unmoderated)
        {
            return false;
        }

        // A Quarantined row is final: a later scan must never turn a blocked file back into Clean.
        if (file.ModerationStatus == FileModerationStatus.Quarantined
            && status != FileModerationStatus.Quarantined)
        {
            return false;
        }

        file.ModerationStatus = status;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// A scan whose bytes changed under it is abandoned. Return OUR claim on the row to
    /// <see cref="FileModerationStatus.Unmoderated"/> — but only while it is still Scanning, so a
    /// newer worker's verdict (Clean, Block, Error) or a re-upload that already reset it is never
    /// overwritten — and offer a scan for the replacement. A full queue is covered by the sweeper's
    /// stale-Scanning pass. Never leaves a row Scanning with no queued scan.
    /// </summary>
    private async Task ResetStaleScanAndRequeueAsync(ProjectFile file, CancellationToken ct)
    {
        await _db.Entry(file).ReloadAsync(ct);

        // Conditional on the row still being OUR in-flight scan. A re-upload resets it to
        // Unmoderated and a newer worker may already hold a verdict; neither is overwritten.
        if (file.ModerationStatus == FileModerationStatus.Scanning)
        {
            file.ModerationStatus = FileModerationStatus.Unmoderated;
            await _db.SaveChangesAsync(ct);
        }
        else if (file.ModerationStatus != FileModerationStatus.Unmoderated)
        {
            // A newer worker already holds a verdict (Clean, Quarantined, Error): do not overwrite
            // it and do not re-offer a file that is already classified.
            return;
        }

        _enqueuer?.TryQueue(file.Id);
    }

    /// <summary>
    /// True while the row is still ours to scan: not Quarantined, still pointing at the key we read,
    /// and (when the store gives an ETag) still the same object bytes. Re-reads the row every call,
    /// so a quarantine that lands between the initial check and a provider call is seen.
    /// </summary>
    private async Task<bool> StillOursAndUnquarantinedAsync(
        ProjectFile file, string scannedKey, string? scannedEtag, CancellationToken ct)
    {
        await _db.Entry(file).ReloadAsync(ct);

        if (file.ModerationStatus == FileModerationStatus.Quarantined) return false;
        if (!string.Equals(file.StorageKey, scannedKey, StringComparison.Ordinal)) return false;

        return !await BytesChangedInPlaceAsync(file.ProjectId, scannedKey, scannedEtag, ct);
    }

    /// <summary>
    /// The last check before any provider call: re-read the row and the object ETag, and answer null
    /// (nothing sent) when a quarantine or a replacement landed in the meantime.
    /// </summary>
    /// <summary>
    /// One helper for every third-party egress in <see cref="ProcessAsync"/>: re-read the row and its
    /// object identity immediately before <paramref name="egress"/> and answer
    /// <c>(Aborted: true, default)</c> — never invoking it — when a quarantine or replacement landed.
    /// Routing both the ASR call and the moderation call through here keeps a future call site from
    /// drifting back to a plain <c>await</c>.
    /// </summary>
    private async Task<(bool Aborted, T? Result)> EgressIfCurrentAsync<T>(
        ProjectFile file, string scannedKey, string? scannedEtag,
        Func<Task<T>> egress, CancellationToken ct)
    {
        if (!await StillOursAndUnquarantinedAsync(file, scannedKey, scannedEtag, ct))
        {
            _logger.LogInformation(
                "File {FileId} was quarantined or replaced before a provider call; nothing was sent", file.Id);
            await AbortStaleScanAsync(file, ct);
            return (true, default);
        }

        return (false, await egress());
    }

    private async Task<ModerationDecision?> ModerateIfCurrentAsync(
        ModerationInput input, ModerationAttribution attribution,
        ProjectFile file, string scannedKey, string? scannedEtag, CancellationToken ct)
    {
        (bool aborted, ModerationDecision? decision) = await EgressIfCurrentAsync(
            file, scannedKey, scannedEtag,
            () => _gate.ModerateAsync(input, attribution, ct), ct);
        return aborted ? null : decision;
    }

    /// <summary>
    /// The scan is no longer current. A Quarantined row is final (nothing to do); anything else is
    /// returned to Unmoderated and re-offered, so it is never left Scanning without a scan.
    /// </summary>
    private async Task AbortStaleScanAsync(ProjectFile file, CancellationToken ct)
    {
        if (file.ModerationStatus == FileModerationStatus.Quarantined) return;
        await ResetStaleScanAndRequeueAsync(file, ct);
    }

    /// <summary>
    /// Records Error even when the failure detached the entity or the caller's token is already
    /// cancelled: the ingest surface fails open, so the file must never be left in <c>Scanning</c>.
    /// A row replaced during the scan, or already Quarantined, is left alone.
    /// </summary>
    private async Task MarkErrorAfterFailureAsync(Guid fileId, string scannedKey, string? scannedEtag)
    {
        try
        {
            ProjectFile? file = await _db.ProjectFiles
                .FirstOrDefaultAsync(f => f.Id == fileId, CancellationToken.None);
            if (file is null) return;

            // Read the row's CURRENT values, not the tracked entity's pre-scan values.
            await _db.Entry(file).ReloadAsync(CancellationToken.None);

            bool replaced = !string.Equals(file.StorageKey, scannedKey, StringComparison.Ordinal)
                            || await BytesChangedInPlaceAsync(
                                file.ProjectId, scannedKey, scannedEtag, CancellationToken.None);

            if (replaced)
            {
                if (file.ModerationStatus != FileModerationStatus.Quarantined)
                    await ResetStaleScanAndRequeueAsync(file, CancellationToken.None);
                return;
            }

            if (file.ModerationStatus == FileModerationStatus.Quarantined)
                return;

            file.ModerationStatus = FileModerationStatus.Error;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A stale Scanning row is reset to Unmoderated by the worker on its next start.
            _logger.LogError(ex, "Could not record Error for file {FileId}", fileId);
        }
    }

    /// <summary>
    /// True when the object at <paramref name="key"/> no longer has the ETag the scan read. A
    /// presigned PUT can overwrite the SAME key with different bytes; the storage key alone cannot
    /// see that. A store that gives no ETag leaves the key check as the only guard.
    /// </summary>
    private async Task<bool> BytesChangedInPlaceAsync(
        Guid projectId, string key, string? scannedEtag, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scannedEtag)) return false;

        try
        {
            string? current = await _storage.GetObjectETagAsync(projectId, key, ct);
            return current is { Length: > 0 }
                   && !string.Equals(current, scannedEtag, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not re-read the object ETag; the storage-key check remains");
            return false;
        }
    }

    private static ModerationContentKind? KindOf(string? mimeType)
    {
        string mime = (mimeType ?? string.Empty).Trim();
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ModerationContentKind.Image;
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return ModerationContentKind.Video;
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return ModerationContentKind.Audio;
        return null;
    }
}
