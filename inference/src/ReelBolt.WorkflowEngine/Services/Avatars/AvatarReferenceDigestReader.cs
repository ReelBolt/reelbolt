using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>
/// Hashes a reference file's bytes as they are NOW, so the resolver can tell an unchanged reference from one whose
/// object was replaced since attestation (docs/avatars.md "The engine resolver").
/// </summary>
public interface IAvatarReferenceDigestReader
{
    /// <summary>
    /// The lower-case hex SHA-256 of the object at <paramref name="storageKey"/>, or null when the object is gone, the key
    /// is outside <c>projects/{projectId}/</c>, or the object cannot be read (fail closed: the rule reads that as missing).
    /// Cancellation propagates.
    /// </summary>
    Task<string?> ReadSha256Async(Guid projectId, string storageKey, CancellationToken ct);
}

/// <summary>Streams the object from the store and hashes it; never buffers the whole object.</summary>
public sealed class S3AvatarReferenceDigestReader : IAvatarReferenceDigestReader
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger<S3AvatarReferenceDigestReader> _logger;

    public S3AvatarReferenceDigestReader(IAmazonS3 s3, IConfiguration configuration, ILogger<S3AvatarReferenceDigestReader> logger)
    {
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    public async Task<string?> ReadSha256Async(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(storageKey) || !storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            return null;

        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(_bucket, storageKey, ct).ConfigureAwait(false);
            await using Stream body = response.ResponseStream;
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(body, ct).ConfigureAwait(false));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: an object we cannot read is not a reference we may send.
            _logger.LogWarning(ex, "Could not read an avatar reference object of project {ProjectId}", projectId);
            return null;
        }
    }
}
