using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Factory for creating <see cref="IVideoGenerationClient"/> instances based on provider configuration,
/// and caches them by <see cref="ResolvedVideoGenerationProvider.CacheKey"/> so repeated resolutions
/// of the same configuration reuse the same client/HttpClient. Mirrors <see cref="TranscriptionClientFactory"/>
/// exactly.
/// </summary>
public class VideoGenerationClientFactory : IVideoGenerationClientFactory
{
    private readonly ConcurrentDictionary<string, IVideoGenerationClient> _clients = new();

    public IVideoGenerationClient Get(ResolvedVideoGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        return _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));
    }

    private static IVideoGenerationClient Build(ResolvedVideoGenerationProvider provider)
    {
        return provider.Kind switch
        {
            InferenceProviderKind.MiniMax => BuildMiniMax(provider),
            InferenceProviderKind.Higgsfield => BuildHiggsfield(provider),
            InferenceProviderKind.ComfyUI => BuildComfyUi(provider),
            InferenceProviderKind.FalAi => BuildFalAi(provider),
            _ => throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which is not supported " +
                "for video generation. Only MiniMax, Higgsfield, ComfyUI and FalAi video generation are " +
                "currently supported.")
        };
    }

    /// <summary>
    /// fal.ai: one queue host (<c>https://queue.fal.run</c>, overridable on the row) and one
    /// <c>Authorization: Key</c> credential over many hosted video endpoints, the same shape the image and
    /// analysis arms use. The row's own model is the connection test's target; a take's endpoint comes
    /// from the step's <c>Model</c> through <see cref="FalAiVideoGenerationClient.Catalog"/>.
    /// </summary>
    private static IVideoGenerationClient BuildFalAi(ResolvedVideoGenerationProvider provider)
    {
        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? FalAiVideoGenerationClient.DefaultEndpoint
            : provider.Endpoint;

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");

        // Each queue call is short (a submit answers immediately, a status read is a lookup, the result
        // is already stored), so the row's own timeout bounds a hung host rather than a long generation:
        // the WAIT belongs to the caller's poll budget, which is what a step's TimeoutSeconds configures.
        if (provider.TimeoutSeconds > 0)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        }

        return new FalAiVideoGenerationClient(
            httpClient,
            provider.ApiKey,
            GeneratedClipUrlGuard.CreateDownloadClient(
                provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null),
            probeModel: provider.ModelName);
    }

    private static IVideoGenerationClient BuildMiniMax(ResolvedVideoGenerationProvider provider)
    {
        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(provider.Endpoint);

        if (provider.TimeoutSeconds > 0)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        }

        return new MiniMaxVideoGenerationClient(httpClient, provider.ApiKey, downloadClient:
            GeneratedClipUrlGuard.CreateDownloadClient(provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null));
    }

    private static IVideoGenerationClient BuildHiggsfield(ResolvedVideoGenerationProvider provider)
    {
        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = new Uri(provider.Endpoint);

        if (provider.TimeoutSeconds > 0)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        }

        return new HiggsfieldVideoGenerationClient(httpClient, provider.ApiKey, downloadClient:
            GeneratedClipUrlGuard.CreateDownloadClient(provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null));
    }

    /// <summary>
    /// A self-hosted server has no vendor default: a blank endpoint throws here (the same rule
    /// OpenJev applies) rather than quietly posting a prompt anywhere.
    /// </summary>
    private static IVideoGenerationClient BuildComfyUi(ResolvedVideoGenerationProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint) || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? root))
        {
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' (ComfyUI) has no usable endpoint. A self-hosted ComfyUI server is only reachable at the address an administrator registered; there is no default.");
        }

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = root;

        if (provider.TimeoutSeconds > 0)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
        }

        return new ComfyUiVideoGenerationClient(httpClient, provider.ApiKey);
    }
}
