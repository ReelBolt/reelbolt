using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs <see cref="IImageAnalysisClient"/> instances, and caches them by
/// <see cref="ResolvedImageAnalysisProvider.CacheKey"/> so repeated resolutions of the same
/// configuration reuse the same client/HttpClient. Mirrors <see cref="ImageClientFactory"/> exactly.
/// </summary>
public sealed class ImageAnalysisClientFactory : IImageAnalysisClientFactory
{
    private readonly ConcurrentDictionary<string, IImageAnalysisClient> _clients = new();
    private readonly IUsageRecorder? _recorder;
    private readonly Microsoft.Extensions.Logging.ILogger<ImageAnalysisClientFactory>? _logger;

    /// <param name="recorder">
    /// When supplied, every client handed out meters the images it returns; null returns it raw. The
    /// decorator is applied OUTSIDE the cache, because the metering scope carries the organization and
    /// that is deliberately not part of the cache key.
    /// </param>
    public ImageAnalysisClientFactory(
        IUsageRecorder? recorder = null,
        Microsoft.Extensions.Logging.ILogger<ImageAnalysisClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public IImageAnalysisClient Get(ResolvedImageAnalysisProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        IImageAnalysisClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));
        return _recorder is null ? inner : new MeteringImageAnalysisClient(inner, provider, _recorder, _logger);
    }

    private static IImageAnalysisClient Build(ResolvedImageAnalysisProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.FalAi => BuildFalAi(provider),

        // Every other kind: no segmentation or depth API (and the controller already rejects the
        // combination at the boundary — this is the second line of defence, so a row written straight
        // to the database fails with a sentence rather than reaching a factory arm that does not exist).
        _ => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which is not supported " +
            "for segmentation or depth estimation. Only FalAi is supported today.")
    };

    /// <summary>
    /// fal.ai. The queue host has a documented default, so a blank endpoint is filled in rather than
    /// throwing — unlike the self-hosted kinds, where a blank endpoint genuinely has no correct answer.
    /// The stored key is sent verbatim as <c>Authorization: Key &lt;key&gt;</c> and nothing is read from
    /// the environment.
    /// </summary>
    private static IImageAnalysisClient BuildFalAi(ResolvedImageAnalysisProvider provider)
    {
        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? FalAiAnalysisClient.DefaultEndpoint
            : provider.Endpoint;

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        // The poll loop bounds itself with its own deadline, so the HttpClient timeout must not cut
        // a legitimately queued job off first.
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        return new FalAiAnalysisClient(
            httpClient,
            provider.ApiKey,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null),
            probeModel: provider.ModelName);
    }
}
