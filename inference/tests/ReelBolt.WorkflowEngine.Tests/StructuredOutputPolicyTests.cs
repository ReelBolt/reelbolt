using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The structured-output validator must never reject what the real consumer of the same output accepts
/// (QA-SOS-1), and the repair turn must propagate cancellation/timeout exactly like a normal run (QA-SOS-2).
/// </summary>
public class StructuredOutputPolicyTests
{
    [Fact]
    public void Analyzer_entryPoints_as_objects_are_accepted()
    {
        string json = "{\"entryPoints\":[{\"path\":\"src/main.tsx\",\"type\":\"app\",\"description\":\"x\"}],\"directories\":[]}";
        StructuredOutputValidator.Validate(json, typeof(CodeStructureOutput)).Should().BeNull();
    }

    [Fact]
    public void Analyzer_string_where_a_list_is_expected_is_accepted()
    {
        string json = "{\"entryPoints\":\"src/main.tsx\"}";
        StructuredOutputValidator.Validate(json, typeof(CodeStructureOutput)).Should().BeNull();
    }

    [Fact]
    public void Single_quoted_sfx_plan_is_accepted()
    {
        string json = "{'cues': [{'atSec': 1.0, 'label': 'whoosh'}]}";
        StructuredOutputValidator.Validate(json, typeof(SfxPlanOutput)).Should().BeNull();
    }

    [Fact]
    public void Single_quoted_take_selection_is_accepted()
    {
        string json = "{'selections': [{'shotId': 'a', 'takeId': 'b'}]}";
        StructuredOutputValidator.Validate(json, typeof(TakeSelectionOutput)).Should().BeNull();
    }

    [Fact]
    public void Genuinely_unparseable_content_is_still_rejected()
    {
        StructuredOutputValidator.Validate("{\"cues\": 12abc}", typeof(SfxPlanOutput)).Should().NotBeNull();
    }

