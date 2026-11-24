using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ReelBolt.Shared.Drain;

public static class DrainServiceCollectionExtensions
{
    /// <summary>Registers the drain monitor as <see cref="IDrainState"/> and a hosted service. Idempotent.</summary>
    public static IServiceCollection AddReelBoltDrain(this IServiceCollection services)
    {
        services.AddOptions<DrainOptions>();
        services.TryAddSingleton(TimeProvider.System);
        if (services.All(d => d.ServiceType != typeof(DrainMonitor)))
        {
            services.AddSingleton<DrainMonitor>();
            services.AddSingleton<IDrainState>(sp => sp.GetRequiredService<DrainMonitor>());
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DrainMonitor>());
        }

        return services;
    }
}
