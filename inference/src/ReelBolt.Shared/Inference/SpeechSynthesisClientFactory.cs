using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs <see cref="ISpeechSynthesisClient"/> instances for a Fish Audio or an
/// OpenAI-compatible speech synthesis backend, and caches them by <see cref="ResolvedSpeechSynthesisProvider.CacheKey"/>
/// so repeated resolutions of the same configuration reuse the same client/HttpClient.
/// Mirrors <see cref="TranscriptionClientFactory"/> exactly.
/// </summary>
public sealed class SpeechSynthesisClientFactory : ISpeechSynthesisClientFactory
{
    /// <summary>
    /// Fish Audio API key placeholder for backends that may run without a key, keeping
    /// consistency with <see cref="TranscriptionClientFactory"/>'s NoKeyPlaceholder.
    /// </summary>
    private const string NoKeyPlaceholder = "not-required";

    private readonly ConcurrentDictionary<string, ISpeechSynthesisClient> _clients = new();

    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<SpeechSynthesisClientFactory>? _logger;

    /// <param name="recorder">When supplied, every client handed out meters input bytes (B3c); null returns it raw.</param>
    public SpeechSynthesisClientFactory(IUsageRecorder? recorder = null, ILogger<SpeechSynthesisClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public ISpeechSynthesisClient Get(ResolvedSpeechSynthesisProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        ISpeechSynthesisClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));

        // Wrapped per call, outside the cache: the owning organization is not part of CacheKey.
        return _recorder is null ? inner : new MeteringSpeechSynthesisClient(inner, provider, _recorder, _logger);
    }

    private static ISpeechSynthesisClient Build(ResolvedSpeechSynthesisProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.FishAudio => BuildFishAudio(provider),
        InferenceProviderKind.OpenAICompatible => BuildOpenAICompatible(provider),

        // Every other kind: no compatible TTS API (and the controller already rejects the
        // combination at the boundary — this is the second line of defence).
        _ => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is not supported for speech synthesis. " +
            "Only FishAudio and OpenAICompatible providers are supported for text-to-speech synthesis.")
    };

    /// <summary>
    /// The LOCAL TTS path (docs/voiceover.md "A local OpenAI-compatible TTS server"): the bundled
    /// speaches container, or any server speaking OpenAI's <c>/audio/speech</c>. Same credential
    /// rule as every other kind here — the stored key is sent as <c>Authorization: Bearer</c> when
    /// present and NOTHING is read from the environment when it is blank: a blank key on a public
    /// endpoint fails at the vendor with its own 401, loudly, rather than silently spending the
    /// host's <c>OPENAI_API_KEY</c>. Redirects are refused for the same SSRF reason as Fish.
    /// </summary>
    private static ISpeechSynthesisClient BuildOpenAICompatible(ResolvedSpeechSynthesisProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' has no endpoint; an OpenAI-compatible speech server is only reachable at the address an operator supplied.");

        SocketsHttpHandler handler = ProviderRowTransport.CreateNoRedirectHandler(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        return new OpenAICompatibleSpeechSynthesisClient(httpClient, provider.ModelName);
    }

    private static ISpeechSynthesisClient BuildFishAudio(ResolvedSpeechSynthesisProvider provider)
    {
        string apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? NoKeyPlaceholder : provider.ApiKey;

        // The endpoint comes from an operator-supplied row, and it was validated once, at write
        // time. A 3xx from it would hand the request to a second host that was never validated at
        // all — the "point the vendor at 169.254.169.254 and let the vendor's own client fetch it"
        // move. Refusing to follow redirects turns such a response into an ordinary non-success
        // status the caller can report, rather than a request to somewhere unforeseen.
        SocketsHttpHandler handler = ProviderRowTransport.CreateNoRedirectHandler(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));

        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(provider.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (apiKey != NoKeyPlaceholder)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return new FishAudioSpeechSynthesisClient(httpClient, provider.ModelName);
    }
}
