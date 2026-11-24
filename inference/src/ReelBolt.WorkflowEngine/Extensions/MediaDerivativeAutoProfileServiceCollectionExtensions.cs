using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine;

/// <summary>
/// Wires the production camera-profile auto-detector into <see cref="MediaDerivativeDispatcher"/> through DI.
/// </summary>
/// <remarks>
/// The dispatcher takes an optional <c>Func&lt;MediaProbeResult, string?&gt;</c> (null in tests and in every
/// manual construction). Registering that delegate in the container makes the single production instance
/// resolve it without changing the constructor's default: a High-confidence camera profile then rides along
/// on the upload plan, while a caller that never registers the delegate keeps the old "explicit request
/// only" behaviour.
/// </remarks>
public static class MediaDerivativeAutoProfileServiceCollectionExtensions
{
    /// <summary>Registers the detector-backed selector the dispatcher's optional constructor parameter consumes.</summary>
    public static IServiceCollection AddMediaDerivativeAutoProfile(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<Func<MediaProbeResult, string?>>(
            _ => MediaDerivativeDispatcher.AutoProfileFromProbe);
        return services;
    }
}
