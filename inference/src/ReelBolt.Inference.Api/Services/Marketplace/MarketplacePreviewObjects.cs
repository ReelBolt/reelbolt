using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>The platform object store as the preview checks need it. Only keys under <c>marketplace/</c> are ever touched.</summary>
public interface IMarketplacePreviewObjects
{
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Deletes keys, skipping quarantine keys and any key in <paramref name="exclude"/> (held evidence).</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct, IReadOnlySet<string>? exclude = null);

    /// <summary>Copies an object; the source is kept.</summary>
    Task CopyAsync(string fromKey, string toKey, CancellationToken ct);

    /// <summary>Copies then deletes; the source is removed only after the copy succeeded.</summary>
    Task MoveAsync(string fromKey, string toKey, CancellationToken ct);

    /// <summary>Deletes everything under <paramref name="prefix"/> except evidence under <c>quarantine/</c>.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct, IReadOnlySet<string>? exclude = null);
}

public sealed class S3MarketplacePreviewObjects(IAmazonS3 s3, IConfiguration configuration) : IMarketplacePreviewObjects
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    private static void Require(string key)
    {
        if (!key.StartsWith(MarketplacePreviewKeys.Root, StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Only platform marketplace keys are handled here.");
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        Require(key);
        GetObjectResponse response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        return new ResponseOwningStream(response);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct, IReadOnlySet<string>? exclude = null)
    {
        foreach (string key in keys)
        {
            Require(key);

            // Evidence is never deleted.
            if (MarketplacePreviewKeys.IsQuarantine(key) || exclude?.Contains(key) == true)
                continue;
            await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
        }
    }

    public async Task CopyAsync(string fromKey, string toKey, CancellationToken ct)
    {
        Require(fromKey);
        Require(toKey);
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = fromKey,
            DestinationBucket = _bucket,
            DestinationKey = toKey
        }, ct);
    }

    public async Task MoveAsync(string fromKey, string toKey, CancellationToken ct)
    {
        Require(fromKey);
        Require(toKey);
        if (MarketplacePreviewKeys.IsQuarantine(fromKey))
            throw new InvalidOperationException("Quarantined evidence is never moved or deleted.");
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = fromKey,
            DestinationBucket = _bucket,
            DestinationKey = toKey
        }, ct);
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = fromKey }, ct);
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct, IReadOnlySet<string>? exclude = null)
    {
        Require(prefix);
        string? token = null;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, ct);
            string[] keys = [.. (page.S3Objects ?? []).Select(o => o.Key).Where(k => !MarketplacePreviewKeys.IsQuarantine(k))];
            if (keys.Length > 0)
                await DeleteAsync(keys, ct, exclude);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
