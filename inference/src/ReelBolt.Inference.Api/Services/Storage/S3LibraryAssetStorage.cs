using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// S3-compatible object storage for library-owned brand assets. One key per asset:
/// <c>design-libraries/{organizationId}/{libraryId}/assets/{assetId}/{name}</c>. The bytes are inert
/// (a sanitized SVG or a checked font container); this class only moves them and never parses them.
/// </summary>
public sealed class S3LibraryAssetStorage : ILibraryAssetStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3LibraryAssetStorage> _logger;

    public S3LibraryAssetStorage(
        IAmazonS3 s3Client, IConfiguration configuration, ILogger<S3LibraryAssetStorage> logger)
    {
        _s3Client = s3Client;
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    /// <summary>The one place the library asset key shape is built.</summary>
    public static string BuildStorageKey(Guid organizationId, Guid libraryId, Guid assetId, string fileName) =>
        $"design-libraries/{organizationId}/{libraryId}/assets/{assetId}/{fileName}";

    public async Task<string> UploadAsync(
        Guid organizationId, Guid libraryId, Guid assetId,
        Stream content, string fileName, string contentType, CancellationToken ct)
    {
        string storageKey = BuildStorageKey(organizationId, libraryId, assetId, fileName);
        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = storageKey,
            InputStream = content,
            ContentType = contentType,
        }, ct);

        _logger.LogInformation("Uploaded library asset {StorageKey}", storageKey);
        return storageKey;
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        try
        {
            GetObjectResponse response = await _s3Client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucketName, Key = storageKey }, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The row exists but the object does not (a failed upload, an external purge): absent, not
            // a server error. The content route answers 404.
            return null;
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        await _s3Client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _bucketName, Key = storageKey }, ct);
        _logger.LogInformation("Deleted library asset {StorageKey}", storageKey);
    }
}
