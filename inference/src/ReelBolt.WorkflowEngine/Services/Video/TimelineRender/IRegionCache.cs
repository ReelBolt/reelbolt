using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The cross-execution cache of rendered region mezzanines, keyed by
/// <see cref="RegionRecipeHasher"/>'s hash. A hit skips ffmpeg for that region entirely.
/// </summary>
public interface IRegionCache
{
    /// <summary>Copies the cached mezzanine for <paramref name="recipeHash"/> to <paramref name="destinationPath"/>, or returns false.</summary>
    Task<bool> TryFetchAsync(Guid projectId, string recipeHash, string destinationPath, CancellationToken ct);

    /// <summary>Stores a freshly rendered mezzanine under <paramref name="recipeHash"/>. Best-effort: a failure is logged by the caller and never fails the render.</summary>
    Task StoreAsync(Guid projectId, string recipeHash, string localPath, CancellationToken ct);
}

/// <summary>No cache: every region renders. Used by the golden-parity harness (whose P1 leg requires byte-identical argv across two runs) and by tests.</summary>
public sealed class NullRegionCache : IRegionCache
{
    public static readonly NullRegionCache Instance = new();

    public Task<bool> TryFetchAsync(Guid projectId, string recipeHash, string destinationPath, CancellationToken ct) => Task.FromResult(false);

    public Task StoreAsync(Guid projectId, string recipeHash, string localPath, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The production cache: mezzanines live in the project's object store under
/// <c>projects/{projectId}/agentFiles/timeline-render-cache/{hash}.mp4</c>, so a hit is a HEAD plus a
/// download and the artifact is scoped to the project it was rendered from (the hash covers asset
/// fingerprints, so a same-recipe object in another project would be identical bytes anyway, but a
/// per-project prefix is what the ownership checks everywhere else assume).
/// </summary>
public sealed class ObjectStoreRegionCache : IRegionCache
{
    public const string CacheDirectory = "timeline-render-cache";

    private readonly IProjectFileWorkspace _workspace;

    public ObjectStoreRegionCache(IProjectFileWorkspace workspace) => _workspace = workspace;

    public static string StorageKeyFor(Guid projectId, string recipeHash) =>
        $"projects/{projectId:D}/agentFiles/{CacheDirectory}/{recipeHash}.mp4";

    public async Task<bool> TryFetchAsync(Guid projectId, string recipeHash, string destinationPath, CancellationToken ct)
    {
        string key = StorageKeyFor(projectId, recipeHash);
        if (!await _workspace.ArtifactExistsAsync(projectId, key, ct).ConfigureAwait(false))
            return false;

        await _workspace.DownloadStorageKeyToFileAsync(projectId, key, destinationPath, ct).ConfigureAwait(false);
        return true;
    }

    public Task StoreAsync(Guid projectId, string recipeHash, string localPath, CancellationToken ct) =>
        _workspace.UploadArtifactAsync(projectId, localPath, $"{CacheDirectory}/{recipeHash}.mp4", "video/mp4", ct);
}
