using System.ClientModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenAI;
using OpenAI.Chat;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

// ReelBolt.WorkflowEngine declares the same friend assembly in Properties/AssemblyInfo.cs. This
// file is the only place in ReelBolt.Shared that needs one, and it is declared here rather than
// in a new Properties/AssemblyInfo.cs so the test seam stays next to the single internal member it
// serves — DecisionClientFactory.SelectApiKey, whose credential rule is meant to be asserted
// directly instead of inferred from a constructed HttpClient.
[assembly: InternalsVisibleTo("ReelBolt.WorkflowEngine.Tests")]

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Internal interface for providing chat completions to LogprobDecisionClient.
/// Used to enable testability by allowing injection of fake completions.
/// </summary>
internal interface IOpenAIChatCompletion
{
    Task<ChatCompletion> GetCompletionAsync(
        string prompt,
        ChatCompletionOptions options,
        CancellationToken ct);
}

/// <summary>
/// Constructs <see cref="IDecisionClient"/> instances for a TypeSafe backend or an OpenAI-compatible
/// backend (Azure OpenAI, OpenAI-compatible, DeepSeek) and caches them by
/// <see cref="ResolvedDecisionProvider.CacheKey"/> so repeated resolutions of the same configuration
/// reuse the same client/HttpClient. Mirrors <see cref="ChatClientFactory"/> exactly.
/// </summary>
public sealed class DecisionClientFactory : IDecisionClientFactory
{
    /// <summary>
    /// <see cref="ApiKeyCredential"/> throws on a null/empty string, but OpenAI-compatible
    /// self-hosted deployments (vLLM in particular) commonly run with no key at all.
    /// Same convention as <see cref="ChatClientFactory"/>'s NoKeyPlaceholder.
    /// </summary>
    private const string NoKeyPlaceholder = "not-required";

    private readonly ConcurrentDictionary<string, IDecisionClient> _clients = new();
    private readonly IUsageRecorder? _recorder;
    private readonly ILogger<DecisionClientFactory>? _logger;

