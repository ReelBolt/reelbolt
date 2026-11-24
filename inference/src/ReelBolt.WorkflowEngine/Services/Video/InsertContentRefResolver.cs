using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// T-106: resolves an <see cref="InsertContentRef"/> of kind <c>Step</c> or <c>StepResult</c> to the stored object
/// it names, as a <see cref="ProjectWorkspaceFile"/> the insert loop treats like a project file. Fails closed: a
/// malformed reference, a step this run did not produce, a step result of another project (or a pruned or
/// out-of-prefix one) and a key outside this project all answer null, and the caller reports the single
/// <c>content_not_found</c> — a foreign id cannot be told apart from one that never existed.
/// </summary>
internal static class InsertContentRefResolver
{
    /// <summary>
    /// What a reference resolved to: the file, or null with <see cref="StepResultSourceResolver.NotPlayableCode"/> when
    /// the project's own step result cannot be used (failed, unfinished, not a video or image), or null with no code
    /// when it is simply not found (malformed, foreign, missing, pruned, out of the project's addressable outputs).
    /// </summary>
    public sealed record Resolution(ProjectWorkspaceFile? File, string? FailureCode);

    public static async Task<ProjectWorkspaceFile?> ResolveAsync(
        InsertContentRef reference,
        Guid projectId,
        IReadOnlyList<StepOutputHistoryEntry> history,
        IServiceScopeFactory? scopes,
        CancellationToken ct) =>
        (await ResolveDetailedAsync(reference, projectId, history, scopes, ct)).File;

    public static async Task<Resolution> ResolveDetailedAsync(
        InsertContentRef reference,
        Guid projectId,
        IReadOnlyList<StepOutputHistoryEntry> history,
        IServiceScopeFactory? scopes,
        CancellationToken ct)
    {
        if (reference.Validate() is not null)
            return new(null, null);

        string? key;
        switch (reference.Kind)
        {
            case InsertContentRef.StepKind:
                // The freshest entry for that order (a review loop's re-run supersedes the earlier one).
                key = history.LastOrDefault(h => h.StepOrder == reference.StepOrder)?.OutputStorageKey;
                if (!InProject(key, projectId))
                    return new(null, null);
                break;
            case InsertContentRef.StepResultKind:
                // Insert content takes stills as well as clips, so images are allowed.
                StepResultSourceResolution resolved = await StepResultSourceResolver.ResolveAsync(
                    scopes, projectId, new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: reference.StepResultId), allowImages: true, ct);
                if (resolved.StorageKey is null)
                    return new(null, resolved.Code == StepResultSourceResolver.NotPlayableCode ? resolved.Code : null);
                key = resolved.StorageKey;
                break;
            default:
                return new(null, null); // ProjectFile is resolved by the caller's project file list
        }

        if (key is null)
            return new(null, null);

        string fileName = Path.GetFileName(key);
        string category = key.StartsWith(ProjectOutputKeys.GeneratedPrefix(projectId), StringComparison.Ordinal) ? "agentFiles" : "outputFiles";
        return new(new ProjectWorkspaceFile(
            Guid.Empty, projectId, fileName, null, category, key, MimeTypeFor(fileName), 0, DateTime.UtcNow, null), null);
    }

    private static bool InProject(string? key, Guid projectId) =>
        !string.IsNullOrWhiteSpace(key)
        && key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal);

    /// <summary>video/* or image/* for the extensions a render or still has; anything else is not insertable.</summary>
    internal static string MimeTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
