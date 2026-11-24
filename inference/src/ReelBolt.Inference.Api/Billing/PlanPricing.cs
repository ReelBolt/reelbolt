using System.Text.Json;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>
/// Catalog-derived prices the <c>plans</c> row does not carry. The annual price is a catalog field
/// (<c>priceAnnual</c>, 10 x monthly), not a column: no migration, the catalog stays the one source.
/// </summary>
public static class PlanPricing
{
    private static readonly Lazy<IReadOnlyDictionary<string, decimal>> Shipped = new(() => LoadShipped());

    /// <summary>The annual price (per seat on a per-seat plan) of <paramref name="plan"/>, or null when it has none.</summary>
    public static decimal? AnnualPriceUsd(Plan plan)
        => Shipped.Value.TryGetValue(plan.Key, out decimal price) ? price : null;

    /// <summary>Reads each plan's <c>priceAnnual</c> from catalog JSON, keyed by plan id.</summary>
    public static IReadOnlyDictionary<string, decimal> ParseAnnualPrices(string json)
    {
        Dictionary<string, decimal> result = new(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("plans", out JsonElement plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement p in plans.EnumerateArray())
            {
                if (p.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    && p.TryGetProperty("priceAnnual", out JsonElement price) && price.ValueKind == JsonValueKind.Number)
                {
                    result[id.GetString()!] = price.GetDecimal();
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, decimal> LoadShipped()
    {
        try
        {
            return File.Exists(PlanCatalogSeeder.DefaultPath)
                ? ParseAnnualPrices(File.ReadAllText(PlanCatalogSeeder.DefaultPath))
                : new Dictionary<string, decimal>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, decimal>();
        }
    }
}
