using System.Linq.Expressions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Media;

/// <summary>One class of video source: how many there are and how many already have a live proxy.</summary>
public sealed record MediaDerivativeSourceCoverage(int Total, int WithProxy)
{
    /// <summary>Sources in this class with no live <c>Proxy</c> row — the backlog the sweeper works.</summary>
    public int WithoutProxy => Total - WithProxy;
}

/// <summary>
/// The answer to "how many video sources still play their original", without SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two buckets, because the two source shapes are different tables.</b> An uploaded video is a
/// <c>project_files</c> row; an unregistered render exists only as a
/// <c>workflow_step_results.output_storage_key</c>. Both are media sources in the editor and both need
/// a proxy, so reporting one total without the split would hide which table repairs what.
/// </para>
/// <para>
/// <b>The step-result bucket mirrors the media pool's registered-render rule.</b> A render that was
/// also registered as a project file is listed as the <c>file:</c> source and MUST NOT be counted (or
/// requested) as a <c>step_result:</c> one — the same bytes under two references would double every
/// count and every request.
/// </para>
/// </remarks>
public sealed record MediaDerivativeCoverageReport(
    MediaDerivativeSourceCoverage VideoProjectFiles,
    MediaDerivativeSourceCoverage StepResultRenders,
    DateTime AsOf)
{
    /// <summary>Every video source the editor can show (project files plus unregistered renders).</summary>
    public int VideoSources => VideoProjectFiles.Total + StepResultRenders.Total;

    /// <summary>Video sources that already have a live proxy.</summary>
    public int WithProxy => VideoProjectFiles.WithProxy + StepResultRenders.WithProxy;

    /// <summary>Video sources that would play their original today.</summary>
    public int WithoutProxy => VideoProjectFiles.WithoutProxy + StepResultRenders.WithoutProxy;
}

/// <summary>What one sweep did, with the coverage it observed before/while requesting.</summary>
public sealed record MediaDerivativeSweepResult(
    int Requested,
    int Deferred,
    int Failed,
    bool BatchFull,
    MediaDerivativeCoverageReport Coverage);

