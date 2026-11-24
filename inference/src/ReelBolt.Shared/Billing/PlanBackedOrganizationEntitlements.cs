using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Billing;

/// <summary>Drops cached entitlements; B9a's webhook processor calls it when a subscription changes.</summary>
public interface IEntitlementCache
{
    /// <summary>Forget one org's cached entitlements (this process only; the other service waits out the TTL).</summary>
    void Invalidate(Guid organizationId);

    /// <summary>Forget everything.</summary>
    void InvalidateAll();
}

/// <summary>Reads the plan an org is currently on. Split from the cache so tests need no database.</summary>
public interface IPlanSource
{
    /// <summary>The plan of the org's entitling subscription, or null when it has none.</summary>
    Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>The plan with <paramref name="key"/>, or null.</summary>
    Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The active plans of the catalogue, for naming the plan that unlocks a feature. Empty by default (a source without a catalogue).</summary>
    Task<IReadOnlyList<Plan>> GetCatalogueAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Plan>>([]);
}

/// <summary><see cref="IPlanSource"/> over the <c>plans</c>/<c>subscriptions</c> tables of either service's context.</summary>
public sealed class EfPlanSource<TContext>(IServiceScopeFactory scopes, TimeProvider? time = null) : IPlanSource where TContext : DbContext
{
    /// <summary>Statuses that still entitle: PastDue keeps the plan during the provider's dunning window.</summary>
    private static readonly SubscriptionStatus[] Entitling =
        [SubscriptionStatus.Trialing, SubscriptionStatus.Active, SubscriptionStatus.PastDue];

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        DbContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        // A manual (provider-less) subscription lapses at its period end: nothing writes a "Canceled"
        // state at midnight, so the read is where the gift stops entitling. A provider-backed row is
        // the provider's to expire — its status is authoritative and a stale PeriodEnd must not
        // downgrade a paying organization.
        DateTime now = Now;
        // Explicit org id, so tenant query filters (which scope to the caller's org) are bypassed on purpose.
        Guid? planId = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && Entitling.Contains(s.Status)
                && (s.Provider != null || s.PeriodEnd == null || s.PeriodEnd > now))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => (Guid?)s.PlanId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return planId is null
            ? null
            : await db.Set<Plan>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        DbContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<Plan>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Plan>> GetCatalogueAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        DbContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<Plan>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.IsActive).OrderBy(p => p.SortOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The plan-backed <see cref="IOrganizationEntitlements"/> (B8a, replacing the config default):
/// <c>Billing:Enabled=false</c> returns <see cref="OrganizationEntitlementSet.Unlimited"/> for every
/// org; otherwise the org's entitling subscription's plan, else the Trial plan, decides. A missing
/// Trial plan row (catalog not seeded) degrades to the config default rather than throwing.
/// Results are cached per org for <see cref="BillingOptions.EntitlementCacheSeconds"/>.
/// </summary>
public sealed class PlanBackedOrganizationEntitlements(
    IPlanSource plans, IOptions<BillingOptions> options, TenancyOptions tenancy, TimeProvider? clock = null)
    : IOrganizationEntitlements, IEntitlementCache
{
    private readonly ConcurrentDictionary<Guid, (OrganizationEntitlementSet Set, DateTimeOffset Expires)> _cache = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        BillingOptions opts = options.Value;
        if (!opts.Enabled) return OrganizationEntitlementSet.Unlimited;

        DateTimeOffset now = _clock.GetUtcNow();
        if (opts.EntitlementCacheSeconds > 0 && _cache.TryGetValue(organizationId, out var hit) && hit.Expires > now)
            return hit.Set;

        Plan? plan = await plans.GetSubscribedPlanAsync(organizationId, cancellationToken).ConfigureAwait(false)
                     ?? await plans.GetPlanByKeyAsync(opts.TrialPlanKey, cancellationToken).ConfigureAwait(false);
        OrganizationEntitlementSet set = plan is null
            ? tenancy.DefaultEntitlements with { Tier = opts.TrialPlanKey }
            : PlanEntitlements.ToSet(plan);
        // The catalogue travels with the set (and its cache) so a refusal can name the plan that unlocks its feature.
        // Best effort and display only: an unreadable catalogue never changes what the plan allows.
        try
        {
            IReadOnlyList<Plan> catalogue = await plans.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            if (catalogue.Count > 0) set = set with { UpgradePlans = PlanUnlocks.OptionsFrom(catalogue) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }

        if (opts.EntitlementCacheSeconds > 0)
            _cache[organizationId] = (set, now.AddSeconds(opts.EntitlementCacheSeconds));
        return set;
    }

    /// <inheritdoc />
    public void Invalidate(Guid organizationId) => _cache.TryRemove(organizationId, out _);

    /// <inheritdoc />
    public void InvalidateAll() => _cache.Clear();
}

/// <summary>DI for the plan-backed entitlements.</summary>
public static class PlanEntitlementsServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the config-default <see cref="IOrganizationEntitlements"/> with the plan-backed one
    /// over <typeparamref name="TContext"/>, and exposes <see cref="IEntitlementCache"/> as the same instance.
    /// Call after <c>AddReelBoltEntitlements</c>.
    /// </summary>
    public static IServiceCollection AddReelBoltPlanEntitlements<TContext>(this IServiceCollection services, IConfiguration configuration)
        where TContext : DbContext
    {
        services.Configure<BillingOptions>(configuration.GetSection(BillingOptions.SectionName));
        services.TryAddSingleton<IPlanSource, EfPlanSource<TContext>>();
        services.AddSingleton<PlanBackedOrganizationEntitlements>();
        services.Replace(ServiceDescriptor.Singleton<IOrganizationEntitlements>(sp => sp.GetRequiredService<PlanBackedOrganizationEntitlements>()));
        services.AddSingleton<IEntitlementCache>(sp => sp.GetRequiredService<PlanBackedOrganizationEntitlements>());
        return services;
    }
}
