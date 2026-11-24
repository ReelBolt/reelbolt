using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The "waiting for a human" predicate of the admin review queue, behind a seam because <c>check_json</c> is jsonb: PostgreSQL
/// needs a JSON containment test, the InMemory test provider only a string test. The predicate must live in SQL so an author can
/// never push real pending versions off a page by flooding the table with others.
/// </summary>
public interface IReviewQueueQuery
{
    /// <summary>The versions whose check says <c>decision = pending</c> and that were not superseded.</summary>
    IQueryable<MarketplaceRecipeVersion> Pending(IQueryable<MarketplaceRecipeVersion> source);
}

public sealed class NpgsqlReviewQueueQuery : IReviewQueueQuery
{
    public IQueryable<MarketplaceRecipeVersion> Pending(IQueryable<MarketplaceRecipeVersion> source) =>
        source.Where(v => EF.Functions.JsonContains(v.CheckJson!, "{\"decision\":\"pending\"}")
            && !EF.Functions.JsonContains(v.CheckJson!, "{\"superseded\":true}"));
}

/// <summary>Test implementation: the check is written compact (<c>ToJsonString</c>), so the same containment is a substring.</summary>
public sealed class InMemoryReviewQueueQuery : IReviewQueueQuery
{
    public IQueryable<MarketplaceRecipeVersion> Pending(IQueryable<MarketplaceRecipeVersion> source) =>
        source.Where(v => v.CheckJson != null && v.CheckJson.Contains("\"decision\":\"pending\"") && !v.CheckJson.Contains("\"superseded\":true"));
}
