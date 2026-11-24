using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// What an object delete of one recipe must leave alone: the whole prefix of every version that holds staged CSAM evidence and every
/// key that version lists (<see cref="MarketplaceEvidenceGuard"/>). Quarantined keys are skipped separately by the store.
/// </summary>
public sealed record RecipeObjectProtection(IReadOnlyList<string> Prefixes, IReadOnlySet<string> Keys)
{
    public static readonly RecipeObjectProtection None = new([], new HashSet<string>());

    public bool Protects(string key) =>
        MarketplacePreviewKeys.IsQuarantine(key) || Keys.Contains(key) || Prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    public static async Task<RecipeObjectProtection> LoadAsync(InferenceApiDbContext db, Guid recipeId, CancellationToken ct)
    {
        var checks = await db.MarketplaceRecipeVersions.AsNoTracking().Where(v => v.RecipeId == recipeId)
            .Select(v => new { v.Id, v.CheckJson }).ToListAsync(ct);
        List<string> prefixes = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var v in checks.Where(c => MarketplaceEvidenceGuard.IsHeld(c.CheckJson)))
        {
            prefixes.Add(MarketplacePreviewKeys.VersionPrefix(recipeId, v.Id));
            keys.UnionWith(MarketplaceEvidenceGuard.HeldKeys(v.CheckJson));
        }

        return new RecipeObjectProtection(prefixes, keys);
    }
}
