using System.Globalization;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.History;

/// <summary>
/// The storage-key shapes project versioning relies on (contract section 3): the live keys the
/// copy-on-change decorator protects, and the content-addressed blob key a protected copy lives
/// under. Kept in one place so the decorator, the eraser and the GC cannot disagree on a prefix.
/// </summary>
public static class ProjectHistoryStorageKeys
{
    /// <summary>The category folders whose live objects are protected before an overwrite/delete/move.</summary>
    public static readonly string[] ProtectedCategories = ["userFiles", "agentFiles"];

    /// <summary>The folder protected copies live under, inside the project prefix.</summary>
    public const string HistoryFolder = "history";

    /// <summary>True for a live project-file key under <c>projects/{p}/userFiles/</c> or <c>.../agentFiles/</c>.</summary>
    public static bool IsProtectedLiveKey(Guid projectId, string? storageKey)
    {
        if (string.IsNullOrEmpty(storageKey))
            return false;

        string projectPrefix = $"projects/{projectId.ToString("D", CultureInfo.InvariantCulture)}/";
        if (!storageKey.StartsWith(projectPrefix, StringComparison.Ordinal))
            return false;

        foreach (string category in ProtectedCategories)
        {
            if (storageKey.StartsWith(projectPrefix + category + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>projects/{p}/history/blobs/{hex}</c> where <c>hex</c> is the SHA-256 hex of the ContentId
    /// string (contract section 2). Content-addressed, so one object serves every version that
    /// captured those bytes.
    /// </summary>
    public static string BlobKey(Guid projectId, string contentId) =>
        $"projects/{projectId.ToString("D", CultureInfo.InvariantCulture)}/{HistoryFolder}/blobs/{CanonicalJson.Sha256Hex(contentId)}";

    /// <summary>The project prefix history objects live under.</summary>
    public static string HistoryPrefix(Guid projectId) =>
        $"projects/{projectId.ToString("D", CultureInfo.InvariantCulture)}/{HistoryFolder}/";
}
