using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Agents;

/// <summary>
/// Abstract base class for ReelBolt agents in the API service.
/// </summary>
public abstract class ReelBoltAgentBase : IReelBoltAgent
{
    private readonly IAgentChatClientProvider _chatClients;
    private readonly List<AIFunction> _tools;
    private readonly Type? _outputSchemaType;

    protected ReelBoltAgentBase(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        string name,
        string description,
        AgentType agentType,
        string defaultSystemPrompt,
        IEnumerable<AIFunction>? tools = null,
        Type? outputSchemaType = null,
        string? systemPromptSuffix = null)
    {
        _chatClients = chatClients;
        _outputSchemaType = outputSchemaType;
        Name = name;
        Description = description;
        AgentType = agentType;
        _tools = tools?.ToList() ?? new List<AIFunction>();

        string configKey = $"Agents:{name}:SystemPrompt";
        SystemPrompt = BuildSystemPrompt(
            configuration[configKey] ?? defaultSystemPrompt,
            _outputSchemaType);

        // Per-call context a subclass appends AFTER the (possibly config-overridden) prompt, so an
        // operator override of the prompt can never drop it.
        if (!string.IsNullOrWhiteSpace(systemPromptSuffix))
            SystemPrompt = SystemPrompt + "\n\n" + systemPromptSuffix;

        if (_outputSchemaType != null)
        {
            OutputSchemaJson = GenerateJsonSchemaDocumentation(_outputSchemaType);
        }
    }

    public string Name { get; }
    public string Description { get; }
    public string SystemPrompt { get; }
    public AgentType AgentType { get; }
    public IReadOnlyList<AIFunction> Tools => _tools.AsReadOnly();
    public string? OutputSchemaJson { get; }
    public Type? OutputSchemaType => _outputSchemaType;

    public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default) =>
        RunAsync([new ChatMessage(ChatRole.User, prompt)], agentDefinitionId, ct);

    /// <summary>
    /// Runs the agent against a full multi-turn conversation (prior user AND assistant turns in
    /// order) instead of a single user message. This is what keeps the workflow assistant's chat
    /// stateful: the client (assistant-ui) already ships the complete <c>messages[]</c> history on
    /// every request, and this overload threads it straight through to the underlying
    /// <c>AIAgent.RunAsync</c> so the model sees the whole conversation rather than only the
    /// latest message.
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        IList<ChatMessage> messages,
        Guid? agentDefinitionId = null,
        CancellationToken ct = default,
        ChatOptions? chatOptions = null)
    {
        AIAgent agent = await CreateAgentAsync(agentDefinitionId, ct);

        ChatResponseFormat? responseFormat = null;
        if (_outputSchemaType != null)
        {
            var method = typeof(ChatResponseFormat).GetMethod(nameof(ChatResponseFormat.ForJsonSchema),
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);

            if (method != null)
            {
                var genericMethod = method.MakeGenericMethod(_outputSchemaType);
                responseFormat = genericMethod.Invoke(null, null) as ChatResponseFormat;
            }
        }

        // Options are built only when there is something to carry, so a run that passes neither a
        // schema nor per-call options keeps the exact request shape it had before this parameter
        // existed — and so does every provider path behind it.
        AgentResponse agentResponse;
        if (responseFormat is null && chatOptions is null)
        {
            agentResponse = await agent.RunAsync(messages, cancellationToken: ct);
        }
        else
        {
            var runOptions = chatOptions is null
                ? new AgentRunOptions()
                : new ChatClientAgentRunOptions(chatOptions);
            runOptions.ResponseFormat = responseFormat;

            agentResponse = await agent.RunAsync(messages, options: runOptions, cancellationToken: ct);
        }

        var chatResponse = agentResponse.AsChatResponse();
        string output = chatResponse.Text ?? string.Empty;

        // Extract token usage from the response
        int totalTokens = 0;
        int? inputTokens = null;
        int? outputTokens = null;

        if (chatResponse.Usage != null)
        {
            inputTokens = (int?)(chatResponse.Usage.InputTokenCount ?? 0);
            outputTokens = (int?)(chatResponse.Usage.OutputTokenCount ?? 0);
            totalTokens = (int)(chatResponse.Usage.TotalTokenCount ??
                          ((inputTokens ?? 0) + (outputTokens ?? 0)));
        }

        return new AgentRunResult
        {
            Output = output,
            TokensUsed = totalTokens,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            FinishReason = chatResponse.FinishReason?.Value
        };
    }

    private async ValueTask<AIAgent> CreateAgentAsync(Guid? agentDefinitionId, CancellationToken ct)
    {
        IChatClient chatClient = await _chatClients.GetAsync(AgentType, agentDefinitionId, ct);
        return chatClient.AsAIAgent(
            instructions: SystemPrompt,
            name: Name,
            tools: _tools.Cast<AITool>().ToList());
    }

    private static string BuildSystemPrompt(string basePrompt, Type? outputSchemaType)
    {
        if (outputSchemaType == null)
            return basePrompt;

        const string instruction = """

        ## Output Contract (Mandatory)
        - Return ONLY a single valid JSON object matching the configured schema.
        - Do not include markdown, code fences, commentary, explanations, or extra text.
        - Do not wrap JSON in backticks.
        - Every field must conform to the schema's expected shape and types.
        """;

        if (basePrompt.Contains("## Output Contract (Mandatory)", StringComparison.Ordinal))
            return basePrompt;

        return $"{basePrompt}\n{instruction}";
    }

    private static string GenerateJsonSchemaDocumentation(Type type)
    {
        try
        {
            var properties = new Dictionary<string, object>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propSchema = new Dictionary<string, object>
                {
                    { "type", GetJsonType(prop.PropertyType) }
                };

                if (IsCollection(prop.PropertyType, out var elementType))
                {
                    propSchema["type"] = "array";
                    if (elementType != null)
                    {
                        propSchema["items"] = new Dictionary<string, object>
                        {
                            { "type", GetJsonType(elementType) }
                        };
                    }
                }

                properties[prop.Name] = propSchema;
            }

            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "schemaType", type.FullName ?? type.Name },
                { "properties", properties }
            };

            return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"Failed to generate schema documentation: {ex.Message}\"}}";
        }
    }

    private static string GetJsonType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType == typeof(string))
            return "string";
        if (underlyingType == typeof(int) || underlyingType == typeof(long) ||
            underlyingType == typeof(short) || underlyingType == typeof(byte))
            return "integer";
        if (underlyingType == typeof(float) || underlyingType == typeof(double) ||
            underlyingType == typeof(decimal))
            return "number";
        if (underlyingType == typeof(bool))
            return "boolean";
        if (IsCollection(type, out _))
            return "array";

        return "object";
    }

    private static bool IsCollection(Type type, out Type? elementType)
    {
        elementType = null;

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            var enumerableInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableInterface != null)
            {
                elementType = enumerableInterface.GetGenericArguments()[0];
                return true;
            }
        }

        return false;
    }
}
