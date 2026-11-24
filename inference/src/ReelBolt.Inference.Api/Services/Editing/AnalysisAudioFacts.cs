using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// Whether a source has sound, according to a <c>VideoAnalyze</c> artifact's <c>Sources[]</c>: the
/// timeline seeder's fallback for a source no derivative run has probed yet (QA C-323-1).
/// </summary>
/// <remarks>
/// <para>
/// The artifact read is the newest <c>VideoAnalyze</c> step's one of the seed's own execution (the
/// analysis that compile step cut from), else the project's newest, the same one the media pool
/// falls back to (P2-B1). Only a <c>VideoAnalyze</c> step's artifact counts: a compile step stores
/// its EDL under the same <c>video-analysis/</c> prefix, later and with no <c>Sources</c> (T-107). The
/// reading rule is the media pool's: a named audio codec is sound, a named video codec with no
/// audio codec is a silent file, and an entry that names neither predates codec probing and says
/// nothing, so it is left out of the answer.
/// </para>
/// <para>
/// A missing or unreadable artifact is a warning and an empty answer, never a failed seed: the
/// artifact is a hint, and without it the seed is what it was before the fallback existed.
/// </para>
/// </remarks>
public static class AnalysisAudioFacts
{
    /// <summary>Must match the writer's (<c>VideoAnalyzeStepExecutor.ArtifactJsonOptions</c>).</summary>
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<Dictionary<string, bool>> LoadAsync(
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger logger,
        Guid projectId,
        Guid executionId,
        CancellationToken ct)
    {
        Dictionary<string, bool> byKey = new(StringComparer.Ordinal);
        string artifactPrefix = $"projects/{projectId}/agentFiles/video-analysis/";

        string? artifactKey = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                        r.WorkflowStep.StepType == StepType.VideoAnalyze &&
                        r.ArtifactStorageKey != null &&
                        r.ArtifactStorageKey.StartsWith(artifactPrefix))
            .OrderByDescending(r => r.WorkflowExecutionId == executionId)
            .ThenByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => r.ArtifactStorageKey)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(artifactKey)) return byKey;
        if (await Outputs.OutputGate.IsKeyHeldAsync(db, projectId, artifactKey, ct)) return byKey;

        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, artifactKey, ct);
            VideoAnalysisArtifact? artifact =
                await JsonSerializer.DeserializeAsync<VideoAnalysisArtifact>(stream, ArtifactJsonOptions, ct);

            foreach (VideoAnalysisSourceInfo source in artifact?.Sources ?? [])
            {
                if (string.IsNullOrEmpty(source.StorageKey) || source.Media is null) continue;
                if (HasAudio(source.Media) is { } hasAudio)
                    byKey.TryAdd(source.StorageKey, hasAudio);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Could not read the video-analysis artifact for project {ProjectId}; " +
                "seeding without its audio facts",
                projectId);
        }

        return byKey;
    }

    /// <summary>True for a named audio codec, false for a video codec alone, null when neither was probed.</summary>
    public static bool? HasAudio(VideoAnalysisMedia media) =>
        !string.IsNullOrWhiteSpace(media.AudioCodec)
            ? true
            : !string.IsNullOrWhiteSpace(media.VideoCodec) ? false : null;
}