/// <summary>
/// The durable repair for derivative requests that were lost: finds every video source with no live
/// proxy and publishes one request for its derivative set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Uploads publish <c>MediaDerivativesRequested</c> best-effort; a broker
/// outage loses the hint and, before this service, nothing re-derived the work. In production that
/// left 107 of 152 video files playing originals of up to 71 MB in 1 MB chunks. The source rows are
/// the durable record that the work exists, so the repair is a selection over them — see
/// <see cref="MediaDerivativeBackfillOptions"/> for why an outbox was rejected.
/// </para>
/// <para>
/// <b>Idempotent by construction, on both sides.</b> A source leaves the candidate set as soon as a
/// live <c>Proxy</c> row exists, so once the engine has caught up a re-run selects nothing. A repeat
/// that lands while the engine is still working is also harmless: the dispatcher's cache pre-check
/// (<c>media_derivatives.cache_key</c>) turns it into a no-op and the unique index is the cross-replica
/// backstop.
/// </para>
/// <para>
/// <b>A source whose ETag cannot be read is deferred, not published empty.</b> An empty ETag makes the
/// engine substitute a content hash (the honest fallback for an upload that must not fail), which is a
/// DIFFERENT cache identity from the S3 ETag every other producer sends — producing a second copy of
/// every derivative of that source. A sweep may simply wait: the source stays a candidate.
/// </para>
/// <para>
/// <b>Nothing here throws on a broker failure.</b> Each publish is isolated; a failed one counts and
/// the remaining sources are still attempted. The failed source remains a candidate for the next tick,
/// which is the whole point of deriving candidates from rows rather than remembering requests.
/// </para>
/// <para>
/// <b>Scope.</b> The selection spans every organization, so it must run inside a system tenant scope;
/// it serves no user and checks no project permission. It never calls the owner-scoped
/// <c>POST /media/derivatives/request</c> and never requires anyone to open a timeline.
/// </para>
/// </remarks>
public sealed class MediaDerivativeCoverageService(
    InferenceApiDbContext db,
    IFileStorageService storage,
    IPublishEndpoint publishEndpoint,
    ITenantContext? tenant,
    ILogger<MediaDerivativeCoverageService> logger,
    ReelBolt.Inference.Api.Services.Uploads.IUploadAnalysisSetting? uploadAnalysis = null)
{
    /// <summary>
    /// A converted proxy (variant above 0) carries a <c>cameraProfile</c> key in its spec JSON; the plain proxy never
    /// does. Coverage counts only the plain proxy, so a source with only a converted row still gets its plain one.
    /// </summary>
    private const string ConvertedProxyMarker = "\"cameraProfile\"";

    /// <summary>
    /// The container extensions the media pool accepts as video for a render. Kept in one place so the
    /// coverage count and the selection can never disagree about which renders are video sources.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> VideoMimeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".m4v"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".webm"] = "video/webm",
            [".mkv"] = "video/x-matroska",
            [".avi"] = "video/x-msvideo",
        };

    /// <summary>An uploaded video with no live proxy, newest-source-first ordering applied by the caller.</summary>
    private static readonly Expression<Func<ProjectFile, bool>> IsVideoProjectFile =
        f => f.MimeType.StartsWith("video/");

    /// <summary>
    /// An unregistered render's output key: under <c>outputFiles/</c>, a video container, and not the
    /// same bytes as a project file that already covers it (the pool's registered-render rule).
    /// </summary>
    /// <remarks>
    /// The project prefix is checked by shape (<c>projects/</c> plus <c>/outputFiles/</c>) rather than
    /// against one project id because the sweep spans every project; the consumer re-checks the exact
    /// key against the owning row before doing any work.
    /// </remarks>
    private static Expression<Func<WorkflowStepResult, bool>> IsVideoRender(InferenceApiDbContext context) =>
        r => r.OutputStorageKey != null
             && r.OutputStorageKey.StartsWith("projects/")
             && r.OutputStorageKey.Contains("/outputFiles/")
             && (r.OutputStorageKey.ToLower().EndsWith(".mp4")
                 || r.OutputStorageKey.ToLower().EndsWith(".m4v")
                 || r.OutputStorageKey.ToLower().EndsWith(".mov")
                 || r.OutputStorageKey.ToLower().EndsWith(".webm")
                 || r.OutputStorageKey.ToLower().EndsWith(".mkv")
                 || r.OutputStorageKey.ToLower().EndsWith(".avi"))
             && !context.ProjectFiles.Any(f => f.ProjectId == r.WorkflowExecution.ProjectId
                                               && f.StorageKey == r.OutputStorageKey);

    /// <summary>A project file that already has a live proxy. The query filter supplies <c>deleted_at IS NULL</c>.</summary>
    private static Expression<Func<ProjectFile, bool>> HasProxyForFile(InferenceApiDbContext context) =>
        f => context.MediaDerivatives.Any(d => d.SourceKind == MediaSourceKind.ProjectFile
                                              && d.SourceProjectFileId == f.Id
                                              && d.Kind == MediaDerivativeKind.Proxy
                                              && !Convert.ToString(d.ParamsJson)!.Contains(ConvertedProxyMarker));

    /// <summary>A render that already has a live proxy.</summary>
    private static Expression<Func<WorkflowStepResult, bool>> HasProxyForRender(InferenceApiDbContext context) =>
        r => context.MediaDerivatives.Any(d => d.SourceKind == MediaSourceKind.StepResult
                                              && d.SourceStepResultId == r.Id
                                              && d.Kind == MediaDerivativeKind.Proxy
                                              && !Convert.ToString(d.ParamsJson)!.Contains(ConvertedProxyMarker));

    /// <summary>
    /// The coverage figures, read under a system scope. Every number is the selection's own predicate,
    /// so <c>WithoutProxy</c> is exactly the backlog a sweep would request.
    /// </summary>
    public async Task<MediaDerivativeCoverageReport> GetCoverageAsync(CancellationToken ct)
    {
        using IDisposable system = tenant.System();

        int filesTotal = await db.ProjectFiles.AsNoTracking().CountAsync(IsVideoProjectFile, ct);
        int filesWithProxy = await db.ProjectFiles.AsNoTracking()
            .Where(IsVideoProjectFile)
            .CountAsync(HasProxyForFile(db), ct);

        Expression<Func<WorkflowStepResult, bool>> videoRender = IsVideoRender(db);
        int rendersTotal = await db.WorkflowStepResults.AsNoTracking().CountAsync(videoRender, ct);
        int rendersWithProxy = await db.WorkflowStepResults.AsNoTracking()
            .Where(videoRender)
            .CountAsync(HasProxyForRender(db), ct);

        return new MediaDerivativeCoverageReport(
            new MediaDerivativeSourceCoverage(filesTotal, filesWithProxy),
            new MediaDerivativeSourceCoverage(rendersTotal, rendersWithProxy),
            DateTime.UtcNow);
    }

    /// <summary>How long a source the sweep already requested is left alone.</summary>
    internal static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(90);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> RecentlyRequested = new();

    /// <summary>
    /// One sweep: select the batch, publish one request per source, report what happened. Never
    /// throws for a broker failure — see the class remarks.
    /// </summary>
    /// <param name="requestedBy">A <c>MediaDerivativeRequests</c> constant, carried onto the event for diagnosis.</param>
    /// <param name="batchSize">Most sources to request in this sweep; clamped to at least one.</param>
    /// <param name="ct">The sweep's own token (a shutdown aborts a tick; the next tick starts over).</param>
    public async Task<MediaDerivativeSweepResult> SweepOnceAsync(
        string requestedBy,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        using IDisposable system = tenant.System();

        int take = Math.Max(1, batchSize);
        List<MediaDerivativeSource> candidates = await FindMissingSourcesAsync(take, ct);

        int requested = 0;
        int deferred = 0;
        int failed = 0;
        int skippedNonMedia = 0;

        foreach (MediaDerivativeSource source in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // A source this process asked for recently is still being worked on (a 4K proxy takes an hour) or
            // has no row for a reason a repeat will not change; asking every sweep only queues duplicate
            // downloads. It becomes a candidate again after the cooldown.
            if (RecentlyRequested.TryGetValue(source.SourceId, out DateTime lastAsked)
                && DateTime.UtcNow - lastAsked < RequestCooldown)
            {
                continue;
            }

            string? mimeType = source.MimeType ?? MimeTypeFor(source.StorageKey);
            if (mimeType is null)
            {
                // The row's MIME is empty or the key's extension is not a video container. It cannot
                // be a video source, so it is not this sweep's work; the next sweep will not select it
                // either, because the predicate is the same one.
                continue;
            }

            // A TypeScript file mislabelled video/vnd.dlna.mpeg-tts is re-downloaded and fails ffprobe on every sweep.
            if (IsNonMediaTransportStreamName(mimeType, source.StorageKey, source.SizeBytes))
            {
                skippedNonMedia++;
                continue;
            }

            // A render that may predate the project's output marker, or a marker that cannot be read, is skipped:
            // no request, no storage call.
            if (await IsWithheldAsync(source, ct))
                continue;

            string? etag = await storage.GetObjectETagAsync(source.ProjectId, source.StorageKey, ct);
            if (string.IsNullOrWhiteSpace(etag))
            {
                deferred++;
                // Debug, not Information: a HEAD failure is already logged by the storage service, and
                // a broker or object-store outage would otherwise print one line per source per sweep.
                // The sweep summary carries the count. No storage key in the message.
                logger.LogDebug(
                    "Media derivative backfill deferred {SourceKind} {SourceId} in project {ProjectId}: " +
                    "the object store answered no ETag; it stays a candidate for the next sweep",
                    source.Kind, source.SourceId, source.ProjectId);
                continue;
            }

            try
            {
                // A video that arrived with no upload request of its own (a synced folder, a repository sync, a
                // request the broker lost) gets its derivatives here, so the project's analysis switch is asked
                // here too. A render that is no project file is never analysed for it.
                bool analyze = source.Kind == MediaSourceKind.ProjectFile
                    && await ReelBolt.Inference.Api.Services.Uploads.UploadDerivativeRequest.AnalyzeForWorkflowsAsync(
                        uploadAnalysis, source.ProjectId, source.SourceId, mimeType, logger, ct);

                await publishEndpoint.Publish(new MediaDerivativesRequested(
                    source.ProjectId,
                    new MediaSourceRef(source.Kind, source.SourceId),
                    source.Kind == MediaSourceKind.ProjectFile ? source.SourceId : null,
                    source.StorageKey,
                    etag,
                    mimeType,
                    MediaDerivativeRequests.DefaultKinds,
                    requestedBy,
                    DateTime.UtcNow,
                    AnalyzeForWorkflows: analyze), ct);

                RecentlyRequested[source.SourceId] = DateTime.UtcNow;
                requested++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A broker outage must not stop the rest of the batch, and the source must stay a
                // candidate: a publish that never happened is exactly the failure this service exists
                // to repair. The message names ids, never the storage key.
                failed++;
                logger.LogWarning(
                    ex,
                    "Media derivative backfill could not publish a request for {SourceKind} {SourceId} " +
                    "in project {ProjectId}; it stays a candidate for the next sweep",
                    source.Kind, source.SourceId, source.ProjectId);
            }
        }

        if (skippedNonMedia > 0)
        {
            logger.LogInformation(
                "Media derivative sweep skipped {SkippedNonMedia} source(s) that cannot be media (tiny .ts files labelled as MPEG transport streams)",
                skippedNonMedia);
        }

        MediaDerivativeCoverageReport coverage = await GetCoverageAsync(ct);

        return new MediaDerivativeSweepResult(
            requested,
            deferred,
            failed,
            BatchFull: candidates.Count >= take,
            coverage);
    }

    /// <summary>
    /// The batch's candidates, oldest source first, split across the two source tables.
    /// </summary>
    /// <remarks>
    /// Each table is asked for at most <paramref name="take"/> rows and the merged list is trimmed to
    /// the same bound, so one table with a large backlog cannot starve the other for more than a tick.
    /// </remarks>
    private async Task<List<MediaDerivativeSource>> FindMissingSourcesAsync(int take, CancellationToken ct)
    {
        // Sources that are already known to be withheld (a stamp at or before the project's stored marker) are left out of
        // the query itself, so they cannot fill the oldest-first batch. A user's own upload and a later render in the same
        // project stay candidates. IsWithheldAsync still judges every candidate that is selected. The project lookup is a
        // plain subquery: ignoring query filters inside it would switch them off for the whole query.
        TimeSpan margin = ContentQuarantineRule.ClockSkewMargin;

        List<MediaDerivativeSource> files = await db.ProjectFiles.AsNoTracking()
            .Where(IsVideoProjectFile)
            // A file whose CSAM verdict is not settled waits: the scan sweeper requests its derivatives after it.
            .Where(f => f.CsamStatus != CsamScanStatus.Pending && f.CsamStatus != CsamScanStatus.Scanning
                        && f.CsamStatus != CsamScanStatus.Quarantined && f.CsamQuarantineKey == null)
            .Where(f => f.OutputProducedAt == null || !db.Projects.Any(p =>
                p.Id == f.ProjectId && p.CsamOutputHoldAt != null && f.OutputProducedAt <= p.CsamOutputHoldAt + margin))
            .Where(f => !db.MediaDerivatives.Any(d => d.SourceKind == MediaSourceKind.ProjectFile
                                                     && d.SourceProjectFileId == f.Id
                                                     && d.Kind == MediaDerivativeKind.Proxy
                                              && !Convert.ToString(d.ParamsJson)!.Contains(ConvertedProxyMarker)))
            .OrderBy(f => f.UploadedAt)
            .ThenBy(f => f.Id)
            .Take(take)
            .Select(f => new MediaDerivativeSource(
                f.ProjectId, MediaSourceKind.ProjectFile, f.Id, f.StorageKey, f.MimeType, f.UploadedAt,
                null, null, null, f.OutputProducedAt, f.SizeBytes))
            .ToListAsync(ct);

        Expression<Func<WorkflowStepResult, bool>> videoRender = IsVideoRender(db);
        List<MediaDerivativeSource> renders = await db.WorkflowStepResults.AsNoTracking()
            .Where(videoRender)
            .Where(r => !db.Projects.Any(p =>
                p.Id == r.WorkflowExecution.ProjectId && p.CsamOutputHoldAt != null
                && (r.ExecutedAt <= p.CsamOutputHoldAt + margin
                    || (r.WorkflowExecution.FirstStartedAt == null && r.WorkflowExecution.StartedAt == null)
                    || r.WorkflowExecution.FirstStartedAt <= p.CsamOutputHoldAt + margin
                    || r.WorkflowExecution.StartedAt <= p.CsamOutputHoldAt + margin)))
            .Where(r => !db.MediaDerivatives.Any(d => d.SourceKind == MediaSourceKind.StepResult
                                                     && d.SourceStepResultId == r.Id
                                                     && d.Kind == MediaDerivativeKind.Proxy
                                              && !Convert.ToString(d.ParamsJson)!.Contains(ConvertedProxyMarker)))
            .OrderBy(r => r.CompletedAt ?? r.ExecutedAt)
            .ThenBy(r => r.Id)
            .Take(take)
            .Select(r => new MediaDerivativeSource(
                r.WorkflowExecution.ProjectId,
                MediaSourceKind.StepResult,
                r.Id,
                r.OutputStorageKey!,
                null,
                r.CompletedAt ?? r.ExecutedAt,
                r.ExecutedAt,
                r.WorkflowExecution.StartedAt,
                RunFirstStartedAt: r.WorkflowExecution.FirstStartedAt))
            .ToListAsync(ct);

        return files
            .Concat(renders)
            .OrderBy(s => s.SortAt)
            .ThenBy(s => s.SourceId)
            .Take(take)
            .ToList();
    }

    private async Task<bool> IsWithheldAsync(MediaDerivativeSource source, CancellationToken ct)
    {
        ProjectOutputHoldState state = await ProjectOutputHold.ReadAsync(db, source.ProjectId, ct);
        if (!state.IsActive)
            return false;
        if (state.Unreadable)
            return true;

        return source.Kind == MediaSourceKind.StepResult
            ? state.HoldsOutput(source.ExecutedAt, ProjectOutputHold.RunStart(source.RunFirstStartedAt, source.RunStartedAt))
            : ProjectOutputHold.IsPlatformMade(source.ProjectId, source.StorageKey, source.OutputProducedAt)
              && state.HoldsFile(ProjectOutputHold.FileStamp(source.OutputProducedAt, source.SortAt));
    }

    /// <summary>
    /// The video MIME type a storage key's extension implies, or null for any other extension.
    /// </summary>
    private static string? MimeTypeFor(string storageKey) =>
        VideoMimeByExtension.TryGetValue(Path.GetExtension(storageKey), out string? mimeType) ? mimeType : null;

    /// <summary>Largest size of a <c>.ts</c> file that is treated as source code rather than an MPEG transport stream.</summary>
    public const long NonMediaTsMaxBytes = 64 * 1024;

    /// <summary>
    /// True for a TypeScript code file mislabelled as an MPEG transport stream: mime video/vnd.dlna.mpeg-tts or
    /// video/mp2t, a <c>.ts</c> name, and a known size under 64 KiB. Narrow on purpose: an unknown size is never skipped.
    /// </summary>
    public static bool IsNonMediaTransportStreamName(string? mimeType, string storageKey, long? sizeBytes) =>
        sizeBytes is { } size && size < NonMediaTsMaxBytes
        && (string.Equals(mimeType, "video/vnd.dlna.mpeg-tts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "video/mp2t", StringComparison.OrdinalIgnoreCase))
        && string.Equals(Path.GetExtension(storageKey), ".ts", StringComparison.OrdinalIgnoreCase);

    /// <summary>One selected source, carrying everything the event needs.</summary>
    private sealed record MediaDerivativeSource(
        Guid ProjectId,
        MediaSourceKind Kind,
        Guid SourceId,
        string StorageKey,
        string? MimeType,
        DateTime SortAt,
        DateTime? ExecutedAt = null,
        DateTime? RunStartedAt = null,
        DateTime? RunFirstStartedAt = null,
        DateTime? OutputProducedAt = null,
        long? SizeBytes = null);
}
