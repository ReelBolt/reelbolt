using Amazon.S3;
using Amazon.S3.Model;

namespace ReelBolt.Shared.Storage;

public sealed class PresignedUrlService : IPresignedUrlService
{
    public const string StagingPrefix = "runner-staging/";
    public static readonly TimeSpan MaxGetTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxPutTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Ceiling for a browser upload's presigned PUT. 4 hours, which is the ceiling and not the
    /// default the API asks for: a presigned PUT cannot carry a maximum length (see
    /// <see cref="PromoteAsync"/>), so the URL's lifetime is the only thing bounding how long a
    /// client may keep writing to one object. 30 minutes — the runner's cap — covers a transfer
    /// between two machines on a datacentre link, or a runner's small staged output; it does NOT
    /// cover a phone on a slow uplink pushing a 500 MB source video, where 2 Mbps is ~35 minutes of
    /// pure transfer and a stalled connection plus a browser retry can double it. 4 hours covers
    /// ~250 MB at 2 Mbps with room to spare, still expires within a working session, and keeps a
    /// leaked URL (a URL is a bearer capability) useful for less than a day. SigV4's own maximum is
    /// 7 days; this is deliberately well inside it.
    /// </summary>
    public static readonly TimeSpan MaxBrowserUploadPutTtl = TimeSpan.FromHours(4);

    private readonly IAmazonS3 _s3;
    private readonly IAmazonS3 _presignS3;
    private readonly string _bucket;
    private readonly Protocol _protocol;

    public PresignedUrlService(IAmazonS3 s3, IAmazonS3 presignS3, string bucket, string publicEndpoint)
    {
        _s3 = s3;
        _presignS3 = presignS3;
        _bucket = bucket;
        _protocol = publicEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTPS
            : Protocol.HTTP;
    }

    public async Task<string> CreateGetAsync(Guid projectId, string key, TimeSpan ttl)
    {
        RequireKeyUnder(key, $"projects/{projectId}/");
        RequireTtl(ttl, MaxGetTtl);
        return await _presignS3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _protocol,
            Expires = DateTime.UtcNow.Add(ttl)
        });
    }

    public async Task<PresignedPut> CreatePutAsync(Guid jobId, int n, string contentType, TimeSpan ttl)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        RequireTtl(ttl, MaxPutTtl);
        string key = $"{StagingPrefix}{jobId}/{n}";
        GetPreSignedUrlRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = _protocol,
            Expires = DateTime.UtcNow.Add(ttl)
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        return new PresignedPut(await _presignS3.GetPreSignedURLAsync(request), key);
    }

    /// <inheritdoc />
    public async Task<PresignedPut> CreateBrowserUploadPutAsync(Guid projectId, string key, string contentType, TimeSpan ttl)
    {
        // The project boundary is enforced here and NOT by the caller: a key outside
        // projects/{projectId}/ would be an arbitrary-write capability for another tenant.
        RequireKeyUnder(key, $"projects/{projectId}/");
        RequireTtl(ttl, MaxBrowserUploadPutTtl);
        GetPreSignedUrlRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Protocol = _protocol,
            Expires = DateTime.UtcNow.Add(ttl)
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        return new PresignedPut(await _presignS3.GetPreSignedURLAsync(request), key);
    }

    public async Task<long> PromoteAsync(string stagingKey, string finalKey, long expectedMaxBytes, CancellationToken cancellationToken = default)
    {
        RequireKeyUnder(stagingKey, StagingPrefix);
        RequireKeyUnder(finalKey, "projects/");
        if (expectedMaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(expectedMaxBytes));

        // R2 and Garage cannot sign a max length into a presigned PUT, so the cap is enforced here.
        // A cheap HEAD first, so an oversized staging object is refused without being copied.
        GetObjectMetadataResponse head = await _s3.GetObjectMetadataAsync(_bucket, stagingKey, cancellationToken);
        if (head.ContentLength > expectedMaxBytes)
        {
            await _s3.DeleteObjectAsync(_bucket, stagingKey, cancellationToken);
            throw new PresignedObjectTooLargeException(stagingKey, head.ContentLength, expectedMaxBytes);
        }
        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = stagingKey,
            DestinationBucket = _bucket,
            DestinationKey = finalKey
        }, cancellationToken);
        // upload-replay-fix: the staging URL is still alive, so a re-PUT between the HEAD above and the copy
        // could carry more bytes than were measured. The cap is therefore judged on the COPY, which no URL
        // can write: an oversized copy is deleted together with the staging object.
        long size;
        try
        {
            size = (await _s3.GetObjectMetadataAsync(_bucket, finalKey, cancellationToken)).ContentLength;
        }
        catch
        {
            // An unmeasured copy is never kept: the caller treats the throw as a failed promotion.
            try { await _s3.DeleteObjectAsync(_bucket, finalKey, CancellationToken.None); } catch { /* the orphan sweep */ }
            throw;
        }
        if (size > expectedMaxBytes)
        {
            await _s3.DeleteObjectAsync(_bucket, finalKey, cancellationToken);
            await _s3.DeleteObjectAsync(_bucket, stagingKey, cancellationToken);
            throw new PresignedObjectTooLargeException(stagingKey, size, expectedMaxBytes);
        }
        await _s3.DeleteObjectAsync(_bucket, stagingKey, cancellationToken);
        return size;
    }

    private static void RequireTtl(TimeSpan ttl, TimeSpan max)
    {
        if (ttl <= TimeSpan.Zero || ttl > max)
            throw new ArgumentOutOfRangeException(nameof(ttl), $"TTL must be in (0, {max}].");
    }

    private static void RequireKeyUnder(string key, string prefix)
    {
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
