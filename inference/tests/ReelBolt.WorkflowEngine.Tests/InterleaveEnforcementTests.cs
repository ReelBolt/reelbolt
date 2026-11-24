using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Where the interleave guard is enforced (docs/video-editing.md "Using every clip offered"): the
/// edit room's synthesis and solo fallback, the solo <c>Agent(VideoStoryEditor)</c> step, the compile
/// step's review fact and warning, and the ReviewLoop's deterministic cap.
/// </summary>
public class InterleaveEnforcementTests
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Json(VideoEditDecisionOutput decision) =>
        JsonSerializer.Serialize(decision, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ---------------------------------------------------------------------------------------------
    // The edit room
    // ---------------------------------------------------------------------------------------------

    private static EditRoomStepExecutor CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        registry = new Mock<IAgentRegistry>();
        return new EditRoomStepExecutor(
            chatClients.Object, toolProvider.Object, registry.Object, Mock.Of<IProjectFileWorkspace>(),
            new WorkflowExecutionContextAccessor(), NullLogger<EditRoomStepExecutor>.Instance, null!);
    }

    private static StepExecutionContext RoomContext(string configJson, string viewJson, string? userRequest)
    {
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 6, StepType = StepType.EditRoom, EditRoomConfigJson = configJson };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(5, "Analyze", viewJson)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            UserRequest = userRequest,
            CancellationToken = CancellationToken.None
        };
    }

    private static string OneSeatRoomConfig() => JsonSerializer.Serialize(
        new EditRoomStepConfig(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Story editor", "story")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false,
            MaxSynthesisAttempts: 2),
        ConfigOptions);

    private const string RoomProposal = "Use src0 as the film's spine with src3 at the narrative hinges and a still at the close.";

    [Fact]
    public async Task The_staging_synthesis_is_sent_back_once_with_precise_feedback_and_the_intercut_retry_wins()
    {
        EditRoomStepExecutor executor = CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CannedChatClient(RoomProposal));

        var prompts = new List<string>();
        var director = new Mock<IReelBoltAgent>();
        director
            .SetupSequence(a => a.RunAsync(Moq.Capture.In(prompts), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = Json(InterleaveFixtures.StagingDecision()), Success = true, InputTokens = 100, OutputTokens = 10 })
            .ReturnsAsync(new AgentRunResult { Output = Json(InterleaveFixtures.IntercutDecision()), Success = true, InputTokens = 200, OutputTokens = 20 });
        registry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            RoomContext(OneSeatRoomConfig(), InterleaveFixtures.StagingViewJson(), InterleaveFixtures.StagingBrief));

        result.Status.Should().Be(StepStatus.Completed);
        prompts.Should().HaveCount(2, "exactly one policy retry");
        prompts[1].Should().Contain("INTERLEAVE CHECK FAILED");
        prompts[1].Should().Contain("src3 (still photo, ids s25..s30");
        prompts[1].Should().Contain("s5|s6", "the composition's scene boundaries are listed");
        prompts[1].Should().Contain("src3 at the narrative hinges", "the room's own proposal is quoted back");

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("keep").GetArrayLength().Should().Be(5);
        JsonElement interleave = doc.RootElement.GetProperty("interleave");
        interleave.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        interleave.GetProperty("retried").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("room").GetProperty("synthesisAttempts").GetInt32().Should().Be(2);
        result.InputTokens.Should().BeGreaterThanOrEqualTo(300, "the policy retry's usage is metered too");
    }

    [Fact]
    public async Task A_retry_that_still_fails_keeps_the_original_decision_and_records_the_finding_without_failing()
    {
        EditRoomStepExecutor executor = CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CannedChatClient(RoomProposal));

        // The retry dodges with an invented justification — still not in the brief.
        VideoEditDecisionOutput stubborn = InterleaveFixtures.StagingDecision("the brief asks for no added imagery");
        stubborn.EditRationale = "retry";
        var director = new Mock<IReelBoltAgent>();
        director
            .SetupSequence(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = Json(InterleaveFixtures.StagingDecision()), Success = true })
            .ReturnsAsync(new AgentRunResult { Output = Json(stubborn), Success = true });
        registry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            RoomContext(OneSeatRoomConfig(), InterleaveFixtures.StagingViewJson(), InterleaveFixtures.StagingBrief));

        result.Status.Should().Be(StepStatus.Completed, "a policy miss is recorded, never a failure");
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("editRationale").GetString().Should().NotBe("retry", "the original decision stands");
        JsonElement interleave = doc.RootElement.GetProperty("interleave");
        interleave.GetProperty("required").GetBoolean().Should().BeTrue();
        interleave.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        interleave.GetProperty("reason").GetString().Should().Be(InterleaveGuard.ReasonSingleSourceUnjustified);
        interleave.GetProperty("retried").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeFalse();
        director.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_verbatim_justification_from_the_brief_is_accepted_without_a_retry()
    {
        EditRoomStepExecutor executor = CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CannedChatClient("Keep s0 through s17."));

        const string brief = "Cut the film. Use only the rendered animation, nothing else from the project.";
        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Output = Json(InterleaveFixtures.StagingDecision("Use only the rendered animation, nothing else from the project")),
                Success = true
            });
        registry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            RoomContext(OneSeatRoomConfig(), InterleaveFixtures.StagingViewJson(), brief));

        result.Status.Should().Be(StepStatus.Completed);
        director.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement interleave = doc.RootElement.GetProperty("interleave");
        interleave.GetProperty("satisfied").GetBoolean().Should().BeTrue();
        interleave.GetProperty("reason").GetString().Should().Be(InterleaveGuard.ReasonJustified);
        interleave.GetProperty("retried").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_solo_fallback_of_a_degraded_room_is_held_to_the_same_guard()
    {
        EditRoomStepExecutor executor = CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        var prompts = new List<string>();
        var solo = new Mock<IReelBoltAgent>();
        solo
            .SetupSequence(a => a.RunAsync(Moq.Capture.In(prompts), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = Json(InterleaveFixtures.StagingDecision()), Success = true })
            .ReturnsAsync(new AgentRunResult { Output = Json(InterleaveFixtures.IntercutDecision()), Success = true });
        registry.Setup(r => r.GetByType(AgentType.VideoStoryEditor, null)).Returns(solo.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            RoomContext(OneSeatRoomConfig(), InterleaveFixtures.StagingViewJson(), InterleaveFixtures.StagingBrief));

        result.Status.Should().Be(StepStatus.Completed);
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("INTERLEAVE CHECK FAILED");
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("keep").GetArrayLength().Should().Be(5);
        doc.RootElement.GetProperty("interleave").GetProperty("satisfied").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_single_source_room_spends_no_retry_and_records_nothing()
    {
        EditRoomStepExecutor executor = CreateRoom(out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> registry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CannedChatClient("Keep s0."));

        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Output = Json(new VideoEditDecisionOutput { Keep = [new VideoEditKeepSpan { FromId = "s0", ToId = "s1" }] }),
                Success = true
            });
        registry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        string view = new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["shots"] = new JsonArray(new JsonObject { ["id"] = "s0" }, new JsonObject { ["id"] = "s1" })
            }
        }.ToJsonString();

        StepExecutionResult result = await executor.ExecuteAsync(RoomContext(OneSeatRoomConfig(), view, "brief"));

        result.Status.Should().Be(StepStatus.Completed);
        director.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.TryGetProperty("interleave", out _).Should().BeFalse();
    }

    [Fact]
    public void The_room_charter_tells_seats_that_keeping_every_scene_is_not_inserting_nothing()
    {
        EditRoomStepExecutor.EditRoomCharterPrompt.Should().Contain("does not mean \"insert nothing\"");
        EditRoomStepExecutor.EditRoomCharterPrompt.Should().Contain("never infer a restriction the brief does not state");
    }

    // ---------------------------------------------------------------------------------------------
    // The solo Agent(VideoStoryEditor) step
    // ---------------------------------------------------------------------------------------------

    private static async Task<(StepExecutionResult Result, List<string> Prompts)> RunSoloStep(
        string viewJson, string? userRequest, params string[] outputs)
    {
        var agent = new SequenceAgent(outputs);
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
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            AgentDefinition = new AgentDefinition { Name = "VideoStoryEditor", AgentType = AgentType.VideoStoryEditor }
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = viewJson,
            StepOutputHistory = [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            UserRequest = userRequest,
            CancellationToken = CancellationToken.None
        };

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return (result, agent.Prompts);
    }

    [Fact]
    public async Task The_solo_editor_is_retried_once_in_step_and_the_satisfying_answer_wins()
    {
        (StepExecutionResult result, List<string> prompts) = await RunSoloStep(
            InterleaveFixtures.StagingViewJson(), InterleaveFixtures.StagingBrief,
            Json(InterleaveFixtures.StagingDecision()), Json(InterleaveFixtures.IntercutDecision()));

        result.Status.Should().Be(StepStatus.Completed);
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("Interleave check (one retry)");
        prompts[1].Should().Contain("s16|s17");
        result.TokensUsed.Should().Be(2, "both calls are counted");

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("keep").GetArrayLength().Should().Be(5);
        doc.RootElement.GetProperty("interleave").GetProperty("satisfied").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("interleave").GetProperty("retried").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_solo_editor_never_fails_its_step_over_the_guard()
    {
        (StepExecutionResult result, List<string> prompts) = await RunSoloStep(
            InterleaveFixtures.StagingViewJson(), InterleaveFixtures.StagingBrief,
            Json(InterleaveFixtures.StagingDecision()), "no json the second time");

        result.Status.Should().Be(StepStatus.Completed);
        prompts.Should().HaveCount(2);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("keep")[0].GetProperty("toId").GetString().Should().Be("s17");
        doc.RootElement.GetProperty("interleave").GetProperty("satisfied").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_single_source_solo_edit_is_returned_byte_for_byte()
    {
        string view = new JsonObject
        {
            ["view"] = new JsonObject { ["shots"] = new JsonArray(new JsonObject { ["id"] = "s0" }) }
        }.ToJsonString();
        string output = "Thinking...\n{\"keep\":[{\"fromId\":\"s0\",\"toId\":\"s0\"}],\"editRationale\":\"x\"}";

        (StepExecutionResult result, List<string> prompts) = await RunSoloStep(view, null, output);

        prompts.Should().HaveCount(1);
        result.Output.Should().Be(output);
    }

    // ---------------------------------------------------------------------------------------------
    // Compile warning and the ReviewLoop cap
    // ---------------------------------------------------------------------------------------------

    private static string CompileOutput(InterleaveVerdict verdict, int score = 8) => new JsonObject
    {
        ["status"] = "completed",
        ["interleave"] = verdict.ToJson()
    }.ToJsonString();

    private static InterleaveVerdict StagingVerdict(VideoEditDecisionOutput decision) => InterleaveGuard.Evaluate(
        InterleaveOffer.FromView(JsonNode.Parse(InterleaveFixtures.StagingViewJson())), decision, InterleaveFixtures.StagingBrief);

    [Fact]
    public void The_review_cap_lowers_the_staging_review_score_below_the_templates_min_score()
    {
        // The staging reviewer gave 8 ("kept whole and in order") and passed it.
        string compile = CompileOutput(StagingVerdict(InterleaveFixtures.StagingDecision()));

        ReviewLoopStepExecutor.ApplyDeterministicCaps(8, compile).Should().Be(ReviewLoopStepExecutor.InterleaveScoreCap);
        ReviewLoopStepExecutor.InterleaveScoreCap.Should().BeLessThan(8, "every video template's MinScore is 8");
        ReviewLoopStepExecutor.DeterministicCapsWouldFire(compile).Should().BeTrue("the calibrated early stop must not skip it");
        ReviewLoopStepExecutor.NarrationCapsWouldFire(compile).Should().BeFalse("the narration caps are untouched");
    }

    [Fact]
    public void The_review_cap_is_inert_for_a_satisfied_verdict_and_for_no_node()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, CompileOutput(StagingVerdict(InterleaveFixtures.IntercutDecision()))).Should().Be(9);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, "{\"status\":\"completed\"}").Should().Be(9);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, "not json").Should().Be(9);
        ReviewLoopStepExecutor.ApplyDeterministicCaps(3, CompileOutput(StagingVerdict(InterleaveFixtures.StagingDecision()))).Should().Be(3, "a cap never raises");
    }

    [Fact]
    public void The_compile_warning_says_in_plain_words_what_was_left_out()
    {
        JsonObject single = VideoCompileStepExecutor.BuildInterleaveWarning(StagingVerdict(InterleaveFixtures.StagingDecision()));
        single["code"]!.GetValue<string>().Should().Be("single_source_edit");
        single["message"]!.GetValue<string>().Should().Contain("only one of the 6 clips");

        var bracketed = new VideoEditDecisionOutput
        {
            Keep =
            [
                new VideoEditKeepSpan { FromId = "s0", ToId = "s17" },
                new VideoEditKeepSpan { FromId = "s25", ToId = "s25" }
            ]
        };
        VideoCompileStepExecutor.BuildInterleaveWarning(StagingVerdict(bracketed))["message"]!.GetValue<string>()
            .Should().Contain("one unbroken run");
    }

    // ---------------------------------------------------------------------------------------------
    // Contract and prompts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_justification_member_is_a_plain_string()
    {
        typeof(VideoEditDecisionOutput).GetProperty(nameof(VideoEditDecisionOutput.SingleSourceJustification))!
            .PropertyType.Should().Be(typeof(string));
    }

    // ---------------------------------------------------------------------------------------------

    private sealed class SequenceAgent : IReelBoltAgent
    {
        private readonly Queue<string> _outputs;

        public SequenceAgent(IEnumerable<string> outputs) => _outputs = new Queue<string>(outputs);

        public List<string> Prompts { get; } = [];
        public Guid? AgentId => null;
        public string Name => "VideoStoryEditor";
        public string Description => "stub";
        public string SystemPrompt => "stub";
        public AgentType AgentType => AgentType.VideoStoryEditor;
        public IReadOnlyList<AIFunction> Tools => Array.Empty<AIFunction>();
        public string? OutputSchemaJson => null;
        public Type? OutputSchemaType => typeof(VideoEditDecisionOutput);

        public Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            string output = _outputs.Count > 0 ? _outputs.Dequeue() : "{}";
            return Task.FromResult(new AgentRunResult { Output = output, TokensUsed = 1 });
        }
    }

    private sealed class CannedChatClient : IChatClient
    {
        private readonly string _text;

        public CannedChatClient(string text) => _text = text;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
