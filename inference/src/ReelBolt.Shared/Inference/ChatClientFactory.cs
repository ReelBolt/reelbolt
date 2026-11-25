using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Anthropic;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Constructs <see cref="IChatClient"/> instances for an Azure OpenAI, an OpenAI-compatible
/// (vLLM, LM Studio, llama.cpp server, LiteLLM, ...), or an Anthropic backend, and caches
/// them by <see cref="ResolvedInferenceProvider.CacheKey"/> so repeated resolutions of the
/// same configuration reuse the same client/HttpClient.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    /// <summary>
    /// <see cref="ApiKeyCredential"/> throws on a null/empty string, but OpenAI-compatible
    /// self-hosted deployments (vLLM in particular) commonly run with no key at all.
    /// Substituting a placeholder here is mandatory, not cosmetic (Risk R5).
    /// </summary>
    private const string NoKeyPlaceholder = "not-required";

    /// <summary>
    /// The default ceiling on generated tokens for an Anthropic request. No AGENT run sets
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.MaxOutputTokens"/> (<c>BuildChatOptions</c>
    /// sets only Temperature/TopP/TopK), so this is what every agent run actually gets; the
    /// provider-test endpoints do set it per request, and that still overrides this.
    /// <para>
    /// Sized for the largest structured outputs the workflow engine produces (video edit decisions,
    /// motion-graphics plans) while staying well under the point where a NON-streaming request
    /// risks an HTTP timeout — agents run through <c>AIAgent.RunAsync</c>, which does not stream.
    /// It also leaves room for any reasoning the model does on its own: ReelBolt sends no
    /// reasoning configuration, and whatever thinking tokens the model spends count against this
    /// same ceiling.
    /// </para>
    /// </summary>
    private const int AnthropicDefaultMaxOutputTokens = 16_384;

    /// <summary>
    /// Anthropic OAuth tokens (as minted by <c>claude setup-token</c>) carry an <c>oat</c> —
    /// "OAuth token" — marker in their prefix and must travel as an <c>Authorization: Bearer</c>
    /// header, whereas a normal console API key travels as <c>x-api-key</c>. Sending either down
    /// the other path fails authentication, and the stored column cannot tell them apart on its
    /// own, so the prefix is the discriminator. See docs/anthropic-provider.md.
    /// </summary>
    private const string AnthropicOAuthTokenPrefix = "sk-ant-oat";

    /// <summary>
    /// <c>anthropic-beta</c> flag required on every Bearer-authenticated (OAuth) request — without
    /// it Anthropic rejects the token outright with "OAuth access token is invalid". See
    /// <see cref="AnthropicOAuthTokenPrefix"/>'s remarks.
    /// </summary>
    private const string AnthropicOAuthBetaFlag = "oauth-2025-04-20";

    /// <summary>
    /// The second <c>anthropic-beta</c> flag the working reproduction in
    /// https://github.com/anthropics/claude-code/issues/40515 sent alongside
    /// <see cref="AnthropicOAuthBetaFlag"/>. Sent only when a provider row has opted into
    /// <see cref="ReelBolt.Shared.Data.Models.InferenceProvider.PersonalOAuthMode"/> — ReelBolt's
    /// own harness is not actually Claude Code, so this is never sent unasked.
    /// </summary>
    private const string ClaudeCodeBetaFlag = "claude-code-20250219";

    /// <summary>
    /// <c>user-agent</c> value from the same working reproduction. Anthropic ties its undocumented
    /// OAuth system-prompt validation (see <see cref="AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt"/>)
    /// to traffic that otherwise looks like the Claude Code CLI; this is part of presenting as that
    /// traffic. Pinned to the exact string the issue confirmed working — may need bumping if
    /// Anthropic's validation becomes version-sensitive.
    /// </summary>
    private const string ClaudeCliUserAgent = "claude-cli/2.1.85 (external, cli)";

    /// <summary>
    /// Explicit opt-in to the Anthropic SDK's own credential resolution (<c>ANTHROPIC_API_KEY</c> /
    /// <c>ANTHROPIC_AUTH_TOKEN</c> / an <c>ant auth login</c> profile) instead of a key stored on
    /// the provider row. Store this literal as the row's API key to use it.
    /// </summary>
    /// <remarks>
    /// A sentinel rather than "an empty key means ambient", because that inference fails open in
    /// three ways that all look identical from the admin UI: a key saved as whitespace, a Data
    /// Protection key-ring mismatch (<c>InferenceProviderResolver</c> deliberately degrades a
    /// failed decrypt to an empty string rather than throwing), and a genuinely blank field. Under
    /// the inference, each of those silently reroutes billing to whatever credential happens to be
    /// in the container's environment — and since executing a workflow needs no admin rights, any
    /// authenticated user could then spend it. <c>AnthropicClient.ShouldAutoResolveCredentials</c>
    /// is get-only, so the SDK's fallback cannot simply be turned off; making the intent explicit
    /// here is what separates "the operator asked for ambient credentials" from "something went
    /// wrong with the stored key".
    /// </remarks>
    public const string AnthropicAmbientCredentialSentinel = "env:";

    /// <summary>
    /// Default base URL for Google Gemini's official OpenAI-compatible endpoint.
    /// </summary>
    public const string GeminiDefaultEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    /// <summary>
    /// Explicit opt-in to ambient environment variable resolution (<c>GEMINI_API_KEY</c> or
    /// <c>GOOGLE_API_KEY</c>) instead of a key stored on the provider row.
    /// Store this literal as the row's API key to use it.
    /// </summary>
    public const string GeminiAmbientCredentialSentinel = "env:";

    /// <summary>
    /// Default base URL for DeepSeek's official OpenAI-compatible endpoint.
    /// </summary>
    public const string DeepSeekDefaultEndpoint = "https://api.deepseek.com/";

    /// <summary>
    /// Explicit opt-in to ambient environment variable resolution (<c>DEEPSEEK_API_KEY</c>)
    /// instead of a key stored on the provider row.
    /// Store this literal as the row's API key to use it.
    /// </summary>
    public const string DeepSeekAmbientCredentialSentinel = "env:";

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<ChatClientFactory>? _logger;
    private readonly LocalRunnerGatewayOptions _localRunnerGateway;

    /// <param name="recorder">
    /// When supplied, every client handed out is wrapped in a <see cref="MeteringChatClient"/> (B3b). Null
    /// (unit tests that build a factory by hand) returns the raw client.
    /// </param>
    /// <param name="localRunnerGateway">
    /// Where the runner gateway's relay lives, for <see cref="InferenceProviderKind.LocalRunner"/> rows. Null reads the
    /// defaults (and <c>RUNNER_INTERNAL_TOKEN</c>); every host that can serve such a row registers it.
    /// </param>
    public ChatClientFactory(
        IUsageRecorder? recorder = null,
        ILogger<ChatClientFactory>? logger = null,
        Microsoft.Extensions.Options.IOptions<LocalRunnerGatewayOptions>? localRunnerGateway = null)
    {
        _recorder = recorder;
        _logger = logger;
        _localRunnerGateway = localRunnerGateway?.Value ?? new LocalRunnerGatewayOptions();
    }

    public IChatClient Get(ResolvedInferenceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        IChatClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ =>
        {
            if (UsesOpenAIResponsesApi(provider))
            {
                _logger?.LogInformation(
                    "Chat client for provider {Provider} ({Model}) built on the OpenAI Responses API at {Host}, store=false",
                    provider.Name.ForLog(), provider.ModelName.ForLog(), OpenAIPlatformHost);
            }

            return Build(provider);
        });

        // The wrapper is per call, not cached with the client: attribution (provider row, owning organization)
        // is not part of CacheKey, so two rows with the same wiring share a client but must not share an owner.
        return _recorder is null ? inner : new MeteringChatClient(inner, provider, _recorder, _logger);
    }

    private IChatClient Build(ResolvedInferenceProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.AzureOpenAI => BuildAzureOpenAI(provider),
        InferenceProviderKind.OpenAICompatible => BuildOpenAICompatible(provider),
        InferenceProviderKind.Anthropic => BuildAnthropic(provider),
        InferenceProviderKind.Gemini => BuildGemini(provider),
        InferenceProviderKind.DeepSeek => BuildDeepSeek(provider),
        InferenceProviderKind.LocalRunner => BuildLocalRunner(provider, _localRunnerGateway, null, _logger),
        _ => throw new NotSupportedException($"Unsupported inference provider kind '{provider.Kind}'.")
    };

    private static IChatClient BuildAzureOpenAI(ResolvedInferenceProvider provider)
    {
        AzureOpenAIClientOptions options = new()
        {
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        AzureOpenAIClient client = new(
            new Uri(provider.Endpoint),
            new ApiKeyCredential(provider.ApiKey),
            options);

        return client.GetChatClient(provider.ModelName).AsIChatClient();
    }

    /// <summary>
    /// The one host whose OpenAI-compatible rows are built on the Responses API instead of Chat
    /// Completions. OpenAI's Chat Completions endpoint refuses function tools together with
    /// <c>reasoning_effort</c> for its newer reasoning models ("use /v1/responses or set reasoning_effort
    /// to 'none'"), and every agent run sends both.
    /// </summary>
    internal const string OpenAIPlatformHost = "api.openai.com";

    /// <summary>True when <paramref name="provider"/> is an OpenAI-compatible row pointed at exactly <see cref="OpenAIPlatformHost"/> over https.</summary>
    internal static bool UsesOpenAIResponsesApi(ResolvedInferenceProvider provider) =>
        provider.Kind == InferenceProviderKind.OpenAICompatible
        && Uri.TryCreate(provider.Endpoint?.Trim(), UriKind.Absolute, out Uri? endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && string.Equals(endpoint.Host, OpenAIPlatformHost, StringComparison.OrdinalIgnoreCase);

    /// <param name="transport">Test seam only: replaces the HTTP transport. Null in production.</param>
    internal static IChatClient BuildOpenAICompatible(
        ResolvedInferenceProvider provider, System.ClientModel.Primitives.PipelineTransport? transport = null)
    {
        string apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? NoKeyPlaceholder : provider.ApiKey;

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(provider.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (transport is not null)
        {
            options.Transport = transport;
        }
        else if (ProviderRowTransport.CreateSdkTransport(
                     ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } guarded)
        {
            options.Transport = guarded;
        }

        OpenAIClient client = new(
            new ApiKeyCredential(apiKey),
            options);

#pragma warning disable OPENAI001 // ResponsesClient is marked experimental in OpenAI 2.x.
        if (!UsesOpenAIResponsesApi(provider))
        {
            return client.GetChatClient(provider.ModelName).AsIChatClient();
        }

        // Outermost: drop the sampling parameters OpenAI refuses while reasoning is on.
        return new ReasoningEffortChatClient(
            new OpenAIResponsesStatelessChatClient(client.GetResponsesClient().AsIChatClient(provider.ModelName)),
            InferenceProviderKind.OpenAICompatible,
            openAIPlatform: true);
#pragma warning restore OPENAI001
    }

    /// <summary>
    /// A model on the user's own machine, reached through the runner gateway's relay: the normal OpenAI-compatible
    /// chat client, pointed at <c>{gateway}/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1</c> and authenticated
    /// with the gateway's shared service token (the stored key is ignored; a LocalRunner row has none). The
    /// organization comes from the provider row, never from a request, and a gateway refusal surfaces as
    /// <see cref="LocalRunnerUnavailableException"/>. Retries are off: a relayed call is not safe to repeat blindly,
    /// and there is no other provider to fall back to.
    /// </summary>
    /// <param name="handler">Test seam only: replaces the HTTP handler. Null in production.</param>
    internal static IChatClient BuildLocalRunner(
        ResolvedInferenceProvider provider, LocalRunnerGatewayOptions gateway, HttpMessageHandler? handler = null, ILogger? logger = null)
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
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
        if (handler is not null)
        {
            options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler, disposeHandler: false));
        }

        OpenAIClient client = new(new ApiKeyCredential(token), options);
        return new LocalRunnerChatClient(client.GetChatClient(provider.ModelName).AsIChatClient(), logger);
    }

    /// <summary>
    /// Builds a client against Anthropic's first-party Messages API through the official
    /// <c>Anthropic</c> SDK. Unlike the two OpenAI arms this is not a Chat Completions endpoint,
    /// so the result is wrapped in <see cref="AnthropicChatOptionsAdapter"/> — see that type for
    /// why an agent's sampling parameters and OpenAI-typed raw options must not reach it.
    /// </summary>
    private static IChatClient BuildAnthropic(ResolvedInferenceProvider provider)
    {
        string key = provider.ApiKey?.Trim() ?? string.Empty;
        bool useAmbient = string.Equals(key, AnthropicAmbientCredentialSentinel, StringComparison.OrdinalIgnoreCase);

        // Fail loudly rather than falling back to whatever credential happens to sit in the
        // container's environment. An empty key here is not "no credential needed" — unlike the
        // OpenAI-compatible arm, where a keyless self-hosted vLLM is a normal deployment — it means
        // the row is misconfigured, or its stored key could not be decrypted (see
        // AnthropicAmbientCredentialSentinel). Silently succeeding on someone else's account is a
        // far worse outcome than an error naming the provider.
        if (!useAmbient && key.Length == 0)
        {
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' (kind Anthropic) has no usable API key. Set " +
                $"one on the provider, or store the literal '{AnthropicAmbientCredentialSentinel}' " +
                "as its key to deliberately use the ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN " +
                "environment variables instead. If a key IS configured, it failed to decrypt — " +
                "check that both services share the same DataProtection:KeysPath (the `dpkeys` " +
                "volume).");
        }

        bool isOAuthToken = key.StartsWith(AnthropicOAuthTokenPrefix, StringComparison.OrdinalIgnoreCase);

        Anthropic.Core.ClientOptions options = new()
        {
            // At most one of these is set. Both are null only under the explicit ambient sentinel,
            // where the SDK resolves credentials itself. See docs/anthropic-provider.md.
            ApiKey = !useAmbient && !isOAuthToken ? key : null,
            AuthToken = !useAmbient && isOAuthToken ? key : null,

            // Null means the SDK's production default (https://api.anthropic.com). The API requires
            // a non-empty endpoint on create, so in practice this is always set; the null branch
            // covers a row written directly to the database or the legacy config fallback.
            BaseUrl = string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint.Trim(),

            Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds),

            ExtraHeaders = BuildAnthropicExtraHeaders(!useAmbient && isOAuthToken, provider.PersonalOAuthMode)
        };

        // An org-owned row dials public addresses only; the SDK keeps enforcing its own Timeout.
        if (ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged))
        {
            options.HttpClient = ProviderRowTransport.CreateSdkHttpClient();
        }

        AnthropicClient client = new(options);

        // The adapter takes ownership of `client`: the SDK's own IChatClient wrapper does not
        // dispose the AnthropicClient it wraps (its Dispose is an empty method), so without this
        // the client — an IDisposable holding an HttpClient — would never be released.
        return new AnthropicChatOptionsAdapter(
            client.AsIChatClient(provider.ModelName, AnthropicDefaultMaxOutputTokens),
            client,
            provider.PersonalOAuthMode);
    }

    /// <summary>
    /// Builds a client against Google Gemini's official OpenAI compatibility endpoint
    /// (<c>https://generativelanguage.googleapis.com/v1beta/openai/</c>) through the official
    /// <see cref="OpenAIClient"/>. Gemini maps standard OpenAI Chat Completions parameters
    /// (including <c>reasoning_effort</c>) natively.
    /// </summary>
    private static IChatClient BuildGemini(ResolvedInferenceProvider provider)
    {
        string key = provider.ApiKey?.Trim() ?? string.Empty;
        bool useAmbient = string.Equals(key, GeminiAmbientCredentialSentinel, StringComparison.OrdinalIgnoreCase);

        string apiKey;
        if (useAmbient)
        {
            string? envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (string.IsNullOrWhiteSpace(envKey))
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind Gemini) has key '{GeminiAmbientCredentialSentinel}', " +
                    "but neither the GEMINI_API_KEY nor GOOGLE_API_KEY environment variable is set.");
            }
            apiKey = envKey.Trim();
        }
        else
        {
            // Fail loudly rather than silently proceeding with an empty key or falling back unasked.
            if (key.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind Gemini) has no usable API key. Set " +
                    $"one on the provider, or store the literal '{GeminiAmbientCredentialSentinel}' " +
                    "as its key to deliberately use the GEMINI_API_KEY / GOOGLE_API_KEY " +
                    "environment variables instead. If a key IS configured, it failed to decrypt — " +
                    "check that both services share the same DataProtection:KeysPath (the `dpkeys` " +
                    "volume).");
            }
            apiKey = key;
        }

        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? GeminiDefaultEndpoint
            : provider.Endpoint.TrimEnd('/') + "/";

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        OpenAIClient client = new(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(provider.ModelName).AsIChatClient();
    }

    /// <summary>
    /// Builds a client against DeepSeek's official OpenAI compatibility endpoint
    /// (<c>https://api.deepseek.com/</c>) through the official <see cref="OpenAIClient"/>.
    /// </summary>
    private static IChatClient BuildDeepSeek(ResolvedInferenceProvider provider)
    {
        string key = provider.ApiKey?.Trim() ?? string.Empty;
        bool useAmbient = string.Equals(key, DeepSeekAmbientCredentialSentinel, StringComparison.OrdinalIgnoreCase);

        string apiKey;
        if (useAmbient)
        {
            string? envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrWhiteSpace(envKey))
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind DeepSeek) has key '{DeepSeekAmbientCredentialSentinel}', " +
                    "but the DEEPSEEK_API_KEY environment variable is not set.");
            }
            apiKey = envKey.Trim();
        }
        else
        {
            // Fail loudly rather than silently proceeding with an empty key or falling back unasked.
            if (key.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind DeepSeek) has no usable API key. Set " +
                    $"one on the provider, or store the literal '{DeepSeekAmbientCredentialSentinel}' " +
                    "as its key to deliberately use the DEEPSEEK_API_KEY " +
                    "environment variable instead. If a key IS configured, it failed to decrypt — " +
                    "check that both services share the same DataProtection:KeysPath (the `dpkeys` " +
                    "volume).");
            }
            apiKey = key;
        }

        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? DeepSeekDefaultEndpoint
            : provider.Endpoint.TrimEnd('/') + "/";

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        OpenAIClient client = new(new ApiKeyCredential(apiKey), options);

        // DeepSeek's thinking is ON by default and is switched off by a body field, not by
        // reasoning_effort, so an effort of "none" needs ReasoningEffortChatClient to add it.
        return new ReasoningEffortChatClient(
            client.GetChatClient(provider.ModelName).AsIChatClient(),
            InferenceProviderKind.DeepSeek);
    }

    /// <summary>
    /// Builds the extra headers an Anthropic request needs, combining two independent concerns:
    /// <paramref name="sendOAuthBeta"/> (the transport-mechanics requirement — a Bearer-
    /// authenticated OAuth token is rejected outright without <see cref="AnthropicOAuthBetaFlag"/>,
    /// see <see cref="AnthropicOAuthTokenPrefix"/>'s remarks) and <paramref name="personalOAuthMode"/>
    /// (the operator's explicit "Dev/OAuth (Personal use only)" opt-in, which adds
    /// <see cref="ClaudeCodeBetaFlag"/> plus the <c>user-agent</c>/<c>x-app</c> pair — see
    /// <see cref="ReelBolt.Shared.Data.Models.InferenceProvider.PersonalOAuthMode"/>). The two
    /// betas combine into one comma-separated header when both apply, matching the exact header
    /// shape the working reproduction in
    /// https://github.com/anthropics/claude-code/issues/40515 used. Returns <c>null</c> — not an
    /// empty dictionary — when neither concern applies, to keep <c>ClientOptions.ExtraHeaders</c>
    /// byte-identical to before this flag existed for every provider that hasn't opted in.
    /// </summary>
    private static Dictionary<string, string>? BuildAnthropicExtraHeaders(bool sendOAuthBeta, bool personalOAuthMode)
    {
        List<string> betaFlags = new();
        if (personalOAuthMode)
            betaFlags.Add(ClaudeCodeBetaFlag);
        if (sendOAuthBeta)
            betaFlags.Add(AnthropicOAuthBetaFlag);

        if (betaFlags.Count == 0 && !personalOAuthMode)
            return null;

        Dictionary<string, string> headers = new();
        if (betaFlags.Count > 0)
        {
            headers["anthropic-beta"] = string.Join(',', betaFlags);
        }

        if (personalOAuthMode)
        {
            headers["user-agent"] = ClaudeCliUserAgent;
            headers["x-app"] = "cli";
        }

        return headers;
    }
}

