using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Graphics;

/// <summary>
/// Copy-on-delete for pictures (decision D10, editor remediation W0.8 T4): a picture (image-editor document)
/// keeps working when a project photo it uses is deleted or moved.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Source</c> or <c>Image</c> layer points at the user's own project file by its storage key. Before that
/// file is deleted, every picture with a layer on the key gets ONE private copy of the object under
/// <c>projects/{p}/agentFiles/image-editor/{documentId}/source-{guid}{ext}</c>, and EVERY layer of that picture
/// using the key is repointed to the copy. The copy's size is recorded on the layers
/// (<see cref="EditImageLayer.CopySizeBytes"/>) so it counts toward the workspace's storage
/// (<c>StorageUsageService</c>, hence the plan quota). The document's and the layers' links to the file are
/// cleared (the database FK is <c>SET NULL</c> too), and only then may the caller delete the original.
/// </para>
/// <para>
/// One request makes at most one copy per (picture, deleted key), so a photo used by N pictures costs N copies.
/// A delete the USER asks for (the two Files delete handlers and the assistant's delete-file tool) therefore
/// runs <see cref="CheckCopiesFitAsync"/> first and is refused with 409 <see cref="OverQuotaCode"/>, before
/// anything is copied or deleted, when the NET growth (the copies minus the deleted files' own bytes) does not
/// fit the plan's storage. The check, the copies' row writes and the deleted rows are one critical section under
/// <see cref="ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock"/>, whose cross-replica guarantee is
/// a PostgreSQL advisory lock. A photo used by one picture never grows storage, so a full workspace can always
/// delete it. A BACKGROUND removal (local/cloud/GitHub sync, an output take's delete or prune) cannot be
/// refused, so it copies regardless and the copies are counted afterwards.
/// </para>
/// <para>
/// Every path that deletes project-file rows of a project that keeps living calls
/// <see cref="CopyBeforeDeleteAsync"/> first: the two Files delete handlers, the assistant's delete-file tool,
/// the three sync removals (local folder, cloud drive, GitHub) and the take delete/prune of a workflow output
/// (an output image can be a picture's photo). Deleting a whole project or account takes its pictures with it,
/// so it needs no copy; a voice's reference recordings and a superseded compile are audio and video, never a
/// picture's layer; and the plan-retention sweep deliberately does not copy (a copy would defeat the plan's
/// retention), so a picture over an expired output keeps its layers and shows a placeholder.
/// </para>
/// </remarks>
public static class PictureSourceCopies
{
    /// <summary>
    /// How many live pictures of the project have at least one live layer showing <paramref name="storageKey"/>:
    /// the number that would each get their own copy if the file were deleted now.
    /// </summary>
    public static Task<int> CountPicturesUsingAsync(
        InferenceApiDbContext db, Guid projectId, string storageKey, CancellationToken ct) =>
        db.EditImageLayers
            .Where(l => l.StorageKey == storageKey && l.Kind != EditImageLayerKind.Text)
            .Join(db.EditImageDocuments.Where(d => d.ProjectId == projectId), l => l.DocumentId, d => d.Id, (_, d) => d.Id)
            .Distinct()
            .CountAsync(ct);

    /// <summary>The machine-readable code of a user delete refused because its pictures' copies do not fit.</summary>
    public const string OverQuotaCode = "PICTURE_COPIES_OVER_QUOTA";

