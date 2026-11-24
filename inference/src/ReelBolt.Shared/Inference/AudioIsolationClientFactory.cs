using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the <see cref="IAudioIsolationClient"/> for a resolved isolation row and caches it by
/// <see cref="ResolvedAudioGenerationProvider.CacheKey"/>, the same shape as <see cref="AudioGenerationClientFactory"/>.
/// Only <c>FalAi</c> is served; the dormant <c>ElevenLabs</c> kind is refused.
/// </summary>
public sealed class AudioIsolationClientFactory : IAudioIsolationClientFactory
{
    private readonly ConcurrentDictionary<string, IAudioIsolationClient> _clients = new();

    public IAudioIsolationClient Get(ResolvedAudioGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = provider.OrganizationId is not null || !provider.IsPlatformManaged;
        return _clients.GetOrAdd(provider.CacheKey + (orgOwned ? "|org" : "|platform"), _ => Build(provider, orgOwned));
    }

    private static IAudioIsolationClient Build(ResolvedAudioGenerationProvider provider, bool publicOnly)
    {
        if (provider.Kind != InferenceProviderKind.FalAi)
        {
            throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which does not serve audio isolation. " +
                "Only FalAi does.");
        }

        TimeSpan? timeout = provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null;
        string root = string.IsNullOrWhiteSpace(provider.Endpoint) ? FalAiAudioIsolationClient.DefaultEndpoint : provider.Endpoint.Trim();
        // S-51: no automatic redirect and no ambient proxy, so a 3xx can never carry the request (and its key) elsewhere.
        SocketsHttpHandler handler = publicOnly
            ? GeneratedClipUrlGuard.CreateUserUrlHandler()
            : new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        HttpClient http = new(handler) { BaseAddress = new Uri(root.TrimEnd('/') + "/") };
        if (timeout is { } t) http.Timeout = t;
        return new FalAiAudioIsolationClient(http, provider.ApiKey, GeneratedClipUrlGuard.CreateDownloadClient(timeout));
    }
}
