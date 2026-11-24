namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// Abstraction for file storage operations (MinIO/S3).
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// The bucket this deployment stores project objects in. Recorded on every project-file row
    /// (and used as the read fallback in <c>ProjectFileMediaController</c>), so a caller that
    /// records a file without going through <see cref="UploadAsync"/> still stores the truth.
    /// </summary>
    string BucketName { get; }

    /// <summary>
    /// Upload a file into project storage.
    /// </summary>
    /// <param name="projectId">Target project.</param>
    /// <param name="content">File stream.</param>
    /// <param name="fileName">Original file name (basename).</param>
    /// <param name="contentType">MIME type.</param>
    /// <param name="metadata">Additional metadata to attach.</param>
    /// <param name="category">
    /// Which subfolder under projects/{id} to place the object. Supported values:
    /// "userFiles" (default), "agentFiles", "outputFiles".
    /// </param>
    /// <param name="originalPath">
    /// Optional original relative path (including any folders) supplied by the client.
    /// Stored in metadata for UI rendering.
    /// </param>
    Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null);

    Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct);
    Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct);

    /// <summary>
    /// HEAD one object under <c>projects/{projectId}/</c> and report what is ACTUALLY stored — its
    /// byte size and the content type the store recorded. Returns <c>null</c> when the object does
    /// not exist (never throws for that). Fails closed like the other members: a key outside the
    /// project's prefix throws rather than reporting on a foreign object.
    /// <para>
    /// This is how a direct-to-storage upload is verified: the client's claimed size and content
    /// type are hints, and the bytes that arrived are the fact (a presigned PUT cannot carry a
    /// maximum length, so the size is only knowable here).
    /// </para>
    /// </summary>
    Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct);

    /// <summary>
    /// Deletes a set of objects that all live under <c>projects/{projectId}/</c> in one
    /// S3 multi-object delete per batch of up to 1,000 keys. Fails closed like
    /// <see cref="DeleteAsync"/>: any key outside the project's prefix is rejected rather than
    /// deleted. Returns the number of objects actually deleted.
    /// </summary>
    Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct);

    Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct);

    /// <summary>
    /// Moves the object only if it still has the ETag that was read (a conditional server-side copy).
    /// Returns false, and moves nothing, when the object changed. The default says "not verified" without
    /// moving, so a store that cannot condition the copy never vouches for the bytes.
    /// </summary>
    Task<bool> MoveIfMatchAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct) =>
        Task.FromResult(false);

    /// <summary>
    /// Server-side copy of one object to a new key, both under <c>projects/{projectId}/</c>; the source is
    /// left in place. Fails closed like <see cref="MoveAsync"/> (a key outside the project's prefix
    /// throws), and a missing source throws the store's own not-found error. Used by a picture's
    /// copy-on-delete (decision D10): the photo a picture uses is copied into the picture's own prefix
    /// before the project file is deleted.
    /// </summary>
    Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct);

    /// <summary>
    /// Reads the object's ETag — half of a media derivative's cache identity
    /// (<c>(SourceStorageKey, SourceETag, ParamsHash)</c>) — with a best-effort object-store HEAD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returns <c>null</c> on ANY failure, and deliberately never throws.</b> Where
    /// <see cref="DownloadAsync"/>/<see cref="DeleteAsync"/>/<see cref="MoveAsync"/> throw for a key
    /// outside the project's own scope, a throwing variant here would make an upload FAIL because of
    /// a metadata read: the file is already stored, and re-uploading is the user's only recourse.
    /// So an out-of-scope key, a missing object, a permissions error and a transport failure all
    /// answer the same <c>null</c>, and the caller publishes an event with an empty ETag — which the
    /// consumer must read as "no content identity available", never as a poison message.
    /// </para>
    /// <para>
    /// A <see cref="System.OperationCanceledException"/> from a cancelled <paramref name="ct"/> is
    /// the one thing that is not swallowed: a cancelled request must not look like a successful
    /// read of nothing.
    /// </para>
    /// </remarks>
    /// <returns>The object's ETag, or <c>null</c> when it could not be read.</returns>
    Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct);
}

/// <summary>What the object store reports for one object: its real size, the content type it
/// recorded (which the uploader set from the value the presigned URL was signed with), and the
/// ETag that identifies the stored bytes when the store provides one, and the time the store last wrote
/// them (UTC; null when the store does not say).</summary>
public sealed record StoredObjectMetadata(long SizeBytes, string? ContentType, string? ETag = null, DateTime? LastModifiedUtc = null);

/// <summary>One object under a project prefix: its key and the store's last-modified time.</summary>
public sealed record StoredObjectEntry(string StorageKey, DateTime LastModifiedUtc);

/// <summary>
/// Read-only listing of a project's objects by category, for the retention sweep's orphan pass: it is
/// how an object that no row and no mark names is found and given a durable mark, so it is eventually
/// purged (SQCR-S-09). It is a separate interface from <see cref="IFileStorageService"/> because not
/// every store can list — a double or a minimal implementation simply does not register it, and the
/// sweep skips that pass rather than failing. An implementation must never return a key outside
/// <c>projects/{projectId}/</c>.
/// </summary>
public interface IObjectListing
{
    /// <summary>The objects under <c>projects/{projectId}/{category}/</c>, oldest first is not required.</summary>
    Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct);
}

public sealed record StoredFileObject(
    string StorageKey,
    string BucketName,
    string StoragePrefix,
    string? StorageMetadataJson,
    string Category,
    string OriginalFileName,
    string OriginalPath,
    string? DirectoryPath,
    string StorageFileName);
