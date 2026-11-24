using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs <see cref="IImageClient"/> instances for a MiniMax, OpenAI-compatible, Black Forest
/// Labs or fal.ai backend, and caches them by <see cref="ResolvedImageProvider.CacheKey"/> so repeated
/// resolutions of the same configuration reuse the same client/HttpClient. Mirrors
/// <see cref="VideoGenerationClientFactory"/> exactly.
/// </summary>
public sealed class ImageClientFactory : IImageClientFactory
{
    private readonly ConcurrentDictionary<string, IImageClient> _clients = new();
    private readonly IUsageRecorder? _recorder;
    private readonly Microsoft.Extensions.Logging.ILogger<ImageClientFactory>? _logger;

    /// <param name="recorder">
    /// When supplied, every client handed out meters the images it returns; null returns it raw.
    /// The decorator is applied OUTSIDE the cache, exactly as
    /// <see cref="SpeechSynthesisClientFactory"/> does, because the metering scope carries the
    /// organization and that is deliberately not part of the cache key.
    /// </param>
    public ImageClientFactory(
        IUsageRecorder? recorder = null,
        Microsoft.Extensions.Logging.ILogger<ImageClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public IImageClient Get(ResolvedImageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        IImageClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));
        return _recorder is null ? inner : new MeteringImageClient(inner, provider, _recorder, _logger);
    }

    /// <inheritdoc />
    public IImageClient GetUnmetered(ResolvedImageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _clients.GetOrAdd(provider.CacheKey, _ => Build(provider));
    }

    private static IImageClient Build(ResolvedImageProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.MiniMax => BuildMiniMax(provider),
        InferenceProviderKind.OpenAICompatible => BuildOpenAICompatible(provider),
        InferenceProviderKind.BlackForestLabs => BuildBlackForestLabs(provider),
        InferenceProviderKind.FalAi => BuildFalAi(provider),

        // Every other kind: no image API (and the controller already rejects the combination at the
        // boundary — this is the second line of defence, so a row written straight to the database
        // fails with a sentence rather than reaching a factory arm that does not exist).
        _ => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which is not supported " +
            "for image generation or editing. Only MiniMax, OpenAICompatible, BlackForestLabs and FalAi " +
            "are supported for images.")
    };

    /// <summary>
    /// MiniMax takes the key verbatim as <c>Authorization: Bearer</c> and has no <c>env:</c>
    /// sentinel — identical to the video arm on the same host. A blank key is therefore a
    /// configuration error the client reports when it builds a request, not something to paper over
    /// with an ambient variable.
    /// </summary>
    private static IImageClient BuildMiniMax(ResolvedImageProvider provider)
    {
        RequireEndpoint(provider);
        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);

        return new MiniMaxImageClient(
            httpClient,
            provider.ApiKey,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null));
    }

    /// <summary>
    /// OpenAI-compatible: the key is set as a DEFAULT header only when one is stored, so a local
    /// endpoint that needs no credential (a bundled image server) works and a hosted endpoint with a
    /// blank key 401s loudly instead of spending the host's <c>OPENAI_API_KEY</c>. Redirects are
    /// refused for the same SSRF reason as the speech and transcription arms.
    /// </summary>
    private static IImageClient BuildOpenAICompatible(ResolvedImageProvider provider)
    {
        RequireEndpoint(provider);
        SocketsHttpHandler handler = ProviderRowTransport.CreateNoRedirectHandler(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        return new OpenAICompatibleImageClient(
            httpClient,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null));
    }

    /// <summary>
    /// Black Forest Labs: <c>x-key</c> rather than a bearer header, and its own default host when
    /// the row's endpoint is blank (BFL is cloud-only, so there IS a correct default here — unlike
    /// the self-hosted kinds, where a blank endpoint must throw).
    /// </summary>
    private static IImageClient BuildBlackForestLabs(ResolvedImageProvider provider)
    {
        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? BlackForestLabsImageClient.DefaultEndpoint
            : provider.Endpoint;

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");

        // The poll loop bounds itself with its own deadline, so the HttpClient timeout must not cut
        // a legitimate long-running FLUX job off first; the download client keeps the configured
        // timeout because fetching a finished image is bounded work.
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        return new BlackForestLabsImageClient(
            httpClient,
            provider.ApiKey,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null));
    }

    /// <summary>
    /// fal.ai: the same queue host and <c>Authorization: Key</c> credential as the segmentation arm
    /// (<see cref="ImageAnalysisClientFactory"/>), with a documented default host so a blank endpoint is
    /// filled in. The poll loop bounds itself, so the HttpClient timeout is infinite; the stored key is
    /// sent verbatim and nothing is read from the environment.
    /// </summary>
    private static IImageClient BuildFalAi(ResolvedImageProvider provider)
    {
        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? FalAiImageClient.DefaultEndpoint
            : provider.Endpoint;

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        return new FalAiImageClient(
            httpClient,
            provider.ApiKey,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null),
            probeModel: provider.ModelName,
            probeCapability: provider.Capability);
    }

    private static void RequireEndpoint(ResolvedImageProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' has no endpoint. A {provider.Kind} image provider " +
                "is only reachable at the address configured on its row.");
        }
    }
}