    /// <summary>
    /// Preflight for a USER-initiated delete of <paramref name="files"/>: would the copies
    /// <see cref="CopyBeforeDeleteAsync"/> is about to make fit the organization's storage quota? Measures one
    /// copy per (picture, key) at the file's recorded size, subtracts the deleted files' own bytes, and asks
    /// <see cref="IPlanGateService.CheckUploadAsync"/> about the net growth. Null (go ahead) when nothing grows,
    /// when no gate is wired, or when billing is off / the plan is unlimited (the gate passes everything).
    /// Reads only; copies nothing.
    /// </summary>
    public static async Task<PictureCopiesOverQuota?> CheckCopiesFitAsync(
        InferenceApiDbContext db,
        IPlanGateService? planGate,
        Guid organizationId,
        Guid projectId,
        IReadOnlyCollection<ProjectFile> files,
        bool folder,
        CancellationToken ct)
    {
        if (planGate is null || files.Count == 0) return null;

        Dictionary<string, long> sizeByKey = files
            .Where(f => !string.IsNullOrEmpty(f.StorageKey))
            .GroupBy(f => f.StorageKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.Ordinal);
        if (sizeByKey.Count == 0) return null;
        List<string> keys = [.. sizeByKey.Keys];

        var copies = await db.EditImageLayers
            .Where(l => keys.Contains(l.StorageKey)
                        && l.Kind != EditImageLayerKind.Text
                        && db.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
            .Select(l => new { l.DocumentId, l.StorageKey })
            .Distinct()
            .ToListAsync(ct);
        if (copies.Count == 0) return null;

        long copyBytes = copies.Sum(c => sizeByKey[c.StorageKey]);
        long freedBytes = files.DistinctBy(f => f.Id).Sum(ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize);
        long growth = copyBytes - freedBytes;
        if (growth <= 0) return null;

        return await planGate.CheckUploadAsync(organizationId, growth, ct) is null
            ? null
            : new PictureCopiesOverQuota(copies.Select(c => c.DocumentId).Distinct().Count(), folder);
    }

    /// <summary>
    /// Gives every picture that uses one of <paramref name="files"/> its own copy, repoints its layers and
    /// clears its links to the files, and SAVES that before returning, so the caller can then delete the
    /// original objects and rows. Returns the number of pictures that got a copy.
    /// </summary>
    /// <remarks>
    /// An original that is already missing from the store is skipped (its layers stay as they are and are
    /// served as a 404 placeholder); any other storage failure deletes the copies made so far and rethrows,
    /// so the delete fails before anything was lost and can simply be retried.
    /// </remarks>
    public static async Task<int> CopyBeforeDeleteAsync(
        InferenceApiDbContext db,
        IFileStorageService storage,
        Guid projectId,
        IReadOnlyCollection<ProjectFile> files,
        ILogger? logger,
        CancellationToken ct,
        Func<string, CancellationToken, Task<bool>>? mayCopy = null)
    {
        if (files.Count == 0) return 0;

        Dictionary<string, ProjectFile> byKey = files
            .Where(f => !string.IsNullOrEmpty(f.StorageKey))
            .GroupBy(f => f.StorageKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<string> keys = [.. byKey.Keys];
        List<Guid> fileIds = [.. files.Select(f => f.Id)];

        List<EditImageLayer> layers = await db.EditImageLayers
            .Where(l => db.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId)
                        && (keys.Contains(l.StorageKey)
                            || (l.SourceProjectFileId != null && fileIds.Contains(l.SourceProjectFileId.Value))))
            .ToListAsync(ct);
        List<EditImageDocument> documents = await db.EditImageDocuments
            .Where(d => d.ProjectId == projectId && d.SourceProjectFileId != null && fileIds.Contains(d.SourceProjectFileId.Value))
            .ToListAsync(ct);

        if (layers.Count == 0 && documents.Count == 0) return 0;

        List<string> made = [];
        HashSet<Guid> pictures = [];
        try
        {
            IEnumerable<IGrouping<(Guid DocumentId, string StorageKey), EditImageLayer>> groups = layers
                .Where(l => l.Kind != EditImageLayerKind.Text && byKey.ContainsKey(l.StorageKey))
                .GroupBy(l => (l.DocumentId, l.StorageKey));

            foreach (IGrouping<(Guid DocumentId, string StorageKey), EditImageLayer> group in groups)
            {
                ProjectFile file = byKey[group.Key.StorageKey];
                // A copy lands outside the publish gate for good: an output without a current verdict is not
                // copied (its layers keep a placeholder once the original goes) and the delete goes on.
                if (mayCopy is not null && !await mayCopy(group.Key.StorageKey, ct)) continue;
                string copyKey = $"{EditImageDocumentSupport.EditorPrefix(projectId, group.Key.DocumentId)}source-{Guid.NewGuid()}{ExtensionOf(group.Key.StorageKey)}";
                try
                {
                    await storage.CopyAsync(projectId, group.Key.StorageKey, copyKey, ct);
                }
                catch (AmazonS3Exception ex) when (IsMissing(ex))
                {
                    logger?.LogWarning(
                        "Picture {DocumentId} references a source object that is already missing; nothing to copy",
                        group.Key.DocumentId);
                    continue;
                }

                made.Add(copyKey);
                pictures.Add(group.Key.DocumentId);
                foreach (EditImageLayer layer in group)
                {
                    layer.StorageKey = copyKey;
                    layer.StorageBucket = storage.BucketName;
                    layer.CopySizeBytes = file.SizeBytes;
                }
            }

            // The FK is SET NULL in the database as well; doing it here keeps the tracked entities (and a
            // provider without FK actions) telling the same story.
            foreach (EditImageLayer layer in layers)
            {
                if (layer.SourceProjectFileId is { } id && fileIds.Contains(id))
                    layer.SourceProjectFileId = null;
            }

            foreach (EditImageDocument document in documents)
                document.SourceProjectFileId = null;

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (made.Count > 0)
            {
                try { await storage.DeleteManyAsync(projectId, made, CancellationToken.None); }
                catch (Exception cleanup) { logger?.LogWarning(cleanup, "Could not remove {Count} picture copies after a failed delete", made.Count); }
            }

            throw;
        }

        if (pictures.Count > 0)
        {
            logger?.LogInformation(
                "Copied {FileCount} deleted project file(s) into {PictureCount} picture(s) of project {ProjectId}",
                byKey.Count, pictures.Count, projectId);
        }

        return pictures.Count;
    }

    /// <summary>
    /// Repoints every layer of the project's pictures that shows <paramref name="oldKey"/> to
    /// <paramref name="newKey"/>, after the file's object was moved (a file move or a folder rename). Does not
    /// save: the caller saves it with the file row's new key.
    /// </summary>
    public static async Task RepointMovedAsync(
        InferenceApiDbContext db, Guid projectId, string oldKey, string newKey, CancellationToken ct)
    {
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;

        List<EditImageLayer> layers = await db.EditImageLayers
            .Where(l => l.StorageKey == oldKey && db.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
            .ToListAsync(ct);
        foreach (EditImageLayer layer in layers)
            layer.StorageKey = newKey;
    }

    private static string ExtensionOf(string key)
    {
        string extension = Path.GetExtension(key).ToLowerInvariant();
        return extension.Length is > 1 and <= 6 && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : string.Empty;
    }

    private static bool IsMissing(AmazonS3Exception ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal);
}

/// <summary>
/// A user's delete refused because the private copies its pictures need (decision D10) do not fit the plan's
/// storage. Carries only a count: never an id or a storage key.
/// </summary>
/// <param name="Pictures">How many pictures would each have needed their own copy.</param>
/// <param name="Folder">Whether the delete was a folder (changes the sentence only).</param>
public sealed record PictureCopiesOverQuota(int Pictures, bool Folder)
{
    /// <summary>The plain sentence shown to the user.</summary>
    public string Message =>
        (Folder ? "Photos in this folder are used by " : "This photo is used by ")
        + (Pictures == 1 ? "1 picture. Keeping its copy" : $"{Pictures} pictures. Keeping their copies")
        + " needs more storage than your plan has left. Delete some pictures or free up space, then try again.";

    /// <summary>The 409 body of a duplicate: <c>{ code, message, pictures }</c>.</summary>
    public object ToDuplicateBody() => new
    {
        code = PictureSourceCopies.OverQuotaCode,
        message = "Duplicating this picture needs more storage than your plan has left. Delete some pictures or free up space, then try again.",
        pictures = Pictures
    };

    /// <summary>The 409 body of a delete: <c>{ code, message, pictures }</c>.</summary>
    public object ToBody() => new { code = PictureSourceCopies.OverQuotaCode, message = Message, pictures = Pictures };
}
