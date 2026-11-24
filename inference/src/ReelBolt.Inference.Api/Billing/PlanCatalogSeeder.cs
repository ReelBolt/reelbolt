using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>
/// Upserts <c>Billing/plans.catalog.json</c> (the one plan catalog, R5; <c>/site</c> vendors a
/// byte-identical copy) into the <c>plans</c> table at startup. The file holds allowances,
/// entitlements and display fields only; credit RATES are rate cards and provider price ids are
/// per-environment data, so the upsert never touches <see cref="Plan.ProviderPriceIdsJson"/>.
/// A plan removed from the catalog is deactivated, never deleted (subscriptions reference it).
/// </summary>
public static class PlanCatalogSeeder
{
    /// <summary>File name, copied next to the assembly under <c>Billing/</c>.</summary>
    public const string CatalogFileName = "plans.catalog.json";

    /// <summary>Configuration key overriding the catalog location.</summary>
    public const string PathKey = "Billing:CatalogPath";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The default catalog location next to the running assembly.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Billing", CatalogFileName);

    /// <summary>Reads and validates the catalog; throws <see cref="InvalidDataException"/> when it is unusable.</summary>
    public static IReadOnlyList<Plan> Load(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("plans", out JsonElement plans) || plans.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("plans.catalog.json has no plans array.");
        }

        List<Plan> result = [];
        HashSet<string> keys = [];
        int order = 0;
        foreach (JsonElement p in plans.EnumerateArray())
        {
            string key = Req(p, "id");
            if (!keys.Add(key))
            {
                throw new InvalidDataException($"Duplicate plan id '{key}'.");
            }

            if (!Enum.TryParse(Req(p, "kind"), ignoreCase: true, out PlanKind kind))
            {
                throw new InvalidDataException($"Plan '{key}' has an unknown kind.");
            }

            Plan plan = new()
            {
                Id = Guid.NewGuid(),
                Key = key,
                Name = Req(p, "name"),
                Kind = kind,
                PriceMonthlyUsd = p.TryGetProperty("priceMonthly", out JsonElement price) ? price.GetDecimal() : null,
                MinSeats = p.TryGetProperty("minSeats", out JsonElement seats) ? seats.GetInt32() : null,
                MonthlyCreditsMillis = Credits(p, "includedCredits"),
                OneTimeCreditsMillis = Credits(p, "creditsOneTime"),
                TopUpAllowed = p.TryGetProperty("topUpAllowed", out JsonElement top) && top.GetBoolean(),
                EntitlementsJson = p.TryGetProperty("entitlements", out JsonElement ent) ? ent.GetRawText() : "{}",
                SortOrder = order++,
                IsActive = true
            };
            // Fail startup on an entitlement the set cannot represent rather than serve a half-parsed plan.
            _ = PlanEntitlements.ToSet(plan);
            result.Add(plan);
        }

        return result;
    }

    /// <summary>Applies the catalog file to <paramref name="db"/>. A missing file is logged and skipped.</summary>
    public static async Task SeedAsync(InferenceApiDbContext db, IConfiguration configuration, ILogger logger, CancellationToken ct = default)
    {
        string path = configuration[PathKey] is { Length: > 0 } configured ? configured : DefaultPath;
        if (!File.Exists(path))
        {
            logger.LogWarning("Plan catalog {Path} not found; plans table left unchanged.", path);
            return;
        }

        await UpsertAsync(db, Load(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
    }

    /// <summary>Upserts by key; unchanged rows are not rewritten.</summary>
    public static async Task UpsertAsync(InferenceApiDbContext db, IReadOnlyList<Plan> catalog, CancellationToken ct = default)
    {
        Dictionary<string, Plan> existing = await db.Plans.ToDictionaryAsync(p => p.Key, ct).ConfigureAwait(false);
        foreach (Plan wanted in catalog)
        {
            if (!existing.Remove(wanted.Key, out Plan? row))
            {
                db.Plans.Add(wanted);
                continue;
            }

            bool changed = row.Name != wanted.Name
                || row.Kind != wanted.Kind
                || row.PriceMonthlyUsd != wanted.PriceMonthlyUsd
                || row.MinSeats != wanted.MinSeats
                || row.MonthlyCreditsMillis != wanted.MonthlyCreditsMillis
                || row.OneTimeCreditsMillis != wanted.OneTimeCreditsMillis
                || row.TopUpAllowed != wanted.TopUpAllowed
                || row.SortOrder != wanted.SortOrder
                || !row.IsActive
                || !JsonEquals(row.EntitlementsJson, wanted.EntitlementsJson);
            if (!changed)
            {
                continue;
            }

            row.Name = wanted.Name;
            row.Kind = wanted.Kind;
            row.PriceMonthlyUsd = wanted.PriceMonthlyUsd;
            row.MinSeats = wanted.MinSeats;
            row.MonthlyCreditsMillis = wanted.MonthlyCreditsMillis;
            row.OneTimeCreditsMillis = wanted.OneTimeCreditsMillis;
            row.TopUpAllowed = wanted.TopUpAllowed;
            row.SortOrder = wanted.SortOrder;
            row.EntitlementsJson = wanted.EntitlementsJson;
            row.IsActive = true;
            row.UpdatedAt = DateTime.UtcNow;
        }

        foreach (Plan removed in existing.Values.Where(p => p.IsActive))
        {
            removed.IsActive = false;
            removed.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool JsonEquals(string a, string b)
        => JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(a, Options), Options)
           == JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(b, Options), Options);

    private static string Req(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : throw new InvalidDataException($"Catalog plan is missing '{name}'.");

    private static long Credits(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) ? checked((long)Math.Round(v.GetDecimal() * 1000m)) : 0;
}
