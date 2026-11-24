using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ReelBolt.Inference.Api.Services.EditTimelines;

/// <summary>
/// Builds the caller-supplied lookups <see cref="EditTimelineValidator"/> needs from the database: the two ownership maps, the known
/// source durations and the segmentation facts. Shared by the edit-timelines controller and <see cref="EditTimelineWriter"/>.
/// Both ownership queries run WITHOUT a project filter on purpose: the question is "who really owns this id", so a foreign id
/// resolves to its other project and the validator rejects it for the right reason. Holds a per-instance duration cache.
/// </summary>
public sealed class EditTimelineValidationContextBuilder(InferenceApiDbContext db, IFileStorageService storage, ILogger logger)
{
    private SourceDurationResolver? _sourceDurations;

    public async Task<EditTimelineValidationContext> BuildAsync(
        Guid projectId,
        EditTimeline document,
        CancellationToken ct)
    {
        HashSet<Guid> referencedFileIds = [];
        HashSet<Guid> referencedStepResultIds = [];

        // Collecting ids is a CONVENIENCE for the validator, not a validation of its own: the
        // validator owns every "must not be null" branch, so a null element is skipped here, not
        // reported. P8: the walk covers the root tracks, every sequence's tracks and every multicam
        // angle (what the flattened document reads) plus the LUT files grades name; provenance step
        // results resolve through the SAME ownership map as AssetRefKind.StepResult.
        foreach (AssetRef? reference in TimelineAssetReferences.MediaSources(document).Concat(TimelineAssetReferences.LutFiles(document)))
        {
            if (reference?.ProjectFileId is { } fileId)
            {
                referencedFileIds.Add(fileId);
            }

            if (reference?.StepResultId is { } stepResultId)
            {
                referencedStepResultIds.Add(stepResultId);
            }
        }

        referencedStepResultIds.UnionWith(TimelineAssetReferences.ProvenanceStepResultIds(document));

        List<Guid> fileIds = referencedFileIds.ToList();
        var fileRows = await db.ProjectFiles
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ProjectId, f.StorageKey, f.UploadedAt, f.OutputProducedAt })
            .ToListAsync(ct);
        Dictionary<Guid, Guid> projectFileOwners = fileRows.ToDictionary(row => row.Id, row => row.ProjectId);

        List<Guid> stepResultIds = referencedStepResultIds.ToList();
        var resultRows = await db.WorkflowStepResults
            .Where(r => stepResultIds.Contains(r.Id))
            .Select(r => new { r.Id, ProjectId = r.WorkflowExecution.ProjectId, r.OutputStorageKey })
            .ToListAsync(ct);
        Dictionary<Guid, Guid> stepResultOwners = resultRows.ToDictionary(row => row.Id, row => row.ProjectId);

        // Sources whose bytes are not served are not read for durations or segmentation facts: they count as unknown.
        HashSet<Guid> unservedFiles = await OutputGate.HeldFileIdsAsync(
            db, projectId, fileRows.Where(r => r.ProjectId == projectId).Select(r => (r.Id, r.StorageKey, r.UploadedAt, r.OutputProducedAt)).ToList(), ct);
        HashSet<Guid> unservedResults = await OutputGate.HeldResultIdsAsync(
            db,
            projectId,
            resultRows.Where(r => r.ProjectId == projectId).Select(r => r.Id).Concat(TimelineAssetReferences.SegmentationMaskStepResultIds(document)).Distinct().ToList(),
            ct);

        // R01.6: the known source durations (ingest probe, else the VideoAnalyze artifact) re-arm the
        // validator's overrun check; a source with no known duration is simply left out (not checked).
        double fps = document.Settings is { Rate: { Den: > 0 } rate } ? (double)rate.Num / rate.Den : 0;
        Dictionary<AssetRef, long> durations = await (_sourceDurations ??= new SourceDurationResolver(db, storage, logger))
            .ResolveAsync(
                projectId,
                TimelineAssetReferences.MediaSources(document).OfType<AssetRef>().Distinct().ToList(),
                fileRows.Where(r => r.ProjectId == projectId && !unservedFiles.Contains(r.Id)).ToDictionary(r => r.Id, r => r.StorageKey),
                resultRows.Where(r => r.ProjectId == projectId && r.OutputStorageKey != null && !unservedResults.Contains(r.Id))
                    .ToDictionary(r => r.Id, r => r.OutputStorageKey!),
                fps,
                ct);

        // Segmentation masks: the same-project VideoAnalyze results (and their g{n} tracks) the masks name.
        Dictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>> segmentationFacts = [];
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> segmentationTracks = await SegmentationTrackIdLookup.ResolveAsync(
            document,
            projectId,
            async (ids, token) => (await db.WorkflowStepResults.AsNoTracking()
                .Where(r => ids.Contains(r.Id) && !unservedResults.Contains(r.Id)
                    && r.WorkflowExecution.ProjectId == projectId
                    && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .Select(r => new { r.Id, r.ArtifactStorageKey })
                .ToListAsync(token)).Select(r => (r.Id, r.ArtifactStorageKey)).ToList(),
            async (key, token) =>
            {
                await using Stream stream = await storage.DownloadAsync(projectId, key, token);
                return await SegmentationTrackIdLookup.ParseAsync(stream, token);
            },
            ct,
            segmentationFacts);

        return new EditTimelineValidationContext(
            ExpectedProjectId: projectId,
            AssetDurationFrames: durations,
            ProjectFileOwners: projectFileOwners,
            StepResultOwners: stepResultOwners,
            FramesPerSecond: fps > 0 ? fps : null,
            SegmentationTrackIds: segmentationTracks,
            SegmentationTrackFacts: segmentationFacts);
    }
}
