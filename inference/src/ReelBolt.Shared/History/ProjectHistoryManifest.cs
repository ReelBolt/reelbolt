using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.History;

/// <summary>
/// One entry of a captured project manifest. This is the in-memory shape both
/// <c>BuildLiveManifestAsync</c> and <c>CaptureAsync</c> work with; a captured version persists it
/// as a <see cref="ProjectVersionItem"/>, and the canonical JSON body (when any) as a
/// <see cref="ProjectHistoryDocument"/>.
/// </summary>
public sealed record ProjectHistoryManifestItem(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name = null,
    string? Category = null,
    string? MimeType = null,
    long SizeBytes = 0,
    string? ContentId = null,
    string? StorageKey = null,
    string? StorageETag = null,
    string? DocumentSha256 = null,
    string? DocumentJson = null,
    int? TimelineRevision = null,
    Guid? DesignSystemVersionId = null,
    DateTime? OutputProducedAt = null,
    bool PlatformMade = false);

/// <summary>The item list of a project as it is now, its digest and the file byte total.</summary>
public sealed record ProjectHistoryManifest(
    IReadOnlyList<ProjectHistoryManifestItem> Items,
    string ManifestDigest,
    long TotalBytes)
{
    /// <summary>Builds the manifest and its digest from an item list.</summary>
    public static ProjectHistoryManifest Create(IEnumerable<ProjectHistoryManifestItem> items)
    {
        List<ProjectHistoryManifestItem> list = items.ToList();
        long totalBytes = list.Where(i => i.Kind == ProjectVersionItemKind.File).Sum(i => i.SizeBytes);
        return new ProjectHistoryManifest(list, ProjectHistoryManifestDigest.Compute(list), totalBytes);
    }
}

/// <summary>
/// The version's <c>ManifestDigest</c>: SHA-256 of the canonical JSON of the item list sorted by
/// <c>(kind, key)</c>. Only identity-bearing fields participate — the kind, the key, the content
/// identity, the document hash or pinned design-system version, and the display path/name — so a
/// re-capture of unchanged items hashes the same whatever order the queries returned them in.
/// </summary>
public static class ProjectHistoryManifestDigest
{
    public static string Compute(IEnumerable<ProjectHistoryManifestItem> items)
    {
        JsonArray array = new();

        foreach (ProjectHistoryManifestItem item in items
                     .OrderBy(i => i.Kind)
                     .ThenBy(i => i.Key, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["kind"] = item.Kind.ToString(),
                ["key"] = item.Key,
                ["contentId"] = item.ContentId,
                ["documentSha256"] = item.DocumentSha256,
                ["designSystemVersionId"] = item.DesignSystemVersionId?.ToString("D"),
                ["name"] = item.Name
            });
        }

        return CanonicalJson.Sha256Hex(CanonicalJson.Serialize(array));
    }
}