    /// <param name="recorder">When supplied, decision-call tokens are metered (B3b); null returns the raw client.</param>
    public DecisionClientFactory(IUsageRecorder? recorder = null, ILogger<DecisionClientFactory>? logger = null)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public IDecisionClient Get(ResolvedDecisionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        bool orgOwned = ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged);
        IDecisionClient inner = _clients.GetOrAdd(ProviderRowTransport.CacheKey(provider.CacheKey, orgOwned), _ => Build(provider));
        return _recorder is null ? inner : new MeteringDecisionClient(inner, provider, _recorder, _logger);
    }

    private static IDecisionClient Build(ResolvedDecisionProvider provider) => provider.Kind switch
    {
        InferenceProviderKind.TypeSafe => BuildTypeSafe(provider),
        InferenceProviderKind.OpenJev => BuildOpenJev(provider),
        InferenceProviderKind.AzureOpenAI => BuildAzureOpenAI(provider),
        InferenceProviderKind.OpenAICompatible => BuildOpenAICompatible(provider),
        InferenceProviderKind.DeepSeek => BuildDeepSeek(provider),

        InferenceProviderKind.Anthropic => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is an Anthropic provider, which cannot serve " +
            "the Decision capability."),

        InferenceProviderKind.Gemini => throw new NotSupportedException(
            $"Inference provider '{provider.Name}' is a Gemini provider, which cannot serve " +
            "the Decision capability."),

        _ => throw new NotSupportedException($"Unsupported inference provider kind '{provider.Kind}'.")
    };

    private static IDecisionClient BuildTypeSafe(ResolvedDecisionProvider provider)
    {
        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? new Uri("https://api.typesafe.ai/")
            : new Uri(provider.Endpoint.TrimEnd('/') + "/");
        httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);

        // Set Authorization header
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

        return new TypeSafeDecisionClient(httpClient, provider.ModelName);
    }

    /// <summary>
    /// Builds the client for a self-hosted System One deployment (Open-Jev, the <c>vllm-jev</c>
    /// plugin, Kev). Deliberately the <em>same client class</em> as
    /// <see cref="BuildTypeSafe"/>: every one of those backends implements the identical
    /// <c>POST v1/systemone</c> request <see cref="TypeSafeDecisionClient"/> already issues, so a
    /// second client would be a copy with nothing in it to differ — only the base address and the
    /// bearer token come from somewhere else, and both are provider-row data.
    /// </summary>
    private static IDecisionClient BuildOpenJev(ResolvedDecisionProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            // Unlike TypeSafe, there is no first-party host to fall back to: a self-hosted
            // endpoint lives wherever the operator put it, and quietly defaulting to a vendor URL
            // would post an agent's decision prompt — and the row's credential — at a host that
            // has nothing to do with this provider.
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' (kind OpenJev) has no endpoint. An " +
                "Open-Jev / Kev / vllm-jev deployment is self-hosted, so there is no vendor URL " +
                "to fall back to — set the provider's endpoint explicitly.");
        }

        if (!Uri.TryCreate(provider.Endpoint.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Inference provider '{provider.Name}' (kind OpenJev) has endpoint " +
                $"'{provider.Endpoint}', which is not an absolute http(s) URL.");
        }

        HttpClient httpClient = ProviderRowTransport.CreateHttpClient(
            ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged));
        httpClient.BaseAddress = baseAddress;
        httpClient.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);

        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SelectApiKey(provider.ApiKey));

        return new TypeSafeDecisionClient(httpClient, provider.ModelName);
    }

    /// <summary>
    /// The bearer token a self-hosted decision provider sends: the stored key, verbatim, with the
    /// shared "no key required" placeholder standing in when there is none. Extracted so the
    /// policy is directly assertable in a test rather than only observable by driving a real
    /// request — it is the whole reason this kind is safe to point at a box that may sit next to
    /// other credentials.
    /// <para>
    /// An Open-Jev / Kev / vllm-jev deployment is often loopback-only and unauthenticated, so a
    /// blank key is a legitimate configuration rather than an error. It must never resolve to an
    /// ambient <c>OPENJEV_API_KEY</c> from the container's environment: executing a workflow needs
    /// no admin rights, so an environment fallback would let any authenticated user spend a
    /// credential the operator never attached to this row. There is deliberately no <c>env:</c>
    /// sentinel for this kind either — the DeepSeek arm's ambient mode is a first-party API that
    /// is unusable without a key, which is not this case.
    /// </para>
    /// </summary>
    internal static string SelectApiKey(string? storedKey) =>
        string.IsNullOrWhiteSpace(storedKey) ? NoKeyPlaceholder : storedKey;

    private static IDecisionClient BuildAzureOpenAI(ResolvedDecisionProvider provider)
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

        OpenAI.Chat.ChatClient chatClient = client.GetChatClient(provider.ModelName);
        return new LogprobDecisionClient(chatClient, provider.ModelName);
    }

    private static IDecisionClient BuildOpenAICompatible(ResolvedDecisionProvider provider)
    {
        string apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? NoKeyPlaceholder : provider.ApiKey;

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(provider.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(provider.TimeoutSeconds)
        };
        if (ProviderRowTransport.CreateSdkTransport(
                ProviderRowTransport.IsOrgOwned(provider.OrganizationId, provider.IsPlatformManaged)) is { } transport)
        {
            options.Transport = transport;
        }

        OpenAIClient client = new(
            new ApiKeyCredential(apiKey),
            options);

        OpenAI.Chat.ChatClient chatClient = client.GetChatClient(provider.ModelName);
        return new LogprobDecisionClient(chatClient, provider.ModelName);
    }

    private static IDecisionClient BuildDeepSeek(ResolvedDecisionProvider provider)
    {
        string key = provider.ApiKey?.Trim() ?? string.Empty;
        bool useAmbient = string.Equals(key, ChatClientFactory.DeepSeekAmbientCredentialSentinel, StringComparison.OrdinalIgnoreCase);

        string apiKey;
        if (useAmbient)
        {
            string? envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrWhiteSpace(envKey))
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind DeepSeek) has key '{ChatClientFactory.DeepSeekAmbientCredentialSentinel}', " +
                    "but the DEEPSEEK_API_KEY environment variable is not set.");
            }
            apiKey = envKey.Trim();
        }
        else
        {
            if (key.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Inference provider '{provider.Name}' (kind DeepSeek) has no usable API key. Set " +
                    $"one on the provider, or store the literal '{ChatClientFactory.DeepSeekAmbientCredentialSentinel}' " +
                    "as its key to deliberately use the DEEPSEEK_API_KEY " +
                    "environment variable instead. If a key IS configured, it failed to decrypt — " +
                    "check that both services share the same DataProtection:KeysPath (the `dpkeys` " +
                    "volume).");
            }
            apiKey = key;
        }

        string endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? ChatClientFactory.DeepSeekDefaultEndpoint
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
        OpenAI.Chat.ChatClient chatClient = client.GetChatClient(provider.ModelName);
        return new LogprobDecisionClient(chatClient, provider.ModelName);
    }
}

