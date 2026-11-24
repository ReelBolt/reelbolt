using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ReelBolt.Shared.Tenancy;

/// <summary>DI registration of the entitlement contract shared by both .NET services.</summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenancyOptions"/> (from the <c>Tenancy</c> section) and the config-backed
    /// <see cref="IOrganizationEntitlements"/>. <c>TryAdd</c>, so billing (B8a) can register its
    /// plan-backed implementation first or afterwards with <c>Replace</c>.
    /// </summary>
    public static IServiceCollection AddReelBoltEntitlements(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions());
        services.TryAddSingleton<IOrganizationEntitlements, ConfigOrganizationEntitlements>();
        return services;
    }
}
