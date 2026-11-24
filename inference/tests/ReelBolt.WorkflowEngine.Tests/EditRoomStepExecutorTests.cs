using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers the parts of <see cref="EditRoomStepExecutor"/> reasonably testable without a real
/// chat-completions backend: config validation, view resolution/offered-id extraction, and the
/// solo-fallback degrade path (triggered here by making <see cref="IAgentChatClientProvider"/>
/// throw when building a room-participant agent — the cheapest reliable way to force "the room
/// failed to start" without standing up a fake <see cref="IChatClient"/>). The actual live group
/// chat is exercised separately by <c>EditRoomGroupChatManagerTests</c> (the scheduler/terminator)
/// and by <c>EditRoomSeatAgent</c>'s own containment behavior, neither of which needs a real model.
/// </summary>
public class EditRoomStepExecutorTests
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid ProjectId = Guid.NewGuid();

    private static EditRoomStepExecutor CreateExecutor(
        out Mock<IAgentChatClientProvider> chatClients,
        out Mock<IAgentToolProvider> toolProvider,
        out Mock<IAgentRegistry> agentRegistry,
        out Mock<IProjectFileWorkspace> workspace)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        agentRegistry = new Mock<IAgentRegistry>();
        workspace = new Mock<IProjectFileWorkspace>();

        // The real (not mocked) accessor: it's a pure AsyncLocal wrapper with no external
        // dependencies, and using it here means these tests actually exercise BeginScope being
        // opened correctly — the exact thing a live run found missing (every tool call inside the
        // room/solo-fallback threw "No workflow execution context is available").
        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new EditRoomStepExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
            executionContextAccessor,
            NullLogger<EditRoomStepExecutor>.Instance,
            null!);
    }

    private static StepExecutionContext CreateContext(
        string? editRoomConfigJson,
        IReadOnlyList<StepOutputHistoryEntry>? history = null,
        string? userRequest = null)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.EditRoom,
            EditRoomConfigJson = editRoomConfigJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history ?? [],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            UserRequest = userRequest,
            CancellationToken = CancellationToken.None
        };
    }

    private static string BuildViewJson(params string[] shotIds)
    {
        var shots = shotIds.Select((id, i) => new { id, startSec = i * 2.0, endSec = i * 2.0 + 2.0, durationSec = 2.0 });
        return JsonSerializer.Serialize(new
        {
            view = new { shots, silences = Array.Empty<object>(), segments = Array.Empty<object>() },
            meta = new { offeredIdCount = shotIds.Length }
        });
    }

    /// <summary>
    /// A bounded view whose shots carry the <c>"src"</c> source-clip index a multi-source
    /// <c>VideoAnalyze</c> step emits on every offered item — the exact shape
    /// <c>VideoAnalyzeStepExecutor.ShotNode</c> produces when more than one clip was analyzed.
    /// </summary>
    private static string BuildMultiSourceViewJson(params (string Id, int Src)[] shotIdsWithSources)
    {
        var shots = shotIdsWithSources.Select((s, i) => new
        {
            id = s.Id,
            startSec = i * 2.0,
            endSec = i * 2.0 + 2.0,
            durationSec = 2.0,
            src = s.Src
        });
        return JsonSerializer.Serialize(new
        {
            view = new { shots, silences = Array.Empty<object>(), segments = Array.Empty<object>() },
            meta = new { offeredIdCount = shotIdsWithSources.Length, sourceCount = shotIdsWithSources.Select(s => s.Src).Distinct().Count() }
        });
    }

    private static string ErrorCode(StepExecutionResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
    }

    private static readonly Guid RoomProviderId = Guid.NewGuid();

    [Fact]
    public async Task Missing_config_json_fails_cleanly_with_EDIT_ROOM_CONFIG_INVALID()
    {
        EditRoomStepExecutor executor = CreateExecutor(out _, out _, out _, out _);
        StepExecutionContext context = CreateContext(editRoomConfigJson: null);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EDIT_ROOM_CONFIG_INVALID");
    }

    [Fact]
    public async Task Malformed_config_json_fails_cleanly_with_EDIT_ROOM_CONFIG_INVALID()
    {
        EditRoomStepExecutor executor = CreateExecutor(out _, out _, out _, out _);
        StepExecutionContext context = CreateContext(editRoomConfigJson: "{not valid json");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EDIT_ROOM_CONFIG_INVALID");
    }

    [Fact]
    public async Task No_prior_step_output_to_resolve_the_view_from_fails_with_VIEW_UNRESOLVED()
    {
        EditRoomStepExecutor executor = CreateExecutor(out _, out _, out _, out _);
        string config = JsonSerializer.Serialize(new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous)), ConfigOptions);
        StepExecutionContext context = CreateContext(config, history: []);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("VIEW_UNRESOLVED");
    }

    [Fact]
    public async Task A_view_with_zero_offered_ids_fails_with_VIEW_UNRESOLVED()
    {
        EditRoomStepExecutor executor = CreateExecutor(out _, out _, out _, out _);
        string config = JsonSerializer.Serialize(new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous)), ConfigOptions);
        string emptyView = JsonSerializer.Serialize(new { view = new { shots = Array.Empty<object>() }, meta = new { } });
        StepExecutionContext context = CreateContext(config, history: [new StepOutputHistoryEntry(1, "Analyze", emptyView)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("VIEW_UNRESOLVED");
    }

    [Fact]
    public async Task Room_construction_failure_falls_back_to_the_solo_editor_and_still_completes()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        // Force "the room failed to start" the cheapest reliable way — no fake IChatClient needed.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s0", toId = "s1", reason = "solo fallback kept these" } },
            editRationale = "solo fallback",
            suggestedTitle = "Fallback Edit"
        });

        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = decisionJson, Success = true });

        agentRegistry
            .Setup(r => r.GetByType(AgentType.VideoStoryEditor, null))
            .Returns(soloAgent.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), FallbackToSoloEditor: true),
            ConfigOptions);
        string viewJson = BuildViewJson("s0", "s1", "s2");
        StepExecutionContext context = CreateContext(config, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("keep")[0].GetProperty("fromId").GetString().Should().Be("s0");
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeTrue();

        soloAgent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Room_failure_with_fallback_disabled_fails_cleanly_with_EDIT_ROOM_FAILED()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out _, out _);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), FallbackToSoloEditor: false),
            ConfigOptions);
        string viewJson = BuildViewJson("s0", "s1");
        StepExecutionContext context = CreateContext(config, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EDIT_ROOM_FAILED");
    }

    [Fact]
    public async Task Solo_fallback_drops_a_Keep_span_referencing_an_id_that_was_never_offered()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        // s0/s1 were offered; s99 was never shown to any agent.
        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[]
            {
                new { fromId = "s0", toId = "s0", reason = "valid" },
                new { fromId = "s99", toId = "s99", reason = "hallucinated id" }
            },
            editRationale = "solo fallback",
            suggestedTitle = "Fallback Edit"
        });

        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = decisionJson, Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoStoryEditor, null)).Returns(soloAgent.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous)), ConfigOptions);
        string viewJson = BuildViewJson("s0", "s1");
        StepExecutionContext context = CreateContext(config, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        System.Text.Json.Nodes.JsonArray? keep = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonArray>(doc.RootElement.GetProperty("keep").GetRawText());
        keep.Should().HaveCount(1, "the span referencing the unoffered id 's99' must be dropped, never trusted");
        keep![0]!["fromId"]!.GetValue<string>().Should().Be("s0");
        doc.RootElement.GetProperty("room").GetProperty("droppedSpanCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_completed_room_run_persists_ChatTranscriptJson_with_one_entry_per_turn()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        // Every room-participant agent (each seat and the director) resolves through this same
        // provider call — a single canned FakeChatClient stands in for a real model, mentioning
        // the offered id "s1" so ExtractOfferedIdMentions has something to find.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("Let's keep s1 through the intro."));

        // Outside the live chat loop, the director's STANDALONE synthesis call (via IAgentRegistry,
        // not IAgentChatClientProvider) converts the room's discussion into the final decision.
        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s1", toId = "s1", reason = "room agreed" } },
            editRationale = "room synthesis",
            suggestedTitle = "Room Edit"
        });
        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = decisionJson, Success = true });
        agentRegistry
            .Setup(r => r.GetByType(AgentType.VideoEditDirector, null))
            .Returns(directorAgent.Object);

        // One seat, one round, a 2-turn ceiling, FixedTurns (ignore the sentinel/convergence
        // checks) — the smallest room shape that still produces a deterministic "seat then
        // director" transcript: turn 0 is the seat, turn 1 is the director.
        var config = new EditRoomStepConfig(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false);
        string configJson = JsonSerializer.Serialize(config, ConfigOptions);
        string viewJson = BuildViewJson("s0", "s1");
        StepExecutionContext context = CreateContext(configJson, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.ChatTranscriptJson.Should().NotBeNullOrEmpty();

        using JsonDocument transcriptDoc = JsonDocument.Parse(result.ChatTranscriptJson!);
        JsonElement.ArrayEnumerator turns = transcriptDoc.RootElement.EnumerateArray();
        List<JsonElement> turnList = turns.ToList();

        turnList.Should().HaveCount(2, "one seat turn plus one director turn, per the 1-seat/1-round/2-turn-ceiling config");

        turnList[0].GetProperty("turnIndex").GetInt32().Should().Be(0);
        turnList[0].GetProperty("speaker").GetString().Should().Be("Seat0");
        turnList[0].GetProperty("speakerRole").GetString().Should().Be("editor");
        turnList[0].GetProperty("text").GetString().Should().Be("Let's keep s1 through the intro.",
            "DB persistence uses the FULL untruncated turn text, unlike the ~600-char SSE broadcast");
        turnList[0].GetProperty("truncated").GetBoolean().Should().BeFalse();
        turnList[0].GetProperty("idsMentioned").EnumerateArray().Select(e => e.GetString()).Should().Contain("s1");

        turnList[1].GetProperty("turnIndex").GetInt32().Should().Be(1);
        turnList[1].GetProperty("speaker").GetString().Should().Be("Director");
        turnList[1].GetProperty("speakerRole").GetString().Should().Be("director");
    }

    [Fact]
    public async Task A_room_run_records_one_usage_row_per_seat_turn_under_the_step_scope()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        // The metering wrapper the real ChatClientFactory puts around every client, over a canned model that
        // reports usage on the streaming path the room drives its participants through.
        CapturingSink sink = new();
        UsageRecorder recorder = new(new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        UsageReportingChatClient model = new(inputTokens: 100, outputTokens: 20);
        IChatClient metered = new MeteringChatClient(
            model, new ResolvedInferenceProvider(Guid.NewGuid(), "room", InferenceProviderKind.OpenAICompatible, "http://x", "room-model", "", 30), recorder);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metered);

        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = SimpleDecisionJson("s1"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, null)).Returns(directorAgent.Object);

        // Two seats + the director, FixedTurns, a 3-turn ceiling: exactly three live model turns.
        var config = new EditRoomStepConfig(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first"), new EditRoomSeat("Seat1", "second")],
            Rounds: 1,
            MaxTurns: 3,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false);
        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(config, ConfigOptions),
            history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        Guid stepResult = Guid.NewGuid();
        using (UsageScope.Begin(Guid.NewGuid(), ProjectId, context.Execution.Id, stepResult, null, UsageSource.Workflow))
        {
            StepExecutionResult result = await executor.ExecuteAsync(context);
            result.Status.Should().Be(StepStatus.Completed);
        }

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        model.Calls.Should().Be(3);
        sink.Events.Where(e => e.Kind == UsageKind.LlmInputTokens).Should().HaveCount(3, "one input row per seat turn");
        sink.Events.Where(e => e.Kind == UsageKind.LlmOutputTokens).Should().HaveCount(3);
        sink.Events.Should().OnlyContain(e => e.StepResultId == stepResult && e.ModelName == "room-model");
        sink.Events.Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Room_charter_prompt_carries_the_multi_source_single_clip_span_rule()
    {
        // The solo VideoStoryEditor prompt has carried a "Multiple source clips" section since
        // multi-source support shipped; the room charter every seat (and the director's
        // room-participant turns) runs under must state the same hard rule, or the room can
        // converge on a cross-clip span the compile step then rejects as MIXED_SOURCE_SPAN.
        EditRoomStepExecutor.EditRoomCharterPrompt.Should().Contain("\"src\"",
            "seats must be told what the src index on every offered id means");
        EditRoomStepExecutor.EditRoomCharterPrompt.Should().Contain("SAME clip",
            "the one hard multi-source rule — a single kept run never bridges two clips — must be stated");
    }

    [Fact]
    public async Task Solo_fallback_drops_a_Keep_span_bridging_two_source_clips()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        // s0/s1 belong to clip 0, s2/s3 to clip 1. "s1 → s2" bridges the two physical files — the
        // exact shape VideoCompileStepExecutor rejects hard as MIXED_SOURCE_SPAN. The room-level
        // validation must drop it (degrade, not fail) while keeping the two valid single-clip spans.
        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[]
            {
                new { fromId = "s0", toId = "s1", reason = "clip 0 material" },
                new { fromId = "s1", toId = "s2", reason = "bridges clips (invalid)" },
                new { fromId = "s2", toId = "s3", reason = "clip 1 material" }
            },
            editRationale = "solo fallback",
            suggestedTitle = "Cross-clip Edit"
        });

        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = decisionJson, Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoStoryEditor, null)).Returns(soloAgent.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous)), ConfigOptions);
        string viewJson = BuildMultiSourceViewJson(("s0", 0), ("s1", 0), ("s2", 1), ("s3", 1));
        StepExecutionContext context = CreateContext(config, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        List<JsonElement> keep = doc.RootElement.GetProperty("keep").EnumerateArray().ToList();
        keep.Should().HaveCount(2, "the cross-clip span must be dropped, the two single-clip spans kept");
        keep[0].GetProperty("fromId").GetString().Should().Be("s0");
        keep[0].GetProperty("toId").GetString().Should().Be("s1");
        keep[1].GetProperty("fromId").GetString().Should().Be("s2");
        keep[1].GetProperty("toId").GetString().Should().Be("s3");

        JsonElement room = doc.RootElement.GetProperty("room");
        room.GetProperty("droppedSpanCount").GetInt32().Should().Be(1);
        room.GetProperty("droppedMixedSourceSpanCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Solo_fallback_usage_is_reported_as_the_step_token_count()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no provider"));

        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s0", toId = "s1", reason = "r" } },
            editRationale = "solo",
            suggestedTitle = "T"
        });
        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = decisionJson, Success = true, InputTokens = 120, OutputTokens = 30, PrimaryProviderId = RoomProviderId, PrimaryModelName = "room-model" });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoStoryEditor, null)).Returns(soloAgent.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), FallbackToSoloEditor: true),
            ConfigOptions);
        StepExecutionContext context = CreateContext(
            config, history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.TokensUsed.Should().Be(150);
        result.InputTokens.Should().Be(120);
        result.OutputTokens.Should().Be(30);
        result.PrimaryProviderId.Should().Be(RoomProviderId);
        result.PrimaryModelName.Should().Be("room-model");
    }

    [Fact]
    public async Task Every_synthesis_attempt_usage_is_summed_into_the_step_token_count()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("Keep s0."));

        string bad = "not json at all";
        string good = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s0", toId = "s1", reason = "r" } },
            editRationale = "ok",
            suggestedTitle = "T"
        });
        var director = new Mock<IReelBoltAgent>();
        director
            .SetupSequence(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = bad, Success = true, InputTokens = 1000, OutputTokens = 100 })
            .ReturnsAsync(new AgentRunResult { Output = good, Success = true, InputTokens = 2000, OutputTokens = 200 });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, null)).Returns(director.Object);

        var config = new EditRoomStepConfig(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1, MaxTurns: 2, Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20, PersistTranscript: false, MaxSynthesisAttempts: 2);
        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(config, ConfigOptions),
            history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.TokensUsed.Should().BeGreaterThanOrEqualTo(3300, "both synthesis attempts (3300) plus any reported turn usage");
        result.InputTokens.Should().BeGreaterThanOrEqualTo(3000);
    }

    [Fact]
    public async Task Synthesis_retries_with_mixed_source_feedback_then_accepts_the_corrected_decision()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        // A real (canned) room run over a multi-source view — both seats and the director speak.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("Keep s0 from the first clip and s2 from the second."));

        string mixedDecisionJson = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s0", toId = "s2", reason = "bridges clips (invalid)" } },
            editRationale = "first synthesis attempt",
            suggestedTitle = "Bad Bridge"
        });
        string correctedDecisionJson = JsonSerializer.Serialize(new
        {
            keep = new[]
            {
                new { fromId = "s0", toId = "s1", reason = "clip 0 run" },
                new { fromId = "s2", toId = "s2", reason = "clip 1 run" }
            },
            editRationale = "corrected synthesis attempt",
            suggestedTitle = "Cross-clip Sequence"
        });

        var capturedPrompts = new List<string>();
        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .SetupSequence(a => a.RunAsync(Moq.Capture.In(capturedPrompts), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = mixedDecisionJson, Success = true })
            .ReturnsAsync(new AgentRunResult { Output = correctedDecisionJson, Success = true });
        agentRegistry
            .Setup(r => r.GetByType(AgentType.VideoEditDirector, null))
            .Returns(directorAgent.Object);

        var config = new EditRoomStepConfig(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false,
            MaxSynthesisAttempts: 2);
        string configJson = JsonSerializer.Serialize(config, ConfigOptions);
        string viewJson = BuildMultiSourceViewJson(("s0", 0), ("s1", 0), ("s2", 1));
        StepExecutionContext context = CreateContext(configJson, history: [new StepOutputHistoryEntry(1, "Analyze", viewJson)]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);

        List<JsonElement> keep = doc.RootElement.GetProperty("keep").EnumerateArray().ToList();
        keep.Should().HaveCount(2, "the corrected second synthesis attempt must be the accepted decision");
        keep[0].GetProperty("toId").GetString().Should().Be("s1");
        keep[1].GetProperty("fromId").GetString().Should().Be("s2");

        JsonElement room = doc.RootElement.GetProperty("room");
        room.GetProperty("degraded").GetBoolean().Should().BeFalse("a successful retry is not a degrade");
        room.GetProperty("synthesisAttempts").GetInt32().Should().Be(2);
        room.GetProperty("droppedSpanCount").GetInt32().Should().Be(0);
        room.GetProperty("droppedMixedSourceSpanCount").GetInt32().Should().Be(0);

        capturedPrompts.Should().HaveCount(2);
        capturedPrompts[1].Should().Contain("different source clips",
            "the retry prompt must tell the director exactly why the first decision was rejected");
    }

    /// <summary>
    /// Trivial canned <see cref="IChatClient"/> — always returns the same fixed assistant text
    /// regardless of the messages sent to it, on both the streaming path (what the group chat host
    /// actually drives participants through — see <c>EditRoomSeatAgent</c>'s doc comment) and the
    /// non-streaming path. Zero network calls.
    /// </summary>
    // --- The workflow's user request must reach a room. A room step is documented as a drop-in
    // replacement for the solo agent step it supersedes, and StepExecutionContext.BuildAgentInput
    // appends the request for every ordinary Agent step — but RoomStepExecutorBase builds its own
    // three prompts from the bounded view and, for a while, appended it to none of them. The
    // symptom was silent: a real run asked the graphics room, in the request, to plan one tracked
    // screen insert per plate region; the room never saw the sentence and planned zero. ---

    [Fact]
    public async Task The_user_request_reaches_every_room_participant_in_the_opening_message()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        PromptCapturingChatClient roomClient = new("Let's keep s1 through the intro.");
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(roomClient);

        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = SimpleDecisionJson("s1"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), Rounds: 1, MaxTurns: 2),
            ConfigOptions);
        StepExecutionContext context = CreateContext(
            config,
            history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))],
            userRequest: "Keep it under 40 seconds and open on something human.");

        await executor.ExecuteAsync(context);

        roomClient.Prompts.Should().NotBeEmpty("the room must actually have run");
        roomClient.Prompts.Should().AllSatisfy(p =>
            p.Should().Contain("Keep it under 40 seconds and open on something human."));
    }

    [Fact]
    public async Task The_user_request_reaches_the_synthesis_call()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("Let's keep s1 through the intro."));

        string? synthesisPrompt = null;
        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((prompt, _, _) => synthesisPrompt = prompt)
            .ReturnsAsync(new AgentRunResult { Output = SimpleDecisionJson("s1"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), Rounds: 1, MaxTurns: 2),
            ConfigOptions);
        StepExecutionContext context = CreateContext(
            config,
            history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))],
            userRequest: "Land the reveal in the back half.");

        await executor.ExecuteAsync(context);

        synthesisPrompt.Should().NotBeNull();
        synthesisPrompt.Should().Contain("Land the reveal in the back half.");
    }

    [Fact]
    public async Task The_user_request_reaches_the_solo_fallback_prompt()
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        // Same "the room could not start" trigger the other fallback tests use.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        string? soloPrompt = null;
        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((prompt, _, _) => soloPrompt = prompt)
            .ReturnsAsync(new AgentRunResult { Output = SimpleDecisionJson("s0"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoStoryEditor, null)).Returns(soloAgent.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous)), ConfigOptions);
        StepExecutionContext context = CreateContext(
            config,
            history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))],
            userRequest: "Both chroma-plate clips must be in the cut.");

        await executor.ExecuteAsync(context);

        soloPrompt.Should().NotBeNull();
        soloPrompt.Should().Contain("Both chroma-plate clips must be in the cut.");
    }

    [Fact]
    public async Task No_user_request_leaves_the_room_prompts_exactly_as_they_were()
    {
        // The overwhelmingly common case (every seeded template runs with RequiresUserInput false)
        // must be byte-identical to the pre-fix prompt — no stray separator, no empty section.
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);

        PromptCapturingChatClient roomClient = new("Let's keep s1 through the intro.");
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(roomClient);

        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = SimpleDecisionJson("s1"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);

        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), Rounds: 1, MaxTurns: 2),
            ConfigOptions);
        StepExecutionContext context = CreateContext(
            config, history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        await executor.ExecuteAsync(context);

        roomClient.Prompts.Should().NotBeEmpty();
        roomClient.Prompts.Should().AllSatisfy(p => p.Should().NotContain("User Request:"));
    }

    // --- Dynamics in the room (docs/design/dynamic-edit.md): a run with a pace tells the seats and the director that the catalogue
    // moves are allowed, through the style nudge's room wording; a run without one prompts as before. ---
    private static IDisposable EnterDynamicsRun(ReelBolt.Shared.Styles.StyleDynamics? dynamics) =>
        ReelBolt.WorkflowEngine.Services.Styles.RunStyleAmbient.Enter(new ReelBolt.WorkflowEngine.Services.Styles.RunStyleState(
            new ReelBolt.Shared.Styles.RunStyleSnapshot(null, null, null, new ReelBolt.Shared.Styles.StyleConfig(Dynamics: dynamics)),
            null,
            ReelBolt.WorkflowEngine.Services.Styles.StyleNudgeService.Instance));

    private static async Task<(PromptCapturingChatClient Room, string? Synthesis, StepExecutionResult Result)> RunRoomAsync(string directorJson)
    {
        EditRoomStepExecutor executor = CreateExecutor(
            out Mock<IAgentChatClientProvider> chatClients, out _, out Mock<IAgentRegistry> agentRegistry, out _);
        PromptCapturingChatClient roomClient = new("Let's keep s1 through the intro.");
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(roomClient);
        string? synthesisPrompt = null;
        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((prompt, _, _) => synthesisPrompt = prompt)
            .ReturnsAsync(new AgentRunResult { Output = directorJson, Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.VideoEditDirector, It.IsAny<Guid?>())).Returns(director.Object);
        string config = JsonSerializer.Serialize(
            new EditRoomStepConfig(View: new ExtractInputRef(ExtractInputSource.Previous), Rounds: 1, MaxTurns: 2),
            ConfigOptions);
        StepExecutionContext context = CreateContext(
            config, history: [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);
        StepExecutionResult result = await executor.ExecuteAsync(context);
        return (roomClient, synthesisPrompt, result);
    }

    [Fact]
    public async Task A_dynamics_run_tells_seats_and_director_the_moves_are_allowed_and_how_to_cut_to_the_beat()
    {
        using IDisposable run = EnterDynamicsRun(ReelBolt.Shared.Styles.StyleDynamics.Medium);
        var (room, synthesis, _) = await RunRoomAsync(SimpleDecisionJson("s1"));

        room.PromptsWithInstructions.Should().NotBeEmpty();
        room.PromptsWithInstructions.Should().AllSatisfy(p =>
        {
            p.Should().Contain("the catalogue moves ARE allowed");            // (a) overrides the charter's "no effect" line
            p.Should().Contain("dynamics list").And.Contain("words and offered ids");
            p.Should().Contain("Cutting to the beat").And.Contain("music.beat").And.Contain("beatSec");  // (b)
            p.Should().Contain("at most a few moves").And.Contain("anchor id");  // (c)
        });
        synthesis.Should().NotBeNull();
        synthesis.Should().Contain("the catalogue moves ARE allowed").And.Contain("Cutting to the beat").And.Contain("at most a few moves");
    }

    [Fact]
    public async Task A_run_without_dynamics_prompts_the_room_exactly_as_before()
    {
        var (withNone, synthesisNone, _) = await RunRoomAsync(SimpleDecisionJson("s1"));
        using IDisposable run = EnterDynamicsRun(null);
        var (withNull, synthesisNull, _) = await RunRoomAsync(SimpleDecisionJson("s1"));

        string[] all = [.. withNone.PromptsWithInstructions, .. withNull.PromptsWithInstructions, synthesisNone ?? "", synthesisNull ?? ""];
        all.Should().AllSatisfy(p => p.Should().NotContain("ARE allowed").And.NotContain("Cutting to the beat"));
        withNull.PromptsWithInstructions.Should().Equal(withNone.PromptsWithInstructions);
        synthesisNull.Should().Be(synthesisNone);
    }

    [Fact]
    public async Task Dynamics_entries_in_the_directors_synthesis_survive_to_the_step_output_as_the_decisions_Dynamics()
    {
        string json = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s1", toId = "s1", reason = "canned" } },
            editRationale = "canned",
            suggestedTitle = "Canned",
            dynamics = new[] { new { effect = "PunchIn", anchorId = "s1", moment = "OnBeat", intensity = "Strong", reason = "the hit" } }
        });
        using IDisposable run = EnterDynamicsRun(ReelBolt.Shared.Styles.StyleDynamics.Fast);
        var (_, _, result) = await RunRoomAsync(json);

        result.Status.Should().Be(StepStatus.Completed);
        VideoEditDecisionOutput? decision = JsonSerializer.Deserialize<VideoEditDecisionOutput>(result.Output, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        decision!.Dynamics.Should().ContainSingle();
        decision.Dynamics[0].Effect.Should().Be("PunchIn");
        decision.Dynamics[0].AnchorId.Should().Be("s1");
        decision.Dynamics[0].Moment.Should().Be("OnBeat");
    }

    private static string SimpleDecisionJson(string shotId) => JsonSerializer.Serialize(new
    {
        keep = new[] { new { fromId = shotId, toId = shotId, reason = "canned" } },
        editRationale = "canned",
        suggestedTitle = "Canned"
    });

    /// <summary>
    /// <see cref="FakeChatClient"/> plus a record of every prompt it was handed, so a test can
    /// assert on what the room participants actually saw rather than only on what came back.
    /// </summary>
    private sealed class PromptCapturingChatClient : IChatClient
    {
        private readonly string _text;
        private readonly List<string> _prompts = [];
        private readonly List<string> _withInstructions = [];

        /// <summary>Each call's messages preceded by the agent's instructions (the seat's charter and style blocks).</summary>
        public IReadOnlyList<string> PromptsWithInstructions => _withInstructions;

        public PromptCapturingChatClient(string text) => _text = text;

        public IReadOnlyList<string> Prompts => _prompts;

        // The seat's charter reaches the client as the agent's instructions (options) or a system message, so both are recorded.
        private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options = null)
        {
            _prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            _withInstructions.Add(string.Join("\n", messages.Select(m => m.Text).Prepend(options?.Instructions)));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _text;

        public FakeChatClient(string text) => _text = text;

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

    /// <summary>
    /// A room seat never gets <c>LookAtMoment</c>: it spends, and several seats asking about the same moments would pay
    /// for one answer several times. Every other tool of the agent's grant stays. The solo fallback is an ordinary run
    /// of the story editor and keeps the tool (it does not go through the room-turn list).
    /// </summary>
    [Fact]
    public void A_room_turn_never_gets_the_paid_look_at_a_moment()
    {
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider
            .Setup(p => p.GetTools(AgentType.VideoStoryEditor))
            .Returns(
            [
                AIFunctionFactory.Create(() => "x", "ListProjectFiles"),
                AIFunctionFactory.Create(() => "x", "ListVideoEvents"),
                AIFunctionFactory.Create(() => "x", "LookAtMoment"),
                AIFunctionFactory.Create(() => "x", "FailWorkflow")
            ]);

        var executor = new TestableEditRoomStepExecutor(toolProvider.Object);

        executor.GetRoomTurnToolsPublic(AgentType.VideoStoryEditor).Select(t => t.Name)
            .Should().Equal("ListProjectFiles", "ListVideoEvents", "FailWorkflow");
    }

    /// <summary>Exposes the protected room-turn tool list for direct assertion.</summary>
    private sealed class TestableEditRoomStepExecutor(IAgentToolProvider toolProvider) : EditRoomStepExecutor(
        new Mock<IAgentChatClientProvider>().Object, toolProvider, new Mock<IAgentRegistry>().Object,
        new Mock<IProjectFileWorkspace>().Object, new WorkflowExecutionContextAccessor(),
        NullLogger<EditRoomStepExecutor>.Instance, null!)
    {
        public IReadOnlyList<AIFunction> GetRoomTurnToolsPublic(AgentType agentType) => GetRoomTurnTools(agentType);
    }
}
