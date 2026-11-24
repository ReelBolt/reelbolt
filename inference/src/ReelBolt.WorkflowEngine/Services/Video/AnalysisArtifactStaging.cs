using Microsoft.Extensions.Logging;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The analysis artifact (<c>analysis.json</c>) crosses to and from a remote media session through the
/// object store. A 404 there means the object is not where the key says: a named failure, not an
/// "Unexpected error". The storage key goes to the log only; the run page gets a plain sentence.
/// </summary>
public static class AnalysisArtifactStaging
{
    /// <summary>Failure code, classified PlatformInternal (refundable).</summary>
    public const string UnavailableCode = "ANALYSIS_ARTIFACT_UNAVAILABLE";

    /// <summary>What the customer sees when the artifact could not be read.</summary>
    public const string ReadMessage =
        "The analysis for this video could not be read. Nothing was charged; run it again.";

    /// <summary>What the customer sees when the artifact could not be stored.</summary>
    public const string WriteMessage =
        "The analysis for this video could not be saved. Nothing was charged; run it again.";

    /// <summary>
    /// Stages the artifact into the session. True on success; false when the object store answered 404
    /// (logged with the key). Any other failure propagates.
    /// </summary>
    public static async Task<bool> TryStageAsync(
        IMediaWorkspace workspace, string storageKey, string virtualPath, ILogger logger, CancellationToken ct)
    {
        try
        {
            await workspace.StageAsync(storageKey, virtualPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (RunnerSessionOpException ex) when (ex.IsObjectStoreNotFound)
        {
            logger.LogWarning(ex, "Analysis artifact '{StorageKey}' is not in the object store (stage answered 404)", storageKey);
            return false;
        }
    }
}