/// <summary>
/// Answers a <see cref="DecisionQuestion"/> by requesting a single-token completion with
/// top-logprobs from an OpenAI-compatible chat backend (Azure OpenAI, OpenAI-compatible,
/// DeepSeek) and reading the offered-label probabilities off the returned distribution.
/// </summary>
internal sealed class LogprobDecisionClient : IDecisionClient
{
    private readonly IOpenAIChatCompletion _chatCompletion;
    private readonly string _modelName;
    private readonly int _topLogProbabilityCount;

    /// <summary>
    /// Constructor for production use with a ChatClient.
    /// </summary>
    public LogprobDecisionClient(ChatClient chatClient, string modelName, int topLogProbabilityCount = 8)
        : this(new ChatClientAdapter(chatClient), modelName, topLogProbabilityCount)
    {
    }

    /// <summary>
    /// Constructor for internal/test use with IOpenAIChatCompletion interface.
    /// </summary>
    internal LogprobDecisionClient(IOpenAIChatCompletion chatCompletion, string modelName, int topLogProbabilityCount = 8)
    {
        _chatCompletion = chatCompletion ?? throw new ArgumentNullException(nameof(chatCompletion));
        _modelName = modelName;
        _topLogProbabilityCount = topLogProbabilityCount;
    }

    public async Task<DecisionResult> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        var answers = new Dictionary<string, DecisionAnswer>();
        int totalInputTokens = 0;
        int totalOutputTokens = 0;

        // Process each question with one completion call per question
        foreach (var question in questions)
        {
            var prompt = BuildPrompt(state, question);
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 1,
                IncludeLogProbabilities = true,
                TopLogProbabilityCount = _topLogProbabilityCount
            };

            var completion = await _chatCompletion.GetCompletionAsync(prompt, options, ct);

            totalInputTokens += completion.Usage.InputTokenCount;
            totalOutputTokens += completion.Usage.OutputTokenCount;

