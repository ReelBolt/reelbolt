using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// The object-store seam of the workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library
/// (global avatars)"). The library's objects live under an ORGANIZATION prefix
/// (<see cref="AvatarLibraryStorage"/>), which the project-scoped <see cref="IFileStorageService"/> cannot address, so
/// this seam moves bytes between a project and the library and nothing else. <see cref="AvatarLibraryService"/> is its
/// only caller; no other code reads or writes the library prefix.
/// </summary>
/// <remarks>
/// Every method checks its keys before it touches the store and throws <see cref="UnauthorizedAccessException"/> for a
/// key outside its scope: a library key must be exactly an object of the library layout
/// (<see cref="AvatarLibraryStorage.Parse"/>), and a project key must sit under <c>projects/{projectId}/</c> with no
/// <c>..</c>. Copies are server-side; the source of a copy is never modified.
/// </remarks>
public interface IAvatarLibraryStore
{
    /// <summary>Copies a project object into the library (publish). The destination is a NEW library key of the caller's item.</summary>
    Task CopyIntoLibraryAsync(Guid projectId, string projectKey, string libraryKey, CancellationToken ct);

    /// <summary>
    /// Copies a library object to a project key (import). The destination must be a NEW key the caller built: it is
    /// never a key a project file already uses, so no live project file is overwritten.
    /// </summary>
    Task CopyIntoProjectAsync(string libraryKey, Guid projectId, string projectKey, CancellationToken ct);

    /// <summary>Opens a library object, or answers null when it is absent.</summary>
    Task<Stream?> OpenReadAsync(string libraryKey, CancellationToken ct);

    /// <summary>Deletes a library object; deleting an absent key is a no-op.</summary>
    Task DeleteAsync(string libraryKey, CancellationToken ct);

    /// <summary>
    /// Every library object under <paramref name="prefix"/>, which must be an organization's library prefix
    /// (<see cref="AvatarLibraryStorage.OrganizationPrefix"/>) or one item's (<see cref="AvatarLibraryStorage.ItemPrefix"/>).
    /// Keys that are not of the library layout are left out.
    /// </summary>
    Task<IReadOnlyList<StoredObjectEntry>> ListAsync(string prefix, CancellationToken ct);

    /// <summary>
    /// Every library object of EVERY organization (the storage sweep's orphan pass). Only keys of the library layout
    /// are returned; at most a bounded number per call.
    /// </summary>
    Task<IReadOnlyList<StoredObjectEntry>> ListAllAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class S3AvatarLibraryStore(IAmazonS3 s3, IConfiguration configuration) : IAvatarLibraryStore
{
    /// <summary>The most keys one listing returns: far above what the per-kind item cap and the reference limits allow.</summary>
    public const int MaxListedKeys = 20_000;

    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task CopyIntoLibraryAsync(Guid projectId, string projectKey, string libraryKey, CancellationToken ct)
    {
        RequireProjectKey(projectId, projectKey);
        RequireLibraryKey(libraryKey);
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = projectKey,
            DestinationBucket = _bucket,
            DestinationKey = libraryKey
        }, ct);
    }

    public async Task CopyIntoProjectAsync(string libraryKey, Guid projectId, string projectKey, CancellationToken ct)
    {
        RequireLibraryKey(libraryKey);
        RequireProjectKey(projectId, projectKey);
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = libraryKey,
            DestinationBucket = _bucket,
            DestinationKey = projectKey
        }, ct);
    }

    public async Task<Stream?> OpenReadAsync(string libraryKey, CancellationToken ct)
    {
        RequireLibraryKey(libraryKey);
        try
        {
            GetObjectResponse response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = libraryKey }, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string libraryKey, CancellationToken ct)
    {
        RequireLibraryKey(libraryKey);
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = libraryKey }, ct);
    }

    public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(string prefix, CancellationToken ct)
    {
        if (!IsLibraryPrefix(prefix)) throw new UnauthorizedAccessException("The prefix is not an avatar library prefix.");
        return ListUnderAsync(prefix, ct);
    }

    // The organizations/ root holds nothing but the libraries today; whatever else may live there later is left out
    // by the layout check below.
    public Task<IReadOnlyList<StoredObjectEntry>> ListAllAsync(CancellationToken ct) => ListUnderAsync("organizations/", ct);

    private async Task<IReadOnlyList<StoredObjectEntry>> ListUnderAsync(string prefix, CancellationToken ct)
    {
        List<StoredObjectEntry> entries = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);

            foreach (S3Object obj in page.S3Objects ?? [])
            {
                if (AvatarLibraryStorage.Parse(obj.Key) is null) continue;
                if (entries.Count >= MaxListedKeys) return entries;
                entries.Add(new StoredObjectEntry(obj.Key, obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow));
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);

        return entries;
    }

    /// <summary>Whether <paramref name="prefix"/> is an organization's library prefix or one item's prefix, and nothing wider.</summary>
    public static bool IsLibraryPrefix(string? prefix)
    {
        // A probe object name turns the prefix into a key the one layout check can judge.
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith('/')) return false;
        if (AvatarLibraryStorage.Parse(prefix + "x") is not null) return true;
        return AvatarLibraryStorage.Parse(prefix + Guid.Empty.ToString("D") + "/x") is not null;
    }

    private static void RequireLibraryKey(string key)
    {
        if (AvatarLibraryStorage.Parse(key) is null)
            throw new UnauthorizedAccessException("The key is not an avatar library object.");
    }

    private static void RequireProjectKey(Guid projectId, string key)
    {
        if (string.IsNullOrEmpty(key)
            || !key.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)
            || key.Contains("..", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The key is not an object of the project.");
    }
}
