using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Storage;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// The object-store half of output verification (D12), over the same two S3 clients the rest of the
/// engine uses: D2's shared factory registers the default (in-cluster endpoint) client and a keyed
/// "presign" client built on <c>MinIO:PublicEndpoint</c>, and promotion goes through
/// <see cref="IPresignedUrlService.PromoteAsync"/>, which is where the size cap is enforced and where
/// the staging object is deleted.
/// <para>
/// Presigning here rather than reusing <see cref="IPresignedUrlService.CreateGetAsync"/> is not an
/// oversight: that method deliberately signs only keys under <c>projects/{projectId}/</c> (the D2
/// security boundary), and the object being verified is a STAGING object under
/// <c>runner-staging/{jobId}/{n}</c>. The key is not a caller-supplied path — it is derived from the
/// job id and index by <see cref="StagingKeyFor"/> — so this is the same presigning rule applied to a
/// different, equally narrow key space, and a caller can no more point it somewhere else than it can
/// point <c>CreateGetAsync</c> outside a project.
/// </para>
/// </summary>
public sealed class S3RunnerOutputStore : IRunnerOutputStore
{
    /// <summary>
    /// The staging key a job's nth object lands on. Derived here and nowhere else, and kept identical
    /// to what <see cref="IPresignedUrlService.CreatePutAsync"/> signs, so a PUT the engine authorized
    /// is the same object this reads back.
    /// </summary>
    public static string StagingKeyFor(Guid jobId, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return PresignedUrlService.StagingPrefix + jobId.ToString("D") + "/" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A presigned GET of a staging object is consumed within one verification; the cap is D2's own GET cap.</summary>
    public static readonly TimeSpan MaxStagingGetTtl = TimeSpan.FromMinutes(15);

    private readonly IAmazonS3 _s3;
    private readonly IAmazonS3 _presignS3;
    private readonly IPresignedUrlService _presignedUrls;
    private readonly string _bucket;
    private readonly Protocol _protocol;
    private readonly ILogger<S3RunnerOutputStore> _logger;

    public S3RunnerOutputStore(
        IAmazonS3 s3,
        [FromKeyedServices(S3ClientFactory.PresignServiceKey)] IAmazonS3 presignS3,
        IPresignedUrlService presignedUrls,
        IConfiguration configuration,
        ILogger<S3RunnerOutputStore> logger)
    {
        _s3 = s3;
        _presignS3 = presignS3;
        _presignedUrls = presignedUrls;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _protocol = S3ClientFactory.ResolvePublicEndpoint(configuration)
            .StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTPS
            : Protocol.HTTP;
        _logger = logger;
    }

    public async Task<long?> TryGetSizeAsync(string key, CancellationToken ct)
    {
        try
        {
            GetObjectMetadataResponse head = await _s3.GetObjectMetadataAsync(_bucket, key, ct).ConfigureAwait(false);
            return head.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The ordinary "the runner never uploaded anything" case, not an error.
            return null;
        }
    }

    public async Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct)
    {
        RequireStagingKey(stagingKey);
        if (ttl <= TimeSpan.Zero || ttl > MaxStagingGetTtl)
            throw new ArgumentOutOfRangeException(nameof(ttl), "The staging GET TTL must be in (0, " + MaxStagingGetTtl + "].");

        return await _presignS3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = stagingKey,
            Verb = HttpVerb.GET,
            Protocol = _protocol,
            Expires = DateTime.UtcNow.Add(ttl)
        }).ConfigureAwait(false);
    }

    public async Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct)
    {
        RequireStagingKey(stagingKey);
        // Delegated, not reimplemented: PromoteAsync is D2's HEAD-cap-copy-delete and the single place
        // that knows a cap cannot be signed into a presigned PUT.
        return await _presignedUrls.PromoteAsync(stagingKey, finalKey, maxBytes, ct).ConfigureAwait(false);
    }

    public async Task DiscardAsync(string key, CancellationToken ct)
    {
        RequireStagingKey(key);
        await _s3.DeleteObjectAsync(_bucket, key, ct).ConfigureAwait(false);
    }

    public async Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct)
    {
        if (maxBytes > 0 && sizeBytes > maxBytes)
            return null;

        using GetObjectResponse response = await _s3.GetObjectAsync(_bucket, key, ct).ConfigureAwait(false);
        await using Stream stream = response.ResponseStream;
        byte[] digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    public async Task<byte[]?> TryDownloadAsync(string key, string destinationPath, long maxBytes, CancellationToken ct)
    {
        RequireStagingKey(key);
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(_bucket, key, ct).ConfigureAwait(false);
            if (maxBytes > 0 && response.ContentLength > maxBytes)
                return null;

            byte[] head = new byte[16];
            int headLength = 0;
            await using Stream source = response.ResponseStream;
            await using FileStream target = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (maxBytes > 0 && total > maxBytes)
                    return null;
                if (headLength < head.Length)
                {
                    int take = Math.Min(read, head.Length - headLength);
                    Array.Copy(buffer, 0, head, headLength, take);
                    headLength += take;
                }
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            return head[..headLength];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not download staging object '{Key}' for local verification", key);
            return null;
        }
    }

    private static void RequireStagingKey(string key)
    {
        if (string.IsNullOrEmpty(key) ||
            !key.StartsWith(PresignedUrlService.StagingPrefix, StringComparison.Ordinal) ||
            key.Length == PresignedUrlService.StagingPrefix.Length ||
            key.Contains('\\') ||
            key.Split('/').Any(s => s is ".." or "." or ""))
        {
            throw new ArgumentException(
                "Key must be a normalized object key under '" + PresignedUrlService.StagingPrefix + "'.", nameof(key));
        }
    }
}
