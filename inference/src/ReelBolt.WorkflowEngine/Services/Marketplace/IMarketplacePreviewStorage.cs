using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Storage;

namespace ReelBolt.WorkflowEngine.Services.Marketplace;

/// <summary>The object store as the preview job needs it: one bounded read of a project object, two writes under the platform prefix.</summary>
public interface IMarketplacePreviewStorage
{
    /// <summary>Downloads <paramref name="sourceKey"/> to <paramref name="destinationPath"/>; throws <see cref="MarketplaceSourceException"/> when it is missing or over <paramref name="maxBytes"/>.</summary>
    Task DownloadSourceAsync(string sourceKey, string destinationPath, long maxBytes, CancellationToken ct);

    /// <summary>Uploads <paramref name="localPath"/> to a per-run staging key under <c>marketplace/</c>; refuses any other key, the final keys included.</summary>
    Task UploadAsync(string platformKey, string localPath, string contentType, CancellationToken ct);
}

/// <summary>A refusal of the source object with a stable <see cref="failure code"/>.</summary>
public sealed class MarketplaceSourceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class S3MarketplacePreviewStorage(IAmazonS3 s3, IConfiguration configuration) : IMarketplacePreviewStorage
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task DownloadSourceAsync(string sourceKey, string destinationPath, long maxBytes, CancellationToken ct)
    {
        if (!MarketplacePreviewKeys.IsProjectSourceKey(sourceKey))
            throw new MarketplaceSourceException(ReelBolt.Shared.IntegrationEvents.MarketplacePreviewFailureCodes.SourceInvalid);

        GetObjectResponse response;
        try
        {
            response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = sourceKey }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MarketplaceSourceException(ReelBolt.Shared.IntegrationEvents.MarketplacePreviewFailureCodes.SourceMissing);
        }

        using (response)
        {
            if (response.ContentLength > maxBytes || response.ContentLength < 0)
                throw new MarketplaceSourceException(ReelBolt.Shared.IntegrationEvents.MarketplacePreviewFailureCodes.SourceTooLarge);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using FileStream file = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81_920];
            long written = 0;
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > maxBytes)
                    throw new MarketplaceSourceException(ReelBolt.Shared.IntegrationEvents.MarketplacePreviewFailureCodes.SourceTooLarge);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }

    public async Task UploadAsync(string platformKey, string localPath, string contentType, CancellationToken ct)
    {
        // The engine never writes a final key: only a per-run staging key.
        if (!platformKey.StartsWith(MarketplacePreviewKeys.Root, StringComparison.Ordinal) || platformKey.Contains("..", StringComparison.Ordinal)
            || !platformKey.Contains("/staging/", StringComparison.Ordinal))
            throw new InvalidOperationException("The engine uploads a marketplace preview only to a staging key.");

        await using FileStream file = File.OpenRead(localPath);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = platformKey,
            InputStream = file,
            ContentType = contentType,
            AutoCloseStream = false
        }, ct);
    }
}
