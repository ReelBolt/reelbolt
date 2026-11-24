using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Compositions;

/// <summary>
/// What part of a film a revision is about. Every member is optional and they compose: an empty
/// (or null) target means "the whole film, wherever the instruction points". Designed for more than
/// today's front-end: the execution page sends <see cref="Scenes"/> (chips from the scene manifest);
/// the editor-v2 live front-end will send a <see cref="Region"/> (a selection on its timeline) or
/// <see cref="FrameRanges"/> (a selection on the composition itself). The runner turns all three into
/// one plain list of frame windows the agent is told to confine its edits to.
/// </summary>
public sealed record CompositionRevisionTargets
{
    /// <summary>Scene names as the version's scene manifest names them. Unknown names are reported to the agent, never fatal.</summary>
    [JsonPropertyName("scenes")]
    public IReadOnlyList<string>? Scenes { get; init; }

    /// <summary>Frame windows of a composition (inclusive start, exclusive end).</summary>
    [JsonPropertyName("frameRanges")]
    public IReadOnlyList<CompositionFrameRange>? FrameRanges { get; init; }

    /// <summary>A region of the rendered film in seconds — the shape a timeline selection naturally has.</summary>
    [JsonPropertyName("region")]
    public CompositionTimeRegion? Region { get; init; }

    /// <summary>The most scenes one request may name (a manifest is capped at 200 scenes; a request names a handful).</summary>
    public const int MaxScenes = 50;

    /// <summary>The most frame ranges one request may carry.</summary>
    public const int MaxFrameRanges = 20;

    /// <summary>The longest scene name accepted.</summary>
    public const int MaxSceneNameChars = 200;

    /// <summary>True when nothing is targeted, i.e. the request is about the whole film.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        (Scenes is null || Scenes.Count == 0)
        && (FrameRanges is null || FrameRanges.Count == 0)
        && Region is null;

    /// <summary>
    /// The validation the API applies before anything is written. Returns every problem, so a client
    /// fixes them in one pass; an empty list means valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (Scenes is { } scenes)
        {
            if (scenes.Count > MaxScenes)
                errors.Add($"targets.scenes may name at most {MaxScenes} scenes.");
            if (scenes.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxSceneNameChars))
                errors.Add($"targets.scenes entries must be non-empty names of at most {MaxSceneNameChars} characters.");
        }

        if (FrameRanges is { } ranges)
        {
            if (ranges.Count > MaxFrameRanges)
                errors.Add($"targets.frameRanges may carry at most {MaxFrameRanges} ranges.");
            if (ranges.Any(r => r.FromFrame < 0 || r.ToFrame <= r.FromFrame))
                errors.Add("targets.frameRanges entries need 0 <= fromFrame < toFrame.");
        }

        if (Region is { } region
            && (!double.IsFinite(region.FromSeconds) || !double.IsFinite(region.ToSeconds)
                || region.FromSeconds < 0 || region.ToSeconds <= region.FromSeconds))
        {
            errors.Add("targets.region needs 0 <= fromSeconds < toSeconds.");
        }

        return errors;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The persisted form (<c>composition_revisions.targets_json</c>); null for an empty target.</summary>
    public static string? Serialize(CompositionRevisionTargets? targets) =>
        targets is null || targets.IsEmpty ? null : JsonSerializer.Serialize(targets, Json);

    /// <summary>Tolerant read: a malformed or absent value is "no target", never an exception.</summary>
    public static CompositionRevisionTargets? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            CompositionRevisionTargets? parsed = JsonSerializer.Deserialize<CompositionRevisionTargets>(json, Json);
            return parsed is null || parsed.IsEmpty ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A frame window of a composition: <c>[FromFrame, ToFrame)</c>. <see cref="CompositionId"/> null = the version's own composition.</summary>
public sealed record CompositionFrameRange(
    [property: JsonPropertyName("fromFrame")] int FromFrame,
    [property: JsonPropertyName("toFrame")] int ToFrame,
    [property: JsonPropertyName("compositionId")] string? CompositionId = null);

/// <summary>A region of the rendered film in seconds: <c>[FromSeconds, ToSeconds)</c>.</summary>
public sealed record CompositionTimeRegion(
    [property: JsonPropertyName("fromSeconds")] double FromSeconds,
    [property: JsonPropertyName("toSeconds")] double ToSeconds);

/// <summary>
/// The object-store layout of composition workspaces, in one place so the engine that writes an
/// archive and the API that might one day serve or purge it can never disagree.
/// <c>agentFiles</c> rather than <c>outputFiles</c> on purpose: an archive is not a playable render
/// and must never be listed as one (the same reason <c>ArtifactStorageKey</c> is its own column).
/// </summary>
public static class CompositionWorkspaceKeys
{
    /// <summary>The prefix every archive of a project lives under.</summary>
    public static string ProjectPrefix(Guid projectId) => $"projects/{projectId}/agentFiles/workspaces/";

    /// <summary>
    /// <c>projects/{projectId}/agentFiles/workspaces/{workspaceId}/{versionId}.tar.gz</c>. Keyed by the
    /// version's ID rather than its number: the object is written before the number is arbitrated, so
    /// two concurrent appends can never target (and overwrite) the same object.
    /// </summary>
    public static string ArchiveKey(Guid projectId, Guid workspaceId, Guid versionId) =>
        $"{ProjectPrefix(projectId)}{workspaceId}/{versionId}.tar.gz";

    /// <summary>True when <paramref name="key"/> is an archive of <paramref name="projectId"/> — the check a reader applies before trusting a stored key.</summary>
    public static bool BelongsTo(string? key, Guid projectId) =>
        key is not null
        && key.StartsWith(ProjectPrefix(projectId), StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal);
}
