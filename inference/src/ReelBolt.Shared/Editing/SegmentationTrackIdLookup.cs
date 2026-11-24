using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// The ONE place a validation context's <see cref="EditTimelineValidationContext.SegmentationTrackIds"/> is built,
/// shared by the API (PATCH, import, restore, proposals) and the engine (render re-validation). The callers
/// supply only the two storage-specific steps as delegates, so the rules cannot diverge:
/// <list type="bullet">
/// <item>only step results of THIS project whose step type is VideoAnalyze are loaded (the caller's query);</item>
/// <item>the artifact key must lie under <c>projects/{projectId}/agentFiles/video-analysis/</c>;</item>
/// <item>at most <see cref="MaxStepResults"/> distinct results are read per document;</item>
/// <item>anything foreign, missing, unreadable or track-less is simply absent (fail closed): the validator then
/// reports its single "does not name a video analysis of this project" violation, with no existence oracle.</item>
/// </list>
/// </summary>
public static class SegmentationTrackIdLookup
{
    /// <summary>Most distinct VideoAnalyze results one document's masks may name.</summary>
    public const int MaxStepResults = 32;

    /// <summary>Largest artifact read, in bytes, by callers that bound the download.</summary>
    public const long MaxArtifactBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record CachedTracks(IReadOnlyDictionary<string, SegmentationTrackFacts> Tracks, DateTime ExpiresAt);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Project, Guid Id, string Key), CachedTracks> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCachedArtifacts = 256;

    public static string ArtifactPrefix(Guid projectId) => $"projects/{projectId}/agentFiles/video-analysis/";

    /// <param name="loadArtifactKeys">
    /// Given the masked ids, returns (id, ArtifactStorageKey) for those that are VideoAnalyze results of the
    /// document's own project. MUST filter by project and step type in the query.
    /// </param>
    /// <param name="readArtifact">Opens the artifact at a key already scope-checked by this class.</param>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlySet<string>>> ResolveAsync(
        EditTimeline document,
        Guid projectId,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<(Guid Id, string? ArtifactKey)>>> loadArtifactKeys,
        Func<string, CancellationToken, Task<VideoAnalysisArtifact?>> readArtifact,
        CancellationToken ct,
        IDictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>>? facts = null)
    {
        Dictionary<Guid, IReadOnlySet<string>> result = [];
        IReadOnlyList<Guid> wanted = TimelineAssetReferences.SegmentationMaskStepResultIds(document);
        if (wanted.Count == 0)
        {
            return result;
        }

        string prefix = ArtifactPrefix(projectId);
        foreach ((Guid id, string? key) in await loadArtifactKeys(wanted.Take(MaxStepResults).ToList(), ct))
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(prefix, StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                // Artifacts are immutable per key and autosave PATCHes constantly: a short in-memory cache by
                // (project, result, key) keeps a save from re-downloading up to 32 of them.
                (string Project, Guid Id, string Key) cacheKey = (projectId.ToString("N"), id, key);
                if (!Cache.TryGetValue(cacheKey, out CachedTracks? cached) || cached.ExpiresAt < DateTime.UtcNow)
                {
                    VideoAnalysisArtifact? artifact = await readArtifact(key, ct);
                    IReadOnlyDictionary<string, SegmentationTrackFacts> byId = SegmentationTrackFacts.FromArtifact(artifact);
                    cached = new CachedTracks(byId, DateTime.UtcNow + CacheTtl);
                    if (Cache.Count >= MaxCachedArtifacts)
                    {
                        Cache.Clear();
                    }

                    Cache[cacheKey] = cached;
                }

                if (cached.Tracks.Count > 0)
                {
                    result[id] = cached.Tracks.Keys.ToHashSet(StringComparer.Ordinal);
                    if (facts is not null)
                    {
                        facts[id] = cached.Tracks;
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unreadable artifact: the id stays absent, so the mask is refused (fail closed).
            }
        }

        return result;
    }

    /// <summary>Parses a video-analysis artifact JSON stream with the options the writers use.</summary>
    /// <remarks>Reads at most <see cref="MaxArtifactBytes"/>; a larger artifact throws, which the lookup treats as absent (fail closed).</remarks>
    public static async Task<VideoAnalysisArtifact?> ParseAsync(Stream stream, CancellationToken ct)
    {
        using MemoryStream bounded = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + read > MaxArtifactBytes)
            {
                throw new InvalidDataException("The analysis artifact is larger than the size this lookup reads.");
            }

            bounded.Write(buffer, 0, read);
        }

        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<VideoAnalysisArtifact>(bounded, ArtifactJsonOptions, ct);
    }
}
