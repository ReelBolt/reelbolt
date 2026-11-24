using System.Collections.Concurrent;
using Amazon.S3;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The browser-decodable preview of one segmentation track's mask (the viewer draws the real outline from it;
/// browsers cannot decode the FFV1 mask the export uses). <c>200</c> video bytes (Range supported, hardened by
/// <see cref="SafeContentPolicy"/>) when stored; <c>202 {"state":"pending"}</c> after queueing production in the
/// WorkflowEngine; <c>404</c> otherwise. Same authorization as <see cref="SegmentationTracksController"/>: View on
/// the project, and every refusal (foreign or missing result, unknown track, unpreviewable track, unverified mask,
/// output hold (404 with the OUTPUT_UNAVAILABLE body), unreadable artifact) is a 404 except a same-org member without View (403, like every
/// project route). No storage key, bucket or URL appears in any body.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/step-results/{stepResultId:guid}/segmentation-tracks/{trackId}/mask-preview")]
[Authorize]
public class SegmentationMaskPreviewController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly IAmazonS3 _s3;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<SegmentationMaskPreviewController> _logger;

    public SegmentationMaskPreviewController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IAmazonS3 s3,
        IPublishEndpoint publishEndpoint,
        ILogger<SegmentationMaskPreviewController> logger)
    {
        _db = db;
        _projectAccess = projectAccess;
        _storage = storage;
        _s3 = s3;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    /// <summary>Seconds a client is told to wait before asking again after a 202.</summary>
    public const int RetryAfterSeconds = 2;

    private sealed record CachedKeys(IReadOnlyDictionary<string, string> PreviewKeys, DateTime ExpiresAt);

    // The artifact is immutable per key; polling must not re-download and re-parse it on every request.
    private static readonly ConcurrentDictionary<(Guid Project, Guid Id, string Key), CachedKeys> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCached = 256;

    private async Task<bool> QuarantinedAsync(Guid projectId, DateTime? executedAt, DateTime? runFirstStartedAt, DateTime? supersededAt, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await _db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.ContentQuarantinedAt)
                .FirstOrDefaultAsync(ct);
            return marker is not null && (supersededAt is not null || ContentQuarantineRule.Predates(marker, executedAt, runFirstStartedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid projectId, Guid stepResultId, string trackId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (string.IsNullOrWhiteSpace(trackId)) return NotFound();

        var row = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == stepResultId
                && r.WorkflowExecution.ProjectId == projectId
                && r.WorkflowStep.StepType == StepType.VideoAnalyze)
            .Select(r => new { r.ArtifactStorageKey, r.ExecutedAt, r.SupersededAt, r.WorkflowExecution.FirstStartedAt })
            .FirstOrDefaultAsync(ct);
        string? artifactKey = row?.ArtifactStorageKey;
        if (row is null || artifactKey is null
            || !artifactKey.StartsWith(SegmentationTrackIdLookup.ArtifactPrefix(projectId), StringComparison.Ordinal)
            || artifactKey.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        // The held check comes AFTER the project-scoped lookup: a result of another project is a plain 404, never the
        // hold body (which would confirm the id exists).
        if (await Services.Outputs.OutputGate.IsResultHeldAsync(_db, stepResultId, ct)) return Services.Outputs.OutputGate.Unavailable();

        // A result that may predate the project's content-quarantine marker, or was superseded once it is set, serves
        // nothing and derives nothing. A failed read refuses too.
        if (await QuarantinedAsync(projectId, row.ExecutedAt, row.FirstStartedAt, row.SupersededAt, ct)) return NotFound();

        (Guid, Guid, string) cacheKey = (projectId, stepResultId, artifactKey);
        if (!Cache.TryGetValue(cacheKey, out CachedKeys? cached) || cached.ExpiresAt < DateTime.UtcNow)
        {
            VideoAnalysisArtifact? artifact;
            try
            {
                await using Stream stream = await _storage.DownloadAsync(projectId, artifactKey, ct);
                artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read the video-analysis artifact of step result {StepResultId}", stepResultId);
                return NotFound();
            }

            Dictionary<string, string> keys = new(StringComparer.Ordinal);
            foreach (VideoSegmentationTrack t in (artifact?.SegmentationTracks ?? []).Take(SegmentationTracksController.MaxTracks))
            {
                if (SegmentationMaskPreviewIdentity.IsPreviewable(projectId, t))
                {
                    keys[t.Id] = SegmentationMaskPreviewIdentity.CacheKey(t.MaskContentHash, t.Id);
                }
            }

            cached = new CachedKeys(keys, DateTime.UtcNow + CacheTtl);
            if (Cache.Count >= MaxCached)
            {
                Cache.Clear();
            }

            Cache[cacheKey] = cached;
        }

        if (!cached.PreviewKeys.TryGetValue(trackId, out string? previewKey)) return NotFound();

        string storageKey = SegmentationMaskPreviewIdentity.StorageKey(projectId, previewKey);
        if (await _storage.GetMetadataAsync(projectId, storageKey, ct) is not null)
        {
            try
            {
                return await RangedObjectStreamer.StreamAsync(
                    this, _s3, _storage.BucketName, storageKey, SegmentationMaskPreviewIdentity.ContentType, "mask-preview.mp4", ct);
            }
            catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
            {
                return NotFound();
            }
        }

        // The engine left a marker when the mask failed its hash check: no preview will ever exist.
        if (await _storage.GetMetadataAsync(projectId, SegmentationMaskPreviewIdentity.NoneMarkerKey(projectId, previewKey), ct) is not null)
        {
            return NotFound();
        }

        try
        {
            await _publishEndpoint.Publish(
                new SegmentationMaskPreviewRequested(projectId, stepResultId, trackId, DateTime.UtcNow), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Best effort, like every derivative request: the next poll asks again.
            _logger.LogWarning(ex, "Could not publish a mask preview request for step result {StepResultId}", stepResultId);
        }

        Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status202Accepted, new SegmentationMaskPreviewResponse(SegmentationMaskPreviewResponse.Pending));
    }

    private static bool IsMissingObjectError(AmazonS3Exception ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
        string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
        string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal);
}