            // Extract logprobs and compute answer
            var answer = ProcessCompletion(question, completion);
            answers[question.Name] = answer;
        }

        return new DecisionResult(
            Model: _modelName,
            Answers: answers,
            InputTokens: totalInputTokens,
            OutputTokens: totalOutputTokens);
    }

    /// <summary>
    /// Build prompt with labeled options based on DecisionKind.
    /// </summary>
    private static string BuildPrompt(DecisionState state, DecisionQuestion question)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(state.Content);
        sb.AppendLine();
        sb.AppendLine(question.Instructions);
        sb.AppendLine();

        switch (question.Kind)
        {
            case DecisionKind.Choice:
                for (int i = 0; i < question.Options.Count; i++)
                {
                    char label = (char)('A' + i);
                    sb.AppendLine($"{label}: {question.Options[i]}");
                }
                sb.AppendLine("Reply with a single letter: A, B, C, etc.");
                break;

            case DecisionKind.Score:
                for (int i = 0; i < question.Options.Count; i++)
                {
                    sb.AppendLine($"{i}: {question.Options[i]}");
                }
                sb.AppendLine("Reply with a single digit: 0, 1, 2, etc.");
                break;

            case DecisionKind.Noul:
                sb.AppendLine("Y: Yes");
                sb.AppendLine("N: No");
                sb.AppendLine("Reply with Y or N.");
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Process completion to extract logprobs and compute decision answer.
    /// </summary>
    private static DecisionAnswer ProcessCompletion(DecisionQuestion question, OpenAI.Chat.ChatCompletion completion)
    {
        // Get first (and only) token with its logprobs
        if (completion.ContentTokenLogProbabilities == null || completion.ContentTokenLogProbabilities.Count == 0)
            throw new InvalidOperationException("No token logprobs in response");

        var tokenLogProb = completion.ContentTokenLogProbabilities[0];
        var chosenToken = tokenLogProb.Token;
        var topLogProbs = tokenLogProb.TopLogProbabilities ?? new List<OpenAI.Chat.ChatTokenTopLogProbabilityDetails>();

        // Build probability map from logprobs: exp(logprob) values
        var probabilities = new Dictionary<string, double>();
        foreach (var topLogProb in topLogProbs)
        {
            var prob = Math.Exp(topLogProb.LogProbability);
            probabilities[topLogProb.Token] = prob;
        }

        // Ensure chosen token is in the map
        if (!probabilities.ContainsKey(chosenToken))
        {
            probabilities[chosenToken] = Math.Exp(tokenLogProb.LogProbability);
        }

        return question.Kind switch
        {
            DecisionKind.Choice => ProcessChoice(question, chosenToken, probabilities),
            DecisionKind.Score => ProcessScore(question, chosenToken, probabilities),
            DecisionKind.Noul => ProcessNoul(chosenToken, probabilities),
            _ => throw new InvalidOperationException($"Unknown DecisionKind: {question.Kind}")
        };
    }

    /// <summary>
    /// Process a Choice decision: map labels back to options, renormalize over present labels only.
    /// </summary>
    private static DecisionAnswer ProcessChoice(DecisionQuestion question, string chosenToken, Dictionary<string, double> rawProbs)
    {
        // Map label (A, B, C) to option index
        var optionMap = new Dictionary<string, int>();
        for (int i = 0; i < question.Options.Count; i++)
        {
            optionMap[((char)('A' + i)).ToString()] = i;
        }

        // Collect probabilities for offered labels only, renormalize
        var offeredProbs = new Dictionary<string, double>();
        foreach (var label in rawProbs.Keys)
        {
            if (optionMap.TryGetValue(label, out var idx))
            {
                offeredProbs[question.Options[idx]] = rawProbs[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No offered label appears in top logprobs");

        // Renormalize to sum to 1
        double sum = offeredProbs.Values.Sum();
        var normalizedProbs = offeredProbs.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value / sum);

        // Choose highest probability option
        var chosenOption = normalizedProbs.MaxBy(kvp => kvp.Value).Key;
        var maxProb = normalizedProbs[chosenOption];

        return new DecisionAnswer(
            Name: question.Name,
            Kind: DecisionKind.Choice,
            Choice: chosenOption,
            Score: null,
            Noul: null,
            Confidence: maxProb,
            Probabilities: normalizedProbs);
    }

    /// <summary>
    /// Process a Score decision: compute probability-weighted average of level indices.
    /// </summary>
    private static DecisionAnswer ProcessScore(DecisionQuestion question, string chosenToken, Dictionary<string, double> rawProbs)
    {
        // Map digit labels (0, 1, 2...) to level indices
        var offeredProbs = new Dictionary<int, double>();
        foreach (var label in rawProbs.Keys)
        {
            if (int.TryParse(label, out int levelIdx) && levelIdx >= 0 && levelIdx < question.Options.Count)
            {
                offeredProbs[levelIdx] = rawProbs[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No offered label appears in top logprobs");

        // Renormalize
        double sum = offeredProbs.Values.Sum();
        var normalizedProbs = offeredProbs.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value / sum);

        // Compute weighted score and max probability
        double weightedScore = 0;
        foreach (var kvp in normalizedProbs)
        {
            weightedScore += kvp.Key * kvp.Value;
        }

        var maxProb = normalizedProbs.Values.Max();

        return new DecisionAnswer(
            Name: question.Name,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: weightedScore,
            Noul: null,
            Confidence: maxProb,
            Probabilities: new Dictionary<string, double>());
    }

    /// <summary>
    /// Process a Noul decision: extract P(Y), Confidence is null.
    /// </summary>
    private static DecisionAnswer ProcessNoul(string chosenToken, Dictionary<string, double> rawProbs)
    {
        // Extract P(Y) and P(N)
        var offeredProbs = new Dictionary<string, double>();
        foreach (var label in rawProbs.Keys)
        {
            if (label == "Y" || label == "N")
            {
                offeredProbs[label] = rawProbs[label];
            }
        }

        if (offeredProbs.Count == 0)
            throw new InvalidOperationException("No Y/N label appears in top logprobs");

        // Renormalize
        double sum = offeredProbs.Values.Sum();
        var pY = offeredProbs.ContainsKey("Y") ? offeredProbs["Y"] / sum : 0.0;

        return new DecisionAnswer(
            Name: "noul",
            Kind: DecisionKind.Noul,
            Choice: null,
            Score: null,
            Noul: pY,
            Confidence: null,
            Probabilities: new Dictionary<string, double>());
    }
}

/// <summary>
/// Adapter that wraps ChatClient and implements IOpenAIChatCompletion.
/// </summary>
internal sealed class ChatClientAdapter : IOpenAIChatCompletion
{
    private readonly ChatClient _chatClient;

    public ChatClientAdapter(ChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    public async Task<ChatCompletion> GetCompletionAsync(
        string prompt,
        ChatCompletionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        var message = new UserChatMessage(prompt);
        return await _chatClient.CompleteChatAsync(
            new[] { message },
            options,
            ct);
    }
}
