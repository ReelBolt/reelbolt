using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// The S3 multipart calls behind a large direct upload (docs/design/local-folder-sync-and-storage.md,
/// "Uploads without a size cap"). Create, Complete and Abort are server-side calls; only the per-part
/// PUTs are presigned for the client, because the bucket's CORS policy allows GET/HEAD/PUT only.
/// Every key must sit under <c>projects/{projectId}/</c>: the project boundary is enforced here, as
/// <see cref="PresignedUrlService"/> enforces it for the single PUT, and never left to a caller.
/// </summary>
public interface IMultipartObjectStore
{
    /// <summary>Opens a multipart upload for <paramref name="key"/> and returns the storage's upload id.</summary>
    Task<string> CreateAsync(Guid projectId, string key, string contentType, CancellationToken ct);

    /// <summary>A presigned PUT for one part (1-based). <paramref name="ttl"/> is capped at <see cref="PresignedUrlService.MaxBrowserUploadPutTtl"/>.</summary>
    Task<string> PresignPartAsync(Guid projectId, string key, string uploadId, int partNumber, TimeSpan ttl);

    /// <summary>
    /// Completes the upload from the client's part list. Returns false when storage no longer knows the
    /// upload id (completed by an earlier call, or aborted) — the caller then decides from a HEAD. Throws
    /// <see cref="DirectUploadRefusedException"/> <c>UPLOAD_INCOMPLETE</c> when storage rejects the part list.
    /// </summary>
    Task<bool> CompleteAsync(Guid projectId, string key, string uploadId, IReadOnlyList<DirectUploadCompletedPart> parts, CancellationToken ct);

    /// <summary>Aborts the upload and frees its parts. Never throws for an upload that is already gone.</summary>
    Task AbortAsync(Guid projectId, string key, string uploadId, CancellationToken ct);
}

/// <summary>
/// <see cref="IMultipartObjectStore"/> over the same two clients <see cref="S3ClientFactory.AddReelBoltS3"/>
/// registers: the in-cluster client for the server-side calls, and the client signed against
/// <c>MinIO:PublicEndpoint</c> for the part URLs (SigV4 signs the host, so a URL minted against the
/// in-cluster host would be unusable from a browser or a desktop app).
/// </summary>
public sealed class S3MultipartObjectStore : IMultipartObjectStore
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonS3 _presignS3;
    private readonly string _bucket;
    private readonly Protocol _protocol;

    public S3MultipartObjectStore(IAmazonS3 s3, IAmazonS3 presignS3, string bucket, string publicEndpoint)
    {
        _s3 = s3;
        _presignS3 = presignS3;
        _bucket = bucket;
        _protocol = publicEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP;
    }

    public async Task<string> CreateAsync(Guid projectId, string key, string contentType, CancellationToken ct)
    {
        RequireProjectKey(projectId, key);
        InitiateMultipartUploadRequest request = new() { BucketName = _bucket, Key = key };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        InitiateMultipartUploadResponse response = await _s3.InitiateMultipartUploadAsync(request, ct);
        return response.UploadId;
    }

    public Task<string> PresignPartAsync(Guid projectId, string key, string uploadId, int partNumber, TimeSpan ttl)
    {
        RequireProjectKey(projectId, key);
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("An upload id is required.", nameof(uploadId));
        if (partNumber is < 1 or > DirectUploadLimits.MaxParts) throw new ArgumentOutOfRangeException(nameof(partNumber));
        if (ttl <= TimeSpan.Zero || ttl > PresignedUrlService.MaxBrowserUploadPutTtl)
            throw new ArgumentOutOfRangeException(nameof(ttl), $"TTL must be in (0, {PresignedUrlService.MaxBrowserUploadPutTtl}].");

        return _presignS3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = _protocol,
            Expires = DateTime.UtcNow.Add(ttl),
            UploadId = uploadId,
            PartNumber = partNumber
        });
    }

    public async Task<bool> CompleteAsync(Guid projectId, string key, string uploadId, IReadOnlyList<DirectUploadCompletedPart> parts, CancellationToken ct)
    {
        RequireProjectKey(projectId, key);
        CompleteMultipartUploadRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber, p.ETag)).ToList()
        };

        try
        {
            await _s3.CompleteMultipartUploadAsync(request, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            return false;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "InvalidPart" or "InvalidPartOrder" or "EntityTooSmall" or "MalformedXML")
        {
            throw new DirectUploadRefusedException(
                "UPLOAD_INCOMPLETE",
                "Storage did not accept the list of uploaded parts: a part is missing or was not uploaded completely. Upload the missing parts, or start the upload again.",
                StatusCodes.Status409Conflict);
        }
    }

    public async Task AbortAsync(Guid projectId, string key, string uploadId, CancellationToken ct)
    {
        RequireProjectKey(projectId, key);
        try
        {
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = uploadId }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload" || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already completed or aborted.
        }
    }

    /// <summary>The project boundary, exactly as <see cref="PresignedUrlService"/> enforces it.</summary>
    internal static void RequireProjectKey(Guid projectId, string key)
    {
        string prefix = $"projects/{projectId}/";
        if (string.IsNullOrEmpty(key)
            || !key.StartsWith(prefix, StringComparison.Ordinal)
            || key.Length == prefix.Length
            || key.Contains('\\')
            || key.Split('/').Any(s => s is ".." or "." or ""))
        {
            throw new ArgumentException($"Key must be a normalized object key under '{prefix}'.", nameof(key));
        }
    }
}
