using System.Reflection;
using System.Text.Json;
using System.Collections;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Skills;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Skills;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;

namespace ReelBolt.WorkflowEngine.Agents;

public abstract class ReelBoltAgentBase : IReelBoltAgent
{
    private readonly IAgentChatClientProvider _chatClients;
    private readonly ISkillAgentToolsFactory _skillAgentToolsFactory;
    private readonly List<AIFunction> _tools;
    private readonly Type? _outputSchemaType;
    private readonly int _agentRunTimeoutSeconds;
    private readonly ChatOptions _chatOptions;
    private readonly ToolLoopBudgetOptions _toolLoopBudget;
    private readonly ToolLoopOptions _toolLoop;
    private readonly string? _fallbackReasoningEffort;

    protected ReelBoltAgentBase(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        string name,
        string description,
        AgentType agentType,
        string defaultSystemPrompt,
        ISkillAgentToolsFactory skillAgentToolsFactory,
        IEnumerable<AIFunction>? tools = null,
        Guid? agentId = null,
        Type? outputSchemaType = null,
        AgentModelSettings? defaultModelSettings = null)
    {
        _chatClients = chatClients;
        _skillAgentToolsFactory = skillAgentToolsFactory;
        _outputSchemaType = outputSchemaType;
        Name = name;
        Description = description;
        AgentType = agentType;
        AgentId = agentId;
        _tools = tools?.ToList() ?? new List<AIFunction>();
        // Ceiling raised from 1800 (30 min) to 3600 (1 hour): a from-scratch retry (no partial
        // progress carries over between attempts — see AgentStepExecutor) hits the SAME wall every
        // time if the true per-attempt duration for a given task sits close to or above the
        // configured limit, so a config value sitting AT the old ceiling could never actually be
        // raised past it. Observed live: MotionGraphicsPlanner (tool-bound, occasionally renders a
        // real Remotion asset) has both succeeded in ~28 minutes and been cut off by the 30-minute
        // timeout on a different run of the exact same step — normal variance for a reasoning-
        // heavy model's "thinking" time on a complex multi-round tool-calling task, not a stuck
        // loop (verified: the underlying vLLM server answers a trivial request in ~1.5s immediately
        // afterward, so it isn't globally wedged).
        // Per-agent override, falling back to the global value. Added because a single global cap
        // cannot fit agents whose workloads differ by an order of magnitude: on a self-hosted
        // backend measured at ~9.6 output tokens/sec, RemotionComponentTranslator's multi-round
        // tool-calling run (sandbox setup, skill loading, writing N TSX files, lint/type repair
        // cycles) legitimately needs longer than the global 3000s, while giving every agent that
        // much rope would let a genuinely stuck one sit there for an hour before anyone noticed.
        // The ceiling is 4 hours rather than the old 1: that limit was only ever defensible while
        // a 2-hour RabbitMQ consumer_timeout capped the whole execution anyway, and that coupling
        // is gone (see WorkflowExecutionRunner) — an agent timeout is now a real budget rather
        // than a number the transport would overrule first.
        _agentRunTimeoutSeconds = Math.Clamp(
            configuration.GetValue<int?>($"Agents:{name}:RunTimeoutSeconds")
                ?? configuration.GetValue("WorkflowEngine:AgentRunTimeoutSeconds", 300),
            30,
            14400);

        // The agent's own tool-calling loop: how far it may run and how much of it is re-sent to the
        // model on every call (docs/workflow-execution-optimization.md "Compaction inside the tool loop").
        _toolLoopBudget = ToolLoopBudgetOptions.From(configuration);
        _toolLoop = ToolLoopOptions.From(configuration);

        string configKey = $"Agents:{name}:SystemPrompt";
        SystemPrompt = BuildSystemPrompt(
            configuration[configKey] ?? defaultSystemPrompt,
            _outputSchemaType);

        _chatOptions = BuildChatOptions(configuration, name, defaultModelSettings);
        DefaultReasoningEffort = defaultModelSettings?.ReasoningEffort;
        _fallbackReasoningEffort = ResolveConfiguredReasoningEffort(configuration, name, defaultModelSettings);

        // Generate JSON schema documentation if output type is specified
        if (_outputSchemaType != null)
        {
            OutputSchemaJson = GenerateJsonSchemaDocumentation(_outputSchemaType);
        }
    }

