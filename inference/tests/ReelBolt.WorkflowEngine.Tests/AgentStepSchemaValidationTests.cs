using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// An agent that DECLARES a structured output schema must actually produce a parseable JSON
/// object, and the step that produced it is where that has to be caught.
///
/// <para>
/// Regression test for a failure seen live while producing a real promo video: a
/// <c>VideoStoryEditor</c> step returned 38KB of pure reasoning prose containing not a single
/// brace, was recorded <c>Completed</c>, and the workflow went on to spend roughly fifty more
/// minutes running MotionGraphicsPlanner, SoundDesigner and Colorist on top of a decision that did
/// not exist — until <c>VideoCompile</c> finally failed with "Decision input did not contain a
/// recognizable JSON object", a message naming the compile step rather than the step that actually
/// broke. The schema contract was declared (<c>ChatResponseFormat.ForJsonSchema</c>) but never
/// verified.
/// </para>
/// </summary>
public class AgentStepSchemaValidationTests
{
    [Fact]
    public async Task A_schema_declaring_agent_that_returns_only_prose_fails_its_own_step()
    {
        StepExecutionResult result = await RunAgentReturning(
            "I'll analyse the available context before deciding. The four shots map to four "
            + "source clips. Plan: open on the hero and close on it. No JSON here at all.",
            outputSchemaType: typeof(VideoEditDecisionOutput));

        result.Status.Should().Be(StepStatus.Failed);
        // The message must name the agent and its schema, so the failure points at the step that
        // actually broke instead of at a distant consumer.
        result.ErrorDetails.Should().Contain("VideoStoryEditor");
        result.ErrorDetails.Should().Contain(nameof(VideoEditDecisionOutput));
    }

