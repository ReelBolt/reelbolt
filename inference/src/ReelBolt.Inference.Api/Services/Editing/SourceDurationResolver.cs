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
/// R01.6: the known duration, in timeline frames, of the sources a document references, so the
/// validator can refuse a clip that runs past the end of its source.
/// </summary>
/// <remarks>
/// <para>
/// The ingest probe (<c>media_derivatives.source_probe_json</c>, via <see cref="StoredMediaProbes"/>) wins;
/// a step-result source no derivative run probed falls back to the project's newest <c>VideoAnalyze</c>
/// artifact's <c>Sources[]</c>. A source neither knows is simply absent from the answer: the validator reads an
/// absent duration as "not checked", never as a violation (fail open).
/// </para>
/// <para>
/// One instance serves one request: probes and the artifact are read once and cached for the instance's
/// lifetime, so a save that validates several documents (or a document with many clips of one source)
/// never repeats the queries. Frames are at the timeline's rate, rounded UP so the check can only ever
/// prove an overrun.
/// </para>
/// </remarks>
public sealed class SourceDurationResolver(InferenceApiDbContext db, IFileStorageService storage, ILogger logger)
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, double?> _secondsByKey = new(StringComparer.Ordinal);
    private Dictionary<string, double>? _artifactSeconds;

    /// <summary>Durations in frames at <paramref name="framesPerSecond"/>, for the refs whose duration is known.</summary>
    public async Task<Dictionary<AssetRef, long>> ResolveAsync(
        Guid projectId,
        IReadOnlyCollection<AssetRef> references,
        IReadOnlyDictionary<Guid, string> fileKeys,
        IReadOnlyDictionary<Guid, string> stepResultKeys,
        double framesPerSecond,
        CancellationToken ct)
    {
        Dictionary<AssetRef, long> result = [];
        if (references.Count == 0 || !double.IsFinite(framesPerSecond) || framesPerSecond <= 0) return result;

        Dictionary<AssetRef, string> keyByRef = [];
        HashSet<string> artifactEligible = new(StringComparer.Ordinal);
        foreach (AssetRef reference in references)
        {
            string? key = reference.ProjectFileId is { } fileId && fileKeys.TryGetValue(fileId, out string? fk) ? fk
                : reference.StepResultId is { } resultId && stepResultKeys.TryGetValue(resultId, out string? sk) ? sk
                : null;
            if (string.IsNullOrEmpty(key)) continue;

            keyByRef[reference] = key;
            // A project file's duration is its ingest probe's; the analysis artifact is the fallback for
            // step results only, so a save that names only uploaded files never reads an artifact.
            if (reference.StepResultId is not null) artifactEligible.Add(key);
        }

        List<string> unread = keyByRef.Values.Where(k => !_secondsByKey.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList();
        if (unread.Count > 0)
        {
            Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, projectId, unread, ct);
            foreach (string key in unread)
            {
                _secondsByKey[key] = probes.TryGetValue(key, out MediaSourceProbe? probe) && probe.DurationSec > 0
                    ? probe.DurationSec
                    : null;
            }

            List<string> unprobed = unread.Where(k => _secondsByKey[k] is null && artifactEligible.Contains(k)).ToList();
            if (unprobed.Count > 0)
            {
                _artifactSeconds ??= await LoadArtifactSecondsAsync(projectId, ct);
                foreach (string key in unprobed)
                {
                    if (_artifactSeconds.TryGetValue(key, out double seconds)) _secondsByKey[key] = seconds;
                }
            }
        }

        foreach ((AssetRef reference, string key) in keyByRef)
        {
            if (_secondsByKey.TryGetValue(key, out double? seconds) && seconds is { } known)
                result[reference] = (long)Math.Ceiling(known * framesPerSecond);
        }

        return result;
    }

    private async Task<Dictionary<string, double>> LoadArtifactSecondsAsync(Guid projectId, CancellationToken ct)
    {
        Dictionary<string, double> byKey = new(StringComparer.Ordinal);
        string artifactPrefix = $"projects/{projectId}/agentFiles/video-analysis/";

        string? artifactKey = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId &&
                        r.WorkflowStep.StepType == StepType.VideoAnalyze &&
                        r.ArtifactStorageKey != null &&
                        r.ArtifactStorageKey.StartsWith(artifactPrefix))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
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
                if (!string.IsNullOrEmpty(source.StorageKey) && source.Media is { DurationSec: > 0 } media)
                    byKey.TryAdd(source.StorageKey, media.DurationSec);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A hint only: without it the check is skipped for those sources (fail open).
            logger.LogWarning(ex, "Could not read the video-analysis artifact for project {ProjectId}; source durations unchecked", projectId);
        }

        return byKey;
    }
}
