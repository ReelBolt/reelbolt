using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.ApiKeys;

/// <summary>One window of a key's usage: the cap in credits (null = no cap), what it used so far and when it resets (UTC).</summary>
public sealed record ApiKeyWindowView(decimal? CapCredits, decimal UsedCredits, DateTime ResetsAt);

public sealed record ApiKeyUsageView(ApiKeyWindowView Daily, ApiKeyWindowView Weekly, ApiKeyWindowView Monthly);

/// <summary>Reads how much of each cap window a set of keys has used.</summary>
public interface IApiKeyUsageReader
{
    /// <summary>False when billing is off: no credit holds exist, so caps are not enforced and there is no usage to show.</summary>
    bool CapsEnforced { get; }

    /// <summary>Usage per key id from ONE grouped query, or null when <see cref="CapsEnforced"/> is false.</summary>
    Task<Dictionary<Guid, ApiKeyUsageView>?> ReadAsync(IReadOnlyCollection<ApiKey> keys, DateTime nowUtc, CancellationToken ct);
}

public sealed class ApiKeyUsageReader(InferenceApiDbContext db, IOptions<BillingOptions> billing) : IApiKeyUsageReader
{
    public bool CapsEnforced => billing.Value.Enabled;

    public async Task<Dictionary<Guid, ApiKeyUsageView>?> ReadAsync(IReadOnlyCollection<ApiKey> keys, DateTime nowUtc, CancellationToken ct)
    {
        if (!CapsEnforced)
        {
            return null;
        }

        Dictionary<Guid, long[]> used = await ApiKeyBudgetWindows.UsedAsync(db, [.. keys.Select(k => k.Id)], nowUtc, ct);
        return keys.ToDictionary(k => k.Id, k =>
        {
            List<ApiKeyWindowUsage> w = ApiKeyBudgetWindows.Usage(k, used[k.Id], nowUtc);
            return new ApiKeyUsageView(View(w[0]), View(w[1]), View(w[2]));
        });
    }

    private static ApiKeyWindowView View(ApiKeyWindowUsage w) => new(w.CapMillis / 1000m, w.UsedMillis / 1000m, w.ResetsAt);
}
