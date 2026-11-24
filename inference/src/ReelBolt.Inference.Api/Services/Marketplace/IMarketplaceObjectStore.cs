using Amazon.S3;
using Amazon.S3.Model;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The platform-owned objects of a recipe (its preview and poster, under <c>marketplace/{recipeId}/</c>). Moderation only needs to
/// delete them and to ask whether one still exists. The preview layer has its own interface for writing them; the restack unifies the two.
/// </summary>
public interface IMarketplaceObjectStore
{
    /// <summary>Deletes every object under the recipe's prefix except quarantined and held evidence. Returns how many could not be deleted.</summary>
    Task<int> DeleteRecipeObjectsAsync(Guid recipeId, RecipeObjectProtection protection, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);
}

public sealed class S3MarketplaceObjectStore(IAmazonS3 s3, IConfiguration configuration, ILogger<S3MarketplaceObjectStore> logger) : IMarketplaceObjectStore
{
    private const int DeleteBatchSize = 1000;
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task<int> DeleteRecipeObjectsAsync(Guid recipeId, RecipeObjectProtection protection, CancellationToken ct)
    {
        string prefix = $"marketplace/{recipeId}/";
        List<string> keys = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);
            // Quarantined evidence and held (staged) evidence are never deleted.
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key).Where(k => !protection.Protects(k)));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        int notDeleted = 0;
        foreach (string[] batch in keys.Chunk(DeleteBatchSize))
        {
            DeleteObjectsRequest request = new() { BucketName = _bucket, Quiet = true };
            foreach (string key in batch)
                request.AddKey(key);
            DeleteObjectsResponse response = await s3.DeleteObjectsAsync(request, ct);
            notDeleted += response.DeleteErrors?.Count ?? 0;
        }

        if (notDeleted > 0)
            logger.LogWarning("Deleting the objects of marketplace recipe {RecipeId} left {Count} object(s) behind", recipeId, notDeleted);
        return notDeleted;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(_bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
