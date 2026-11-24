using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds the web search client for a resolved provider (kind -> client): DeepSeek, Gemini, and an
/// OpenAICompatible row whose host is exactly <c>api.openai.com</c>. The public entry returns the METERED client.
/// </summary>
public sealed class WebSearchClientFactory : IWebSearchClientFactory
{
    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<WebSearchClientFactory>? _logger;

    public WebSearchClientFactory(IUsageRecorder? recorder = null, ILogger<WebSearchClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public IWebSearchClient Create(ResolvedWebSearchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // One handler per client, never a shared static one. A bring-your-own row is user-typed input: it connects
        // only to public addresses and follows no redirect. Platform rows keep the plain handler, still without
        // redirects, proxy or cookies.
        SocketsHttpHandler handler = provider.IsPlatformManaged
            ? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }
            : GeneratedClipUrlGuard.CreateUserUrlHandler();
        HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        IWebSearchClient raw;
        try
        {
            raw = CreateRaw(provider, http, ownsClient: true);
        }
        catch
        {
            http.Dispose();
            throw;
        }

        return _recorder is null ? raw : new MeteringWebSearchClient(raw, provider, _recorder, _logger);
    }

    /// <summary>The unmetered client over a supplied <see cref="HttpClient"/> (tests inject a stub handler).</summary>
    internal static IWebSearchClient CreateRaw(ResolvedWebSearchProvider provider, HttpClient http, bool ownsClient = false)
    {
        switch (provider.Kind)
        {
            case InferenceProviderKind.DeepSeek:
                return new DeepSeekWebSearchClient(provider, http, ownsClient);
            case InferenceProviderKind.Gemini:
                return new GeminiWebSearchClient(provider, http, ownsClient);
            case InferenceProviderKind.OpenAICompatible:
                if (!OpenAIWebSearchClient.IsOpenAiEndpoint(provider.Endpoint))
                {
                    throw new WebSearchException(
                        WebSearchException.UnsupportedEndpoint,
                        "Web search through an OpenAI-compatible row requires the host api.openai.com.");
                }

                return new OpenAIWebSearchClient(provider, http, ownsClient);
            default:
                throw new WebSearchException(
                    WebSearchException.UnsupportedEndpoint, $"Provider kind {provider.Kind} does not serve web search.");
        }
    }
}
