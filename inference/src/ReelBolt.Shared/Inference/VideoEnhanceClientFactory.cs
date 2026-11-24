using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the <see cref="IVideoUpscaleClient"/>, <see cref="IFrameInterpolationClient"/> and
/// <see cref="IVideoOutpaintClient"/> for a resolved row and caches each by
/// <see cref="ResolvedAudioGenerationProvider.CacheKey"/> (the resolved shape is shared with the audio capabilities:
/// endpoint, model, key, timeout). Only <c>FalAi</c> is served.
/// </summary>
public sealed class VideoEnhanceClientFactory : IVideoUpscaleClientFactory, IFrameInterpolationClientFactory,
    IVideoOutpaintClientFactory
{
    private readonly TimeSpan _pollTimeout;
    private readonly int _maxSourceSeconds;

    /// <param name="configuration">
    /// Binds <c>VideoEnhance:Hosted:PollTimeoutMinutes</c> (default 30) and <c>VideoEnhance:Hosted:MaxSourceSeconds</c>
    /// (default 120); null or a non-positive value keeps the default.
    /// </param>
    public VideoEnhanceClientFactory(IConfiguration? configuration = null)
    {
        int minutes = configuration?.GetValue<int?>(VideoEnhanceCreditPricing.PollTimeoutMinutesKey) ?? 0;
        _pollTimeout = TimeSpan.FromMinutes(minutes > 0 ? minutes : VideoEnhanceCreditPricing.DefaultPollTimeoutMinutes);
        int seconds = configuration?.GetValue<int?>(VideoEnhanceCreditPricing.MaxSourceSecondsKey) ?? 0;
        _maxSourceSeconds = seconds > 0 ? seconds : VideoEnhanceCreditPricing.DefaultMaxSourceSeconds;
    }

    /// <summary>The poll budget the factory's clients use.</summary>
    public TimeSpan PollTimeout => _pollTimeout;

    private readonly ConcurrentDictionary<string, IVideoUpscaleClient> _upscalers = new();
    private readonly ConcurrentDictionary<string, IFrameInterpolationClient> _interpolators = new();
    private readonly ConcurrentDictionary<string, IVideoOutpaintClient> _outpainters = new();

    IVideoUpscaleClient IVideoUpscaleClientFactory.Get(ResolvedAudioGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _upscalers.GetOrAdd(KeyOf(provider), _ => BuildUpscaler(provider, IsOrgOwned(provider)));
    }

    IFrameInterpolationClient IFrameInterpolationClientFactory.Get(ResolvedAudioGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _interpolators.GetOrAdd(KeyOf(provider), _ => BuildInterpolator(provider, IsOrgOwned(provider)));
    }

    IVideoOutpaintClient IVideoOutpaintClientFactory.Get(ResolvedAudioGenerationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _outpainters.GetOrAdd(KeyOf(provider), _ => BuildOutpainter(provider, IsOrgOwned(provider)));
    }

    private static bool IsOrgOwned(ResolvedAudioGenerationProvider provider) =>
        provider.OrganizationId is not null || !provider.IsPlatformManaged;

    private static string KeyOf(ResolvedAudioGenerationProvider provider) =>
        provider.CacheKey + (IsOrgOwned(provider) ? "|org" : "|platform");

    private IVideoUpscaleClient BuildUpscaler(ResolvedAudioGenerationProvider provider, bool publicOnly)
    {
        (HttpClient http, HttpClient download) = BuildHttp(provider, publicOnly, "upscaling");
        return new FalAiVideoUpscaleClient(http, provider.ApiKey, download, pollTimeout: _pollTimeout, maxSourceSeconds: _maxSourceSeconds);
    }

    private IFrameInterpolationClient BuildInterpolator(ResolvedAudioGenerationProvider provider, bool publicOnly)
    {
        (HttpClient http, HttpClient download) = BuildHttp(provider, publicOnly, "frame interpolation");
        return new FalAiFrameInterpolationClient(http, provider.ApiKey, download, pollTimeout: _pollTimeout, maxSourceSeconds: _maxSourceSeconds);
    }

    private IVideoOutpaintClient BuildOutpainter(ResolvedAudioGenerationProvider provider, bool publicOnly)
    {
        (HttpClient http, HttpClient download) = BuildHttp(provider, publicOnly, "outpainting");
        return new FalAiVideoOutpaintClient(http, provider.ApiKey, download, pollTimeout: _pollTimeout, maxSourceSeconds: _maxSourceSeconds);
    }

    private static (HttpClient Http, HttpClient Download) BuildHttp(ResolvedAudioGenerationProvider provider, bool publicOnly, string what)
    {
        if (provider.Kind != InferenceProviderKind.FalAi)
        {
            throw new NotSupportedException(
                $"Inference provider '{provider.Name}' is of kind '{provider.Kind}', which does not serve video {what}. " +
                "Only FalAi does.");
        }

        TimeSpan? timeout = provider.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(provider.TimeoutSeconds) : null;
        string root = string.IsNullOrWhiteSpace(provider.Endpoint) ? FalQueueClient.DefaultEndpoint : provider.Endpoint.Trim();
        // S-51: no automatic redirect and no ambient proxy, so a 3xx can never carry the request (and its key) elsewhere.
        SocketsHttpHandler handler = publicOnly
            ? GeneratedClipUrlGuard.CreateUserUrlHandler()
            : new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        HttpClient http = new(handler) { BaseAddress = new Uri(root.TrimEnd('/') + "/") };
        if (timeout is { } t) http.Timeout = t;
        return (http, GeneratedClipUrlGuard.CreateDownloadClient(timeout));
    }
}