    public Guid? AgentId { get; }
    public string Name { get; }
    public string Description { get; }
    public string SystemPrompt { get; }
    public AgentType AgentType { get; }
    public IReadOnlyList<AIFunction> Tools => _tools.AsReadOnly();
    public string? OutputSchemaJson { get; }
    public Type? OutputSchemaType => _outputSchemaType;

    /// <summary>
    /// The reasoning effort this agent CLASS ships with - the last step of the resolution order
    /// (definition row, then <c>Agents:&lt;name&gt;:ReasoningEffort</c>, then this). Exposed so a test can
    /// hold it equal to the value <c>DatabaseSeeder</c> writes into the built-in row.
    /// </summary>
    public string? DefaultReasoningEffort { get; }

    public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        => RunCoreAsync(prompt, agentDefinitionId, withTools: true, ct);

    /// <summary>
    /// The structured-output repair turn: same agent, same model/provider, same system prompt and
    /// response format, but with NO tools (neither the agent's own nor its skill tools), so a repair
    /// can never re-run a render or a sandbox write the first answer already performed.
    /// </summary>
    public async Task<AgentRunResult?> RunRepairAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        => await RunCoreAsync(prompt, agentDefinitionId, withTools: false, ct);

    private async Task<AgentRunResult> RunCoreAsync(string prompt, Guid? agentDefinitionId, bool withTools, CancellationToken ct)
    {
        AgentResponse agentResponse;
        ToolLoopRunStats loopStats = new();
        string loopOutcome = "failed";
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_agentRunTimeoutSeconds));
        CancellationToken effectiveToken = timeoutCts.Token;
        // Lets a tool exclude a long render from this budget (AgentRunBudget.Exclude); restored below.
        AgentRunBudget? outerBudget = AgentRunBudget.Current;
        AgentRunBudget.Current = new AgentRunBudget(timeoutCts, TimeSpan.FromSeconds(_agentRunTimeoutSeconds));

        try
        {
            // Structured output is enforced via ChatResponseFormat.ForJsonSchema<T>() when OutputSchemaType is specified
            AIAgent agent = await CreateAgentAsync(agentDefinitionId, loopStats, effectiveToken, withTools);

            // _chatOptions (temperature/top-p/top-k/reasoning effort) always applies; ResponseFormat
            // is layered on top of it only when this agent requires structured output.
            var runOptions = new ChatClientAgentRunOptions(await BuildRunChatOptionsAsync(agentDefinitionId, effectiveToken));

            if (_outputSchemaType != null)
            {
                // Use reflection to call ChatResponseFormat.ForJsonSchema<T>() with the runtime type
                var method = typeof(ChatResponseFormat).GetMethod(nameof(ChatResponseFormat.ForJsonSchema),
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                var responseFormat = method?.MakeGenericMethod(_outputSchemaType).Invoke(null, null) as ChatResponseFormat;
                if (responseFormat != null)
                {
                    runOptions.ResponseFormat = responseFormat;
                }
            }

            agentResponse = await agent.RunAsync(prompt, options: runOptions, cancellationToken: effectiveToken);
            loopOutcome = "completed";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            loopOutcome = "timed out";
            throw new TimeoutException(
                $"Agent '{Name}' timed out after {_agentRunTimeoutSeconds} seconds while processing the step.");
        }
        finally
        {
            AgentRunBudget.Current = outerBudget;
            // Written for failed and timed-out runs too: those are the ones that burned the tokens.
            ToolLoopDiagnostics.LogRun(Name, loopOutcome, loopStats, _toolLoop.MaxIterations);
        }

        var chatResponse = agentResponse.AsChatResponse();
        // A structured-output agent's result is its FINAL answer only. The model's text between tool calls ("I'll check the
        // project files first") is narration: it is kept as a note in the diagnostics, never concatenated into the output.
        (string answerText, List<string> interimNotes) = _outputSchemaType is not null
            ? SplitFinalAnswer(chatResponse)
            : (chatResponse.Text ?? string.Empty, []);
        // The same final message may still carry a sentence before or after the JSON, or a code fence: the output is the JSON value only.
        if (_outputSchemaType is not null)
        {
            (string isolated, string? droppedAroundJson) = StructuredAnswer.Isolate(answerText, StructuredAnswer.ExpectedKind(_outputSchemaType));
            answerText = isolated;
            if (!string.IsNullOrWhiteSpace(droppedAroundJson))
                interimNotes.Add(droppedAroundJson);
        }

        string output = PostProcessOutput(answerText);

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

        (IReadOnlyList<AgentToolCallTrace> toolCalls, IReadOnlyList<string> reasoning) = ExtractDiagnostics(agentResponse, chatResponse);

        if (interimNotes.Count > 0)
            reasoning = [.. reasoning, .. interimNotes.Select(n => "[note between tool calls] " + n)];

        // "Ran out of turns" must not look like "finished": the forced final answer is persisted on the
        // step result (ReasoningJson) next to the run's other diagnostics.
        if (loopStats.IterationCapReached)
        {
            reasoning =
            [
                .. reasoning,
                $"[tool loop] Stopped at the iteration cap ({_toolLoop.MaxIterations}) after {loopStats.ModelCalls} model calls; " +
                "the answer was forced with tools disabled and may be incomplete."
            ];
        }

        ResolvedInferenceProvider? provider = await TryResolveProviderAsync(agentDefinitionId, ct);

        return new AgentRunResult
        {
            Output = output,
            TokensUsed = totalTokens,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            PrimaryProviderId = provider?.ProviderId,
            PrimaryModelName = provider?.ModelName,
            ToolCalls = toolCalls,
            Reasoning = reasoning,
            ModelCalls = loopStats.ModelCalls,
            PeakContextChars = loopStats.PeakContextChars,
            CharsCompacted = loopStats.MaxCharsCompacted,
            ToolLoopCapReached = loopStats.IterationCapReached
        };
    }

    /// <summary>
    /// The text of the last assistant message that carries no tool call (the final answer), and the text of the earlier
    /// assistant messages that sat between tool calls. A response with no tool calls, or whose last message still asks for a
    /// tool (the loop was cut off), keeps the whole text: nothing is hidden when there is no clear final answer.
    /// </summary>
    internal static (string Answer, List<string> InterimNotes) SplitFinalAnswer(ChatResponse response)
    {
        List<ChatMessage> assistant = [.. response.Messages.Where(m => m.Role == ChatRole.Assistant)];
        bool AsksForTool(ChatMessage m) => m.Contents.Any(c => c is FunctionCallContent);
        if (assistant.Count < 2 || AsksForTool(assistant[^1]))
            return (response.Text ?? string.Empty, []);

        List<string> notes = [.. assistant.Take(assistant.Count - 1)
            .Select(m => (m.Text ?? string.Empty).Trim()).Where(t => t.Length > 0)];
        return (assistant[^1].Text ?? string.Empty, notes);
    }

    /// <summary>
    /// Best-effort provider lookup for usage attribution. The run has already produced its output, so a
    /// resolver hiccup here must never fail it: attribution is simply left null.
    /// </summary>
    private async Task<ResolvedInferenceProvider?> TryResolveProviderAsync(Guid? agentDefinitionId, CancellationToken ct)
    {
        try
        {
            return await _chatClients.ResolveProviderAsync(AgentType, agentDefinitionId ?? AgentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deterministic clean-up of the model's final answer before anything downstream sees it (the
    /// step result, the next agents' prompts, the step cache). The default returns the output
    /// unchanged; an override must be total — never throw, and return its input byte-identical
    /// when there is nothing to fix, so an already-clean output is never reformatted.
    /// </summary>
    protected virtual string PostProcessOutput(string output) => output;

    internal static (IReadOnlyList<AgentToolCallTrace> ToolCalls, IReadOnlyList<string> Reasoning) ExtractDiagnostics(object agentResponse, object chatResponse)
    {
        List<AgentToolCallTrace> toolCalls = [];
        List<string> reasoning = [];

        // QA-P0-25: a turn can issue several calls in parallel, and their results come back in a
        // different order (or as separate tool messages), so a result must be paired with the call
        // that carries the same CallId — not with whichever call was added last. Without a CallId on
        // either side the pairing falls back to the last call, exactly as before.
        Dictionary<string, int> callIndexByCallId = new(StringComparer.Ordinal);

        ExtractFromContainer(agentResponse, toolCalls, reasoning, callIndexByCallId);
        ExtractFromContainer(chatResponse, toolCalls, reasoning, callIndexByCallId);

        return (toolCalls, reasoning);
    }

    private static void ExtractFromContainer(
        object container, List<AgentToolCallTrace> toolCalls, List<string> reasoning, Dictionary<string, int> callIndexByCallId)
    {
        foreach (object content in EnumerateProperty(container, "Contents"))
            ExtractFromContent(content, toolCalls, reasoning, callIndexByCallId);

        foreach (object message in EnumerateProperty(container, "Messages"))
        {
            foreach (object content in EnumerateProperty(message, "Contents"))
            {
                ExtractFromContent(content, toolCalls, reasoning, callIndexByCallId);
            }
        }
    }

    private static void ExtractFromContent(
        object content, List<AgentToolCallTrace> toolCalls, List<string> reasoning, Dictionary<string, int> callIndexByCallId)
    {
        string typeName = content.GetType().Name;

        if (typeName.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) || typeName.Contains("ToolCall", StringComparison.OrdinalIgnoreCase))
        {
            string toolName = ReadStringProperty(content, "Name", "FunctionName", "ToolName") ?? "unknown_tool";
            string? arguments = ReadStringProperty(content, "Arguments", "ArgumentsJson", "Input", "Value");
            string? callId = ReadStringProperty(content, "CallId");
            if (!string.IsNullOrEmpty(callId))
                callIndexByCallId[callId] = toolCalls.Count;
            toolCalls.Add(new AgentToolCallTrace
            {
                ToolName = toolName,
                Arguments = arguments,
                Result = null
            });
            return;
        }

        if (typeName.Contains("FunctionResult", StringComparison.OrdinalIgnoreCase) || typeName.Contains("ToolResult", StringComparison.OrdinalIgnoreCase))
        {
            string? resultText = ReadStringProperty(content, "Result", "Output", "Value", "Text", "Content");
            if (!string.IsNullOrWhiteSpace(resultText) && toolCalls.Count > 0)
            {
                string? callId = ReadStringProperty(content, "CallId");
                int index = -1;
                if (!string.IsNullOrEmpty(callId))
                    callIndexByCallId.TryGetValue(callId, out index);
                if (index < 0)
                    index = toolCalls.Count - 1;

                AgentToolCallTrace call = toolCalls[index];
                toolCalls[index] = new AgentToolCallTrace
                {
                    ToolName = call.ToolName,
                    Arguments = call.Arguments,
                    Result = resultText
                };
            }
            return;
        }

        if (typeName.Contains("Reasoning", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Thought", StringComparison.OrdinalIgnoreCase))
        {
            string? text = ReadStringProperty(content, "Text", "Content", "Value", "Reasoning");
            if (!string.IsNullOrWhiteSpace(text))
            {
                reasoning.Add(text);
            }
        }
    }

    private static IEnumerable<object> EnumerateProperty(object instance, string propertyName)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property == null)
            yield break;

        object? value = property.GetValue(instance);
        if (value is null || value is string)
            yield break;

        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item != null)
                    yield return item;
            }
        }
    }

    private static string? ReadStringProperty(object instance, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                continue;

            object? value = property.GetValue(instance);
            if (value is null)
                continue;

            if (value is string text && !string.IsNullOrWhiteSpace(text))
                return text;

            if (value is JsonElement jsonElement)
                return jsonElement.ToString();

            if (value is IDictionary dictionary)
                return SerializeArguments(dictionary);

            if (value is not string)
                return value.ToString();
        }

        return null;
    }

    private const int MaxRecordedArgumentChars = 2000;

    /// <summary>
    /// FunctionCallContent.Arguments is a dictionary, whose ToString() is only its .NET type name;
    /// record it as JSON (long values truncated) so a stored tool call shows what was asked.
    /// </summary>
    public static string SerializeArguments(IDictionary dictionary)
    {
        Dictionary<string, object?> copy = [];
        foreach (DictionaryEntry entry in dictionary)
        {
            object? value = entry.Value;
            // Real providers hand tool arguments over as JsonElement values, not .NET strings; unwrap
            // string elements so the redaction and the per-value cap below apply on that path too.
            if (value is JsonElement { ValueKind: JsonValueKind.String } element)
                value = element.GetString();
            if (value is string text)
            {
                text = ReelBolt.Shared.DesignSystems.Importers.SecretRedactor.Redact(text).text;
                value = text.Length > MaxRecordedArgumentChars ? text[..MaxRecordedArgumentChars] + "...[truncated]" : text;
            }
            copy[entry.Key?.ToString() ?? string.Empty] = value;
        }

        try
        {
            // Defence in depth for non-string values (nested objects/arrays): redact the final JSON too.
            string json = ReelBolt.Shared.DesignSystems.Importers.SecretRedactor.Redact(JsonSerializer.Serialize(copy)).text;
            return json.Length <= MaxRecordedArgumentChars * 2 ? json : json[..(MaxRecordedArgumentChars * 2)] + "...[truncated]";
        }
        catch (NotSupportedException)
        {
            return string.Join(", ", copy.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private async ValueTask<AIAgent> CreateAgentAsync(Guid? agentDefinitionId, ToolLoopRunStats loopStats, CancellationToken ct, bool withTools = true)
    {
        // Structured output is applied via AgentRunOptions at runtime, not here.
        // Prefer the caller-supplied definition id (the actual WorkflowStep.AgentDefinitionId
        // driving this run) so a per-agent provider override resolves correctly; AgentId (set
        // once at DI-registration time) is only a fallback for callers that don't have one.
        IChatClient chatClient = await _chatClients.GetAsync(AgentType, agentDefinitionId ?? AgentId, ct);
        chatClient = ToolLoopPipeline.Wrap(chatClient, _toolLoopBudget, _toolLoop, loopStats);

        // Skill resolution rule — see SkillCatalog.DefaultsFor's own doc comment for the full
        // rationale. This C# class's own AgentType is never AgentType.Custom for any of the
        // built-in agent subclasses (each hardcodes its own specific AgentType in its base(...)
        // call) — AgentType.Custom is only ever used by a generic implementation resolved
        // per-AgentDefinition row via AgentRegistry's _customAgentsById. So checking THIS
        // instance's own AgentType (not the caller-supplied agentDefinitionId) is the correct,
        // and simpler, way to tell "the built-in agent this class represents" apart from "a
        // per-definition Custom agent" — mirroring how AgentStepExecutor/ParallelStepExecutor
        // already only ever pass a non-null customAgentId when AgentType == Custom.
        IReadOnlyList<SkillDescriptor> skills = AgentType != AgentType.Custom
            ? SkillCatalog.DefaultsFor(AgentType)
                .Select(SkillCatalog.Find)
                .Where(s => s != null)
                .Select(s => s!)
                .ToList()
            : await ResolveCustomAgentSkillsAsync(agentDefinitionId ?? AgentId, ct);

        // The run's editing style (docs/editing-styles.md): appended LAST, after the agent's own
        // prompt and output contract, as a delimited [STYLE INSTRUCTIONS] block. Unchanged — the
        // same string — for a run with no style and no output-format override.
        // The run's pinned brand and product profile (docs/design-system.md) follow the style in
        // that order, so the skills advertisement stays the final part of the instructions.
        // The run brief (docs/run-brief.md) comes first of the run blocks: prompt, output contract,
        // [RUN BRIEF], [STYLE INSTRUCTIONS], [DESIGN SYSTEM], [PRODUCT PROFILE], then [PROJECT SUBJECTS]
        // (docs/design/avatar-objects.md: the shot planner's subjects, or the subjects the takes a judge sees show).
        string systemPrompt = Services.Avatars.ProjectSubjectsAmbient.Decorate(
            DesignSystemAmbient.Decorate(
                RunStyleAmbient.Decorate(RunBriefAmbient.Decorate(SystemPrompt, AgentType), AgentType),
                AgentType),
            AgentType);

        if (!withTools)
        {
            // Repair turn: no tools at all, and no skills advertisement (it would promise tools).
            return chatClient.AsAIAgent(instructions: systemPrompt, name: Name, tools: new List<AITool>());
        }

        if (skills.Count == 0)
        {
            return chatClient.AsAIAgent(
                instructions: systemPrompt,
                name: Name,
                tools: _tools.Cast<AITool>().ToList());
        }

        string instructions = systemPrompt + SkillPromptBuilder.BuildAdvertisement(skills);
        List<AITool> tools = [.. _tools, .. _skillAgentToolsFactory.CreateBoundTools(skills)];
        return chatClient.AsAIAgent(instructions: instructions, name: Name, tools: tools);
    }

    /// <summary>
    /// Resolves the skill set for a Custom (non-built-in) agent from its own
    /// <c>AgentDefinition</c> row.
    ///
    /// NOTE (post-merge review, 2026-09): the sibling data-model change this stub was waiting on
    /// HAS landed — <c>AgentDefinition.AssignedSkillsJson</c> exists, is mapped read-only in
    /// <c>WorkflowEngineDbContext</c>, and is written by <c>PUT /api/v1/agents/{id}/skills</c>
    /// (custom agents only). This method is still a stub because this path is currently
    /// UNREACHABLE: no <c>IReelBoltAgent</c> implementation with <c>AgentType.Custom</c> is ever
    /// registered in the engine (see <c>Program.cs</c>/<c>AgentRegistry</c> — a Custom step
    /// resolves to no agent and is Skipped), so there is no runtime consumer to wire this to yet.
    /// Whoever adds an engine-side Custom agent implementation must ALSO implement this method
    /// (read <c>AssignedSkillsJson</c> for <paramref name="agentDefinitionId"/> via a scoped
    /// <c>WorkflowEngineDbContext</c>, map names through <see cref="SkillCatalog.Find"/>), or the
    /// admin UI's per-custom-agent skill assignment will silently do nothing.
    /// </summary>
    private static Task<IReadOnlyList<SkillDescriptor>> ResolveCustomAgentSkillsAsync(Guid? agentDefinitionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillDescriptor>>([]);

    /// <summary>
    /// The effort that applies when the definition row stores none: configuration, then the class
    /// default. Validated here so a typo fails at startup, not mid-workflow: an unrecognised value would
    /// otherwise make this agent's chat template 500 on every request it ever makes (see
    /// <see cref="AgentReasoningEffort"/>).
    /// </summary>
    private static string? ResolveConfiguredReasoningEffort(IConfiguration configuration, string name, AgentModelSettings? defaults)
    {
        string? reasoningEffort = configuration[$"Agents:{name}:ReasoningEffort"] ?? defaults?.ReasoningEffort;

        if (reasoningEffort != null && !AgentReasoningEffort.IsValid(reasoningEffort))
        {
            throw new InvalidOperationException(
                $"Agent '{name}': ReasoningEffort '{reasoningEffort}' is not one of the values the " +
                $"deployed chat template accepts ({string.Join(", ", AgentReasoningEffort.Vocabulary)}).");
        }

        return AgentReasoningEffort.Normalize(reasoningEffort);
    }

    /// <summary>
    /// This run's <see cref="ChatOptions"/>: the agent's sampling settings plus the reasoning effort
    /// resolved as definition row, then configuration, then the class default.
    /// </summary>
    /// <remarks>
    /// The effort travels provider-neutrally as <see cref="ChatOptions.Reasoning"/> and is projected per
    /// provider kind at the client boundary (<see cref="ReasoningEffortMapper"/>): the OpenAI-shaped kinds
    /// send the same <c>reasoning_effort</c> they always did, Anthropic is floored at <c>low</c>, and
    /// DeepSeek turns <c>none</c> into its <c>thinking</c> disable field. The lookup is best-effort - a
    /// store hiccup must never fail a run that would have succeeded with the default.
    /// </remarks>
    private async Task<ChatOptions> BuildRunChatOptionsAsync(Guid? agentDefinitionId, CancellationToken ct)
    {
        string? stored = null;

        try
        {
            stored = await _chatClients.ResolveReasoningEffortAsync(AgentType, agentDefinitionId ?? AgentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to configuration / the class default.
        }

        string? effort = AgentReasoningEffort.Resolve(stored, _fallbackReasoningEffort);

        if (AgentReasoningEffort.ToGeneric(effort) is not { } generic)
        {
            return _chatOptions;
        }

        // Clone: _chatOptions is this singleton agent's field, shared across concurrent runs.
        ChatOptions options = _chatOptions.Clone();
        options.Reasoning = new ReasoningOptions { Effort = generic };
        return options;
    }

    internal static ChatOptions BuildChatOptions(IConfiguration configuration, string name, AgentModelSettings? defaults)
    {
        float? temperature = configuration.GetValue<float?>($"Agents:{name}:Temperature") ?? defaults?.Temperature;
        float? topP = configuration.GetValue<float?>($"Agents:{name}:TopP") ?? defaults?.TopP;
        int? topK = configuration.GetValue<int?>($"Agents:{name}:TopK") ?? defaults?.TopK;

        return new ChatOptions
        {
            Temperature = temperature,
            TopP = topP,
            TopK = topK
        }
        // Prompt caching (initiative anthropic-prompt-caching): this agent's system prompt — its
        // configured prompt, the mandated output contract, the run's style and pinned design/product
        // blocks and its skill advertisement — is constant for the whole run. The system block is
        // therefore the stable prefix, and marking it caches tools + system for every model call of
        // every tool-loop iteration. Anthropic-only: the property is ignored by every other client.
        .WithMode(AnthropicPromptCacheMode.SystemAndTurn);
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

    /// <summary>
    /// Generates a human-readable JSON schema documentation string for UI display.
    /// The actual schema enforcement is handled by ChatResponseFormat.ForJsonSchema at runtime.
    /// </summary>
    private static string GenerateJsonSchemaDocumentation(Type type)
    {
        try
        {
            var properties = new Dictionary<string, object>();

            // Use reflection to extract properties from the output schema type
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propSchema = new Dictionary<string, object>
                {
                    { "type", GetJsonType(prop.PropertyType) }
                };

                // Add description if available from XML docs or attributes
                var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                    .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
                if (descAttr != null)
                {
                    propSchema["description"] = descAttr.Description;
                }

                // Handle collections
                if (IsCollection(prop.PropertyType, out var elementType))
                {
                    propSchema["type"] = "array";
                    if (elementType != null && !IsSimpleType(elementType))
                    {
                        // Recursively generate schema for complex array elements
                        var itemsJson = GenerateJsonSchemaDocumentation(elementType);
                        var itemsSchema = JsonSerializer.Deserialize<Dictionary<string, object>>(itemsJson);
                        if (itemsSchema != null)
                        {
                            propSchema["items"] = itemsSchema;
                        }
                    }
                    else if (elementType != null)
                    {
                        propSchema["items"] = new Dictionary<string, object>
                        {
                            { "type", GetJsonType(elementType) }
                        };
                    }
                }
                // Handle nested objects
                else if (!IsSimpleType(prop.PropertyType))
                {
                    var nestedJson = GenerateJsonSchemaDocumentation(prop.PropertyType);
                    var nestedSchema = JsonSerializer.Deserialize<Dictionary<string, object>>(nestedJson);
                    if (nestedSchema != null && nestedSchema.ContainsKey("properties"))
                    {
                        propSchema["properties"] = nestedSchema["properties"];
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
        // Handle nullable types
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

    private static bool IsSimpleType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return underlyingType.IsPrimitive ||
               underlyingType == typeof(string) ||
               underlyingType == typeof(decimal) ||
               underlyingType == typeof(DateTime) ||
               underlyingType == typeof(DateTimeOffset) ||
               underlyingType == typeof(Guid);
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
            // Try to get element type from IEnumerable<T>
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
