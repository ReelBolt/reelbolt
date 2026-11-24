using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// T-102: loads the chain of compiles behind a seed step result (its manifest, each chained compile's manifest and
/// config, the VideoAnalyze tracks its region effects name) and hands it to <see cref="LineageSeeder"/>. Everything
/// is read project-scoped; anything unreadable degrades the affected levels to the per-span fallback, never throws.
/// </summary>
public static class LineageSeedResolver
{
    private const int MaxDepth = 8;

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    public static async Task<SeededTimeline?> TrySeedAsync(
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger logger,
        Guid projectId,
        WorkflowStepResult seed,
        TimelineManifest seedManifest,
        Guid newTimelineId,
        SeedAssetResolver assets,
        CancellationToken ct)
    {
        try
        {
            List<LineageLevel> levels = [];
            HashSet<Guid> visited = [];
            await LoadLevelAsync(db, storage, logger, projectId, seed, seedManifest, assets, levels, visited, 0, ct);

            List<string> keys = levels.SelectMany(l => (l.Manifest.Sources ?? []).Select(s => s.StorageKey))
                .Concat(levels.Select(l => l.Manifest.OutputVideo.StorageKey)).OfType<string>().Distinct().ToList();
            Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, projectId, keys, ct);
            // The analysis fallback is only for SOURCES that never got a stored probe (as in the plain seeding); a compile's
            // own output has no analysis facts, so a missing output probe must not trigger an artifact read.
            List<string> sourceKeys = levels.SelectMany(l => (l.Manifest.Sources ?? []).Select(s => s.StorageKey)).OfType<string>().Distinct().ToList();
            Dictionary<string, bool> analysed = sourceKeys.All(probes.ContainsKey)
                ? []
                : await AnalysisAudioFacts.LoadAsync(db, storage, logger, projectId, seed.WorkflowExecutionId, ct);
            bool HasAudio(string key) => probes.TryGetValue(key, out MediaSourceProbe? p)
                ? p.HasAudio
                : analysed.TryGetValue(key, out bool a) && a;

            return LineageSeeder.TrySeed(levels, newTimelineId, assets.ResolveIdentity, HasAudio, assets.ResolveKey,
                key => probes.TryGetValue(key, out MediaSourceProbe? p) ? p.AltersPixelsOnDecode : null,
                StoredMediaProbes.InputProfileResolver(probes, assets.ResolveKey));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The lineage is an improvement over the plain seeding, never a reason to refuse it.
            logger.LogWarning(ex, "Lineage seeding of step result {StepResultId} failed; using the plain manifest seeding", seed.Id);
            return null;
        }
    }

    private static async Task LoadLevelAsync(
        InferenceApiDbContext db, IFileStorageService storage, ILogger logger, Guid projectId, WorkflowStepResult result,
        TimelineManifest manifest, SeedAssetResolver assets, List<LineageLevel> levels, HashSet<Guid> visited, int depth, CancellationToken ct)
    {
        if (depth >= MaxDepth || !visited.Add(result.Id))
        {
            return;
        }

        // Name every source by identity so the seeder never guesses: a source the manifest left unnamed is looked up by key.
        TimelineManifest normalized = manifest with
        {
            Sources = (manifest.Sources ?? []).Select(s =>
            {
                if (s.ProjectFileId is not null || s.StepResultId is not null)
                {
                    return s;
                }

                AssetRef? r = assets.ResolveKey(s.StorageKey);
                return r is null ? s : s with { ProjectFileId = r.ProjectFileId, StepResultId = r.StepResultId };
            }).ToList()
        };

        VideoCompileStepConfig? config = null;
        WorkflowStep? step = await db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(s => s.Id == result.WorkflowStepId, ct);
        if (step is { StepType: StepType.VideoCompile, VideoCompileConfigJson: { Length: > 0 } json })
        {
            try { config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, ConfigJson); }
            catch (JsonException) { config = null; }
        }

        Guid? analysisId = null;
        List<LineageSegmentationTrack> tracks = [];
        if (config is { } c && (c.RegionEffects is { Count: > 0 } || c.CensorLabels is { Count: > 0 }))
        {
            WorkflowStepResult? analysis = c.AnalysisStepResultId is { } explicitId
                ? await db.WorkflowStepResults.AsNoTracking().FirstOrDefaultAsync(r => r.Id == explicitId && r.WorkflowExecution.ProjectId == projectId && r.WorkflowStep.StepType == StepType.VideoAnalyze, ct)
                : await db.WorkflowStepResults.AsNoTracking()
                    .Where(r => r.WorkflowExecutionId == result.WorkflowExecutionId && r.WorkflowStep.StepOrder == c.AnalysisStepOrder
                                && r.WorkflowStep.StepType == StepType.VideoAnalyze && r.ArtifactStorageKey != null)
                    .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt).FirstOrDefaultAsync(ct);
            if (analysis is not null && await Outputs.OutputGate.IsResultHeldAsync(db, analysis.Id, ct)) analysis = null;
            string prefix = SegmentationTrackIdLookup.ArtifactPrefix(projectId);
            if (analysis?.ArtifactStorageKey is { } key && key.StartsWith(prefix, StringComparison.Ordinal) && !key.Contains("..", StringComparison.Ordinal))
            {
                try
                {
                    await using Stream stream = await storage.DownloadAsync(projectId, key, ct);
                    VideoAnalysisArtifact? artifact = await SegmentationTrackIdLookup.ParseAsync(stream, ct);
                    IReadOnlyDictionary<string, SegmentationTrackFacts> facts = SegmentationTrackFacts.FromArtifact(artifact);
                    tracks = (artifact?.SegmentationTracks ?? []).Select(t => new LineageSegmentationTrack(t.Id, t.SourceIndex, t.Prompt, facts.GetValueOrDefault(t.Id))).ToList();
                    analysisId = analysis.Id;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not read the analysis artifact {Key} for lineage seeding", key);
                }
            }
        }

        levels.Add(new LineageLevel(result.Id, normalized, config, analysisId, tracks));

        foreach (Guid inputId in normalized.Sources!.Select(s => s.StepResultId).OfType<Guid>().Distinct())
        {
            WorkflowStepResult? input = await db.WorkflowStepResults.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == inputId && r.WorkflowExecution.ProjectId == projectId, ct);
            if (input is null || string.IsNullOrWhiteSpace(input.TimelineStorageKey)
                || await Outputs.OutputGate.IsResultHeldAsync(db, input.Id, ct))
            {
                continue;
            }

            try
            {
                await using Stream stream = await storage.DownloadAsync(projectId, input.TimelineStorageKey, ct);
                TimelineManifest? inner = await JsonSerializer.DeserializeAsync<TimelineManifest>(stream, ManifestJson, ct);
                if (inner is not null)
                {
                    await LoadLevelAsync(db, storage, logger, projectId, input, inner, assets, levels, visited, depth + 1, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not read the chained manifest of step result {StepResultId} for lineage seeding", inputId);
            }
        }
    }
}
