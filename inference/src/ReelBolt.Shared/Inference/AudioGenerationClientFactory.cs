using System.Collections.Concurrent;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the <see cref="IAudioGenerationClient"/> for a resolved music or sound-effect row and caches it by
/// <see cref="ResolvedAudioGenerationProvider.CacheKey"/>, the same shape as <see cref="VideoGenerationClientFactory"/>.
/// Only <c>FalAi</c> is served; a blank key throws inside the client's constructor. The direct
/// <see cref="ElevenLabsAudioGenerationClient"/> stays in the tree, dormant: this factory never builds it.
/// </summary>
public sealed class AudioGenerationClientFactory : IAudioGenerationClientFactory
{
    private readonly ConcurrentDictionary<string, IAudioGenerationClient> _clients = new();

    public IAudioGenerationClient Get(ResolvedAudioGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // The transport differs for an org-owned row (public-only connect), so it is part of the cache key: an
        // org row must never be served a platform row's client that happens to share endpoint, model and key.
        string key = provider.CacheKey + (IsOrgOwned(provider) ? "|org" : "|platform");
        return _clients.GetOrAdd(key, _ => Build(provider));
    }

    /// <summary>A row a tenant configured (its endpoint passed only a write-time check), as opposed to a platform row.</summary>
    private static bool IsOrgOwned(ResolvedAudioGenerationProvider provider) =>
        provider.OrganizationId is not null || !provider.IsPlatformManaged;

    private static IAudioGenerationClient Build(ResolvedAudioGenerationProvider provider)
    {
        TimeSpan? timeout = provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null;
        bool publicOnly = IsOrgOwned(provider);
        return provider.Kind switch
        {
            InferenceProviderKind.FalAi => new FalAiAudioGenerationClient(
                Http(provider.Endpoint, FalAiAudioGenerationClient.DefaultEndpoint, timeout, publicOnly),
                provider.ApiKey,
                GeneratedClipUrlGuard.CreateDownloadClient(timeout),
                probeModel: provider.ModelName),
            InferenceProviderKind.ElevenLabs => throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind 'ElevenLabs', which is not offered: music and sound " +
                "effects are generated through fal only."),
            _ => throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which does not serve music or sound " +
                "effect generation. Only FalAi does.")
        };
    }

    /// <summary>
    /// A client on the row's endpoint (or the vendor host). The row's timeout bounds one request; a music
    /// generation on ElevenLabs answers synchronously, so it must cover the longest track.
    /// </summary>
    /// <remarks>
    /// S-51: no automatic redirect and no ambient proxy on any row, so a 3xx can never carry the request (and its
    /// key) to another host. An org-owned row additionally dials only public addresses, checked on the address
    /// actually connected to (<see cref="GeneratedClipUrlGuard.CreateUserUrlHandler"/>), which closes DNS
    /// rebinding past the write-time public-endpoint gate.
    /// </remarks>
    private static HttpClient Http(string? endpoint, string fallback, TimeSpan? timeout, bool publicOnly)
    {
        string root = string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint.Trim();
        SocketsHttpHandler handler = publicOnly
            ? GeneratedClipUrlGuard.CreateUserUrlHandler()
            : new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        HttpClient client = new(handler) { BaseAddress = new Uri(root.TrimEnd('/') + "/") };
        if (timeout is { } t) client.Timeout = t;
        return client;
    }
}
