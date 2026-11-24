using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>One segmentation track as the editor's mask preview needs it: no storage key, no mask hash.</summary>
public sealed record SegmentationTrackResponse(
    string TrackId,
    string Label,
    int FirstFrame,
    int LastFrame,
    double Fps,
    IReadOnlyList<SegmentationBoxKeyframeResponse> BoxKeyframes,
    double CoverageEndSec,
    SegmentationMaskPreviewResponse MaskPreview);

/// <summary>
/// Whether the track's browser-decodable mask preview can be played: <c>ready</c> (stored, GET the
/// <c>mask-preview</c> route), <c>pending</c> (can be made: that GET answers 202 and starts it) or <c>none</c>
/// (cannot be made: no verifiable mask, or its hash check failed). Never a key, bucket or URL.
/// </summary>
public sealed record SegmentationMaskPreviewResponse(string State)
{
    public const string Ready = "ready";
    public const string Pending = "pending";
    public const string None = "none";
}

/// <summary>A tracked box in source-fraction coordinates (0..1); <c>T</c> is seconds on the source timeline.</summary>
public sealed record SegmentationBoxKeyframeResponse(double T, double X, double Y, double W, double H);

/// <summary>
/// Read side of a VideoAnalyze step result's segmentation tracks (the editor previews a segmentation mask
/// as a tracked box). Scoped to the project and to VideoAnalyze results: a foreign, missing, non-analysis
/// unreadable or track-less result is the same 404. Every refusal is that 404 except a same-org member without
/// View, who gets 403 like every project route (<see cref="IProjectAccessService"/>). The artifact is read only under the project's video-analysis
/// prefix, through <see cref="SegmentationTrackIdLookup"/>'s parser.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/step-results/{stepResultId:guid}/segmentation-tracks")]
[Authorize]
public class SegmentationTracksController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly ILogger<SegmentationTracksController> _logger;

    public SegmentationTracksController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        ILogger<SegmentationTracksController> logger)
    {
        _db = db;
        _projectAccess = projectAccess;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Most tracks returned; the mask holds at most 8 of them.</summary>
    public const int MaxTracks = 64;

    /// <summary>Most box keyframes returned per track; longer tracks are evenly thinned (first and last kept).</summary>
    public const int MaxKeyframesPerTrack = 2000;

    // PreviewKeys: trackId -> the track's preview cache key, for the tracks a preview can be made for. The state
    // itself is NOT cached (it flips to ready within seconds of a request): it is read per response.
    private sealed record CachedResponse(List<SegmentationTrackResponse> Tracks, DateTime ExpiresAt, IReadOnlyDictionary<string, string> PreviewKeys);

    // Artifacts are immutable per key: a short bounded cache by (project, result, key) keeps a looping client from
    // re-downloading and re-parsing up to 64 MB per GET (the shared lookup caches facts, not boxes, so this is its twin).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Project, Guid Id, string Key), CachedResponse> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCachedResponses = 256;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SegmentationTrackResponse>>> Get(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        string? key = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.Id == stepResultId
                && r.WorkflowExecution.ProjectId == projectId
                && r.WorkflowStep.StepType == StepType.VideoAnalyze)
            .Select(r => r.ArtifactStorageKey)
            .FirstOrDefaultAsync(ct);
        if (key is null
            || !key.StartsWith(SegmentationTrackIdLookup.ArtifactPrefix(projectId), StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        // After the project-scoped lookup: an id from another project is a plain 404, never a hold answer.
        if (await Services.Outputs.OutputGate.IsResultHeldAsync(_db, stepResultId, ct)) return Services.Outputs.OutputGate.Unavailable();

        (Guid, Guid, string) cacheKey = (projectId, stepResultId, key);
        if (!Cache.TryGetValue(cacheKey, out CachedResponse? cached) || cached.ExpiresAt < DateTime.UtcNow)
        {
            VideoAnalysisArtifact? artifact;
            try
            {
                await using Stream stream = await _storage.DownloadAsync(projectId, key, ct);
                artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read the video-analysis artifact of step result {StepResultId}", stepResultId);
                return NotFound();
            }

            // A non-finite time cannot be serialised (System.Text.Json refuses NaN): such a track is dropped, never a 500.
            List<SegmentationTrackResponse> built = (artifact?.SegmentationTracks ?? [])
                .Where(t => !string.IsNullOrEmpty(t.Id) && double.IsFinite(t.StartSec) && double.IsFinite(t.EndSec))
                .Take(MaxTracks)
                .Select(ToResponse)
                .ToList();
            Dictionary<string, string> previewKeys = new(StringComparer.Ordinal);
            foreach (VideoSegmentationTrack t in (artifact?.SegmentationTracks ?? [])
                .Where(t => !string.IsNullOrEmpty(t.Id) && double.IsFinite(t.StartSec) && double.IsFinite(t.EndSec))
                .Take(MaxTracks))
            {
                if (SegmentationMaskPreviewIdentity.IsPreviewable(projectId, t))
                {
                    previewKeys[t.Id] = SegmentationMaskPreviewIdentity.CacheKey(t.MaskContentHash, t.Id);
                }
            }

            cached = new CachedResponse(built, DateTime.UtcNow + CacheTtl, previewKeys);
            if (Cache.Count >= MaxCachedResponses)
            {
                Cache.Clear();
            }

            Cache[cacheKey] = cached;
        }

        if (cached.Tracks.Count == 0) return NotFound();

        SegmentationTrackResponse[] withState = await Task.WhenAll(cached.Tracks.Select(async t =>
            cached.PreviewKeys.TryGetValue(t.TrackId, out string? previewKey)
                ? t with { MaskPreview = new SegmentationMaskPreviewResponse(await PreviewStateAsync(projectId, previewKey, ct)) }
                : t));
        return Ok(withState);
    }

    /// <summary>ready when the preview object is stored, none when the engine left an "unverified" marker, else pending. A failed read is pending.</summary>
    private async Task<string> PreviewStateAsync(Guid projectId, string previewKey, CancellationToken ct)
    {
        try
        {
            if (await _storage.GetMetadataAsync(projectId, SegmentationMaskPreviewIdentity.StorageKey(projectId, previewKey), ct) is not null)
            {
                return SegmentationMaskPreviewResponse.Ready;
            }

            return await _storage.GetMetadataAsync(projectId, SegmentationMaskPreviewIdentity.NoneMarkerKey(projectId, previewKey), ct) is not null
                ? SegmentationMaskPreviewResponse.None
                : SegmentationMaskPreviewResponse.Pending;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the mask preview state of {PreviewKey}", previewKey);
            return SegmentationMaskPreviewResponse.Pending;
        }
    }

    private static SegmentationTrackResponse ToResponse(VideoSegmentationTrack t)
    {
        double fps = double.IsFinite(t.Fps) && t.Fps > 0 ? t.Fps : 30;
        int first = t.Facts?.FirstNonEmptyFrame ?? (int)Math.Round(t.StartSec * fps);
        int last = t.Facts?.LastNonEmptyFrame ?? Math.Max(first, (int)Math.Round(t.EndSec * fps) - 1);
        IReadOnlyList<VideoSegmentationBoxFrame> boxes = t.BoxKeyframes ?? [];
        IEnumerable<VideoSegmentationBoxFrame> kept = boxes.Count <= MaxKeyframesPerTrack
            ? boxes
            : Enumerable.Range(0, MaxKeyframesPerTrack).Select(i => boxes[(int)((long)i * (boxes.Count - 1) / (MaxKeyframesPerTrack - 1))]);
        return new SegmentationTrackResponse(
            t.Id,
            t.Prompt ?? string.Empty,
            first,
            last,
            fps,
            kept.Where(b => double.IsFinite(b.TimeSec) && double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.W) && double.IsFinite(b.H))
                .Select(b => new SegmentationBoxKeyframeResponse(b.TimeSec, b.X, b.Y, b.W, b.H))
                .ToList(),
            SegmentationTrackFacts.CoverageEndOf(t),
            new SegmentationMaskPreviewResponse(SegmentationMaskPreviewResponse.None));
    }
}