    [Fact]
    public async Task A_schema_declaring_agent_passes_when_its_answer_follows_reasoning_prose()
    {
        // The common, legitimate shape for a reasoning model: extensive prose, then the real
        // answer last. Validation uses the same last-object extractor the consumers use, so this
        // must not be mistaken for the prose-only failure above.
        StepExecutionResult result = await RunAgentReturning(
            "Let me think. Candidate spans {s0,s0} and {s1,s1} look best.\n"
            + "{\"keep\":[{\"shotId\":\"s0\"}],\"editRationale\":\"opens on the hero\"}",
            outputSchemaType: typeof(VideoEditDecisionOutput));

        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task An_agent_with_no_declared_schema_is_not_required_to_emit_json()
    {
        // Prose-only agents are a normal, supported case — the check must key off the declared
        // schema, not apply to every agent.
        StepExecutionResult result = await RunAgentReturning(
            "A plain prose summary with no JSON whatsoever.",
            outputSchemaType: null);

        result.Status.Should().Be(StepStatus.Completed);
    }

    // OnScreenText drift of any shape is now kept leniently; these are shapes that still cannot be read.
    private const string WrongStringForList =
        "{\"scenes\":[{\"duration\":{\"line\":\"Northwind Staffing\"}}]}";
    private const string WrongObjectForArray =
        "{\"scenes\":{\"onScreenText\":[\"a\"]}}";
    private const string ValidScript = "{\"scenes\":[{\"onScreenText\":[\"Northwind Staffing\"]}]}";

    [Theory]
    [InlineData(WrongStringForList)]
    [InlineData(WrongObjectForArray)]
    [InlineData("Here is the script, I could not format it as JSON.")]
    public async Task Wrong_shape_runs_one_repair_turn_carrying_the_error_and_a_fix_completes(string wrong)
    {
        var agent = new StubAgent(wrong, typeof(ScriptwriterOutput)) { RepairOutput = ValidScript };
        StepExecutionResult result = await RunAgent(agent);

        agent.RepairPrompts.Should().HaveCount(1);
        agent.RepairPrompts[0].Should().Contain("Return ONLY the corrected JSON object");
        agent.RepairPrompts[0].Should().Contain(wrong);
        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be(ValidScript);
        result.TokensUsed.Should().Be(1 + 7); // first run + repair turn
    }

    [Fact]
    public async Task Repair_prompt_names_the_deserializer_path()
    {
        var agent = new StubAgent(WrongObjectForArray, typeof(ScriptwriterOutput)) { RepairOutput = ValidScript };
        await RunAgent(agent);
        agent.RepairPrompts[0].Should().Contain("Path: $.scenes");
    }

    [Fact]
    public async Task Still_wrong_after_repair_fails_with_the_schema_invalid_code()
    {
        var agent = new StubAgent(WrongObjectForArray, typeof(ScriptwriterOutput)) { RepairOutput = WrongObjectForArray };
        StepExecutionResult result = await RunAgent(agent);

        agent.RepairPrompts.Should().HaveCount(1);
        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("AGENT_OUTPUT_SCHEMA_INVALID");
        result.ErrorDetails.Should().Contain("VideoStoryEditor").And.Contain(nameof(ScriptwriterOutput));
        result.TokensUsed.Should().Be(8);
    }

    [Fact]
    public async Task A_valid_first_answer_never_calls_the_repair_turn()
    {
        var agent = new StubAgent(ValidScript, typeof(ScriptwriterOutput)) { RepairOutput = ValidScript };
        StepExecutionResult result = await RunAgent(agent);

        agent.RepairPrompts.Should().BeEmpty();
        result.Status.Should().Be(StepStatus.Completed);
    }

    public static IEnumerable<object[]> RegisteredSchemaTypes() => new[]
    {
        typeof(CodeStructureOutput), typeof(RouteAndApiOutput), typeof(MotionGraphicsPlanOutput),
        typeof(TakeSelectionOutput), typeof(ComponentInventoryOutput), typeof(MusicPlanOutput),
        typeof(DirectorOutput), typeof(VideoReviewOutput), typeof(NarrationPlanOutput),
        typeof(GraphicDesignOutput), typeof(StyleAndThemeOutput), typeof(ColorGradePlanOutput),
        typeof(PickupPlanOutput), typeof(DependencyAnalysisOutput), typeof(RemotionProjectBuildOutput),
        typeof(GeneratedShotPlanOutput), typeof(RenderManifestOutput), typeof(VideoEditDecisionOutput),
        typeof(ReviewOutput), typeof(SfxPlanOutput), typeof(CompositionRevisionOutput),
        typeof(AnimationStrategyOutput), typeof(ScriptwriterOutput),
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(RegisteredSchemaTypes))]
    public void Every_registered_schema_type_accepts_an_empty_object_and_a_default_instance(Type schemaType)
    {
        // Guards against the validator ever being stricter than the contract: an empty object and a
        // camelCase-serialized default instance must both validate.
        StructuredOutputValidator.Validate("{}", schemaType).Should().BeNull();
        string json = System.Text.Json.JsonSerializer.Serialize(
            Activator.CreateInstance(schemaType),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        StructuredOutputValidator.Validate(json, schemaType).Should().BeNull();
    }

    private static async Task<StepExecutionResult> RunAgentReturning(string output, Type? outputSchemaType)
        => await RunAgent(new StubAgent(output, outputSchemaType));

    private static async Task<StepExecutionResult> RunAgent(StubAgent agent)
    {

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        var executor = new AgentStepExecutor(
            registry.Object,
            new WorkflowExecutionContextAccessor(),
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);

        var step = new WorkflowStep
        {
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition
            {
                Name = "VideoStoryEditor",
                AgentType = AgentType.VideoStoryEditor
            }
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = new List<WorkflowStep> { step },
            AccumulatedOutput = string.Empty,
            StepOutputHistory = new List<StepOutputHistoryEntry>(),
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        return await executor.ExecuteAsync(context);
    }

    private sealed class StubAgent : IReelBoltAgent
    {
        private readonly string _output;

        public StubAgent(string output, Type? outputSchemaType)
        {
            _output = output;
            OutputSchemaType = outputSchemaType;
        }

        public Guid? AgentId => null;
        public string Name => "VideoStoryEditor";
        public string Description => "stub";
        public string SystemPrompt => "stub";
        public AgentType AgentType => AgentType.VideoStoryEditor;
        public IReadOnlyList<AIFunction> Tools => Array.Empty<AIFunction>();
        public string? OutputSchemaJson => null;
        public Type? OutputSchemaType { get; }

        public Action? OnRun { get; set; }

        public string? RepairOutput { get; set; }
        public List<string> RepairPrompts { get; } = new();

        public Task<AgentRunResult?> RunRepairAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        {
            if (RepairOutput == null)
                return Task.FromResult<AgentRunResult?>(null);
            RepairPrompts.Add(prompt);
            return Task.FromResult<AgentRunResult?>(new AgentRunResult { Output = RepairOutput, TokensUsed = 7 });
        }

        public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        {
            OnRun?.Invoke();
            return Task.FromResult(new AgentRunResult { Output = _output, TokensUsed = 1 });
        }
    }
}