/// <summary>
/// Keeps every call on the OpenAI Responses API stateless.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>store: false</c> on every request, so nothing is kept server side and the Microsoft.Extensions.AI
/// adapter returns no <see cref="ChatResponse.ConversationId"/>. Callers therefore keep resending the full
/// history, exactly as on Chat Completions, and no <c>previous_response_id</c> is ever sent.</item>
/// <item>With reasoning on (an effort other than <c>none</c>), <c>include: ["reasoning.encrypted_content"]</c>,
/// so each reasoning item comes back with its encrypted state and can be replayed between tool calls under
/// <c>store: false</c>. It is not asked for otherwise, because OpenAI refuses that include for models that
/// do not reason.</item>
/// <item>A reasoning item in the history that carries no encrypted state (reasoning left at the model's
/// default) is dropped before sending: under <c>store: false</c> the server cannot resolve it by id.</item>
/// </list>
/// A <c>CreateResponseOptions</c> the caller already supplied through
/// <see cref="ChatOptions.RawRepresentationFactory"/> is kept and extended, never replaced.
/// </remarks>
public sealed class OpenAIResponsesStatelessChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(DropUnreplayableReasoning(messages), Stateless(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(DropUnreplayableReasoning(messages), Stateless(options), cancellationToken);

    internal static ChatOptions Stateless(ChatOptions? options)
    {
        ChatOptions stateless = options?.Clone() ?? new ChatOptions();
        bool reasoningOn = stateless.Reasoning?.Effort is ReasoningEffort effort && effort != ReasoningEffort.None;
        Func<IChatClient, object?>? inherited = stateless.RawRepresentationFactory;

#pragma warning disable OPENAI001 // CreateResponseOptions is marked experimental in OpenAI 2.x.
        stateless.RawRepresentationFactory = client =>
        {
            OpenAI.Responses.CreateResponseOptions raw =
                inherited?.Invoke(client) as OpenAI.Responses.CreateResponseOptions ?? new();
            raw.StoredOutputEnabled = false;
            if (reasoningOn && !raw.IncludedProperties.Contains(OpenAI.Responses.IncludedResponseProperty.ReasoningEncryptedContent))
            {
                raw.IncludedProperties.Add(OpenAI.Responses.IncludedResponseProperty.ReasoningEncryptedContent);
            }

            return raw;
        };
#pragma warning restore OPENAI001

        return stateless;
    }

    internal static IEnumerable<ChatMessage> DropUnreplayableReasoning(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage>? rewritten = null;
        int index = 0;
        foreach (ChatMessage message in messages)
        {
            if (message.Contents.Any(IsUnreplayableReasoning))
            {
                rewritten ??= messages.Take(index).ToList();
                ChatMessage copy = message.Clone();
                copy.Contents = message.Contents.Where(c => !IsUnreplayableReasoning(c)).ToList();
                rewritten.Add(copy);
            }
            else
            {
                rewritten?.Add(message);
            }

            index++;
        }

        return rewritten ?? messages;
    }

    private static bool IsUnreplayableReasoning(AIContent content)
    {
        if (content is not TextReasoningContent reasoning || !string.IsNullOrEmpty(reasoning.ProtectedData))
        {
            return false;
        }

#pragma warning disable OPENAI001
        return reasoning.RawRepresentation is not OpenAI.Responses.ReasoningResponseItem item
            || string.IsNullOrEmpty(item.EncryptedContent);
#pragma warning restore OPENAI001
    }
}
