using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Storage;

public sealed record ProjectWorkspaceFile(
    Guid Id,
    Guid ProjectId,
    string OriginalFileName,
    string? OriginalPath,
    string Category,
    string StorageKey,
    string MimeType,
    long SizeBytes,
    DateTime UploadedAt,
    string? AgentSummary,
    string? DirectoryPath = null);

public interface IProjectFileWorkspace
{
    Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct);
    Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct);
    Task<ProjectWorkspaceFile> WriteTextFileAsync(
        Guid projectId,
        string fileName,
        string content,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles",
        string? originalPath = null);

    /// <summary>
    /// Streams the S3 object at <paramref name="storageKey"/> straight to a local file at
    /// <paramref name="destinationPath"/> — never buffered through a <c>MemoryStream</c>/
    /// <c>byte[]</c> (R21), so this is safe for large video/audio sources. <paramref name="storageKey"/>
    /// must already lie under <c>projects/{projectId}/</c> (see <c>ValidateProjectScope</c>).
    /// </summary>
    Task DownloadStorageKeyToFileAsync(
        Guid projectId, string storageKey, string destinationPath, CancellationToken ct);

    /// <summary>
    /// The size-bounded twin of
    /// <see cref="DownloadStorageKeyToFileAsync(Guid,string,string,CancellationToken)"/>: the same streamed
    /// download, but the object is refused when its own declared length already exceeds
    /// <paramref name="maxBytes"/> — before a single byte of the body is read — and the copy stops the moment
    /// the stream passes the bound anyway (a length that lies). Either way it throws
    /// <see cref="StorageObjectTooLargeException"/> and leaves no claim that the file is whole.
    ///
    /// <para>
    /// This is the only correct way to bring an object into memory under a cap: the caller cannot check the
    /// size after a download without having already paid for it in temp space and bandwidth.
    /// </para>
    /// </summary>
    Task DownloadStorageKeyToFileAsync(
        Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct);

    /// <summary>
    /// Uploads a local binary file to S3 via a multipart <c>TransferUtility</c> (streamed, never a
    /// whole-file <c>byte[]</c> — R21) <b>and</b> creates a corresponding <see cref="ProjectWorkspaceFile"/>
    /// row, for artifacts meant to be reachable/re-editable as ordinary project files (e.g. the
    /// final edited video). The caller must set <paramref name="summaryStatus"/>/
    /// <paramref name="indexingStatus"/> so binary media is never queued for text summarization or
    /// vector indexing (plan §1.5 — mirrors the R22 fix, independently, for this upload path).
    /// </summary>
    Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        SummaryStatus summaryStatus,
        FileIndexingStatus indexingStatus,
        CancellationToken ct,
        string category = "outputFiles",
        string? originalPath = null);

    /// <summary>
    /// Creates a <see cref="ProjectWorkspaceFile"/> row for an object that is <b>already in the
    /// object store</b>, without uploading anything.
    /// <para>
    /// <b>Why this exists next to <see cref="UploadBinaryFileAsync"/>.</b> That method takes a LOCAL
    /// file path, which is exactly what a step publishing through <c>IMediaWorkspace.PublishAsync</c>
    /// does not have: the media-session publish is what put the bytes in the bucket, and it may have
    /// done so from a runner whose disk this process cannot read. Re-uploading the same bytes to get a
    /// row would be a second copy of the same object under a second key, and on a runner-backed step
    /// it would not work at all. This is the registration half of that method with the transport
    /// removed: the caller states the key it already published, and gets back the row that makes the
    /// file visible in the project.
    /// </para>
    /// <para>
    /// <paramref name="storageKey"/> must already lie under <c>projects/{projectId}/</c> (see
    /// <c>ValidateProjectScope</c>). The row's size is read from the object store with a HEAD, and
    /// <paramref name="sizeBytes"/> is only the caller's expectation (logged when it differs). The
    /// caller's figure may come from a remote runner's own stat, and storage usage sums the row's size,
    /// so it is never trusted. A HEAD that fails throws: no row is registered with an unknown size.
    /// </para>
    /// </summary>
    Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
        Guid projectId,
        string storageKey,
        string fileName,
        string contentType,
        long sizeBytes,
        SummaryStatus summaryStatus,
        FileIndexingStatus indexingStatus,
        CancellationToken ct,
        string category = "outputFiles");

    /// <summary>
    /// Uploads a local file to S3 via a multipart <c>TransferUtility</c> (streamed) as a bare
    /// artifact — returns only the storage key, creates <b>no</b> <see cref="ProjectWorkspaceFile"/>
    /// row. For the large analysis/EDL JSON artifacts (plan §1.5) that are referenced only via
    /// <c>WorkflowStepResult.ArtifactStorageKey</c>, never surfaced in the project files UI.
    /// </summary>
    Task<string> UploadArtifactAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles");

    /// <summary>
    /// Answers "is the bare artifact at <paramref name="storageKey"/> already in the object store?"
    /// with a HEAD (metadata) request — never by downloading the object. <paramref name="storageKey"/>
    /// must lie under <c>projects/{projectId}/</c> (see <c>ValidateProjectScope</c>).
    ///
    /// <para>
    /// This is what lets a content-addressed producer treat the OBJECT STORE as its cache instead of
    /// holding a process-wide in-memory dictionary: because the key is derived from the content being
    /// produced, a hit means "a previous run uploaded exactly these bytes", and no decision can leak
    /// across executions the way a singleton's table could. A missing object is <c>false</c>; every
    /// other failure (auth, network, throttling) propagates, because "could not tell" is not the same
    /// answer as "not there" and a caller that silently re-synthesized on a transient S3 error would
    /// pay for work it already had.
    /// </para>
    /// </summary>
    Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct);

    /// <summary>
    /// The version of the stored object at <paramref name="storageKey"/> as the store reports it (its ETag, from
    /// one HEAD), or null when the object is absent or the store gives none. Two reads that answer the same
    /// value saw the same bytes. <paramref name="storageKey"/> must lie under <c>projects/{projectId}/</c>.
    /// The default answers null, so an implementation that cannot tell simply has no version.
    /// </summary>
    Task<string?> GetObjectVersionAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Deletes one bare artifact. <paramref name="storageKey"/> must lie under <c>projects/{projectId}/agentFiles/voiceover/</c> (no <c>..</c>, backslash or encoded segments).
    /// For a producer that must not leave a refunded attempt's content-addressed objects behind as a free
    /// cache for a later run; it deletes ONLY keys the attempt itself created. Default: does nothing, so a
    /// workspace that cannot delete keeps working.
    /// </summary>
    Task DeleteArtifactAsync(Guid projectId, string storageKey, CancellationToken ct) => Task.CompletedTask;
}
