using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ReelBolt.Shared.Metering;

public static class MeteringServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model price service reading <typeparamref name="TContext"/>, which must map the model catalog,
    /// <c>model_capability_prices</c>, <c>rate_multipliers</c> and <c>pricing_settings</c> (the API owns them, the
    /// engine maps them read-only).
    /// </summary>
    public static IServiceCollection AddReelBoltModelPrices<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddSingleton<IModelPriceSource, EfModelPriceSource<TContext>>();
        services.TryAddSingleton<IModelPriceService>(sp => new ModelPriceService(sp.GetRequiredService<IModelPriceSource>()));
        return services;
    }

    /// <summary>
    /// Binds <see cref="MeteringOptions"/> from the <c>Metering</c> section (read by <see cref="UsageRecorder"/>).
    /// </summary>
    public static IServiceCollection AddReelBoltMeteringOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MeteringOptions>(configuration.GetSection(MeteringOptions.SectionName));
        return services;
    }

    /// <summary>
    /// Registers the usage recorder: prices through <see cref="IModelPriceService"/> and writes batches to
    /// <c>usage_events</c> through <typeparamref name="TContext"/>. Call it BEFORE the services that
    /// generate usage so the hosted recorder stops after them and flushes their last rows.
    /// </summary>
    public static IServiceCollection AddReelBoltUsageRecorder<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddReelBoltModelPrices<TContext>();
        services.TryAddSingleton<IUsageEventSink, EfUsageEventSink<TContext>>();
        services.TryAddSingleton<UsageRecorder>();
        services.TryAddSingleton<IUsageRecorder>(sp => sp.GetRequiredService<UsageRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<UsageRecorder>());
        return services;
    }
}
