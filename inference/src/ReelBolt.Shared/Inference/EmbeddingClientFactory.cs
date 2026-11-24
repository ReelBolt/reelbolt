using System.ClientModel;
using System.Collections.Concurrent;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs embedding generators for Azure OpenAI deployments and OpenAI-compatible servers
/// (text-embeddings-inference, vLLM, Ollama, LiteLLM, ...), caching by
/// <see cref="ResolvedEmbeddingProvider.CacheKey"/>. Reuses the existing <c>OpenAI</c> /
/// <c>Azure.AI.OpenAI</c> packages (<c>AsIEmbeddingGenerator</c>), so no new dependency enters the
/// Microsoft.Extensions.AI set that has to move together.
/// </summary>
public sealed class EmbeddingClientFactory : IEmbeddingClientFactory
{
    private const string NoKeyPlaceholder = "not-required";

    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _clients = new();

    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<EmbeddingClientFactory>? _logger;
    private readonly LocalRunnerGatewayOptions _localRunnerGateway;

    /// <param name="recorder">When supplied, every generator handed out meters its tokens (B3c); null returns it raw.</param>
    public EmbeddingClientFactory(IUsageRecorder? recorder = null, ILogger<EmbeddingClientFactory>? logger = null,
        Microsoft.Extensions.Options.IOptions<LocalRunnerGatewayOptions>? localRunnerGateway = null)
    {
        _recorder = recorder;
        _logger = logger;
        _localRunnerGateway = localRunnerGateway?.Value ?? new LocalRunnerGatewayOptions();
    }

    public IEmbeddingGenerator<string, Embedding<float>> Get(ResolvedEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // A LocalRunner client is addressed by organization + device, and the organization is not part of CacheKey
        // (which must stay unchanged for every other kind), so it is built per call and never cached.
        IEmbeddingGenerator<string, Embedding<float>> inner = provider.Kind == InferenceProviderKind.LocalRunner
            ? BuildLocalRunner(provider, _localRunnerGateway, null, _logger)
            : _clients.GetOrAdd(
                ProviderRowTransport.CacheKey(provider.CacheKey, ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)),
                _ => Build(provider));

        // Wrapped per call, outside the cache: the owning organization is not part of CacheKey.
        return _recorder is null ? inner : new MeteringEmbeddingGenerator(inner, provider, _recorder, _logger);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> Build(ResolvedEmbeddingProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.AzureOpenAI => new AzureOpenAIClient(
                new Uri(provider.Endpoint),
                new ApiKeyCredential(provider.ApiKey),
                Guarded(provider, new AzureOpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds) }))
            .GetEmbeddingClient(provider.ModelName)
            .AsIEmbeddingGenerator(),

        InferenceProviderKind.OpenAICompatible => new OpenAIClient(
                new ApiKeyCredential(string.IsNullOrWhiteSpace(provider.ApiKey) ? NoKeyPlaceholder : provider.ApiKey),
                Guarded(provider, new OpenAIClientOptions
                {
                    Endpoint = new Uri(provider.Endpoint.TrimEnd('/') + "/"),
                    NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
                }))
            .GetEmbeddingClient(provider.ModelName)
            .AsIEmbeddingGenerator(),

        InferenceProviderKind.Anthropic => throw Unsupported(provider,
            "Anthropic exposes no embeddings API"),
        InferenceProviderKind.DeepSeek => throw Unsupported(provider,
            "DeepSeek's API documents only chat models and has no embeddings endpoint"),

        _ => throw Unsupported(provider, "this provider kind does not serve embeddings")
    };

    /// <summary>An org-owned row's SDK client dials public addresses only; a platform row keeps the SDK transport.</summary>
    private static TOptions Guarded<TOptions>(ResolvedEmbeddingProvider provider, TOptions options)
        where TOptions : System.ClientModel.Primitives.ClientPipelineOptions
    {
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        return options;
    }

    /// <summary>
    /// Embeddings from a model on the user's own machine, relayed through the runner gateway: the OpenAI embedding
    /// client over the same relay base address and bearer as <see cref="ChatClientFactory.BuildLocalRunner"/>, with
    /// gateway failures mapped to <see cref="LocalRunnerUnavailableException"/>. Never falls back to another provider.
    /// </summary>
    /// <param name="handler">Test seam only: replaces the HTTP handler. Null in production.</param>
    internal static IEmbeddingGenerator<string, Embedding<float>> BuildLocalRunner(
        ResolvedEmbeddingProvider provider, LocalRunnerGatewayOptions gateway, HttpMessageHandler? handler = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (provider.OrganizationId is not { } organizationId)
        {
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' (kind LocalRunner) has no owning organization; a local model is always an organization's own.");
        }

        if (!Guid.TryParse(provider.Endpoint?.Trim(), out Guid deviceId))
        {
            throw new LocalRunnerUnavailableException(
                LocalRunnerUnavailableException.ReasonFor(System.Net.HttpStatusCode.NotFound, "device_not_found"), "device_not_found");
        }

        string? token = gateway.ResolveInternalToken();
        if (token is null)
        {
            throw new LocalRunnerUnavailableException(
                LocalRunnerUnavailableException.ReasonFor(System.Net.HttpStatusCode.ServiceUnavailable, "internal_api_disabled"),
                "internal_api_disabled");
        }

        gateway.Tls.EnsureTransportAllowed();
        handler ??= gateway.Tls.GetHandler();

        OpenAIClientOptions options = new()
        {
            Endpoint = gateway.RelayBaseAddress(organizationId, deviceId),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)
        };
        if (handler is not null)
        {
            options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler, disposeHandler: false));
        }

        OpenAIClient client = new(new ApiKeyCredential(token), options);
        return new LocalRunnerEmbeddingGenerator(client.GetEmbeddingClient(provider.ModelName).AsIEmbeddingGenerator(), logger);
    }

    private static NotSupportedException Unsupported(ResolvedEmbeddingProvider provider, string why) =>
        new($"Inference provider '{provider.Name}' (kind {provider.Kind}) cannot serve the Embedding capability — {why}. " +
            "Use a provider of kind AzureOpenAI, OpenAICompatible or LocalRunner (for example the bundled `embeddings` service).");
}
