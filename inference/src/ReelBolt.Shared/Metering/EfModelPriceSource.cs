using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Reads the price tables through whichever service's DbContext maps them (the Inference API owns them, the
/// engine maps them read-only). Opens its own scope per load because the price service is a singleton. Every
/// capability is loaded regardless of tenant (query filters bypassed) and of its enabled flag: pricing a call
/// that already happened must not depend on whether its row was disabled a second later.
/// </summary>
public sealed class EfModelPriceSource<TContext>(IServiceScopeFactory scopes) : IModelPriceSource
    where TContext : DbContext
{
    public async Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();

        List<PricedCapability> capabilities = await db.Set<ModelCapability>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(c => new PricedCapability(
                c.Id, c.Model.ProviderId, c.OrganizationId, c.Model.Provider.Kind, c.Capability, c.Model.ModelCode))
            .ToListAsync(ct);
        List<ModelCapabilityPrice> prices = await db.Set<ModelCapabilityPrice>().AsNoTracking().ToListAsync(ct);
        List<RateMultiplier> multipliers = await db.Set<RateMultiplier>().AsNoTracking().ToListAsync(ct);
        PricingSettings? settings = await db.Set<PricingSettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == PricingSettings.SingletonId, ct);

        return new ModelPriceSnapshot(
            capabilities,
            prices,
            multipliers,
            settings?.Markup ?? PricingDefaults.DefaultMarkup,
            settings is { CreditUsdValue: > 0m } ? settings.CreditUsdValue : PricingDefaults.CreditUsdValue);
    }
}
