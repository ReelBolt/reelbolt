using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared;
using System.Net;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// S3-compatible file storage service targeting MinIO.
/// </summary>
public class S3FileStorageService : IFileStorageService, IObjectListing
{
    /// <summary>Maximum keys per S3 multi-object delete (the hard API limit).</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>A defensive ceiling on one listing call, so a runaway prefix cannot exhaust memory.</summary>
    private const int MaxListedKeys = 100_000;

    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3FileStorageService> _logger;

    public S3FileStorageService(IAmazonS3 s3Client, IConfiguration configuration, ILogger<S3FileStorageService> logger)
    {
        _s3Client = s3Client;
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    /// <inheritdoc />
    public string BucketName => _bucketName;

    /// <summary>
    /// The deterministic target of one upload. Extracted so the copy-on-change decorator can know the
    /// destination key BEFORE the bytes are written (it must protect the old bytes under that key
    /// first) without duplicating the path normalization rules.
    /// </summary>
    internal readonly record struct UploadTarget(
        string StoragePrefix,
        string StorageKey,
        string NormalizedOriginalPath,
        string NormalizedFileName,
        string? DirectoryPath,
        string StorageFileName);

    /// <summary>Unknown categories become <c>userFiles</c>, so a key can never escape the project prefix.</summary>
    internal static string NormalizeCategory(string? category) =>
        category is "userFiles" or "agentFiles" or "outputFiles" ? category : "userFiles";

    internal static UploadTarget BuildUploadTarget(
        Guid projectId,
        Guid fileId,
        string fileName,
        string category,
        string? originalPath)
    {
        string normalizedOriginalPath = ProjectFilePath.NormalizeRelativePath(
            string.IsNullOrWhiteSpace(originalPath) ? fileName : originalPath);
        string normalizedFileName = ProjectFilePath.GetFileName(normalizedOriginalPath);
        string? directoryPath = ProjectFilePath.GetDirectoryPath(normalizedOriginalPath);
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, normalizedFileName);

        string storagePrefix = $"projects/{projectId}/{category}/";
        string storageKey = string.IsNullOrWhiteSpace(directoryPath)
            ? $"{storagePrefix}{storageFileName}"
            : $"{storagePrefix}{directoryPath}/{storageFileName}";

        return new UploadTarget(
            storagePrefix,
            storageKey,
            normalizedOriginalPath,
            normalizedFileName,
            directoryPath,
            storageFileName);
    }

