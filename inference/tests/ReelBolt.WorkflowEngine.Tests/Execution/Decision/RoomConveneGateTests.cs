using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Covers the convening seam the room infrastructure gained on top of
/// <see cref="RoomStepExecutorBase{TDecision}"/>: the <see cref="IRoomStepConfig.ConveneGate"/>
/// config surface, the three virtual seams, and the three arms that exist: <c>Off</c> (which must be
/// byte-identically the pre-change path), <c>Shadow</c> (post-synthesis calibration observations,
/// strictly additive) and <c>Gate</c> (a pre-room decision on whether the room is needed at all).
///
/// <para>
/// The color grade room stands in for all three rooms: the convening surface lives entirely in the
/// shared base and the shared <see cref="IRoomStepConfig"/> implementation, so a per-room
/// re-test would restate the same assertions three times. The three rooms' own configs are covered
/// together by <see cref="Every_room_config_implements_the_convene_surface_with_the_same_defaults"/>.
/// </para>
///
/// <para>
/// <b>No live Decision provider is involved anywhere here</b> — <see cref="IDecisionGate"/> is
/// always a Moq mock, never the real <see cref="DecisionGate"/>. That is deliberate and load
/// bearing: this environment has no <c>Capability = Decision</c> provider row and zero
/// <c>decision_observations</c>, so no live calibration data exists or could be produced. Nothing
/// in this file asserts a calibrated threshold; the 0.85/0.25 wiring is checked as a value passed
/// through, never as a tuned one.
/// </para>
/// </summary>
public class RoomConveneGateTests
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid ProjectId = Guid.NewGuid();

    /// <summary>
    /// The exact key set of the <c>room</c> metadata object a room step emits. Frozen here so a
    /// gate arm that adds an observability key to it fails loudly instead of silently widening the
    /// persisted output contract every downstream consumer deserializes.
    /// </summary>
    private static readonly string[] ExpectedRoomMetaKeys =
    [
        "seats", "rounds", "maxTurns", "turnCount", "terminationReason", "converged",
        "synthesisAttempts", "droppedSpanCount", "degraded", "degradeReason",
        "transcriptArtifactStorageKey"
    ];

    // ---------------------------------------------------------------------
    // Construction — mirrors ColorGradeRoomStepExecutorTests exactly, with the gate threaded in.
    // ---------------------------------------------------------------------

    private static SeamProbeExecutor CreateExecutor(
        IDecisionGate? decisionGate,
        out Mock<IAgentChatClientProvider> chatClients,
        out Mock<IAgentRegistry> agentRegistry)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        // The real (not mocked) accessor — same rationale as the other room suites: it is a pure
        // AsyncLocal wrapper, and using it means these tests exercise BeginScope actually opening.
        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new SeamProbeExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
            executionContextAccessor, NullLogger<ColorGradeRoomStepExecutor>.Instance, decisionGate);
    }

    private static ShadowQuestionsRoomExecutor CreateShadowExecutor(
        IDecisionGate? decisionGate,
        Func<ColorGradePlanOutput, IReadOnlyList<DecisionGateQuestion>?> buildQuestions,
        out Mock<IAgentChatClientProvider> chatClients,
        out Mock<IAgentRegistry> agentRegistry)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new ShadowQuestionsRoomExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
            executionContextAccessor, NullLogger<ColorGradeRoomStepExecutor>.Instance, decisionGate,
            buildQuestions);
    }

    private static StepExecutionContext CreateContext(
        string colorGradeRoomConfigJson, IReadOnlyList<StepOutputHistoryEntry> history,
        string? userRequest = null, string? retryReason = null,
        CancellationToken cancellationToken = default)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.ColorGradeRoom,
            ColorGradeRoomConfigJson = colorGradeRoomConfigJson
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = cancellationToken,
            UserRequest = userRequest
        };

        // The two optional pieces are exactly the ones the opening-message construction site
        // composes in (WithUserRequest/WithRetryGuidance), so a test can reproduce the pre-agenda
        // message in the same terms the production code builds it.
        if (!string.IsNullOrWhiteSpace(retryReason))
            context.RecordRetryFeedback(1, retryReason);

        return context;
    }

    private static string BuildViewJson(params string[] shotIds)
    {
        var shots = shotIds.Select((id, i) => new
        {
            id,
            startSec = i * 2.0,
            endSec = i * 2.0 + 2.0,
            v = new { temp = "Cool", tone = "Balanced", sat = "Natural" }
        });
        return JsonSerializer.Serialize(new
        {
            view = new { shots },
            meta = new { offeredIdCount = shotIds.Length }
        });
    }

    private static string BuildPlanJson(string look = "Warm", string rationale = "test") =>
        JsonSerializer.Serialize(new
        {
            look,
            strength = "Subtle",
            shadowTone = "Neutral",
            highlightTone = "Neutral",
            reason = "test reason",
            planRationale = rationale
        });

    /// <summary>
    /// The smallest room shape that still produces a deterministic "seat then director" transcript
    /// and a completed step: one seat, one round, a two-turn ceiling, FixedTurns. PersistTranscript
    /// is off so no artifact upload is attempted.
    /// </summary>
    private static ColorGradeRoomStepConfig SmallestRoom(ConveneGateMode gate = ConveneGateMode.Off) =>
        new(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false,
            ConveneGate: gate);

    /// <summary>Wires the canned chat client every room participant resolves through and the director agent the standalone synthesis call resolves through.</summary>
    private static void WireHappyPathRoom(
        Mock<IAgentChatClientProvider> chatClients,
        Mock<IAgentRegistry> agentRegistry)
    {
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("s1 leans cool; a subtle warm lift suits it."));

        WireHappyPathDirector(agentRegistry);
    }

    /// <summary>
    /// The director half of <see cref="WireHappyPathRoom"/> on its own, for suites that supply the
    /// participants' chat client themselves (e.g. to capture what a participant was actually sent).
    /// </summary>
    private static void WireHappyPathDirector(Mock<IAgentRegistry> agentRegistry)
    {
        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = BuildPlanJson(rationale: "room synthesis"), Success = true });
        agentRegistry
            .Setup(r => r.GetByType(AgentType.ColorGradeDirector, null))
            .Returns(directorAgent.Object);
    }

    private static JsonElement RoomMeta(StepExecutionResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("room").Clone();
    }

    /// <summary>
    /// The grade the room's canned director synthesizes, and the values the deliberate-answer test
    /// uses. Every word is deliberately NOT the first option of its list (Look's first is "None",
    /// Strength's is "Subtle", both tones' is "Neutral"), so any implementation that echoed
    /// <c>Options[0]</c> — or hardcoded a default — fails rather than passing by coincidence.
    /// </summary>
    private const string DecisionLook = "Filmic";
    private const string DecisionStrength = "Strong";
    private const string DecisionShadowTone = "Deepened";
    private const string DecisionHighlightTone = "Brightened";

    /// <summary>
    /// The solo <see cref="AgentType.Colorist"/> agent's own four questions, read out of
    /// <see cref="AgentStepExecutor"/> by reflection. <c>BuildColoristQuestions</c> is
    /// <c>private static</c> to that class and that file is explicitly out of scope for this
    /// change, so reflection is the only way to compare the room's copies against the original —
    /// and that comparison is the entire reason the duplication is allowed to exist. If the solo
    /// builder is ever renamed or made non-private, this helper fails loudly rather than silently
    /// comparing nothing.
    /// </summary>
    private static IReadOnlyList<DecisionGateQuestion> SoloColoristQuestions()
    {
        MethodInfo? builder = typeof(AgentStepExecutor).GetMethod(
            "BuildColoristQuestions", BindingFlags.NonPublic | BindingFlags.Static);

        builder.Should().NotBeNull(
            "AgentStepExecutor.BuildColoristQuestions is the solo Colorist agent's own question " +
            "builder, and the room's four questions are a copy of it");

        string json = JsonSerializer.Serialize(
            new ColorGradePlanOutput
            {
                Look = DecisionLook,
                Strength = DecisionStrength,
                ShadowTone = DecisionShadowTone,
                HighlightTone = DecisionHighlightTone
            },
            ConfigOptions);

        return (IReadOnlyList<DecisionGateQuestion>)builder!.Invoke(null, [json])!;
    }

    /// <summary>
    /// Wires the canned chat client the room's one seat resolves through and the director agent its
    /// standalone synthesis call resolves through, for the edit room's decision shape.
    /// </summary>
    private static void WireHappyPathEditRoom(
        Mock<IAgentChatClientProvider> chatClients,
        Mock<IAgentRegistry> agentRegistry)
    {
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("s0 and s1 both read well; keep them."));

        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Success = true,
                Output = JsonSerializer.Serialize(
                    new
                    {
                        keep = new[] { new { fromId = "s0", toId = "s1", reason = "test" } },
                        editRationale = "room synthesis",
                        suggestedTitle = "Test"
                    },
                    ConfigOptions)
            });
        agentRegistry
            .Setup(r => r.GetByType(AgentType.VideoEditDirector, null))
            .Returns(directorAgent.Object);
    }

    /// <summary>The edit room's counterpart of <see cref="WireHappyPathEditRoom"/>, for the graphics room's decision shape (an empty plan — a fully valid outcome).</summary>
    private static void WireHappyPathGraphicsRoom(
        Mock<IAgentChatClientProvider> chatClients,
        Mock<IAgentRegistry> agentRegistry)
    {
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("p0 is the only candidate; I would leave it plain."));

        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Success = true,
                Output = JsonSerializer.Serialize(
                    new
                    {
                        overlays = Array.Empty<object>(),
                        inserts = Array.Empty<object>(),
                        planRationale = "room synthesis"
                    },
                    ConfigOptions)
            });
        agentRegistry
            .Setup(r => r.GetByType(AgentType.MotionGraphicsDirector, null))
            .Returns(directorAgent.Object);
    }

    /// <summary>
    /// The smallest EDIT room shape that still produces a deterministic transcript and a completed
    /// step — the same shape as <see cref="SmallestRoom"/>, in the edit room's config type.
    /// PersistTranscript is off so no artifact upload is attempted.
    /// </summary>
    private static EditRoomStepConfig SmallestEditRoom(ConveneGateMode gate) =>
        new(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false,
            ConveneGate: gate);

    /// <summary>The smallest GRAPHICS room shape — <see cref="SmallestEditRoom"/> in the graphics room's config type.</summary>
    private static GraphicsRoomStepConfig SmallestGraphicsRoom(ConveneGateMode gate) =>
        new(
            View: new ExtractInputRef(ExtractInputSource.Previous),
            Seats: [new EditRoomSeat("Seat0", "first")],
            Rounds: 1,
            MaxTurns: 2,
            Termination: EditRoomTerminationMode.FixedTurns,
            RoomTimeoutSeconds: 20,
            PersistTranscript: false,
            ConveneGate: gate);

    /// <summary>
    /// A step context for the EDIT room — the same shape <see cref="CreateContext"/> builds, with
    /// the edit room's step type and config column.
    /// </summary>
    private static StepExecutionContext CreateEditRoomContext(
        string editRoomConfigJson, IReadOnlyList<StepOutputHistoryEntry> history)
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
            StepOutputHistory = history,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>The GRAPHICS room counterpart of <see cref="CreateEditRoomContext"/>.</summary>
    private static StepExecutionContext CreateGraphicsRoomContext(
        string graphicsRoomConfigJson, IReadOnlyList<StepOutputHistoryEntry> history)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.GraphicsRoom,
            GraphicsRoomConfigJson = graphicsRoomConfigJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>The bounded view the edit and graphics rooms deliberate over — one <c>s{n}</c> shot list, plus a <c>p0</c> placement for the graphics room.</summary>
    private static string BuildEditRoomViewJson() =>
        JsonSerializer.Serialize(
            new
            {
                view = new
                {
                    shots = new[]
                    {
                        new { id = "s0", startSec = 0.0, endSec = 2.0, durationSec = 2.0 },
                        new { id = "s1", startSec = 2.0, endSec = 4.0, durationSec = 2.0 }
                    },
                    silences = Array.Empty<object>(),
                    segments = Array.Empty<object>(),
                    placements = new[] { new { id = "p0", region = "LowerThird", fit = 80, text = "Light" } }
                },
                meta = new { offeredIdCount = 3 }
            });

    // =====================================================================
    // The config surface
    // =====================================================================

    [Fact]
    public void A_default_ColorGradeRoomStepConfig_reports_exactly_the_four_convene_defaults()
    {
        var config = new ColorGradeRoomStepConfig();
        var asInterface = (IRoomStepConfig)config;

        config.ConveneGate.Should().Be(ConveneGateMode.Off);
        config.ConveneAcceptAt.Should().Be(0.85);
        config.ConveneMinMargin.Should().Be(0.25);
        config.ConveneProviderId.Should().BeNull();

        asInterface.ConveneGate.Should().Be(ConveneGateMode.Off);
        asInterface.ConveneAcceptAt.Should().Be(0.85);
        asInterface.ConveneMinMargin.Should().Be(0.25);
        asInterface.ConveneProviderId.Should().BeNull();
    }

    [Fact]
    public void Every_room_config_implements_the_convene_surface_with_the_same_defaults()
    {
        IRoomStepConfig[] configs =
        [
            new EditRoomStepConfig(),
            new GraphicsRoomStepConfig(),
            new ColorGradeRoomStepConfig()
        ];

        foreach (IRoomStepConfig config in configs)
        {
            config.ConveneGate.Should().Be(ConveneGateMode.Off, $"{config.GetType().Name} must default the gate off");
            config.ConveneAcceptAt.Should().Be(0.85, $"{config.GetType().Name} accept-at default");
            config.ConveneMinMargin.Should().Be(0.25, $"{config.GetType().Name} min-margin default");
            config.ConveneProviderId.Should().BeNull($"{config.GetType().Name} has no provider by default");
        }
    }

    [Fact]
    public void Persisted_config_json_without_the_convene_members_still_deserializes_to_the_off_defaults()
    {
        // The additive-member contract: an EditRoom/GraphicsRoom/ColorGradeRoom step row written
        // before this change (or by an older deployment) must keep deserializing, with the gate
        // simply off. The members are last in each primary constructor precisely so this holds.
        const string legacyJson = """{"version":1,"rounds":2,"maxTurns":8,"look":"ignored"}""";

        ColorGradeRoomStepConfig? config =
            JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(legacyJson, ConfigOptions);

        config.Should().NotBeNull();
        config!.Rounds.Should().Be(2);
        config.ConveneGate.Should().Be(ConveneGateMode.Off);
        config.ConveneAcceptAt.Should().Be(0.85);
        config.ConveneMinMargin.Should().Be(0.25);
        config.ConveneProviderId.Should().BeNull();
    }

    [Fact]
    public void ConveneGateMode_mirrors_DecisionGateMode_state_for_state()
    {
        // The two enums are intentionally separate (ReelBolt.Shared cannot reference the engine's
        // DecisionGateMode — see ConveneGateMode's remarks), so this is the one place that notices
        // if they drift out of step. Names, not numeric values: the numeric values are coincidental.
        string[] convene = Enum.GetNames<ConveneGateMode>();
        string[] decision = Enum.GetNames<DecisionGateMode>();

        convene.Should().Equal(decision);
    }

    // =====================================================================
    // The seams
    // =====================================================================

    [Fact]
    public void The_grade_rooms_agenda_seam_yields_null_when_there_is_nothing_to_say()
    {
        // The AGENDA seam is the one seam that stays null through a real room on the "no answers"
        // path — but no longer because no room supplies one. Since T-104 the grade room DOES supply
        // it (RenderConveneAgenda over its own question set), so what this pins is the formatter's
        // own contract reaching production: no usable answers means no block at all, never an empty
        // one and never a partial one.
        SeamProbeExecutor executor = CreateExecutor(null, out _, out _);

        executor.ProbeConveneAgenda(null, new Dictionary<string, DecisionAnswer>()).Should().BeNull(
            "an empty answer set produces no agenda — the opening message then stays exactly what it was");
    }

    [Fact]
    public void The_seams_are_overridable_without_touching_the_shared_base()
    {
        // The three concrete rooms compile with no override at all — that is proven by the build.
        // This pins the other half of the contract: a room CAN override them, which is what
        // D-102/D-103 will do, and an override is what the Shadow arm actually consults.
        var overridden = new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
            "which look", ["Warm", "Cool"], "Warm");

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            null, _ => [overridden], out _, out _);

        executor.ProbeShadowQuestions(new ColorGradePlanOutput { Look = "Warm" })
            .Should().ContainSingle().Which.Should().BeSameAs(overridden);
    }

    // =====================================================================
    // The color grade room's question set — the one room the gate ships for (D-102)
    // =====================================================================

    [Fact]
    public void The_color_grade_room_supplies_the_solo_colorists_four_questions()
    {
        SeamProbeExecutor executor = CreateExecutor(null, out _, out _);

        IReadOnlyList<DecisionQuestion>? questions = executor.ProbeConveneQuestions(null);

        questions.Should().NotBeNull(
            "the grade room's decision IS a fixed option set, so it is the one room that supplies " +
            "a convening question set");
        questions!.Should().HaveCount(4);
        questions.Select(q => q.Name).Should().Equal(
            ["Colorist.Look", "Colorist.Strength", "Colorist.ShadowTone", "Colorist.HighlightTone"],
            "the wire names are the SOLO colorist's site strings, not a room-prefixed variant — " +
            "sharing that namespace is the point (decisions.md §7)");
        questions.Should().OnlyContain(q => q.Kind == DecisionKind.Choice);

        // Byte-equality against the solo agent's own builder. The room is allowed to duplicate the
        // four questions; it is not allowed to drift from them, because the option arrays become
        // the observation's options_hash and the names become the calibration view's series key.
        IReadOnlyList<DecisionGateQuestion> solo = SoloColoristQuestions();
        solo.Should().HaveCount(4, "the solo builder is the reference this room is copying");

        for (int i = 0; i < solo.Count; i++)
        {
            questions[i].Name.Should().Be(solo[i].Site);
            questions[i].Instructions.Should().Be(solo[i].Instructions);
            questions[i].Options.Should().Equal(solo[i].Options);
        }
    }

    [Fact]
    public void The_color_grade_rooms_shadow_questions_carry_the_rooms_own_words_on_the_solo_sites()
    {
        SeamProbeExecutor executor = CreateExecutor(null, out _, out _);

        var decision = new ColorGradePlanOutput
        {
            Look = DecisionLook,
            Strength = DecisionStrength,
            ShadowTone = DecisionShadowTone,
            HighlightTone = DecisionHighlightTone
        };

        IReadOnlyList<DecisionGateQuestion>? questions = executor.ProbeShadowQuestions(decision);

        questions.Should().NotBeNull("the grade room is the one room the gate ships for");
        questions!.Should().HaveCount(4);
        questions.Select(q => q.SystemTwoAnswer).Should().Equal(
            [DecisionLook, DecisionStrength, DecisionShadowTone, DecisionHighlightTone],
            "each gate question carries the room's own synthesized word for that field — that is " +
            "what makes the observation's `agreed` column mean something");
        questions.Select(q => q.Site).Should().Equal(
            "Colorist.Look", "Colorist.Strength", "Colorist.ShadowTone", "Colorist.HighlightTone");

        for (int i = 0; i < questions.Count; i++)
        {
            questions[i].SystemTwoAnswer.Should().NotBe(
                questions[i].Options[0],
                "every deliberate answer is a NON-first option, so an implementation that echoed " +
                "Options[0] or a hardcoded default would fail this rather than pass by coincidence");
        }

        // Same Sites AND same Names as the solo builder: (Site, Question) is the calibration view's
        // aggregation key (DecisionCalibrationSite), so a room observation and a solo observation
        // for the same field land on ONE series. Only the recorded AgentType differs.
        IReadOnlyList<DecisionGateQuestion> solo = SoloColoristQuestions();
        solo.Should().HaveCount(4);

        for (int i = 0; i < solo.Count; i++)
        {
            questions[i].Site.Should().Be(solo[i].Site);
            questions[i].Name.Should().Be(solo[i].Name);
            questions[i].Kind.Should().Be(solo[i].Kind);
            questions[i].Instructions.Should().Be(solo[i].Instructions);
            questions[i].Options.Should().Equal(solo[i].Options);
        }
    }

    [Fact]
    public async Task The_real_grade_room_records_its_four_questions_and_the_rooms_own_agent_type_when_shadowing()
    {
        // The two tests above assert the seams in isolation. This one is the end-to-end half: a
        // REAL ColorGradeRoomStepExecutor run in Shadow mode hands the dispatch exactly those four
        // questions, with each SystemTwoAnswer taken off the plan the room actually synthesized —
        // and attributes the observation to the room's own director type, NOT to AgentType.Colorist,
        // even though the SITE strings are the solo colorist's.
        var gate = new Mock<IDecisionGate>();
        AgentType capturedAgentType = default;
        IReadOnlyList<DecisionGateQuestion>? captured = null;

        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, AgentType, DecisionState, IReadOnlyList<DecisionGateQuestion>, DecisionGateConfig, CancellationToken>(
                (_, _, agentType, _, questions, _, _) =>
                {
                    capturedAgentType = agentType;
                    captured = questions;
                });

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().HaveCount(1);

        capturedAgentType.Should().Be(AgentType.ColorGradeDirector,
            "the observation is attributable to the ROOM, not passed off as a solo colorist's — the " +
            "shared thing is the site, not the AgentType");

        captured.Should().NotBeNull();
        captured!.Select(q => q.Site).Should().Equal(
            "Colorist.Look", "Colorist.Strength", "Colorist.ShadowTone", "Colorist.HighlightTone");
        captured.Select(q => q.SystemTwoAnswer).Should().Equal(
            ["Warm", "Subtle", "Neutral", "Neutral"],
            "the values are the synthesized plan's own words — WireHappyPathRoom's canned director " +
            "synthesizes exactly these four");
    }

    [Fact]
    public void The_edit_and_graphics_rooms_DECLARE_their_null_overrides_rather_than_inheriting_the_default()
    {
        // The deliverable here is the EXPLICIT declaration, not the null value. The base seams
        // already return null, so the two behavioural tests above cannot tell "this room
        // deliberately declines the gate, and here is why" from "nobody thought about it" — both
        // produce null. Reflection can tell them apart, and this is the only assertion in the file
        // that actually pins the thing the ticket asked for.
        Type[] rooms = [typeof(EditRoomStepExecutor), typeof(GraphicsRoomStepExecutor)];
        string[] seams = ["BuildConveneQuestions", "BuildConveneAgenda", "BuildShadowQuestions"];

        foreach (Type room in rooms)
        {
            foreach (string seam in seams)
            {
                MethodInfo? declared = room.GetMethod(
                    seam, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                declared.Should().NotBeNull(
                    $"{room.Name} must DECLARE the {seam} override — an inherited base default " +
                    "would make the omission read as an oversight rather than a decision");
                declared!.IsVirtual.Should().BeTrue(
                    $"{room.Name}.{seam} must remain an override of the base seam, not shadow it");
            }
        }
    }

    [Fact]
    public void The_color_grade_room_declares_all_three_convene_seam_overrides()
    {
        // All three seams, and the same structural check the edit and graphics rooms get from the
        // other direction: the grade room must DECLARE the question set, the AGENDA and the shadow
        // question set rather than inheriting a base default that happens to look the same.
        //
        // The agenda override is T-104's: T-103 built and tested the shared RenderConveneAgenda but
        // disclosed that nothing called it, so until this override existed no agenda could reach a
        // live opening message at all.
        Type room = typeof(ColorGradeRoomStepExecutor);

        room.GetMethod("BuildConveneQuestions", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().NotBeNull("the grade room supplies the four Colorist questions");

        room.GetMethod("BuildConveneAgenda", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().NotBeNull(
                "the grade room is the agenda's producer — it renders the shared wording over its OWN question " +
                "set, which is the only way a split outcome reaches a live room's opening message");

        room.GetMethod("BuildShadowQuestions", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().NotBeNull("the grade room supplies the matching gate questions");
    }

    // =====================================================================
    // The edit and graphics rooms' explicit no-op (D-102)
    // =====================================================================

    [Fact]
    public void The_edit_room_declines_all_three_convene_seams_explicitly()
    {
        EditRoomSeamProbeExecutor executor = CreateEditRoomProbeExecutor(null, out _);

        executor.ProbeConveneQuestions(null).Should().BeNull(
            "a VideoEditDecisionOutput is an id-anchored keep/drop list, not a fixed option set");
        executor.ProbeConveneAgenda(null, new Dictionary<string, DecisionAnswer>()).Should().BeNull();
        executor.ProbeShadowQuestions(new VideoEditDecisionOutput()).Should().BeNull(
            "there is no fixed option set to observe against, and gating on a proxy is worse than " +
            "not gating");
    }

    [Fact]
    public void The_graphics_room_declines_all_three_convene_seams_explicitly()
    {
        GraphicsRoomSeamProbeExecutor executor = CreateGraphicsRoomProbeExecutor(null, out _);

        executor.ProbeConveneQuestions(null).Should().BeNull(
            "a MotionGraphicsPlanOutput is an open, generative overlay list, not a fixed option set");
        executor.ProbeConveneAgenda(null, new Dictionary<string, DecisionAnswer>()).Should().BeNull();
        executor.ProbeShadowQuestions(new MotionGraphicsPlanOutput()).Should().BeNull(
            "there is no fixed option set to observe against, and gating on a proxy is worse than " +
            "not gating");
    }

    [Fact]
    public async Task An_edit_room_step_with_ConveneGate_Gate_is_byte_identical_to_Off_and_never_calls_the_gate()
    {
        // ConveneGate = Gate on a room with no question set is a NO-OP, never an error: the room
        // convenes exactly as it does with Off, and no Decision provider is ever resolved.
        //
        // Since T-104 wired the Gate arm, this rests on ONE reason rather than two: the arm is live,
        // but this room overrides BuildConveneQuestions to null, so it has nothing to ask and cannot
        // legitimately convene-or-not on it. The assertion below is the behavioural half of that;
        // the `null` override asserted above is the other half. MockBehavior.Strict makes ANY gate
        // invocation — including an un-set-up DecideAsync — fail the test rather than being silently
        // tolerated, so a future arm that ignored the empty question set could not slip through.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildEditRoomViewJson())];

        EditRoomSeamProbeExecutor gatedRoom = CreateEditRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> gatedChat);
        WireHappyPathEditRoom(gatedChat, gatedRoom.Registry);
        string gatedConfigJson = JsonSerializer.Serialize(SmallestEditRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionResult gated = await gatedRoom.ExecuteAsync(CreateEditRoomContext(gatedConfigJson, history));

        EditRoomSeamProbeExecutor offRoom = CreateEditRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> offChat);
        WireHappyPathEditRoom(offChat, offRoom.Registry);
        string offConfigJson = JsonSerializer.Serialize(SmallestEditRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionResult off = await offRoom.ExecuteAsync(CreateEditRoomContext(offConfigJson, history));

        gated.Status.Should().Be(StepStatus.Completed, because: gated.ErrorDetails ?? gated.Output);
        off.Status.Should().Be(StepStatus.Completed, because: off.ErrorDetails ?? off.Output);
        gated.Output.Should().Be(off.Output,
            "Gate on a room with no fixed option set is a no-op, not an error and not a " +
            "behaviour change");

        gate.Invocations.Should().BeEmpty(
            "the edit room supplies no convening question set, so the gate has nothing to be asked " +
            "even when the mode is Gate");
    }

    [Fact]
    public async Task A_graphics_room_step_with_ConveneGate_Gate_is_byte_identical_to_Off_and_never_calls_the_gate()
    {
        // The graphics room's half of the assertion above, same single reason, same strict mock.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildEditRoomViewJson())];

        GraphicsRoomSeamProbeExecutor gatedRoom = CreateGraphicsRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> gatedChat);
        WireHappyPathGraphicsRoom(gatedChat, gatedRoom.Registry);
        string gatedConfigJson = JsonSerializer.Serialize(SmallestGraphicsRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionResult gated = await gatedRoom.ExecuteAsync(CreateGraphicsRoomContext(gatedConfigJson, history));

        GraphicsRoomSeamProbeExecutor offRoom = CreateGraphicsRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> offChat);
        WireHappyPathGraphicsRoom(offChat, offRoom.Registry);
        string offConfigJson = JsonSerializer.Serialize(SmallestGraphicsRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionResult off = await offRoom.ExecuteAsync(CreateGraphicsRoomContext(offConfigJson, history));

        gated.Status.Should().Be(StepStatus.Completed, because: gated.ErrorDetails ?? gated.Output);
        off.Status.Should().Be(StepStatus.Completed, because: off.ErrorDetails ?? off.Output);
        gated.Output.Should().Be(off.Output,
            "Gate on a room with no fixed option set is a no-op, not an error and not a " +
            "behaviour change");

        gate.Invocations.Should().BeEmpty(
            "the graphics room supplies no convening question set, so the gate has nothing to be " +
            "asked even when the mode is Gate");
    }

    // =====================================================================
    // Off — today's path, byte-identical
    // =====================================================================

    [Fact]
    public async Task Off_never_invokes_the_decision_gate_and_never_resolves_a_decision_provider()
    {
        // MockBehavior.Strict: ANY invocation of the gate — including a property access — fails the
        // test rather than being silently permitted. Off must be a complete no-op.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        ColorGradeRoomStepExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().BeEmpty(
            "with ConveneGate = Off the executor must never call IDecisionGate — and the room " +
            "executor holds no IInferenceProviderResolver at all, so it can never resolve a " +
            "Decision provider either");
    }

    [Fact]
    public async Task The_Off_path_emits_the_same_bytes_whether_a_gate_is_injected_or_not()
    {
        // The strongest available statement of "byte-identical": identical inputs, one executor
        // with a gate injected (Off) and one with no gate at all, same emitted JSON to the byte.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))];

        ColorGradeRoomStepExecutor withGate = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatA, out Mock<IAgentRegistry> regA);
        WireHappyPathRoom(chatA, regA);
        StepExecutionResult gated = await withGate.ExecuteAsync(CreateContext(configJson, history));

        ColorGradeRoomStepExecutor withoutGate = CreateExecutor(null, out Mock<IAgentChatClientProvider> chatB, out Mock<IAgentRegistry> regB);
        WireHappyPathRoom(chatB, regB);
        StepExecutionResult ungated = await withoutGate.ExecuteAsync(CreateContext(configJson, history));

        gated.Status.Should().Be(StepStatus.Completed, because: gated.ErrorDetails ?? gated.Output);
        ungated.Status.Should().Be(StepStatus.Completed, because: ungated.ErrorDetails ?? ungated.Output);
        gated.Output.Should().Be(ungated.Output);

        gate.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Off_paths_room_meta_has_the_frozen_key_set_and_no_added_key()
    {
        ColorGradeRoomStepExecutor executor = CreateExecutor(null, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement room = RoomMeta(result);
        room.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ExpectedRoomMetaKeys);

        room.GetProperty("terminationReason").GetString().Should().Be("ceiling",
            "one seat, one round, a two-turn ceiling and FixedTurns — the same termination the " +
            "existing room suites observe for this shape");
        room.GetProperty("degraded").GetBoolean().Should().BeFalse();
        room.GetProperty("turnCount").GetInt32().Should().Be(3,
            "turnCount is the raw group-chat transcript length: the opening message plus the " +
            "seat turn plus the director turn (the DB-persisted ChatTranscriptJson, which drops " +
            "the opening message, carries 2 — the count the existing room suites assert)");
    }

    // =====================================================================
    // Shadow — post-synthesis observations, zero behaviour change
    // =====================================================================

    [Fact]
    public async Task Shadow_records_one_observation_per_question_with_the_rooms_own_answer_as_system_two()
    {
        var providerId = Guid.NewGuid();
        var questions = new List<DecisionGateQuestion>
        {
            new("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                "what look", ["Warm", "Cool"], "Warm"),
            new("ColorGradeRoom.decision", "strength", DecisionKind.Choice,
                "what strength", ["Subtle", "Normal", "Strong"], "Subtle")
        };

        var gate = new Mock<IDecisionGate>();
        Guid capturedExecutionId = Guid.Empty;
        int capturedStepOrder = -1;
        AgentType capturedAgentType = default;
        DecisionState? capturedState = null;
        IReadOnlyList<DecisionGateQuestion>? capturedQuestions = null;
        DecisionGateConfig? capturedConfig = null;

        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, AgentType, DecisionState, IReadOnlyList<DecisionGateQuestion>, DecisionGateConfig, CancellationToken>(
                (executionId, stepOrder, agentType, state, q, config, _) =>
                {
                    capturedExecutionId = executionId;
                    capturedStepOrder = stepOrder;
                    capturedAgentType = agentType;
                    capturedState = state;
                    capturedQuestions = q;
                    capturedConfig = config;
                });

        // The gate's questions are built from the decision the room actually produced — "Warm" is
        // what the canned director synthesizes, so SystemTwoAnswer must carry "Warm".
        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object,
            decision => questions.Select(q => q with { SystemTwoAnswer = decision.Look }).ToList(),
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        var config = SmallestRoom(ConveneGateMode.Shadow) with
        {
            ConveneAcceptAt = 0.9,
            ConveneMinMargin = 0.3,
            ConveneProviderId = providerId
        };
        string configJson = JsonSerializer.Serialize(config, ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        gate.Verify(
            g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Once, "exactly one shadow run per step, regardless of how many questions it carries");

        capturedExecutionId.Should().Be(context.Execution.Id);
        capturedStepOrder.Should().Be(3);
        capturedAgentType.Should().Be(AgentType.ColorGradeDirector,
            "the observation's AgentType is the room's own decision-maker");

        capturedQuestions.Should().NotBeNull();
        capturedQuestions!.Should().HaveCount(2);
        capturedQuestions.Select(q => q.Name).Should().Equal("look", "strength");
        capturedQuestions.Should().OnlyContain(q => q.SystemTwoAnswer == "Warm",
            "each returned DecisionGateQuestion's SystemTwoAnswer is the room's own synthesized " +
            "value for that field — that is what makes the observation's `agreed` column mean " +
            "something");
        capturedQuestions.Should().OnlyContain(q => q.Site == "ColorGradeRoom.decision");

        capturedState.Should().NotBeNull();
        capturedState!.Content.Should().Contain("shots",
            "the gate is asked the same question the room was — the composed bounded-view input, " +
            "not a summary of what the room decided");

        capturedConfig.Should().NotBeNull();
        capturedConfig!.Mode.Should().Be(DecisionGateMode.Shadow);
        capturedConfig.AcceptAt.Should().Be(0.9);
        capturedConfig.MinMargin.Should().Be(0.3);
        capturedConfig.ProviderId.Should().Be(providerId);
    }

    [Fact]
    public async Task Shadow_with_the_default_null_question_set_never_invokes_the_gate_and_changes_no_output()
    {
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        // A room that supplies NO shadow question set — the shape both the edit and the graphics
        // room take (and the shape the base default still produces for a future room that overrides
        // nothing). The stock grade room used to stand in for this; since D-102 it supplies the four
        // Colorist questions, so it can no longer play that part.
        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))];

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(gate.Object, _ => null, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);
        StepExecutionResult shadow = await executor.ExecuteAsync(CreateContext(configJson, history));

        ColorGradeRoomStepExecutor offExecutor = CreateExecutor(null, out Mock<IAgentChatClientProvider> offChat, out Mock<IAgentRegistry> offRegistry);
        WireHappyPathRoom(offChat, offRegistry);
        string offConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionResult off = await offExecutor.ExecuteAsync(CreateContext(offConfigJson, history));

        shadow.Status.Should().Be(StepStatus.Completed, because: shadow.ErrorDetails ?? shadow.Output);
        shadow.Output.Should().Be(off.Output,
            "Shadow with nothing to observe is indistinguishable from Off");
        gate.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Shadow_observations_never_change_the_completed_decision_or_its_room_meta()
    {
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object,
            decision =>
            [
                new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                    "what look", ["Warm", "Cool"], decision.Look)
            ],
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string shadowConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext shadowContext = CreateContext(shadowConfigJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);
        StepExecutionResult shadow = await executor.ExecuteAsync(shadowContext);

        ColorGradeRoomStepExecutor offExecutor = CreateExecutor(null, out Mock<IAgentChatClientProvider> offChat, out Mock<IAgentRegistry> offRegistry);
        WireHappyPathRoom(offChat, offRegistry);
        string offConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionResult off = await offExecutor.ExecuteAsync(CreateContext(offConfigJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]));

        shadow.Status.Should().Be(StepStatus.Completed);
        shadow.Output.Should().Be(off.Output,
            "the shadow run is a side channel: it must not perturb the decision, the step status " +
            "or the room metadata by even a byte");
        shadow.ChatTranscriptJson.Should().Be(off.ChatTranscriptJson);
        gate.Invocations.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_shadow_gate_that_throws_leaves_the_step_completed_with_its_decision_unchanged()
    {
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the gate itself blew up"));

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object,
            decision =>
            [
                new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                    "what look", ["Warm", "Cool"], decision.Look)
            ],
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed,
            "the room acquires no new failure mode from the gate — a throwing gate must not even " +
            "reach the degrade path");
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("look").GetString().Should().Be("Warm");
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeFalse();
        gate.Invocations.Should().HaveCount(1, "the gate WAS attempted — it just failed harmlessly");
    }

    [Fact]
    public async Task Shadow_with_no_registered_gate_completes_normally()
    {
        // IDecisionGate is registered in the real container, but an unregistered (or
        // DI-failed-to-construct) gate is a legal configuration: the optional constructor
        // parameter is exactly what makes it so. A null gate must be inert, not fatal.
        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            null,
            decision =>
            [
                new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                    "what look", ["Warm", "Cool"], decision.Look)
            ],
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("look").GetString().Should().Be("Warm");
    }

    [Fact]
    public async Task Shadow_observes_the_solo_fallback_decision_too_because_that_is_the_steps_emitted_answer()
    {
        // A room that degrades to the solo fallback still SUCCEEDS, and the solo plan is then the
        // step's authoritative decision. The shadow observation is placed immediately before the
        // Success return (post-synthesis, not post-group-chat), so it records that decision — the
        // agreement column is only meaningful against the answer the step actually published.
        var gate = new Mock<IDecisionGate>();
        IReadOnlyList<DecisionGateQuestion>? captured = null;
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, AgentType, DecisionState, IReadOnlyList<DecisionGateQuestion>, DecisionGateConfig, CancellationToken>(
                (_, _, _, _, q, _, _) => captured = q);

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object,
            decision =>
            [
                new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                    "what look", ["Warm", "Cool", "None"], decision.Look)
            ],
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);

        // Force the room to fail at construction so the solo fallback carries the step.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat-capable provider configured"));

        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = BuildPlanJson(look: "None", rationale: "solo"), Success = true });
        agentRegistry.Setup(r => r.GetByType(AgentType.Colorist, null)).Returns(soloAgent.Object);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("look").GetString().Should().Be("None");
        doc.RootElement.GetProperty("room").GetProperty("degraded").GetBoolean().Should().BeTrue();

        captured.Should().NotBeNull();
        captured!.Should().OnlyContain(q => q.SystemTwoAnswer == "None");
    }

    // =====================================================================
    // The constructor seam — the optional trailing IDecisionGate parameter
    // =====================================================================

    [Fact]
    public async Task The_real_container_injects_the_registered_gate_through_the_optional_trailing_parameter()
    {
        // The build proves one half of this contract: the existing construction sites, which pass
        // no gate, still compile. This proves the other half — that the trailing optional
        // parameter does NOT stop Microsoft DI from supplying the IDecisionGate singleton
        // WorkflowEngineServiceCollectionExtensions already registers. ValidateOnBuild is on, so a
        // registration the container could not satisfy fails here rather than at host startup.
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        var agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();
        WireHappyPathRoom(chatClients, agentRegistry);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(gate.Object);
        services.AddSingleton(chatClients.Object);
        services.AddSingleton(toolProvider.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(workspace.Object);
        services.AddSingleton<IWorkflowExecutionContextAccessor, WorkflowExecutionContextAccessor>();
        services.AddSingleton<DiProbeRoomExecutor>();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        DiProbeRoomExecutor executor = provider.GetRequiredService<DiProbeRoomExecutor>();

        executor.GateObserved.Should().BeSameAs(gate.Object,
            "the container must fill the optional trailing parameter, not leave it null");

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().HaveCount(1, "the container-supplied gate is the one the shadow arm calls");
    }

    // =====================================================================
    // The agenda — the fast model's split as the room's first order of business (D-103)
    // =====================================================================

    /// <summary>
    /// A <see cref="DecisionAnswer"/> whose probabilities are exactly the given option/probability
    /// pairs — the shape a Choice question's answer carries when the provider returned logprobs.
    /// The leading option and the Confidence follow from the pairs, so the fixture cannot describe
    /// an answer that contradicts its own probabilities.
    /// </summary>
    private static DecisionAnswer Split(string name, params (string Option, double P)[] entries) =>
        new(
            Name: name,
            Kind: DecisionKind.Choice,
            Choice: entries.OrderByDescending(e => e.P).First().Option,
            Score: null,
            Noul: null,
            Confidence: entries.Max(e => e.P),
            Probabilities: entries.ToDictionary(e => e.Option, e => e.P, StringComparer.Ordinal));

    /// <summary>The solo Colorist's own <c>Look</c> question — the room's copy of it, by name.</summary>
    private static DecisionQuestion LookQuestion() =>
        new("Colorist.Look", DecisionKind.Choice, "which look",
            ["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"]);

    private static string? Agenda(
        IReadOnlyList<DecisionQuestion> questions, params (string Name, DecisionAnswer Answer)[] answers) =>
        RoomStepExecutorBase<ColorGradePlanOutput>.RenderConveneAgenda(
            questions,
            answers.ToDictionary(a => a.Name, a => a.Answer, StringComparer.Ordinal));

    [Fact]
    public void The_agenda_names_the_top_two_options_and_their_probabilities_as_the_disagreement_to_resolve()
    {
        string? agenda = Agenda(
            [LookQuestion()],
            ("Colorist.Look", Split("Colorist.Look", ("Filmic", 0.44), ("Warm", 0.41), ("Mono", 0.15))));

        agenda.Should().NotBeNull();
        agenda.Should().Contain(
            "The fast model split on Look: Filmic 0.44 vs Warm 0.41. Resolve that disagreement first.",
            "the sentence is normative, and it names the SHORT name (the last dot-segment), the " +
            "two leading options and their probabilities");

        // Mono 0.15 is the third option and must not appear: the agenda is about the split, and a
        // third option's number would only dilute it.
        agenda.Should().NotContain("Mono");
        agenda.Should().NotContain("Colorist.", "the full dotted name stays the wire key, not the prose");
    }

    [Fact]
    public void The_agenda_leads_with_the_sharpest_disagreement()
    {
        string? agenda = Agenda(
            [
                new DecisionQuestion("Colorist.Look", DecisionKind.Choice, "which look", ["Warm", "Mono"]),
                new DecisionQuestion("Colorist.Strength", DecisionKind.Choice, "how strong", ["Subtle", "Strong"])
            ],
            ("Colorist.Look", Split("Colorist.Look", ("Warm", 0.60), ("Mono", 0.30))),
            ("Colorist.Strength", Split("Colorist.Strength", ("Subtle", 0.45), ("Strong", 0.43))));

        agenda.Should().NotBeNull();

        string[] lines = agenda!.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Contain(
            "Strength", "a 0.02 margin is a sharper split than a 0.30 one, so it leads");
        lines[1].Should().Contain("Look");
    }

    [Fact]
    public void The_rendered_probabilities_are_culture_invariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Guards against a vacuous pass on a container whose ICU data is missing (where every
            // culture falls back to the invariant one, which would render 0.44 either way): the
            // culture under test MUST be one that renders the separator as a comma.
            (0.44).ToString("0.00").Should().Be("0,44", "de-DE is the culture this test is about");

            string? agenda = Agenda(
                [LookQuestion()],
                ("Colorist.Look", Split("Colorist.Look", ("Filmic", 0.44), ("Warm", 0.41))));

            agenda.Should().NotBeNull();
            agenda.Should().Contain("Filmic 0.44").And.Contain("Warm 0.41");
            agenda.Should().NotContain("0,44");
            agenda.Should().NotContain("0,41");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Questions_with_no_answer_or_fewer_than_two_probabilities_contribute_no_line()
    {
        DecisionQuestion[] questions = [LookQuestion()];

        Agenda(questions).Should().BeNull("a question the model was never asked an answer for contributes nothing");

        Agenda(questions, ("OtherQuestion", Split("OtherQuestion", ("Warm", 0.6), ("Mono", 0.4))))
            .Should().BeNull("an answer keyed to a DIFFERENT question is not this question's answer");

        Agenda(questions, ("Colorist.Look", new DecisionAnswer(
                "Colorist.Look", DecisionKind.Choice, "Filmic", null, null, 0.9,
                new Dictionary<string, double>(StringComparer.Ordinal) { ["Filmic"] = 0.9 })))
            .Should().BeNull("a single probability entry has no second option to be split against");

        Agenda(questions, ("Colorist.Look", new DecisionAnswer(
                "Colorist.Look", DecisionKind.Choice, "Filmic", null, null, null,
                new Dictionary<string, double>(StringComparer.Ordinal))))
            .Should().BeNull("an empty probability dictionary is the degenerate case of the above");

        // "Contributes no line" is not "drops the whole agenda": one degenerate question must not
        // suppress a healthy one it was asked alongside.
        string? mixed = Agenda(
            [
                new DecisionQuestion("Colorist.Look", DecisionKind.Choice, "which look", ["Warm", "Mono"]),
                new DecisionQuestion("Colorist.Strength", DecisionKind.Choice, "how strong", ["Subtle", "Strong"])
            ],
            ("Colorist.Look", Split("Colorist.Look", ("Warm", 0.60), ("Mono", 0.40))),
            ("Colorist.Strength", Split("Colorist.Strength", ("Subtle", 0.70))));

        mixed.Should().NotBeNull();
        mixed!.Split('\n').Should().ContainSingle()
            .Which.Should().Contain("Look").And.NotContain("Strength");
    }

    [Fact]
    public void A_question_set_that_is_entirely_confident_yields_no_agenda_at_all()
    {
        // The all-confident case is the reason the agenda can be null while the gate still
        // convenes (a Shadow→Gate transition, or a provider that answers without logprobs): the
        // room runs, but there is no disagreement to lead it with, so the opening message must be
        // exactly what it would have been with no gate at all.
        string? agenda = Agenda(
            [
                new DecisionQuestion("Colorist.Look", DecisionKind.Choice, "which look", ["Warm", "Mono"]),
                new DecisionQuestion("Colorist.Strength", DecisionKind.Choice, "how strong", ["Subtle", "Strong"])
            ],
            ("Colorist.Look", Split("Colorist.Look", ("Warm", 0.99))),
            ("Colorist.Strength", Split("Colorist.Strength", ("Subtle", 0.98))));

        agenda.Should().BeNull();
    }

    [Fact]
    public void The_agenda_is_bounded_and_drops_whole_sentences_rather_than_cutting_one()
    {
        // 30 questions, each with a long dotted name and two option words: ~130 characters of
        // sentence per question, so an unbounded block would be ~4000 characters — six or seven
        // times the cap. The cap has to bite, and it has to bite at a sentence boundary.
        var questions = new List<DecisionQuestion>();
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

        for (int i = 0; i < 30; i++)
        {
            string name = $"Colorist.LongFieldName{i:D2}AnalysisDimension";
            questions.Add(new DecisionQuestion(
                name, DecisionKind.Choice, "which option", ["OptionAlpha", "OptionBravo"]));

            // Ascending index = ascending margin, so question 00 is the sharpest split and must
            // therefore be the one the cap keeps.
            double margin = 0.01 + (i * 0.01);
            answers[name] = Split(
                name,
                ("OptionAlpha", 0.50 + (margin / 2)),
                ("OptionBravo", 0.50 - (margin / 2)));
        }

        string? agenda = RoomStepExecutorBase<ColorGradePlanOutput>.RenderConveneAgenda(questions, answers);

        agenda.Should().NotBeNull("sentences this size always fit, so the cap drops a tail rather than everything");
        agenda!.Length.Should().BeLessThanOrEqualTo(
            RoomStepExecutorBase<ColorGradePlanOutput>.MaxConveneAgendaChars);

        string[] lines = agenda.Split('\n');
        lines.Should().HaveCountLessThan(30, "the cap has to actually drop whole sentences on a 30-question set");
        lines.Should().OnlyContain(
            line => line.EndsWith("Resolve that disagreement first.", StringComparison.Ordinal),
            "every kept line is one COMPLETE sentence — never a half-word or a truncated number");
        agenda.Should().EndWith(".", "the block ends on a finished sentence, not mid-number");

        lines[0].Should().Contain(
            "LongFieldName00AnalysisDimension",
            "the most contested split leads the agenda, and so it is the one that survives the cap");
    }

    [Fact]
    public async Task The_no_agenda_opening_message_is_byte_identical_to_the_pre_agenda_composition()
    {
        // The end-to-end half of the no-op guarantee, through the real ExecuteAsync path (which is
        // also where T-104 will wire the agenda in). The opening message is never part of the step
        // result — it is the group chat's first message — so it is read back from what a room
        // PARTICIPANT was actually sent.
        var capturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        SeamProbeExecutor executor = CreateCapturingExecutor(capturer, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathDirector(agentRegistry);

        string viewJson = BuildViewJson("s0", "s1");
        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionContext context = CreateContext(
            configJson,
            [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            userRequest: "make it punchier",
            retryReason: "the previous cut was too slow");

        string expected = context.WithRetryGuidance(
            context.WithUserRequest(executor.ProbeOpeningMessage(viewJson)));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturer.Messages().Should().Contain(
            expected,
            "with no agenda the opening message is byte-for-byte " +
            "context.WithRetryGuidance(context.WithUserRequest(BuildOpeningMessage(viewJson))) — no " +
            "separator, no blank line, no trailing newline");
    }

    [Fact]
    public async Task The_agenda_is_appended_after_the_user_request_and_the_retry_guidance()
    {
        var capturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        SeamProbeExecutor executor = CreateCapturingExecutor(capturer, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathDirector(agentRegistry);

        string viewJson = BuildViewJson("s0", "s1");
        ColorGradeRoomStepConfig config = SmallestRoom(ConveneGateMode.Off);
        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(config, ConfigOptions),
            [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            userRequest: "make it punchier",
            retryReason: "the previous cut was too slow");

        string withoutAgenda = context.WithRetryGuidance(
            context.WithUserRequest(executor.ProbeOpeningMessage(viewJson)));
        const string agenda =
            "The fast model split on Look: Filmic 0.44 vs Warm 0.41. Resolve that disagreement first.";

        await RunRoomWithAgendaAsync(executor, context, config, viewJson, ["s0", "s1"], agenda);

        string sent = capturer.Messages()
            .First(message => message.Contains(agenda, StringComparison.Ordinal));

        sent.Should().Be(
            $"{withoutAgenda}\n\n{agenda}",
            "the agenda is appended AFTER WithRetryGuidance: a retried room must be handed the " +
            "same agenda again, and appending inside BuildOpeningMessage would let a room that " +
            "overrides the opening wording silently drop it");
        sent.Should().Contain("make it punchier", "the full user request is still there");
        sent.Should().Contain("the previous cut was too slow", "the retry guidance is still there");
        sent.Should().EndWith(agenda, "the agenda block ends the opening message");
    }

    [Fact]
    public async Task A_whitespace_agenda_is_as_complete_a_no_op_as_a_null_one()
    {
        string viewJson = BuildViewJson("s0", "s1");
        ColorGradeRoomStepConfig config = SmallestRoom(ConveneGateMode.Off);
        string configJson = JsonSerializer.Serialize(config, ConfigOptions);

        var nullCapturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        SeamProbeExecutor nullExecutor = CreateCapturingExecutor(nullCapturer, out Mock<IAgentRegistry> nullRegistry);
        WireHappyPathDirector(nullRegistry);
        await RunRoomWithAgendaAsync(
            nullExecutor,
            CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", viewJson)]),
            config, viewJson, ["s0", "s1"], agenda: null);

        var blankCapturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        SeamProbeExecutor blankExecutor = CreateCapturingExecutor(blankCapturer, out Mock<IAgentRegistry> blankRegistry);
        WireHappyPathDirector(blankRegistry);
        await RunRoomWithAgendaAsync(
            blankExecutor,
            CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", viewJson)]),
            config, viewJson, ["s0", "s1"], agenda: "  \n\t ");

        // Every message of the whole run, not just the opening one: a whitespace agenda that
        // leaked a separator or a blank line would show up anywhere downstream of the opening
        // message too, and "byte-identical" is the claim being made.
        blankCapturer.Messages().Should().Equal(
            nullCapturer.Messages(),
            "a whitespace agenda must be indistinguishable from no agenda at all");
    }

    /// <summary>
    /// Runs the base class's private <c>RunRoomAsync</c> directly, to pin THAT method's own
    /// composition of the opening message in isolation from whatever produced the agenda.
    /// <c>ExecuteAsync</c> now supplies a real agenda through the <c>Gate</c> arm (see the Gate
    /// section above, which goes through the production path), but that path can only reach an
    /// agenda the gate actually produced; calling the run directly is how the appending itself is
    /// tested against an agenda no gate could have emitted (a whitespace one, for instance).
    /// Reflection is the same tool this file already uses to read the solo Colorist's own question
    /// builder; the alternative — widening RunRoomAsync's visibility to suit a test — would change
    /// the production surface to match the test rather than the reverse.
    /// </summary>
    private static async Task RunRoomWithAgendaAsync(
        ColorGradeRoomStepExecutor executor,
        StepExecutionContext context,
        ColorGradeRoomStepConfig config,
        string viewJson,
        IEnumerable<string> offeredIds,
        string? agenda)
    {
        MethodInfo? run = typeof(RoomStepExecutorBase<ColorGradePlanOutput>).GetMethod(
            "RunRoomAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        run.Should().NotBeNull("RunRoomAsync is the run whose opening message carries the agenda");

        object? task = run!.Invoke(
            executor, [context, (IRoomStepConfig)config, viewJson, new HashSet<string>(offeredIds), agenda, null]);

        await (Task)task!;
    }

    /// <summary>
    /// The stock grade-room probe built around a CAPTURING chat client, so the room's opening
    /// message — which reaches a room participant but never the step result — can be read back.
    /// </summary>
    private static SeamProbeExecutor CreateCapturingExecutor(
        CapturingChatClient capturer, out Mock<IAgentRegistry> agentRegistry)
    {
        var chatClients = new Mock<IAgentChatClientProvider>();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturer);

        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new SeamProbeExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
            executionContextAccessor, NullLogger<ColorGradeRoomStepExecutor>.Instance, null);
    }

    /// <summary>
    /// <see cref="CreateCapturingExecutor"/> with a gate injected, and with the chat-client provider
    /// exposed as well as the capturing client. Both halves matter for the <c>Gate</c> arm: the
    /// capturer is how a test reads the room's opening message back, and the provider mock is how it
    /// proves the room never even built a participant — the two statements together are what make
    /// "RunStreamingAsync was never entered" a measurement rather than an inference.
    /// </summary>
    private static SeamProbeExecutor CreateGatedCapturingExecutor(
        CapturingChatClient capturer, IDecisionGate? decisionGate,
        out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturer);

        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new SeamProbeExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
            executionContextAccessor, NullLogger<ColorGradeRoomStepExecutor>.Instance, decisionGate);
    }

    // =====================================================================
    // Gate — the room is asked whether it is needed at all (T-104)
    //
    // Every case here feeds the executor a hand-built DecisionGateOutcome through a Moq mock. The
    // accept rule itself (all-or-nothing over AcceptAt and MinMargin) belongs to D-004 and is
    // tested there; what this suite owns is what the ROOM does with each outcome shape.
    // =====================================================================

    /// <summary>
    /// The word the solo <see cref="AgentType.Colorist"/> fallback answers with on the
    /// not-convened path. Deliberately neither the first option of the Look list ("None") nor the
    /// word the canned room director synthesizes ("Warm"), so a test that saw one of those would be
    /// seeing the wrong path rather than a coincidence.
    /// </summary>
    private const string SoloFallbackLook = "Mono";

    /// <summary>
    /// The exact sentence the shared renderer produces for a 0.44/0.41 Look split — the fixture's
    /// own disagreement, quoted verbatim so a change to the wording fails here rather than quietly
    /// reaching a live room's opening message.
    /// </summary>
    private const string LookSplitAgendaSentence =
        "The fast model split on Look: Filmic 0.44 vs Warm 0.41. Resolve that disagreement first.";

    /// <summary>A 0.44/0.41 Look split: a genuine disagreement the gate escalates on.</summary>
    private static DecisionResult LookSplitResult() => new(
        Model: "fake-system-one",
        Answers: new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
        {
            ["Colorist.Look"] = Split("Colorist.Look", ("Filmic", 0.44), ("Warm", 0.41), ("Mono", 0.15))
        },
        InputTokens: 11,
        OutputTokens: 7);

    /// <summary>A confident Look: the shape an accepted (do-not-convene) outcome carries.</summary>
    private static DecisionResult ConfidentLookResult() => new(
        Model: "fake-system-one",
        Answers: new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
        {
            ["Colorist.Look"] = Split("Colorist.Look", ("Warm", 0.97), ("Mono", 0.03))
        },
        InputTokens: 11,
        OutputTokens: 7);

    /// <summary>
    /// A gate outcome built by hand — exactly the shape <c>DecisionGate.BuildOutcome</c> produces,
    /// so the executor is fed the real contract without a live provider anywhere near the test.
    /// </summary>
    private static DecisionGateOutcome Outcome(bool hasDecision, bool accepted, DecisionResult? result) =>
        new(hasDecision, accepted, result, new Dictionary<string, (double Top, double Second)>());

    /// <summary>A strict gate mock answering <paramref name="outcome"/>, optionally capturing the request it was handed.</summary>
    private static Mock<IDecisionGate> GateAnswering(DecisionGateOutcome outcome, Action<DecisionGateRequest>? capture = null)
    {
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome)
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => capture?.Invoke(request));

        // The Gate arm's compensating half (T-107) is set up unconditionally on every strict gate
        // this suite builds, so a MockException can never be what "the attach happened" looks like:
        // "the attach was NOT called" is asserted through the recorded invocations, never through the
        // mock throwing. A setup that is never invoked is inert, so this changes no existing
        // assertion — including the ones that require the attach to be absent.
        gate
            .Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return gate;
    }

    /// <summary>One recorded <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/> invocation, in the terms the interface states them.</summary>
    private sealed record AttachCall(
        Guid ExecutionId,
        int StepOrder,
        IReadOnlyDictionary<string, string> Answers,
        int? AgentTokensUsed);

    /// <summary>
    /// The two halves of T-107's write, in one mock: a strict gate that answers <paramref name="outcome"/>
    /// and records every attach it is handed — so "DecideAsync once, then AttachSystemTwoAnswersAsync
    /// once, in that order" is read off the mock's own invocation log rather than inferred.
    /// </summary>
    private static Mock<IDecisionGate> GateAnsweringAndRecordingAttach(
        DecisionGateOutcome outcome, List<AttachCall> attachCalls, Action<DecisionGateRequest>? capture = null)
    {
        Mock<IDecisionGate> gate = GateAnswering(outcome, capture);

        gate
            .Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, IReadOnlyDictionary<string, string>, int?, CancellationToken>(
                (executionId, stepOrder, answers, agentTokensUsed, _) =>
                    attachCalls.Add(new AttachCall(executionId, stepOrder, answers, agentTokensUsed)));

        return gate;
    }

    /// <summary>
    /// The room's four deliberate words — every one NOT the first option of its list (Look's is
    /// "None", Strength's "Subtle", both tones' "Neutral"), so an attach that echoed an option
    /// default, or that attached the GATE's own answer instead of the room's, fails rather than
    /// passing by coincidence.
    /// </summary>
    private static void WireDeliberateRoom(Mock<IAgentRegistry> agentRegistry)
    {
        var directorAgent = new Mock<IReelBoltAgent>();
        directorAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Success = true,
                Output = JsonSerializer.Serialize(
                    new
                    {
                        look = DecisionLook,
                        strength = DecisionStrength,
                        shadowTone = DecisionShadowTone,
                        highlightTone = DecisionHighlightTone,
                        reason = "test reason",
                        planRationale = "room synthesis"
                    },
                    ConfigOptions)
            });

        agentRegistry
            .Setup(r => r.GetByType(AgentType.ColorGradeDirector, null))
            .Returns(directorAgent.Object);
    }

    /// <summary>
    /// A config json for the grade room in <see cref="ConveneGateMode.Gate"/>, with the one seat the
    /// <c>WireHappyPath*</c> helpers feed.
    /// </summary>
    private static string EscalatingGradeRoomConfigJson() =>
        JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);

    /// <summary>The bubbling history every escalating case here deliberates over — the bounded view <c>BuildConveneQuestions</c> reads.</summary>
    private static IReadOnlyList<StepOutputHistoryEntry> GradeRoomHistory() =>
        [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))];

    /// <summary>Registers the solo fallback the room degrades to, returning the mock so a test can assert it ran.</summary>
    private static Mock<IReelBoltAgent> WireSoloFallback(Mock<IAgentRegistry> agentRegistry, AgentRunResult result)
    {
        var soloAgent = new Mock<IReelBoltAgent>();
        soloAgent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        agentRegistry.Setup(r => r.GetByType(AgentType.Colorist, null)).Returns(soloAgent.Object);
        return soloAgent;
    }

    [Fact]
    public async Task An_accepted_gate_does_not_convene_and_emits_the_solo_decision_as_not_convened()
    {
        var capturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        Mock<IDecisionGate> gate = GateAnswering(Outcome(hasDecision: true, accepted: true, ConfidentLookResult()));

        SeamProbeExecutor executor = CreateGatedCapturingExecutor(
            capturer, gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        Mock<IReelBoltAgent> soloAgent = WireSoloFallback(agentRegistry, new AgentRunResult
        {
            Success = true,
            Output = BuildPlanJson(look: SoloFallbackLook, rationale: "solo")
        });

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1,
            "a non-convened room advances exactly like a convened one — it is the same Success call, and " +
            "`not-convened` is a terminationReason, not a status");

        // The never-convened claim, measured at the streaming seam rather than inferred from the
        // output: the group chat is driven entirely through this chat client, and every participant
        // is built through this provider BEFORE anything streams. Zero of each is "the room never
        // started", not "the room ran and produced nothing".
        capturer.Messages().Should().BeEmpty(
            "an accepted outcome must not enter RunStreamingAsync — no participant was ever sent a message");
        chatClients.Invocations.Should().BeEmpty(
            "not even participant construction happened, so the group chat cannot have been built at all");

        soloAgent.Invocations.Should().HaveCount(1,
            "the room's EXISTING solo fallback answers instead — no new solo mechanism was introduced");
        gate.Invocations.Should().HaveCount(1,
            "one DecideAsync and nothing else: the Shadow arm is Shadow-only, so no observation is written here");

        JsonElement room = RoomMeta(result);
        room.GetProperty("terminationReason").GetString().Should().Be("not-convened",
            "the transcript is empty on purpose, and the persisted meta has to say why");
        room.GetProperty("turnCount").GetInt32().Should().Be(0);
        room.GetProperty("synthesisAttempts").GetInt32().Should().Be(0);
        room.GetProperty("transcriptArtifactStorageKey").ValueKind.Should().Be(JsonValueKind.Null);
        room.GetProperty("degraded").GetBoolean().Should().BeFalse(
            "skipping the room is the gate working as designed, not a fault — marking it degraded would " +
            "report a designed outcome as a failure");
        room.GetProperty("degradeReason").ValueKind.Should().Be(JsonValueKind.Null);

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("look").GetString().Should().Be(SoloFallbackLook,
            "the emitted decision is the solo agent's own output, filtered exactly as the degrade path filters it");
        doc.RootElement.GetProperty("planRationale").GetString().Should().Be("solo");

        result.ChatTranscriptJson.Should().BeNull("no room ran, so there is no room transcript to persist");
    }

    [Theory]
    [InlineData("unregistered-agent")]
    [InlineData("fail-workflow")]
    [InlineData("unparseable-json")]
    public async Task A_solo_fallback_that_fails_on_the_not_convened_path_returns_the_rooms_existing_failure(string failureMode)
    {
        // Review focus of this ticket: a non-convened step whose solo agent fails must NOT invent a
        // new failure mode and must NEVER emit a decision. It produces the room's existing
        // ${FailureCodePrefix}_FAILED, exactly as the degrade branch does — and, because it returns
        // before the Success call, writes no observation either.
        Mock<IDecisionGate> gate = GateAnswering(Outcome(hasDecision: true, accepted: true, ConfidentLookResult()));

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out _, out Mock<IAgentRegistry> agentRegistry);

        switch (failureMode)
        {
            case "unregistered-agent":
                // Nothing registered for AgentType.Colorist: the loose registry mock returns null.
                break;

            case "fail-workflow":
                WireSoloFallback(agentRegistry, new AgentRunResult
                {
                    Success = false,
                    Output = string.Empty,
                    FailureReason = "the solo agent invoked FailWorkflow"
                });
                break;

            default:
                WireSoloFallback(agentRegistry, new AgentRunResult
                {
                    Success = true,
                    Output = "I could not decide, sorry — no structured answer here."
                });
                break;
        }

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("COLOR_GRADE_ROOM_FAILED",
            "the not-convened path reuses the room's existing failure mode rather than adding one");

        doc.RootElement.TryGetProperty("look", out _).Should().BeFalse(
            "a failed solo fallback must never be papered over with a silently emitted decision");
        doc.RootElement.TryGetProperty("room", out _).Should().BeFalse(
            "no room metadata is emitted either — there was never a room result to describe");

        gate.Invocations.Should().HaveCount(1,
            "the gate was asked once and nothing else: no shadow observation may be written for a step " +
            "that produced no decision at all");
    }

    [Fact]
    public async Task An_escalating_gate_convenes_the_room_with_the_agenda_after_the_user_request_and_the_retry_guidance()
    {
        // The ColorGrade room's live agenda, end to end through the production ExecuteAsync path —
        // T-103 disclosed that RenderConveneAgenda had no caller at all, and this is the assertion
        // that closes that hole: a REAL ColorGradeRoomStepExecutor (the SeamProbeExecutor adds no
        // agenda override of its own) must turn a split outcome into text a room participant was
        // actually sent.
        var capturer = new CapturingChatClient("s1 leans cool; a subtle warm lift suits it.");
        Mock<IDecisionGate> gate = GateAnswering(Outcome(hasDecision: true, accepted: false, LookSplitResult()));

        SeamProbeExecutor executor = CreateGatedCapturingExecutor(capturer, gate.Object, out _, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathDirector(agentRegistry);

        string viewJson = BuildViewJson("s0", "s1");
        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionContext context = CreateContext(
            configJson,
            [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            userRequest: "make it punchier",
            retryReason: "the previous cut was too slow");

        string withoutAgenda = context.WithRetryGuidance(
            context.WithUserRequest(executor.ProbeOpeningMessage(viewJson)));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().NotBe("not-convened",
            "an escalation convenes: the gate only declines to convene when EVERY question cleared both thresholds");

        capturer.Messages().Should().Contain(
            message => message.Contains(LookSplitAgendaSentence, StringComparison.Ordinal),
            "the grade room's one-line BuildConveneAgenda override must reach a participant's opening message");

        string sent = capturer.Messages().First(message => message.Contains(LookSplitAgendaSentence, StringComparison.Ordinal));

        sent.Should().Be($"{withoutAgenda}\n\n{LookSplitAgendaSentence}",
            "the agenda is appended AFTER WithRetryGuidance(WithUserRequest(BuildOpeningMessage(viewJson))) — " +
            "the disagreement is what the room must settle first, so nothing may be able to push it out of " +
            "the opening message or bury it above the creative direction");
        sent.Should().Contain("make it punchier", "the user request is still there, above the agenda");
        sent.Should().Contain("the previous cut was too slow", "so is the retry guidance");
    }

    [Fact]
    public async Task Gate_without_a_usable_decision_convenes_and_is_byte_identical_to_Off()
    {
        // Three "the gate told us nothing usable" shapes, all of which must land on today's path:
        //
        //  (a) NoDecision — which is ALSO the shape a gate-level TIMEOUT arrives in, since D-004
        //      turns its own deadline into NoDecision rather than throwing. The timeout therefore
        //      needs no case of its own anywhere in the executor.
        //  (b) a gate that throws — any gate failure convenes.
        //  (c) no gate at all — a legal, inert configuration.
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))];
        string offConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        string gateConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);

        ColorGradeRoomStepExecutor offExecutor = CreateExecutor(null, out Mock<IAgentChatClientProvider> offChat, out Mock<IAgentRegistry> offRegistry);
        WireHappyPathRoom(offChat, offRegistry);
        StepExecutionResult off = await offExecutor.ExecuteAsync(CreateContext(offConfigJson, history));

        off.Status.Should().Be(StepStatus.Completed, because: off.ErrorDetails ?? off.Output);

        Mock<IDecisionGate> noDecisionGate = GateAnswering(DecisionGateOutcome.NoDecision);
        ColorGradeRoomStepExecutor noDecisionExecutor = CreateExecutor(noDecisionGate.Object, out Mock<IAgentChatClientProvider> ndChat, out Mock<IAgentRegistry> ndRegistry);
        WireHappyPathRoom(ndChat, ndRegistry);
        StepExecutionResult noDecision = await noDecisionExecutor.ExecuteAsync(CreateContext(gateConfigJson, history));

        noDecision.Status.Should().Be(StepStatus.Completed, because: noDecision.ErrorDetails ?? noDecision.Output);
        noDecision.Output.Should().Be(off.Output,
            "NoDecision convenes and changes nothing else — the same bytes a gate-less run emits");
        noDecisionGate.Invocations.Should().HaveCount(1, "the gate WAS asked; it simply had nothing usable to say");

        var throwingGate = new Mock<IDecisionGate>(MockBehavior.Strict);
        throwingGate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the gate itself blew up"));
        ColorGradeRoomStepExecutor throwingExecutor = CreateExecutor(throwingGate.Object, out Mock<IAgentChatClientProvider> thChat, out Mock<IAgentRegistry> thRegistry);
        WireHappyPathRoom(thChat, thRegistry);
        StepExecutionResult thrown = await throwingExecutor.ExecuteAsync(CreateContext(gateConfigJson, history));

        thrown.Status.Should().Be(StepStatus.Completed, because: thrown.ErrorDetails ?? thrown.Output);
        thrown.Output.Should().Be(off.Output,
            "any gate failure convenes, and the room acquires no new failure mode from the gate");

        ColorGradeRoomStepExecutor nullGateExecutor = CreateExecutor(null, out Mock<IAgentChatClientProvider> ngChat, out Mock<IAgentRegistry> ngRegistry);
        WireHappyPathRoom(ngChat, ngRegistry);
        StepExecutionResult ungated = await nullGateExecutor.ExecuteAsync(CreateContext(gateConfigJson, history));

        ungated.Status.Should().Be(StepStatus.Completed, because: ungated.ErrorDetails ?? ungated.Output);
        ungated.Output.Should().Be(off.Output,
            "an unregistered IDecisionGate is a legal configuration, not a fatal one");
    }

    /// <summary>
    /// QA-16, the grade-room half: the <b>composed</b> path — the real <see cref="DecisionGate"/>
    /// with a real (unresolvable) provider resolution, wired into the real room executor — rather
    /// than a mocked <see cref="DecisionGateOutcome.NoDecision"/> standing in for the gate.
    ///
    /// <para>
    /// This file otherwise never constructs a real gate, which is exactly the gap QA-16 named. The
    /// expectation is unchanged from the mocked shape: no <c>Capability = Decision</c> row resolves,
    /// the gate answers <see cref="DecisionGateOutcome.NoDecision"/>, the room convenes, and the
    /// step emits the same bytes a gate-less run does. No observation row exists, because no
    /// question reached a provider.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_composed_real_gate_path_with_no_resolvable_provider_convenes_the_room()
    {
        IReadOnlyList<StepOutputHistoryEntry> history = [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))];
        string offConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        string gateConfigJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);

        ColorGradeRoomStepExecutor offExecutor = CreateExecutor(null, out Mock<IAgentChatClientProvider> offChat, out Mock<IAgentRegistry> offRegistry);
        WireHappyPathRoom(offChat, offRegistry);
        StepExecutionResult off = await offExecutor.ExecuteAsync(CreateContext(offConfigJson, history));

        off.Status.Should().Be(StepStatus.Completed, because: off.ErrorDetails ?? off.Output);

        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"RoomConveneGateTests_NoProvider_{Guid.NewGuid()}")
            .Options;

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        // Strict on both, so reaching either one fails the test rather than passing it silently:
        // with no provider there is no client to build and no observation store to open.
        var realGate = new DecisionGate(
            resolver.Object,
            new Mock<IDecisionClientFactory>(MockBehavior.Strict).Object,
            new Mock<IServiceScopeFactory>(MockBehavior.Strict).Object,
            NullLogger<DecisionGate>.Instance);

        ColorGradeRoomStepExecutor realExecutor = CreateExecutor(realGate, out Mock<IAgentChatClientProvider> realChat, out Mock<IAgentRegistry> realRegistry);
        WireHappyPathRoom(realChat, realRegistry);
        StepExecutionResult gated = await realExecutor.ExecuteAsync(CreateContext(gateConfigJson, history));

        gated.Status.Should().Be(StepStatus.Completed, because: gated.ErrorDetails ?? gated.Output);
        gated.Output.Should().Be(off.Output,
            "an unresolvable provider is another 'the gate told us nothing usable' shape, so it convenes " +
            "and changes nothing else — the same bytes a gate-less run emits");

        await using var db = new WorkflowEngineDbContext(options);
        (await db.DecisionObservations.CountAsync())
            .Should().Be(0, "no question reached a provider, so no row exists to attach answers to later");
    }

    [Fact]
    public async Task A_gate_cancellation_on_an_already_cancelled_context_is_rethrown_not_swallowed_into_a_convene()
    {
        // The one exception to "a gate failure convenes": the CALLER's own cancellation. Converting
        // it into a convene would start a room run nobody asked for, on a workflow that is being
        // stopped.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out _, out Mock<IAgentRegistry> agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionContext context = CreateContext(
            configJson,
            [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))],
            cancellationToken: new CancellationToken(canceled: true));

        Func<Task> act = () => executor.ExecuteAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.Invocations.Should().HaveCount(1);
        agentRegistry.Invocations.Should().BeEmpty(
            "nothing may be resolved once the caller has cancelled — no room participant, no solo agent");
    }

    [Fact]
    public async Task The_gate_request_carries_a_constant_state_free_of_the_view_and_the_rooms_convene_config()
    {
        var providerId = Guid.NewGuid();
        DecisionGateRequest? captured = null;
        Mock<IDecisionGate> gate = GateAnswering(DecisionGateOutcome.NoDecision, request => captured = request);

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string viewJson = BuildViewJson("s0", "s1");
        ColorGradeRoomStepConfig config = SmallestRoom(ConveneGateMode.Gate) with
        {
            ConveneAcceptAt = 0.9,
            ConveneMinMargin = 0.3,
            ConveneProviderId = providerId
        };

        // A prior step whose output is NOT the view, plus the two prompt-only pieces: each is a
        // distinct thing a wrong implementation might have concatenated into the state.
        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(config, ConfigOptions),
            [
                new StepOutputHistoryEntry(0, "Earlier", """{"prior":"PRIOR-STEP-HISTORY-MARKER"}"""),
                new StepOutputHistoryEntry(1, "Analyze", viewJson)
            ],
            userRequest: "make it punchier",
            retryReason: "the previous cut was too slow");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        captured.Should().NotBeNull("the Gate arm must actually ask — the whole point of this test is what it asks");
        DecisionGateRequest request = captured!;

        request.ExecutionId.Should().Be(context.Execution.Id);
        request.StepOrder.Should().Be(3);
        request.AgentType.Should().Be(AgentType.ColorGradeDirector,
            "the room's own decision-maker asks the pre-room question, consistent with the shadow arm");
        request.Site.Should().Be("ColorGradeRoom.convene");

        // Decision 8, as narrowed by the Security HIGH finding: the Gate path sends a state that
        // carries NO view content at all — a constant. It is no longer the bounded view, so the
        // assertion is negative on the view and everything else the workflow holds; the POSITIVE
        // narrowness test (The_gate_state_transmits_no_media_derived_text_from_the_view) is where
        // the two media-derived sentinels are hunted for.
        request.State.Content.Should().NotBeNullOrWhiteSpace(
            "a constant still has to say something; an empty state would be a silent behaviour change");
        request.State.Content.Should().NotContain("shots",
            "the bounded {view, meta} envelope is no longer transmitted to a hosted third party from this arm");
        request.State.Content.Should().NotContain("s0");
        request.State.Content.Should().NotContain("make it punchier");
        request.State.Content.Should().NotContain("the previous cut was too slow");
        request.State.Content.Should().NotContain("PRIOR-STEP-HISTORY-MARKER");
        viewJson.Should().Contain("shots", "the view really did reach the executor — otherwise the assertions above are vacuous");

        request.Config.Mode.Should().Be(DecisionGateMode.Gate);
        request.Config.AcceptAt.Should().Be(0.9, "the room's ConveneAcceptAt, passed through as an INPUT — this is a " +
            "phase-1 fallback, never asserted here as a calibrated value");
        request.Config.MinMargin.Should().Be(0.3);
        request.Config.ProviderId.Should().Be(providerId);
        request.Config.TimeoutSeconds.Should().Be(5,
            "the gate-level deadline stays D-004's own default: the four Convene* members are the whole config " +
            "surface a room defines, and TimeoutSeconds is not one of them");

        request.SystemTwoAnswer.Should().BeNull("no agent has run yet on the pre-room Gate path");
        request.AgentTokensUsed.Should().BeNull("no agent has run yet, so there are no agent tokens to attribute");
        request.Questions.Should().OnlyContain(q => q.SystemTwoAnswer == null,
            "a pre-room question has no counterfactual, so the observation it writes claims no agreement " +
            "rather than inventing a disagreement");
    }

    // =====================================================================
    // The Gate state is a CONSTANT — the Security HIGH finding this ticket closes (T-108)
    //
    // The two tests below are the pin the finding did not have. The old state test pinned the state
    // to the whole bounded view and asserted only the ABSENCE of the user request and the prior-step
    // history — an assertion the vulnerable code satisfies exactly, which is why it could not catch
    // the leak. Narrowness is therefore asserted POSITIVELY here: two unique sentinels, planted in
    // the two media-derived text fields the upstream VideoAnalyze step produces, hunted for
    // everywhere in the request that was actually transmitted.
    // =====================================================================

    /// <summary>
    /// The ASR transcript sentinel — what <c>view.segments[].text</c> carries. Deliberately a
    /// value that cannot occur anywhere else in the request by coincidence.
    /// </summary>
    private const string AsrSentinel = "ASR-SENTINEL-7f3a";

    /// <summary>The vision-caption sentinel — what <c>view.shots[].c</c> carries.</summary>
    private const string CaptionSentinel = "CAPTION-SENTINEL-91c2";

    /// <summary>The user request the composed Shadow input is wrapped under, as a sentinel of its own.</summary>
    private const string ShadowUserRequestSentinel = "USER-REQUEST-SENTINEL-2b81";

    /// <summary>The prior-iteration retry guidance, likewise.</summary>
    private const string ShadowRetrySentinel = "RETRY-GUIDANCE-SENTINEL-5d40";

    /// <summary>
    /// A bounded view shaped exactly like the one <c>VideoAnalyzeStepExecutor</c> emits, carrying
    /// the two media-derived text fields this ticket is about: the ASR transcript on
    /// <c>segments[].text</c> and vision-caption prose on <c>shots[].c</c> — plus the ordinary shot
    /// ids the room's offered-ids gate needs to reach the convene dispatch point at all.
    /// </summary>
    private static string BuildViewWithMediaSentinels() =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                shots = new object[]
                {
                    new { id = "s0", startSec = 0.0, endSec = 2.0, c = CaptionSentinel },
                    new { id = "s1", startSec = 2.0, endSec = 4.0, c = "a wide establishing shot" }
                },
                segments = new object[]
                {
                    new { startSec = 0.0, endSec = 2.0, text = AsrSentinel }
                }
            },
            meta = new { offeredIdCount = 2 }
        });

    /// <summary>
    /// POSITIVE narrowness: the Gate arm must transmit a state — and a question set — free of the
    /// media-derived text the upstream analysis step puts in the bounded view.
    ///
    /// <para>
    /// This is the assertion the Security finding calls for, and it is deliberately not a length
    /// bound and not a substring-of-the-view check: both of those are satisfied by the vulnerable
    /// expression (<c>State: new DecisionState(viewJson)</c>), which is precisely how the leak got
    /// through. Two unique sentinels are planted on the way in, confirmed present in the view the
    /// executor was actually handed, and then hunted for in the captured request: in the state, in
    /// the questions (names, instructions, options), and finally in the whole serialized request so
    /// no other route can carry them either.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_gate_state_transmits_no_media_derived_text_from_the_view()
    {
        DecisionGateRequest? captured = null;
        Mock<IDecisionGate> gate = GateAnswering(DecisionGateOutcome.NoDecision, request => captured = request);

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string viewJson = BuildViewWithMediaSentinels();
        StepExecutionContext context = CreateContext(
            EscalatingGradeRoomConfigJson(),
            [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            userRequest: ShadowUserRequestSentinel,
            retryReason: ShadowRetrySentinel);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        captured.Should().NotBeNull("the Gate arm has to have asked — a test that captured nothing would pass vacuously");

        // The sentinels really were on the way in. Without this, "they are absent from the request"
        // would also pass for a view that never carried them, which is the vacuous shape again.
        viewJson.Should().Contain(AsrSentinel, "the fixture must plant the transcript sentinel it hunts for");
        viewJson.Should().Contain(CaptionSentinel, "the fixture must plant the caption sentinel it hunts for");

        DecisionGateRequest request = captured!;
        request.Questions.Should().HaveCount(4,
            "the four grade questions are the entire reason this arm exists; a zero-question request would make " +
            "the question assertions below vacuous");

        // 1. The state — what is transmitted verbatim to the hosted Decision provider.
        request.State.Content.Should().NotContain(AsrSentinel,
            "the ASR transcript (view.segments[].text) is media-derived prose that has no business leaving the host " +
            "for these four closed enum-word questions");
        request.State.Content.Should().NotContain(CaptionSentinel,
            "the vision-caption prose (view.shots[].c) is media-derived prose that has no business leaving the host " +
            "for these four closed enum-word questions");

        // 2. The questions, in all three of their free-text-bearing fields. The grade room derives
        //    its option set from a closed static list and ignores viewRoot outright, so there is no
        //    legitimate route for view content into any of them — this asserts that, rather than
        //    assuming it.
        foreach (DecisionGateQuestion question in request.Questions)
        {
            question.Name.Should().NotContain(AsrSentinel);
            question.Name.Should().NotContain(CaptionSentinel);
            question.Site.Should().NotContain(AsrSentinel);
            question.Site.Should().NotContain(CaptionSentinel);
            question.Instructions.Should().NotContain(AsrSentinel);
            question.Instructions.Should().NotContain(CaptionSentinel);
            question.Options.Should().NotContain(option =>
                option.Contains(AsrSentinel, StringComparison.Ordinal) || option.Contains(CaptionSentinel, StringComparison.Ordinal));
        }

        // 3. Everywhere else in the request. The two checks above name the two places the leak was
        //    found and reasoned about; this one is the backstop that makes "the projection cannot
        //    leak them by another route" a measurement instead of a hope.
        string serialized = JsonSerializer.Serialize(request);
        serialized.Should().NotContain(AsrSentinel,
            "no field of the transmitted request may carry the ASR transcript");
        serialized.Should().NotContain(CaptionSentinel,
            "no field of the transmitted request may carry the vision-caption prose");
    }

    /// <summary>
    /// The other half of the same pin: this ticket narrows the <b>Gate</b> arm only. The Shadow arm
    /// deliberately still sends the composed agent input — the bounded view under the workflow's user
    /// request and any prior-iteration retry guidance — and that behaviour must be unchanged, so a
    /// future "tidy-up" that narrowed both arms together fails here rather than silently changing
    /// phase 1's byte-identical Shadow behaviour.
    /// </summary>
    [Fact]
    public async Task Shadow_still_sends_the_composed_input_so_only_the_gate_arm_was_narrowed()
    {
        DecisionState? capturedState = null;
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, AgentType, DecisionState, IReadOnlyList<DecisionGateQuestion>, DecisionGateConfig, CancellationToken>(
                (_, _, _, state, _, _, _) => capturedState = state);

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object,
            _ => [new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice, "what look", ["Warm", "Cool"], "Warm")],
            out Mock<IAgentChatClientProvider> chatClients,
            out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string viewJson = BuildViewWithMediaSentinels();
        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions),
            [new StepOutputHistoryEntry(1, "Analyze", viewJson)],
            userRequest: ShadowUserRequestSentinel,
            retryReason: ShadowRetrySentinel);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedState.Should().NotBeNull("the Shadow arm must have run — it is Shadow mode with a question set and a gate");

        // All three layers of the composed input, by sentinel: the bounded view (including the very
        // media-derived text the Gate arm has just stopped sending — that asymmetry is ON PURPOSE),
        // the workflow's user request, and the retry guidance.
        capturedState!.Content.Should().Contain(CaptionSentinel,
            "the Shadow arm keeps sending the full composed input, byte for byte, by design");
        capturedState.Content.Should().Contain(AsrSentinel);
        capturedState.Content.Should().Contain(ShadowUserRequestSentinel,
            "the composed input is the bounded view UNDER the user request");
        capturedState.Content.Should().Contain(ShadowRetrySentinel,
            "and under any prior-iteration retry guidance");
    }

    // =====================================================================
    // The room's OWN deadline is not defeatable by client config (T-108)
    // =====================================================================

    /// <summary>
    /// <see cref="IRoomStepConfig.RoomTimeoutSeconds"/> is read straight out of the step's
    /// client-supplied config JSON. The lower bound was already guarded; the upper bound was not,
    /// and <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> measures its delay in
    /// milliseconds as a <c>uint</c> — so a value above 4_294_967 s throws
    /// <see cref="ArgumentOutOfRangeException"/>, which the room's never-throws handling catches and
    /// turns into a DEGRADE to the solo fallback, and the step then FAILS where no solo agent is
    /// registered. A number a caller should never have sent must not be able to fail a step. Both
    /// ends are clamped at the use site (the room config records are another department's files and
    /// are out of this ticket's scope), and this test is what proves the step completes instead.
    ///
    /// <para>
    /// The two <c>Off</c>-path values (0 and negative) are the pre-existing guard, kept here so the
    /// clamp is pinned at BOTH ends rather than only the one that was missing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(4_294_968)]
    [InlineData(int.MaxValue)]
    public async Task An_out_of_range_room_timeout_still_yields_a_working_room(int roomTimeoutSeconds)
    {
        // Strict, and Configured Off: ANY gate call — including a property access — fails the test,
        // so this cannot quietly become a Gate-path test.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        ColorGradeRoomStepConfig config = SmallestRoom(ConveneGateMode.Off) with
        {
            RoomTimeoutSeconds = roomTimeoutSeconds
        };

        StepExecutionContext context = CreateContext(
            JsonSerializer.Serialize(config, ConfigOptions),
            [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0", "s1"))]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().NotBe("timeout",
            "a clamped deadline is still a deadline the room beats on its own — a value the clamp has to " +
            "rescue must not turn into a silently timed-out room");
        gate.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_gate_questions_recover_the_solo_colorists_site_and_short_name_so_both_paths_aggregate()
    {
        // The question-type gap, pinned: DecisionQuestion has no Site field, so the site can only be
        // carried in Name — and it must come back out again, or a Gate observation lands on a
        // different (Site, Question) series from the Solo and Shadow rows for the same field.
        DecisionGateRequest? captured = null;
        Mock<IDecisionGate> gate = GateAnswering(DecisionGateOutcome.NoDecision, request => captured = request);

        SeamProbeExecutor executor = CreateExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Gate), ConfigOptions);
        StepExecutionContext context = CreateContext(configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson("s0"))]);

        await executor.ExecuteAsync(context);

        captured.Should().NotBeNull();
        IReadOnlyList<DecisionGateQuestion> questions = captured!.Questions;

        questions.Should().HaveCount(4);
        questions.Select(q => q.Site).Should().Equal(
            "Colorist.Look", "Colorist.Strength", "Colorist.ShadowTone", "Colorist.HighlightTone");

        // DecisionGate.ExecuteAsync looks the answer up by Name and persists it as the observation's
        // Question, so the last dot-segment is what keeps a Gate row on the same series as the
        // shadow one and the solo agent's.
        questions.Select(q => q.Name).Should().Equal("Look", "Strength", "ShadowTone", "HighlightTone");

        IReadOnlyList<DecisionGateQuestion> solo = SoloColoristQuestions();
        solo.Should().HaveCount(4);

        for (int i = 0; i < solo.Count; i++)
        {
            questions[i].Site.Should().Be(solo[i].Site);
            questions[i].Name.Should().Be(solo[i].Name);
            questions[i].Kind.Should().Be(solo[i].Kind);
            questions[i].Instructions.Should().Be(solo[i].Instructions);
            questions[i].Options.Should().Equal(solo[i].Options);
        }
    }

    // =====================================================================
    // Gate, escalated — completing the rows the gate already wrote (T-107)
    //
    // DecideAsync persists its own decision_observations rows when it decides, and
    // on the pre-room Gate path nothing has run yet, so every escalated row is written with NO
    // agreement claim. AttachSystemTwoAnswersAsync is the compensating half: after the room has
    // synthesized its decision, the executor fills those rows in from the SAME seam the Shadow arm
    // uses, keyed by the SHORT question name the rows were written under.
    //
    // The room still persists NOTHING: the attach is a targeted update of rows the gate owns, so
    // there is exactly one row per question. A room that also persisted would double every room
    // site's counts in the calibration view, which is the first thing this initiative ships.
    // =====================================================================

    /// <summary>
    /// The escalating Gate path end to end through the production <c>ExecuteAsync</c>, wired with a
    /// canned room and the deliberate four-word grade: the room runs and synthesizes, and the
    /// executor completes the rows <c>DecideAsync</c> already wrote.
    ///
    /// <para>
    /// <paramref name="attachBehaviour"/> overrides what the attach does — the default records it and
    /// returns. The recorded collections are returned BY REFERENCE and filled in as the run happens,
    /// because the run is started by the caller, not here.
    /// </para>
    /// </summary>
    private static (SeamProbeExecutor Executor, StepExecutionContext Context, List<AttachCall> AttachCalls,
        List<DecisionGateRequest> SentRequests, Mock<IDecisionGate> Gate) EscalatedGradeRoom(
            Func<Task>? attachBehaviour = null)
    {
        return EscalatedGradeRoom(Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachBehaviour);
    }

    /// <summary>The same, for a caller that needs a different gate outcome.</summary>
    private static (SeamProbeExecutor Executor, StepExecutionContext Context, List<AttachCall> AttachCalls,
        List<DecisionGateRequest> SentRequests, Mock<IDecisionGate> Gate) EscalatedGradeRoom(
            DecisionGateOutcome outcome,
            Func<Task>? attachBehaviour = null)
    {
        var attachCalls = new List<AttachCall>();
        var sentRequests = new List<DecisionGateRequest>();

        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            outcome, attachCalls, sentRequests.Add);

        if (attachBehaviour is not null)
        {
            gate
                .Setup(g => g.AttachSystemTwoAnswersAsync(
                    It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Returns(attachBehaviour);
        }

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("s1 leans cool; a subtle warm lift suits it."));
        WireDeliberateRoom(agentRegistry);

        StepExecutionContext context = CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory());
        return (executor, context, attachCalls, sentRequests, gate);
    }

    [Fact]
    public async Task An_escalated_gate_runs_the_room_then_completes_its_rows_with_the_rooms_own_answers()
    {
        (SeamProbeExecutor executor, StepExecutionContext context, List<AttachCall> attachCalls,
            List<DecisionGateRequest> sentRequests, Mock<IDecisionGate> gate) = EscalatedGradeRoom();

        sentRequests.Should().BeEmpty("the gate was not asked yet — this is the pre-run state");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().NotBe("not-convened",
            "an escalating gate convenes the room — the escalated path is the only one this arm exists on");

        // Both halves, once each and in that order, read off the mock's OWN invocation log rather
        // than inferred: the gate must have decided before the room ran, and the attach must follow
        // the room's synthesis, because before it there is no System Two answer to attach.
        gate.Invocations.Select(i => i.Method.Name).Should().Equal(
            nameof(IDecisionGate.DecideAsync), nameof(IDecisionGate.AttachSystemTwoAnswersAsync));

        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "one set of rows exists for this step, so one attach — a room must never double-write them");

        AttachCall attach = attachCalls.Should().ContainSingle().Subject;
        attach.ExecutionId.Should().Be(context.Execution.Id, "the rows are keyed by execution and step order");
        attach.StepOrder.Should().Be(3);

        attach.AgentTokensUsed.Should().BeNull(
            "a room's StepExecutionResult carries TokensUsed = 0 with no aggregated per-seat count, and the " +
            "interface's null means \"leave the stored value alone\" — 0 would record a measurement nobody took");

        // The room's OWN synthesized words, which are deliberately none of the gate's own answers
        // (the gate answered a 0.44/0.41 Look split; the room decided Filmic).
        attach.Answers.Should().HaveCount(4);
        attach.Answers["Look"].Should().Be(DecisionLook);
        attach.Answers["Strength"].Should().Be(DecisionStrength);
        attach.Answers["ShadowTone"].Should().Be(DecisionShadowTone);
        attach.Answers["HighlightTone"].Should().Be(DecisionHighlightTone);
    }

    [Fact]
    public async Task The_attach_keys_are_exactly_the_question_names_the_gate_arm_sent()
    {
        // The silent-no-op guard. AttachSystemTwoAnswersAsync matches rows by question NAME, and the
        // rows were written by DecideAsync from the questions the arm sent — which ToConveneGateQuestion
        // projects as Site: "Colorist.Look" / Name: "Look". A dictionary keyed by Site (or by the
        // dotted name) matches nothing, leaves every row null, and reports no error at all — which is
        // exactly why this equality, and not a "no exception was thrown" assertion, is the check that
        // matters.
        (SeamProbeExecutor executor, StepExecutionContext context, List<AttachCall> attachCalls,
            List<DecisionGateRequest> sentRequests, _) = EscalatedGradeRoom();

        StepExecutionResult result = await executor.ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        AttachCall attach = attachCalls.Should().ContainSingle().Subject;
        DecisionGateRequest sent = sentRequests.Should().ContainSingle(
            "the escalating arm must actually have asked").Subject;

        attach.Answers.Keys.Should().BeEquivalentTo(
            sent.Questions.Select(q => q.Name),
            "the attach key set IS the Name set of the request the arm sent — that is the only keying the " +
            "rows written by DecideAsync can be matched under");

        sent.Questions.Select(q => q.Name).Should().Equal("Look", "Strength", "ShadowTone", "HighlightTone");
        sent.Questions.Select(q => q.Site).Should().Equal(
            "Colorist.Look", "Colorist.Strength", "Colorist.ShadowTone", "Colorist.HighlightTone");

        attach.Answers.Keys.Should().NotContain("Colorist.Look",
            "the dotted Site is what the observation's Site column carries, never its Question column");
        attach.Answers.Keys.Should().NotContain("ColorGradeRoom.convene",
            "the pre-room call's own Site must not leak into the answer keys either");
        attach.Answers.Keys.Should().NotContain("ColorGradeRoom.decision");
    }

    [Fact]
    public async Task Only_non_null_room_answers_are_attached_and_the_dictionary_is_ordinal()
    {
        // Two shapes at once, both of which the real seam can produce. A null SystemTwoAnswer means
        // "System Two has no answer for this field": recording it as an empty-string disagreement
        // would invent an observation that was never made, so it must be skipped entirely. And the
        // comparer must be Ordinal — mirroring AgentStepExecutor's own attach site — which is asserted
        // as the comparer INSTANCE, since a culture-sensitive default would also keep these two names
        // apart and so would not discriminate.
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        List<DecisionGateQuestion> questions =
        [
            new("Colorist.Look", "Look", DecisionKind.Choice, "what look", ["Warm", "Cool"], DecisionLook),
            new("Colorist.Strength", "Strength", DecisionKind.Choice, "what strength", ["Subtle"], null),
            new("Colorist.ShadowTone", "ShadowTone", DecisionKind.Choice, "what shadow", ["Neutral"], DecisionShadowTone),
            new("Colorist.ShadowTone", "shadowTone", DecisionKind.Choice, "what shadow", ["Neutral"], "Lifted"),
            new("Colorist.HighlightTone", "HighlightTone", DecisionKind.Choice, "what highlight", ["Neutral"], DecisionHighlightTone)
        ];

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object, _ => questions,
            out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);

        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("s1 leans cool; a subtle warm lift suits it."));
        WireDeliberateRoom(agentRegistry);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        AttachCall attach = attachCalls.Should().ContainSingle().Subject;
        attach.Answers.Should().NotContainKey("Strength", "a null answer is not an answer, so it is never attached");
        attach.Answers.Keys.Should().BeEquivalentTo(["Look", "ShadowTone", "shadowTone", "HighlightTone"]);
        attach.Answers["ShadowTone"].Should().Be(DecisionShadowTone);
        attach.Answers["shadowTone"].Should().Be("Lifted",
            "case-differing names are distinct questions under Ordinal — collapsing them would attach one " +
            "field's answer to another field's row");

        ((Dictionary<string, string>)attach.Answers).Comparer.Should().BeSameAs(
            StringComparer.Ordinal,
            "mirrors AgentStepExecutor's own attach site, which builds the map with StringComparer.Ordinal");
    }

    [Fact]
    public async Task An_accepted_outcome_never_attaches_answers_because_no_room_ran()
    {
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: true, ConfidentLookResult()), attachCalls);

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);
        WireSoloFallback(agentRegistry, new AgentRunResult
        {
            Success = true,
            Output = BuildPlanJson(look: SoloFallbackLook, rationale: "solo")
        });

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().Be("not-convened");

        gate.Invocations.Select(i => i.Method.Name).Should().Equal(
            [nameof(IDecisionGate.DecideAsync)],
            "the gate WAS asked — it simply accepted — and nothing else was ever called on it");
        attachCalls.Should().BeEmpty(
            "an accepted outcome means no room ran, so there is no System Two answer to attach, and the " +
            "accepted rows are the gate's own to leave alone");
    }

    [Fact]
    public async Task A_NoDecision_outcome_never_attaches_answers_because_the_gate_wrote_no_row()
    {
        // NoDecision is the uniform "nothing happened" value — no provider, the gate-level timeout,
        // any failure — and it is also the shape the convene path takes for a room with no question
        // set. The gate wrote no observation for this step, so an attach would be a call about rows
        // that do not exist.
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(DecisionGateOutcome.NoDecision, attachCalls);

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(IDecisionGate.DecideAsync));
        attachCalls.Should().BeEmpty("NoDecision wrote no rows, so there is nothing to complete");
    }

    [Fact]
    public async Task A_gate_that_threw_never_attaches_answers_because_it_never_decided()
    {
        // A gate failure convenes — but it wrote nothing, so it is not an escalation and the attach
        // must stay away. MockBehavior.Strict, so ANY call other than the one that throws fails here.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the gate itself blew up"));

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(IDecisionGate.DecideAsync));
    }

    [Fact]
    public async Task ConveneGate_Off_never_attaches_answers()
    {
        // MockBehavior.Strict with NO setups at all: the strongest available statement, since any
        // call to the gate — attach included — fails rather than being merely recorded.
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Off), ConfigOptions);
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(configJson, GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ConveneGate_Shadow_never_attaches_answers()
    {
        // Shadow records its own rows AFTER synthesis, with the answer already in hand — so it has
        // nothing to complete, and AttachSystemTwoAnswersAsync must never be reached from it. Both
        // the shadow call and the attach are set up on a strict mock, so "no attach" is a recorded
        // fact rather than an inference from an exception.
        var attachCalls = new List<AttachCall>();
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        gate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gate
            .Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<Guid, int, IReadOnlyDictionary<string, string>, int?, CancellationToken>(
                (executionId, stepOrder, answers, tokens, _) =>
                    attachCalls.Add(new AttachCall(executionId, stepOrder, answers, tokens)));

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        string configJson = JsonSerializer.Serialize(SmallestRoom(ConveneGateMode.Shadow), ConfigOptions);
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(configJson, GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Select(i => i.Method.Name).Should().Equal(
            [nameof(IDecisionGate.RunShadowAsync)],
            "Shadow observes and never completes — its rows were written with their answer already in hand");
        attachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_edit_room_in_Gate_mode_never_attaches_answers_because_it_has_no_question_set()
    {
        // The edit room declines all three convene seams explicitly (decision 7), so Gate mode is an
        // explicit no-op for it: the gate is never consulted, nothing escalates, and BuildShadowQuestions
        // returns null so there is no answer set to attach. The gate is set up to escalate anyway, so
        // "it was never called" is a fact about the room rather than about the mock's configuration.
        (EditRoomSeamProbeExecutor executor, Mock<IDecisionGate> gate, string configJson,
            IReadOnlyList<StepOutputHistoryEntry> history, _) = EditRoomGated();

        StepExecutionResult result = await executor.ExecuteAsync(CreateEditRoomContext(configJson, history));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().BeEmpty(
            "the edit room's BuildConveneQuestions override returns null, so the Gate arm short-circuits " +
            "before the gate — and there is therefore nothing for an attach to complete");
    }

    [Fact]
    public async Task A_graphics_room_in_Gate_mode_never_attaches_answers_because_it_has_no_question_set()
    {
        (GraphicsRoomSeamProbeExecutor executor, Mock<IDecisionGate> gate, string configJson,
            IReadOnlyList<StepOutputHistoryEntry> history, _) = GraphicsRoomGated();

        StepExecutionResult result = await executor.ExecuteAsync(CreateGraphicsRoomContext(configJson, history));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Invocations.Should().BeEmpty(
            "the graphics room's BuildConveneQuestions override returns null — same explicit no-op as the " +
            "edit room's, and the same absent answer set");
    }

    [Fact]
    public async Task A_room_that_escalates_but_supplies_no_question_set_never_attaches_answers()
    {
        // The edit and graphics rooms cannot express this on their own, because they never reach the
        // gate at all. This isolates the OTHER guard: a grade room that escalates normally, runs,
        // synthesizes — and supplies no shadow question set. That guard is what makes the attach a
        // no-op rather than a call with an empty dictionary, exactly as it is on the Shadow arm.
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        ShadowQuestionsRoomExecutor executor = CreateShadowExecutor(
            gate.Object, _ => null,
            out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        WireHappyPathRoom(chatClients, agentRegistry);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().NotBe("not-convened",
            "it DID escalate and convene — so the absence of an attach is the question-set guard, not the " +
            "escalation guard");
        gate.Invocations.Select(i => i.Method.Name).Should().Equal(nameof(IDecisionGate.DecideAsync));
        attachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_room_that_degraded_to_the_solo_fallback_never_attaches_answers()
    {
        // The room ran but its synthesized decision did not survive, so the step emits the SOLO
        // fallback's answer. Attaching it would claim an agreement against an answer this room never
        // gave, against rows written from questions asked BEFORE either ran.
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);

        // The room CANNOT run: resolving any participant's chat client blows up, so RunRoomAsync
        // fails, the room degrades, and (FallbackToSoloColorist defaults to true) the step emits the
        // SOLO colorist's answer instead of anything this room synthesized.
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no chat client for the room"));
        WireSoloFallback(agentRegistry, new AgentRunResult
        {
            Success = true,
            Output = BuildPlanJson(look: SoloFallbackLook, rationale: "solo")
        });

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EscalatingGradeRoomConfigJson(), GradeRoomHistory()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        RoomMeta(result).GetProperty("degraded").GetBoolean().Should().BeTrue(
            "this fixture exists to reach the degrade branch — without it the test would prove nothing");
        RoomMeta(result).GetProperty("terminationReason").GetString().Should().NotBe("not-convened",
            "it DID escalate, so the absence of an attach is about the degraded room, not the gate outcome");
        attachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_attach_that_throws_leaves_the_escalated_steps_status_output_and_decision_unchanged()
    {
        // The same run twice: once with an attach that blows up, once with an attach that returns
        // cleanly. Byte-identical output is the assertion, because "the status is still Completed"
        // alone would not notice an arm that had churned the emitted decision.
        (SeamProbeExecutor throwingExecutor, StepExecutionContext throwingContext, _, _, _) =
            EscalatedGradeRoom(() => throw new InvalidOperationException("the attach blew up"));

        (SeamProbeExecutor cleanExecutor, StepExecutionContext cleanContext, List<AttachCall> cleanCalls, _, _) =
            EscalatedGradeRoom();

        StepExecutionResult thrown = await throwingExecutor.ExecuteAsync(throwingContext);
        StepExecutionResult clean = await cleanExecutor.ExecuteAsync(cleanContext);

        cleanCalls.Should().ContainSingle("the reference run must actually have attached, or the comparison is vacuous");

        thrown.Status.Should().Be(StepStatus.Completed, because: thrown.ErrorDetails ?? thrown.Output);
        thrown.ErrorDetails.Should().BeNull("bookkeeping about a made decision can never fail the step");
        thrown.Output.Should().Be(clean.Output,
            "an attach failure changes neither the emitted decision nor the room metadata");
        thrown.NextStepIndex.Should().Be(clean.NextStepIndex);
        thrown.TokensUsed.Should().Be(clean.TokensUsed);
    }

    [Fact]
    public async Task An_attach_cancellation_on_an_already_cancelled_context_is_rethrown_not_swallowed()
    {
        // The one exception to the never-throws discipline, exactly as on the shadow arm and the gate
        // dispatch point: a cancelled workflow must stop, not quietly continue on to Success. The token
        // is cancelled from INSIDE the attach, so the catch filter sees a genuinely cancelled context
        // (rather than the mock merely throwing for an unrelated reason).
        using var cts = new CancellationTokenSource();

        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        gate
            .Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() =>
            {
                cts.Cancel();
                throw new OperationCanceledException();
            });

        SeamProbeExecutor executor = CreateExecutor(
            gate.Object, out Mock<IAgentChatClientProvider> chatClients, out Mock<IAgentRegistry> agentRegistry);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient("s1 leans cool; a subtle warm lift suits it."));
        WireDeliberateRoom(agentRegistry);

        StepExecutionContext context = CreateContext(
            EscalatingGradeRoomConfigJson(), GradeRoomHistory(), cancellationToken: cts.Token);

        Func<Task> act = () => executor.ExecuteAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The edit room in <see cref="ConveneGateMode.Gate"/> with a gate configured to ESCALATE, plus the
    /// happy-path room wiring — so "the gate was never called" is a fact about the room's own null seam
    /// rather than about the mock being unable to answer.
    /// </summary>
    private static (EditRoomSeamProbeExecutor Executor, Mock<IDecisionGate> Gate, string ConfigJson,
        IReadOnlyList<StepOutputHistoryEntry> History, Mock<IAgentChatClientProvider> ChatClients) EditRoomGated()
    {
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        EditRoomSeamProbeExecutor executor = CreateEditRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients);
        WireHappyPathEditRoom(chatClients, executor.Registry);

        string configJson = JsonSerializer.Serialize(SmallestEditRoom(ConveneGateMode.Gate), ConfigOptions);
        return (executor, gate, configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildEditRoomViewJson())], chatClients);
    }

    /// <summary>The graphics room's counterpart of <see cref="EditRoomGated"/>.</summary>
    private static (GraphicsRoomSeamProbeExecutor Executor, Mock<IDecisionGate> Gate, string ConfigJson,
        IReadOnlyList<StepOutputHistoryEntry> History, Mock<IAgentChatClientProvider> ChatClients) GraphicsRoomGated()
    {
        var attachCalls = new List<AttachCall>();
        Mock<IDecisionGate> gate = GateAnsweringAndRecordingAttach(
            Outcome(hasDecision: true, accepted: false, LookSplitResult()), attachCalls);

        GraphicsRoomSeamProbeExecutor executor = CreateGraphicsRoomProbeExecutor(gate.Object, out Mock<IAgentChatClientProvider> chatClients);
        WireHappyPathGraphicsRoom(chatClients, executor.Registry);

        string configJson = JsonSerializer.Serialize(SmallestGraphicsRoom(ConveneGateMode.Gate), ConfigOptions);
        return (executor, gate, configJson, [new StepOutputHistoryEntry(1, "Analyze", BuildEditRoomViewJson())], chatClients);
    }

    // =====================================================================
    // Test doubles
    // =====================================================================

    /// <summary>
    /// The stock grade room with the base class's three protected seams exposed (and NONE of them
    /// overridden — that is the point of this one), so their defaults can be asserted directly.
    /// </summary>
    private sealed class SeamProbeExecutor : ColorGradeRoomStepExecutor
    {
        public SeamProbeExecutor(
            IAgentChatClientProvider chatClients,
            IAgentToolProvider toolProvider,
            IAgentRegistry agentRegistry,
            IProjectFileWorkspace workspace,
            IWorkflowExecutionContextAccessor executionContextAccessor,
            ILogger<ColorGradeRoomStepExecutor> logger,
            IDecisionGate? decisionGate)
            : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger, decisionGate)
        {
        }

        public IReadOnlyList<DecisionQuestion>? ProbeConveneQuestions(JsonNode? viewRoot) =>
            BuildConveneQuestions(viewRoot);

        /// <summary>
        /// The pre-agenda opening message, so a test can state its expectation in exactly the terms
        /// the production code composes it: WithRetryGuidance(WithUserRequest(BuildOpeningMessage)).
        /// </summary>
        public string ProbeOpeningMessage(string viewJson) => BuildOpeningMessage(viewJson);

        public string? ProbeConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) =>
            BuildConveneAgenda(viewRoot, answers);

        public IReadOnlyList<DecisionGateQuestion>? ProbeShadowQuestions(ColorGradePlanOutput decision) =>
            BuildShadowQuestions(decision);
    }

    /// <summary>
    /// The stock grade room with <see cref="RoomStepExecutorBase{TDecision}.BuildShadowQuestions"/>
    /// overridden — i.e. exactly the shape D-102/D-103 will give the real rooms. Everything else is
    /// the production executor, unmodified.
    /// </summary>
    private sealed class ShadowQuestionsRoomExecutor : ColorGradeRoomStepExecutor
    {
        private readonly Func<ColorGradePlanOutput, IReadOnlyList<DecisionGateQuestion>?> _buildQuestions;

        public ShadowQuestionsRoomExecutor(
            IAgentChatClientProvider chatClients,
            IAgentToolProvider toolProvider,
            IAgentRegistry agentRegistry,
            IProjectFileWorkspace workspace,
            IWorkflowExecutionContextAccessor executionContextAccessor,
            ILogger<ColorGradeRoomStepExecutor> logger,
            IDecisionGate? decisionGate,
            Func<ColorGradePlanOutput, IReadOnlyList<DecisionGateQuestion>?> buildQuestions)
            : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger, decisionGate)
        {
            _buildQuestions = buildQuestions;
        }

        public IReadOnlyList<DecisionGateQuestion>? ProbeShadowQuestions(ColorGradePlanOutput decision) =>
            BuildShadowQuestions(decision);

        protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(ColorGradePlanOutput decision) =>
            _buildQuestions(decision);
    }

    /// <summary>
    /// A container-constructible room executor: unlike <see cref="ShadowQuestionsRoomExecutor"/> it
    /// takes no delegate, so Microsoft DI can build it, and it exposes the base class's protected
    /// <c>DecisionGate</c> field so the injection itself can be asserted.
    /// </summary>
    private sealed class DiProbeRoomExecutor : ColorGradeRoomStepExecutor
    {
        public DiProbeRoomExecutor(
            IAgentChatClientProvider chatClients,
            IAgentToolProvider toolProvider,
            IAgentRegistry agentRegistry,
            IProjectFileWorkspace workspace,
            IWorkflowExecutionContextAccessor executionContextAccessor,
            ILogger<ColorGradeRoomStepExecutor> logger,
            IDecisionGate? decisionGate = null)
            : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger, decisionGate)
        {
        }

        /// <summary>What the container actually injected into the optional trailing parameter.</summary>
        public IDecisionGate? GateObserved => DecisionGate;

        protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(ColorGradePlanOutput decision) =>
        [
            new DecisionGateQuestion("ColorGradeRoom.decision", "look", DecisionKind.Choice,
                "what look", ["Warm", "Cool", "None"], decision.Look)
        ];
    }

    /// <summary>
    /// An <see cref="EditRoomStepExecutor"/> with its three convene seams exposed, so the room's
    /// deliberate <c>null</c> overrides can be asserted directly rather than inferred from the
    /// absence of behaviour. The sandbox tools are <c>null</c> (the same shorthand
    /// <c>EditRoomStepExecutorTests</c> uses): <c>PrepareViewAsync</c> wraps its sandbox read in
    /// its own try/catch and soft-fails, so nothing in these tests needs a real one.
    /// </summary>
    private sealed class EditRoomSeamProbeExecutor : EditRoomStepExecutor
    {
        public EditRoomSeamProbeExecutor(
            IAgentChatClientProvider chatClients,
            IAgentToolProvider toolProvider,
            Mock<IAgentRegistry> agentRegistry,
            IProjectFileWorkspace workspace,
            IWorkflowExecutionContextAccessor executionContextAccessor,
            IDecisionGate? decisionGate)
            : base(chatClients, toolProvider, agentRegistry.Object, workspace,
                   executionContextAccessor, NullLogger<EditRoomStepExecutor>.Instance, null!, decisionGate)
        {
            Registry = agentRegistry;
        }

        /// <summary>The registry this room was built with, so a test can wire a canned director into it.</summary>
        public Mock<IAgentRegistry> Registry { get; }

        public IReadOnlyList<DecisionQuestion>? ProbeConveneQuestions(JsonNode? viewRoot) =>
            BuildConveneQuestions(viewRoot);

        public string? ProbeConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) =>
            BuildConveneAgenda(viewRoot, answers);

        public IReadOnlyList<DecisionGateQuestion>? ProbeShadowQuestions(VideoEditDecisionOutput decision) =>
            BuildShadowQuestions(decision);
    }

    /// <summary>The graphics room's counterpart of <see cref="EditRoomSeamProbeExecutor"/>.</summary>
    private sealed class GraphicsRoomSeamProbeExecutor : GraphicsRoomStepExecutor
    {
        public GraphicsRoomSeamProbeExecutor(
            IAgentChatClientProvider chatClients,
            IAgentToolProvider toolProvider,
            Mock<IAgentRegistry> agentRegistry,
            IProjectFileWorkspace workspace,
            IWorkflowExecutionContextAccessor executionContextAccessor,
            IDecisionGate? decisionGate)
            : base(chatClients, toolProvider, agentRegistry.Object, workspace,
                   executionContextAccessor, NoopPlacementAnnotator(), NullLogger<GraphicsRoomStepExecutor>.Instance,
                   decisionGate)
        {
            Registry = agentRegistry;
        }

        /// <summary>The registry this room was built with, so a test can wire a canned director into it.</summary>
        public Mock<IAgentRegistry> Registry { get; }

        public IReadOnlyList<DecisionQuestion>? ProbeConveneQuestions(JsonNode? viewRoot) =>
            BuildConveneQuestions(viewRoot);

        public string? ProbeConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) =>
            BuildConveneAgenda(viewRoot, answers);

        public IReadOnlyList<DecisionGateQuestion>? ProbeShadowQuestions(MotionGraphicsPlanOutput decision) =>
            BuildShadowQuestions(decision);
    }

    /// <summary>
    /// Builds an <see cref="EditRoomSeamProbeExecutor"/> the same way <see cref="CreateExecutor"/>
    /// builds the grade room, returning the chat-client mock so a test can wire a canned room into it.
    /// </summary>
    private static EditRoomSeamProbeExecutor CreateEditRoomProbeExecutor(
        IDecisionGate? decisionGate, out Mock<IAgentChatClientProvider> chatClients)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        var agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new EditRoomSeamProbeExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry, workspace.Object,
            executionContextAccessor, decisionGate);
    }

    /// <summary>The graphics room's counterpart of <see cref="CreateEditRoomProbeExecutor"/>.</summary>
    private static GraphicsRoomSeamProbeExecutor CreateGraphicsRoomProbeExecutor(
        IDecisionGate? decisionGate, out Mock<IAgentChatClientProvider> chatClients)
    {
        chatClients = new Mock<IAgentChatClientProvider>();
        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());
        var agentRegistry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IProjectFileWorkspace>();

        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        return new GraphicsRoomSeamProbeExecutor(
            chatClients.Object, toolProvider.Object, agentRegistry, workspace.Object,
            executionContextAccessor, decisionGate);
    }

    /// <summary>The graphics room's tool-restriction hook asks for this in every room run; the annotator itself soft-fails to a no-op by contract (its math is covered by <c>MotionGraphicsPlacementAnnotatorTests</c>).</summary>
    private static IMotionGraphicsPlacementAnnotator NoopPlacementAnnotator()
    {
        var annotator = new Mock<IMotionGraphicsPlacementAnnotator>();
        annotator
            .Setup(a => a.AnnotateAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return annotator.Object;
    }

    /// <summary>Trivial canned <see cref="IChatClient"/> — same shape as the other room suites'. Zero network calls.</summary>
    private sealed class FakeChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A canned <see cref="IChatClient"/> that also RECORDS every message list it is asked to
    /// complete. The room's opening message is the group chat's first message, so it never appears
    /// in the step result — this is how a test reads it back, from what a room participant's chat
    /// client was actually sent rather than from a re-derivation of it.
    /// </summary>
    private sealed class CapturingChatClient(string text) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _calls = [];

        /// <summary>Every message of every call, flattened in call order.</summary>
        public List<string> Messages() =>
            _calls.SelectMany(call => call).Select(message => message.Text ?? string.Empty).ToList();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