    /// <summary>
    /// Every agent declaring an OutputSchemaType must be listed here with its validator policy. A new schema
    /// type fails this test until its author declares validated-with-X or exempt-because-Y.
    /// </summary>
    private static readonly Dictionary<Type, string> Policy = new()
    {
        [typeof(CodeStructureOutput)] = "validated-with AnalyzerOutputJson.ReaderOptions (TolerantStringList)",
        [typeof(DependencyAnalysisOutput)] = "validated-with AnalyzerOutputJson.ReaderOptions (TolerantStringList)",
        [typeof(ComponentInventoryOutput)] = "validated-with AnalyzerOutputJson.ReaderOptions (TolerantStringList)",
        [typeof(RouteAndApiOutput)] = "validated-with AnalyzerOutputJson.ReaderOptions (TolerantStringList)",
        [typeof(StyleAndThemeOutput)] = "validated-with AnalyzerOutputJson.ReaderOptions (TolerantStringList)",
        [typeof(ScriptwriterOutput)] = "validated-with ScriptwriterOutputJson.Options",
        [typeof(TakeSelectionOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(SfxPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(MotionPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(DynamicsPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(MusicPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(MediaOverlayPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(MotionGraphicsPlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(ColorGradePlanOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(VideoEditDecisionOutput)] = "validated-with default options + quote-normalised retry (VideoCompile DeserializeWithQuoteFallback)",
        [typeof(PickupPlanOutput)] = "validated-with default tolerant options (consumer uses plain System.Text.Json)",
        [typeof(NarrationPlanOutput)] = "validated-with default tolerant options (consumer uses plain System.Text.Json)",
        [typeof(GeneratedShotPlanOutput)] = "validated-with default tolerant options (consumer uses plain System.Text.Json)",
        [typeof(GraphicDesignOutput)] = "validated-with default tolerant options (consumer uses plain System.Text.Json)",
        [typeof(CompositionRevisionOutput)] = "validated-with default tolerant options (consumer uses plain System.Text.Json)",
        [typeof(DirectorOutput)] = "exempt-because consumed as text by later agents",
        [typeof(ReviewOutput)] = "exempt-because loosely parsed / consumed as text",
        [typeof(VideoReviewOutput)] = "exempt-because loosely parsed / consumed as text",
        [typeof(AnimationStrategyOutput)] = "exempt-because consumed as text by later agents",
        [typeof(RenderManifestOutput)] = "exempt-because final answer is a tool-run summary",
        [typeof(RemotionProjectBuildOutput)] = "exempt-because final answer is a tool-run summary",
    };

    [Fact]
    public void Every_agent_output_schema_type_declares_a_validator_policy()
    {
        Assembly engine = typeof(ReelBoltAgentBase).Assembly;
        List<Type> declared = [];
        Mock<IAgentToolProvider> tools = new();
        tools.Setup(x => x.GetTools(It.IsAny<AgentType>())).Returns([]);
        Mock<ISkillAgentToolsFactory> skills = new();
        skills.Setup(s => s.CreateBoundTools(It.IsAny<IReadOnlyList<ReelBolt.Shared.Skills.SkillDescriptor>>())).Returns([]);
        IConfiguration config = new ConfigurationBuilder().Build();
        foreach (Type t in engine.GetTypes().Where(t => typeof(ReelBoltAgentBase).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic))
        {
            ConstructorInfo? ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor == null)
                continue;
            object?[] args = ctor.GetParameters().Select(p => ArgFor(p.ParameterType, tools.Object, skills.Object, config)).ToArray();
            if (args.Any(a => a == null))
                continue;
            try
            {
                var agent = (ReelBoltAgentBase)ctor.Invoke(args);
                if (agent.OutputSchemaType != null)
                    declared.Add(agent.OutputSchemaType);
            }
            catch (TargetInvocationException)
            {
            }
        }

        Policy.Keys.Where(k => !declared.Contains(k)).Select(k => k.Name).Should().BeEmpty(
            "the reflection walk must find every agent the table lists (and the table must not carry stale rows)");
        declared.Distinct().Where(d => !Policy.ContainsKey(d)).Select(d => d.Name).Should().BeEmpty(
            "every OutputSchemaType must declare validated-with-X or exempt-because-Y in this table");
        Policy.Where(p => p.Value.StartsWith("exempt-because", StringComparison.Ordinal))
            .All(p => StructuredOutputValidator.IsExempt(p.Key)).Should().BeTrue();
        Policy.Where(p => p.Value.StartsWith("validated-with", StringComparison.Ordinal))
            .Any(p => StructuredOutputValidator.IsExempt(p.Key)).Should().BeFalse();
    }

    [Fact]
    public void Analyzer_types_are_validated_through_the_same_options_the_reader_uses()
    {
        foreach (Type t in ScriptwriterOutputOptionsRegistration.AnalyzerOutputTypes)
            StructuredOutputOptions.For(t).Should().BeSameAs(AnalyzerOutputJson.ReaderOptions);
    }

    private static object? ArgFor(Type type, IAgentToolProvider tools, ISkillAgentToolsFactory skills, IConfiguration config)
    {
        if (type == typeof(IAgentToolProvider)) return tools;
        if (type == typeof(ISkillAgentToolsFactory)) return skills;
        if (type == typeof(IConfiguration)) return config;
        if (type == typeof(IAgentChatClientProvider)) return Mock.Of<IAgentChatClientProvider>();
        return type.IsInterface ? MockOf(type) : null;
    }

    private static object? MockOf(Type type)
    {
        try
        {
            return typeof(Mock).GetMethod(nameof(Mock.Of), Type.EmptyTypes)!.MakeGenericMethod(type).Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }

    // ---- RunRepairAsync propagation (QA-SOS-2) and the real no-tools path (QA-SOS-8) ----

    [Fact]
    public async Task RunRepairAsync_returns_the_model_answer_on_the_real_no_tools_path()
    {
        ColoristAgent agent = CreateColorist(new FakeProvider(_ => Task.FromResult("{\"ok\":true}")));

        AgentRunResult? result = await agent.RunRepairAsync("fix it");

        result.Should().NotBeNull();
        result!.Output.Should().Contain("ok");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task RunRepairAsync_propagates_user_cancellation_as_OperationCanceledException()
    {
        using CancellationTokenSource cts = new();
        ColoristAgent agent = CreateColorist(new FakeProvider(async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return "{}";
        }));

        Func<Task> act = () => agent.RunRepairAsync("fix it", null, cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<AggregateException>();
    }

    [Fact]
    public async Task RunRepairAsync_propagates_a_run_timeout_as_TimeoutException()
    {
        ColoristAgent agent = CreateColorist(new FakeProvider(async ct =>
        {
            // Burn the budget (negative exclusion pulls the deadline in) so the linked timeout token fires.
            AgentRunBudget.Current!.Exclude(TimeSpan.FromSeconds(-299.9));
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
            throw new OperationCanceledException();
        }));

        Func<Task> act = () => agent.RunRepairAsync("fix it");

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public void Recorded_tool_arguments_are_secret_redacted()
    {
        string key = "sk-" + new string('a', 40);
        string json = ReelBoltAgentBase.SerializeArguments(new Dictionary<string, object?> { ["content"] = "token " + key });

        json.Should().NotContain(key).And.Contain("[redacted]");
    }

    [Fact]
    public void Recorded_tool_arguments_from_a_real_provider_JsonElement_values_are_redacted()
    {
        string key = "sk-" + new string('a', 40);
        Dictionary<string, object?> parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"content\":\"token " + key + "\",\"nested\":{\"k\":\"" + key + "\"}}")!;

        string json = ReelBoltAgentBase.SerializeArguments(parsed);

        json.Should().NotContain(key).And.Contain("[redacted]");
    }

    [Fact]
    public void A_single_quoted_script_is_not_accepted_by_the_validator_because_voiceover_does_not_retry_quotes()
    {
        string singleQuoted = "{'title':'T','durationInSeconds':10,'scenes':[],'narrative':'N'}";

        StructuredOutputValidator.Validate(singleQuoted, typeof(ReelBolt.Shared.Data.OutputSchemas.ScriptwriterOutput))
            .Should().NotBeNull();
    }

    private static ColoristAgent CreateColorist(FakeProvider provider)
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        Mock<IAgentToolProvider> tools = new();
        tools.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns([]);
        Mock<ISkillAgentToolsFactory> skills = new();
        skills.Setup(s => s.CreateBoundTools(It.IsAny<IReadOnlyList<ReelBolt.Shared.Skills.SkillDescriptor>>())).Returns([]);
        return new ColoristAgent(provider, configuration, tools.Object, skills.Object);
    }

    private sealed class FakeProvider(Func<CancellationToken, Task<string>> answer) : IAgentChatClientProvider
    {
        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult<IChatClient>(new Client(answer));

        private sealed class Client(Func<CancellationToken, Task<string>> answer) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                new(new ChatMessage(ChatRole.Assistant, await answer(cancellationToken)));

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