    public async Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null)
    {
        // Ensure category is one of the known values to prevent escaping the prefix
        category = NormalizeCategory(category);

        UploadTarget target = BuildUploadTarget(projectId, fileId, fileName, category, originalPath);
        string normalizedOriginalPath = target.NormalizedOriginalPath;
        string normalizedFileName = target.NormalizedFileName;
        string? directoryPath = target.DirectoryPath;
        string storageFileName = target.StorageFileName;
        string storagePrefix = target.StoragePrefix;
        string storageKey = target.StorageKey;
        PutObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = storageKey,
            InputStream = content,
            ContentType = contentType
        };
        // standard metadata
        request.Metadata["project-id"] = projectId.ToString();
        request.Metadata["category"] = category;
        request.Metadata["original-path"] = normalizedOriginalPath;
        request.Metadata["original-file-name"] = normalizedFileName;
        request.Metadata["storage-file-name"] = storageFileName;

        if (metadata != null)
        {
            foreach (var (key, value) in metadata)
            {
                request.Metadata[key] = value;
            }
        }

        await _s3Client.PutObjectAsync(request, ct);
        _logger.LogInformation("Uploaded file {FileName} as {StorageKey}", fileName.ForLog(), storageKey.ForLog());
        string metadataJson = JsonSerializer.Serialize(request.Metadata.Keys
            .ToDictionary(k => k, k => request.Metadata[k]));
        return new StoredFileObject(
            storageKey,
            _bucketName,
            storagePrefix,
            metadataJson,
            category,
            normalizedFileName,
            normalizedOriginalPath,
            directoryPath,
            storageFileName);
    }

    public async Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");

        try
        {
            GetObjectMetadataResponse head = await _s3Client.GetObjectMetadataAsync(_bucketName, storageKey, ct);
            return new StoredObjectMetadata(head.ContentLength, head.Headers?.ContentType, head.ETag, head.LastModified?.ToUniversalTime());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Absent is a legitimate answer, not an error: a browser upload whose PUT never
            // happened leaves no object, and finalisation must say so rather than 500.
            return null;
        }
    }

    public async Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");

        GetObjectRequest request = new() { BucketName = _bucketName, Key = storageKey };
        GetObjectResponse response = await _s3Client.GetObjectAsync(request, ct);
        return response.ResponseStream;
    }

    public async Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");

        DeleteObjectRequest request = new() { BucketName = _bucketName, Key = storageKey };
        await _s3Client.DeleteObjectAsync(request, ct);
        await ImagePreviewStore.DeleteAllAsync(_s3Client, _bucketName, projectId, storageKey, ct);
        _logger.LogInformation("Deleted file {StorageKey}", storageKey.ForLog());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct)
    {
        string projectPrefix = $"projects/{projectId}/";
        string prefix = $"{projectPrefix}{category}/";
        List<StoredObjectEntry> entries = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);

            foreach (S3Object obj in page.S3Objects ?? [])
            {
                // Fail closed: a key outside the project is not ours to sweep.
                if (!obj.Key.StartsWith(projectPrefix, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Listed object is not in the project's storage scope.");
                if (entries.Count >= MaxListedKeys) return entries;
                entries.Add(new StoredObjectEntry(obj.Key, obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow));
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);

        return entries;
    }

    public async Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        if (storageKeys.Count == 0)
            return 0;

        // Same fail-closed scope check as DeleteAsync, applied up front so a stray cross-project key
        // aborts the whole batch instead of deleting a foreign object.
        string prefix = $"projects/{projectId}/";
        foreach (string key in storageKeys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Requested object is not in the project's storage scope.");
        }

        int deleted = 0;
        foreach (string[] batch in storageKeys.Chunk(DeleteBatchSize))
        {
            // Quiet: S3 then reports only failures, so the successes are the batch minus DeleteErrors.
            DeleteObjectsRequest request = new() { BucketName = _bucketName, Quiet = true };
            foreach (string key in batch)
                request.AddKey(key);

            DeleteObjectsResponse response = await _s3Client.DeleteObjectsAsync(request, ct);
            int failed = response.DeleteErrors?.Count ?? 0;
            deleted += batch.Length - failed;
            if (failed > 0)
            {
                _logger.LogWarning(
                    "Batch delete of {Count} object(s) under {Prefix} reported {Errors} error(s)",
                    batch.Length, prefix, failed);
            }
        }

        await ImagePreviewStore.DeleteAllAsync(_s3Client, _bucketName, projectId, storageKeys, ct);
        return deleted;
    }

    /// <inheritdoc />
    public async Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (!sourceStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested source file is not in the project's storage scope.");
        if (!destinationStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested destination file is not in the project's storage scope.");

        await _s3Client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucketName,
            SourceKey = sourceStorageKey,
            DestinationBucket = _bucketName,
            DestinationKey = destinationStorageKey
        }, ct);
    }

    public async Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (!sourceStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested source file is not in the project's storage scope.");
        if (!destinationStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested destination file is not in the project's storage scope.");

        if (string.Equals(sourceStorageKey, destinationStorageKey, StringComparison.Ordinal))
            return;

        CopyObjectRequest copyRequest = new()
        {
            SourceBucket = _bucketName,
            SourceKey = sourceStorageKey,
            DestinationBucket = _bucketName,
            DestinationKey = destinationStorageKey
        };

        await _s3Client.CopyObjectAsync(copyRequest, ct);
        await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = sourceStorageKey
        }, ct);
        await ImagePreviewStore.DeleteAllAsync(_s3Client, _bucketName, projectId, sourceStorageKey, ct);

        _logger.LogInformation(
            "Moved file object from {SourceStorageKey} to {DestinationStorageKey}",
            sourceStorageKey.ForLog(),
            destinationStorageKey.ForLog());
    }

    /// <inheritdoc />
    public async Task<bool> MoveIfMatchAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct)
    {
        if (!sourceStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)
            || !destinationStorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested key is not in the project's storage scope.");
        if (string.Equals(sourceStorageKey, destinationStorageKey, StringComparison.Ordinal)) return true;

        try
        {
            await _s3Client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucketName,
                SourceKey = sourceStorageKey,
                DestinationBucket = _bucketName,
                DestinationKey = destinationStorageKey,
                ETagToMatch = "\"" + expectedETag.Trim('"') + "\""
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        await _s3Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucketName, Key = sourceStorageKey }, ct);
        await ImagePreviewStore.DeleteAllAsync(_s3Client, _bucketName, projectId, sourceStorageKey, ct);
        return true;
    }

    /// <summary>
    /// A best-effort object-store HEAD whose only contract is "an ETag or null" — see
    /// <see cref="IFileStorageService.GetObjectETagAsync"/>, which is where the reasoning for the
    /// null-on-any-failure behaviour (rather than the sibling methods' throwing one) is written
    /// down.
    /// </summary>
    public async Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // Checked before touching the store, exactly as Download/Delete/Move do — but a foreign key
        // answers null rather than throwing, because this method's caller is allowed to proceed
        // without a value.
        if (string.IsNullOrWhiteSpace(storageKey) ||
            !storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            GetObjectMetadataResponse metadata = await _s3Client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucketName, Key = storageKey },
                ct);

            // A store that reports no ETag (some S3-compatible implementations omit it) is the same
            // "no content identity available" answer as a failed HEAD, never an empty-but-present
            // identity that a cache key would happily fold in.
            return string.IsNullOrWhiteSpace(metadata.ETag) ? null : metadata.ETag;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not read the ETag for {StorageKey}; continuing without a content identity",
                storageKey.ForLog());

            return null;
        }
    }
}
