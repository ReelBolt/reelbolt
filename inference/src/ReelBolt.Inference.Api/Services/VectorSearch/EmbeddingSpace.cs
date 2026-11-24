using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.VectorSearch;

/// <summary>
/// Names the Qdrant collection for one (project, embedding model, vector dimension). Vectors from
/// different models are not comparable even at equal dimension, and a different dimension is a hard
/// Qdrant error, so each combination gets its own collection. The bare <c>{projectId:N}</c> name
/// used before embeddings became a provider capability is the "legacy" collection: never written or
/// searched any more, but still cleared on per-file deletes. A reindex repopulates under the new name.
/// </summary>
public static class EmbeddingSpace
{
    public static string CollectionPrefix(Guid projectId) => projectId.ToString("N");

    public static string CollectionName(Guid projectId, string? modelId, int dimension)
    {
        string model = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId.Trim();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model)))[..8].ToLowerInvariant();

        StringBuilder slug = new();
        foreach (char c in model.ToLowerInvariant())
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            if (slug.Length >= 40) break;
        }

        return $"{CollectionPrefix(projectId)}_{slug.ToString().Trim('-')}_{hash}_{dimension}";
    }

    /// <summary>True for the legacy bare collection and any model-keyed collection of the project.</summary>
    public static bool BelongsToProject(Guid projectId, string collectionName)
    {
        string prefix = CollectionPrefix(projectId);
        return collectionName == prefix || collectionName.StartsWith(prefix + "_", StringComparison.Ordinal);
    }
}
