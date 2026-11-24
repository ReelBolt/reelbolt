using Amazon.S3;
using Amazon.S3.Model;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>A server-side copy of one object to another key inside the same project (no bytes through the API).</summary>
public interface IStorageObjectCopier
{
    /// <summary>Copies <paramref name="sourceKey"/> to <paramref name="destinationKey"/>; both must sit under <c>projects/{projectId}/</c> or it throws without touching storage.</summary>
    Task CopyAsync(Guid projectId, string sourceKey, string destinationKey, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// A single S3 <c>CopyObject</c> (renders are far below its 5 GB ceiling). Unlike
/// <c>IPresignedUrlService.PromoteAsync</c> the source is kept: a saved copy and the render it came from
/// are independent objects, so deleting either never breaks the other.
/// </remarks>
public sealed class S3StorageObjectCopier(IAmazonS3 s3, IConfiguration configuration) : IStorageObjectCopier
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task CopyAsync(Guid projectId, string sourceKey, string destinationKey, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/";
        if (string.IsNullOrEmpty(sourceKey) || !sourceKey.StartsWith(prefix, StringComparison.Ordinal) || sourceKey.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Copy source is outside the project's storage prefix.");
        if (string.IsNullOrEmpty(destinationKey) || !destinationKey.StartsWith(prefix, StringComparison.Ordinal) || destinationKey.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Copy destination is outside the project's storage prefix.");

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey,
        }, ct);
    }
}
