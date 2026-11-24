using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>A recipe with its two integer sort keys (higher sorts first); the id is the final tie-break.</summary>
public sealed class KeyedRecipe
{
    public MarketplaceRecipe Recipe { get; set; } = null!;
    public long K1 { get; set; }
    public long K2 { get; set; }
}

/// <summary>
/// Full-text search over recipes, behind a seam because the InMemory test provider cannot run PostgreSQL text search.
/// </summary>
public interface IRecipeSearch
{
    /// <summary>The recipes matching <paramref name="text"/>, keyed by (text rank, copies).</summary>
    IQueryable<KeyedRecipe> Ranked(IQueryable<MarketplaceRecipe> source, string text);
}

/// <summary>The real search: the generated <c>search_vector</c> against <c>websearch_to_tsquery('simple', ...)</c>.</summary>
public sealed class NpgsqlRecipeSearch : IRecipeSearch
{
    public IQueryable<KeyedRecipe> Ranked(IQueryable<MarketplaceRecipe> source, string text) =>
        source
            .Where(r => r.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", text)))
            .Select(r => new KeyedRecipe
            {
                Recipe = r,
                K1 = (long)(r.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", text)) * 1000000f),
                K2 = r.CopyOrgs
            });
}

/// <summary>Test implementation: every word must occur in name, description or category; name matches rank higher.</summary>
public sealed class InMemoryRecipeSearch : IRecipeSearch
{
    public IQueryable<KeyedRecipe> Ranked(IQueryable<MarketplaceRecipe> source, string text)
    {
        string[] words = [.. text.ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];
        foreach (string word in words)
        {
            string w = word;
            source = source.Where(r => r.Name.ToLower().Contains(w) || r.Description.ToLower().Contains(w) || r.Category.ToLower().Contains(w));
        }

        string first = words.Length > 0 ? words[0] : "";
        return source.Select(r => new KeyedRecipe
        {
            Recipe = r,
            K1 = r.Name.ToLower().Contains(first) ? 10L : 1L,
            K2 = r.CopyOrgs
        });
    }
}
