using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

public enum SegmentationMaskPreviewOutcome
{
    /// <summary>The preview was encoded and stored.</summary>
    Produced,

    /// <summary>The same preview (same mask hash, track id, recipe) was already stored.</summary>
    CacheHit,

    /// <summary>Nothing was produced: unknown or foreign result or track, an unpreviewable track, or the project's output hold.</summary>
    Refused,

    /// <summary>The mask failed its hash check (<c>SEGMENTATION_MASK_UNVERIFIED</c>): there is no preview, and a marker says so.</summary>
    Unverified,

    /// <summary>The encode or an I/O step failed; a later request tries again.</summary>
    Failed
}

/// <summary>
/// Produces one track's mask preview (see <see cref="SegmentationMaskPreviewIdentity"/>). Free: no credits,
/// no database row, no project file; the object store is the cache. Every decision is re-made here from the
/// database and the artifact, never from the message.
/// </summary>
public sealed class SegmentationMaskPreviewService(
    IDbContextFactory<WorkflowEngineDbContext> dbFactory,
    IProjectFileWorkspace files,
    IVideoToolRunner runner,
    IOptions<VideoEditingOptions> options,
    ILogger<SegmentationMaskPreviewService> logger)
{
    private readonly VideoEditingOptions _options = options.Value;

    /// <summary>Scratch subdirectory of <see cref="VideoEditingOptions.ScratchPath"/> previews are encoded in.</summary>
    public const string ScratchSubdirectory = "segmentation-preview";

    public async Task<SegmentationMaskPreviewOutcome> ProduceAsync(SegmentationMaskPreviewRequested request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId == Guid.Empty || request.StepResultId == Guid.Empty || string.IsNullOrWhiteSpace(request.TrackId))
        {
            return SegmentationMaskPreviewOutcome.Refused;
        }

        string? artifactKey = await ResolveServedArtifactKeyAsync(request, ct).ConfigureAwait(false);
        if (artifactKey is null)
        {
            return SegmentationMaskPreviewOutcome.Refused;
        }

        string root = Path.GetFullPath(_options.ScratchPath);
        string directory = Path.GetFullPath(Path.Combine(
            root, ScratchSubdirectory, request.ProjectId.ToString("D"), Guid.NewGuid().ToString("N")));
        VideoScratchSpace.ValidateScratchScope(root, directory);
        Directory.CreateDirectory(directory);

        try
        {
            return await ProduceInScratchAsync(request, artifactKey, directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Mask preview for step result {StepResultId} track {TrackId} failed", request.StepResultId, request.TrackId);
            return SegmentationMaskPreviewOutcome.Failed;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not remove the mask preview scratch directory {Directory}", directory);
            }
        }
    }

    private async Task<SegmentationMaskPreviewOutcome> ProduceInScratchAsync(
        SegmentationMaskPreviewRequested request, string artifactKey, string directory, CancellationToken ct)
    {
        Guid projectId = request.ProjectId;

        string artifactPath = Path.Combine(directory, "analysis.json");
        await files.DownloadStorageKeyToFileAsync(projectId, artifactKey, artifactPath, SegmentationTrackIdLookup.MaxArtifactBytes, ct).ConfigureAwait(false);
        VideoAnalysisArtifact? artifact;
        await using (FileStream stream = File.OpenRead(artifactPath))
        {
            artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct).ConfigureAwait(false);
        }

        VideoSegmentationTrack? track = artifact?.SegmentationTracks?
            .FirstOrDefault(t => string.Equals(t.Id, request.TrackId, StringComparison.Ordinal));
        if (track is null || !SegmentationMaskPreviewIdentity.IsPreviewable(projectId, track))
        {
            return SegmentationMaskPreviewOutcome.Refused;
        }

        string cacheKey = SegmentationMaskPreviewIdentity.CacheKey(track.MaskContentHash, track.Id);
        if (await files.ArtifactExistsAsync(projectId, SegmentationMaskPreviewIdentity.StorageKey(projectId, cacheKey), ct).ConfigureAwait(false))
        {
            return SegmentationMaskPreviewOutcome.CacheHit;
        }

        if (await files.ArtifactExistsAsync(projectId, SegmentationMaskPreviewIdentity.NoneMarkerKey(projectId, cacheKey), ct).ConfigureAwait(false))
        {
            return SegmentationMaskPreviewOutcome.Unverified;
        }

        string maskPath = Path.Combine(directory, "mask.mkv");
        await files.DownloadStorageKeyToFileAsync(projectId, track.MaskStorageKey, maskPath, ct).ConfigureAwait(false);

        // The same check the render makes before it applies a mask: a mask that is not the one the artifact
        // recorded is never turned into anything the viewer shows.
        if (!await SegmentationMaskStore.VerifyHashAsync(maskPath, track.MaskContentHash, ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "SEGMENTATION_MASK_UNVERIFIED: no preview for step result {StepResultId} track {TrackId}",
                request.StepResultId, request.TrackId);
            string markerPath = Path.Combine(directory, "none.txt");
            await File.WriteAllTextAsync(markerPath, "SEGMENTATION_MASK_UNVERIFIED", ct).ConfigureAwait(false);
            await files.UploadArtifactAsync(
                projectId, markerPath, SegmentationMaskPreviewIdentity.NoneMarkerUploadFileName(cacheKey), "text/plain", ct).ConfigureAwait(false);
            return SegmentationMaskPreviewOutcome.Unverified;
        }

        string outputPath = Path.Combine(directory, "preview.mp4");
        VideoToolResult result = await runner.RunFfmpegAsync(
            SegmentationMaskPreviewArgvBuilder.Build(maskPath, outputPath, track.Fps, track.FrameCount),
            SourceTimeoutPolicy.Analyze(_options),
            ct,
            VideoToolPriority.Low).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(outputPath))
        {
            logger.LogWarning(
                "ffmpeg could not encode the mask preview for step result {StepResultId} track {TrackId} (exit {ExitCode}, timedOut {TimedOut})",
                request.StepResultId, request.TrackId, result.ExitCode, result.TimedOut);
            return SegmentationMaskPreviewOutcome.Failed;
        }

        await files.UploadArtifactAsync(
            projectId, outputPath, SegmentationMaskPreviewIdentity.UploadFileName(cacheKey), SegmentationMaskPreviewIdentity.ContentType, ct).ConfigureAwait(false);
        return SegmentationMaskPreviewOutcome.Produced;
    }

    /// <summary>
    /// The analysis artifact's key when the result is this project's VideoAnalyze result, its artifact sits under
    /// the project's own analysis prefix, and the project's output hold does not cover it; otherwise null. A failed
    /// read refuses (a held project serves nothing).
    /// </summary>
    private async Task<string?> ResolveServedArtifactKeyAsync(SegmentationMaskPreviewRequested request, CancellationToken ct)
    {
        try
        {
            await using WorkflowEngineDbContext db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == request.StepResultId
                    && r.WorkflowExecution.ProjectId == request.ProjectId
                    && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .Select(r => new { r.ArtifactStorageKey, r.ExecutedAt, r.SupersededAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row?.ArtifactStorageKey is not { } key
                || !key.StartsWith(SegmentationTrackIdLookup.ArtifactPrefix(request.ProjectId), StringComparison.Ordinal)
                || key.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }

            // A result that may predate the project's content-quarantine marker, or was superseded once it is set, is
            // not a source for a preview (the same gate every other reader of a stored result applies).
            if (await Execution.Caching.ProjectQuarantineState.ResultPredatesAsync(
                    db, request.ProjectId, row.ExecutedAt, row.FirstStartedAt, ct, row.SupersededAt).ConfigureAwait(false))
            {
                return null;
            }

            ProjectOutputHoldState state = await ProjectOutputHold.ReadAsync(db, request.ProjectId, ct).ConfigureAwait(false);
            if (state.IsActive
                && (state.Unreadable || state.HoldsOutput(row.ExecutedAt, ProjectOutputHold.RunStart(row.FirstStartedAt, row.StartedAt))))
            {
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read step result {StepResultId} for a mask preview; refusing", request.StepResultId);
            return null;
        }
    }
}
