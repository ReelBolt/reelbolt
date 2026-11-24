#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.EditRoom;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="RoomDecisionMode"/> — the opt-in advisory decision questions a room can ask about its
/// own progress. The load-bearing claim under test is a NEGATIVE one: none of it changes the room.
/// <see cref="RoomDecisionMode.Off"/> (the default) and a room whose decision provider is missing,
/// throwing, or answering nonsense all produce the same participant sequence and the same
/// <see cref="RoomGroupChatManager.TerminationReason"/> as the pre-change manager, because the
/// schedule (<c>SelectNextAgentAsync</c>) and the terminator (<c>ShouldTerminateAsync</c>) are
/// untouched by anything recorded here.
///
/// <para>
/// The expected participant sequence pinned below is the one
/// <c>EditRoomGroupChatManagerTests.Round_robins_three_seats_for_two_rounds_then_always_the_director_until_the_ceiling</c>
/// asserts against on the unmodified fixture — three seats, two rounds, an 8-turn ceiling and
/// <see cref="EditRoomTerminationMode.FixedTurns"/> — so a drift in either place fails here too.
/// </para>
///
/// <para>
/// Like the existing room-manager suites, these tests drive the REAL in-process
/// <c>Microsoft.Agents.AI.Workflows</c> engine with trivial, instant, canned-response participants
/// (zero network calls) because <c>GroupChatManager.IterationCount</c> can only be advanced by the
/// framework's own orchestration.
/// </para>
/// </summary>
public class RoomDecisionModeTests
{
    private static readonly HashSet<string> OfferedIds = new(StringComparer.Ordinal) { "s1", "s2", "g1", "t1" };

    private static readonly string[] ExpectedParticipantSequence =
        ["Seat0", "Seat1", "Seat2", "Seat0", "Seat1", "Seat2", "Director", "Director"];

    private const int SeatCount = 3;
    private const int MaxTurns = 8;

    private static EditRoomStepConfig BuildConfig(
        RoomDecisionMode decisionScheduling,
        int rounds = 2,
        int maxTurns = MaxTurns,
        EditRoomTerminationMode termination = EditRoomTerminationMode.FixedTurns) =>
        new(
            Seats:
            [
                new EditRoomSeat("Seat0", "first"),
                new EditRoomSeat("Seat1", "second"),
                new EditRoomSeat("Seat2", "third")
            ],
            Rounds: rounds,
            MaxTurns: maxTurns,
            Termination: termination,
            DecisionScheduling: decisionScheduling);

    // EVERY participant is registered RoomGroupChatManager.ParticipantAliasCount times, seats first
    // and the director last — the exact shape RoomStepExecutorBase.RunRoomAsync builds. The aliases
    // are DISTINCT instances, which is what makes "the same registration twice in a row" observable
    // here at all: see BuildRecordingParticipants.
    private static IReadOnlyList<AIAgent> BuildParticipants(string directorText = "moderating")
    {
        List<AIAgent> participants = [];
        foreach ((string name, string text) in new[]
                 {
                     ("Seat0", "I think we should keep s1."),
                     ("Seat1", "Agreed, and also s2."),
                     ("Seat2", "Sounds good to me.")
                 })
        {
            for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
                participants.Add(new FakeAgent(name, text));
        }

        for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
            participants.Add(new FakeAgent("Director", directorText));

        return participants;
    }

    private static async Task<(IReadOnlyList<ChatMessage> Transcript, EditRoomGroupChatManager Manager)> RunRoomAsync(
        EditRoomStepConfig config,
        IReadOnlyList<AIAgent> participants,
        IDecisionClient? decisionClient = null,
        ILogger? logger = null)
    {
        EditRoomGroupChatManager? captured = null;

        Workflow workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
            {
                var manager = new EditRoomGroupChatManager(agents, config, OfferedIds, "Director", decisionClient, logger)
                {
                    MaximumIterationCount = config.ClampedMaxTurns
                };
                captured = manager;
                return manager;
            })
            .AddParticipants(participants)
            .Build();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow, "opening message", Guid.NewGuid().ToString("D"), cts.Token);

        // GroupChatHost only BUFFERS the opening message (AutoSendTurnToken=false) — a TurnToken
        // must be sent explicitly to kick off the first turn-selection cycle.
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage>? transcript = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
        {
            if (evt is WorkflowOutputEvent outputEvent && outputEvent.Is<List<ChatMessage>>())
            {
                transcript = outputEvent.As<List<ChatMessage>>();
                break;
            }
        }

        transcript.Should().NotBeNull("the workflow must produce a WorkflowOutputEvent<List<ChatMessage>>");
        return (transcript!, captured!);
    }

    private static List<string?> Speakers(IReadOnlyList<ChatMessage> transcript) =>
        transcript.Skip(1).Select(m => m.AuthorName).ToList();

    /// <summary>Room-agnostic variant of <see cref="RunRoomAsync"/> — same engine drive, any room's manager.</summary>
    private static async Task<(IReadOnlyList<ChatMessage> Transcript, RoomGroupChatManager Manager)> RunWithManagerFactoryAsync(
        IRoomStepConfig config,
        IReadOnlyList<AIAgent> participants,
        Func<IReadOnlyList<AIAgent>, IRoomStepConfig, IDecisionClient?, RoomGroupChatManager> managerFor,
        IDecisionClient? decisionClient)
    {
        RoomGroupChatManager? captured = null;

        Workflow workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
            {
                RoomGroupChatManager manager = managerFor(agents, config, decisionClient);
                manager.MaximumIterationCount = config.ClampedMaxTurns;
                captured = manager;
                return manager;
            })
            .AddParticipants(participants)
            .Build();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow, "opening message", Guid.NewGuid().ToString("D"), cts.Token);

        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage>? transcript = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
        {
            if (evt is WorkflowOutputEvent outputEvent && outputEvent.Is<List<ChatMessage>>())
            {
                transcript = outputEvent.As<List<ChatMessage>>();
                break;
            }
        }

        transcript.Should().NotBeNull("the workflow must produce a WorkflowOutputEvent<List<ChatMessage>>");
        return (transcript!, captured!);
    }

    // ---------------------------------------------------------------------
    // DI wiring — the two new dependencies must be INJECTED, not defaulted to null
    // ---------------------------------------------------------------------

    /// <summary>
    /// The two new dependencies were appended as OPTIONAL trailing constructor parameters (so every
    /// existing direct-construction test site keeps compiling), which means a container that failed
    /// to resolve them would silently fall back to <c>null</c> — and the feature would look wired
    /// while doing nothing. Assert against the REAL registrations (<c>AddWorkflowEngineServices</c>,
    /// what Program.cs calls) that both are actually present on all three room executors.
    /// </summary>
    [Fact]
    public void Every_room_executor_receives_both_decision_dependencies_from_the_real_registrations()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();

        foreach (Type executorType in new[] { typeof(EditRoomStepExecutor), typeof(GraphicsRoomStepExecutor), typeof(ColorGradeRoomStepExecutor) })
        {
            object executor = scope.ServiceProvider.GetServices<IStepExecutor>().Single(e => e.GetType() == executorType);

            FieldInfo resolver = FindField(executorType, "DecisionResolver");
            FieldInfo factory = FindField(executorType, "DecisionClientFactory");

            resolver.GetValue(executor).Should().NotBeNull($"{executorType.Name} must receive IInferenceProviderResolver");
            factory.GetValue(executor).Should().NotBeNull($"{executorType.Name} must receive IDecisionClientFactory");
        }
    }

    private static FieldInfo FindField(Type executorType, string name)
    {
        FieldInfo? field = executorType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"{executorType.Name} must declare an inherited protected field '{name}'");
        return field!;
    }

    // ---------------------------------------------------------------------
    // Config surface
    // ---------------------------------------------------------------------

    [Fact]
    public void DecisionScheduling_defaults_to_Off_on_the_shared_surface_and_on_all_three_room_configs()
    {
        new EditRoomStepConfig().DecisionScheduling.Should().Be(RoomDecisionMode.Off);
        new GraphicsRoomStepConfig().DecisionScheduling.Should().Be(RoomDecisionMode.Off);
        new ColorGradeRoomStepConfig().DecisionScheduling.Should().Be(RoomDecisionMode.Off);

        IRoomStepConfig[] configs = [new EditRoomStepConfig(), new GraphicsRoomStepConfig(), new ColorGradeRoomStepConfig()];
        configs.Should().OnlyContain(c => c.DecisionScheduling == RoomDecisionMode.Off);

        RoomDecisionMode[] declared = Enum.GetValues<RoomDecisionMode>();
        declared.Should().Equal(RoomDecisionMode.Off, RoomDecisionMode.Questions, RoomDecisionMode.Adaptive);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"rounds\":1,\"maxTurns\":4}")]
    public void A_config_json_without_the_new_key_still_deserializes_and_means_Off(string json)
    {
        // Same options the room executors deserialize their config with (RoomStepExecutorBase
        // .ConfigJsonOptions): web defaults + string enum values.
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        EditRoomStepConfig edit = JsonSerializer.Deserialize<EditRoomStepConfig>(json, options)!;
        GraphicsRoomStepConfig graphics = JsonSerializer.Deserialize<GraphicsRoomStepConfig>(json, options)!;
        ColorGradeRoomStepConfig grade = JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(json, options)!;

        edit.DecisionScheduling.Should().Be(RoomDecisionMode.Off);
        graphics.DecisionScheduling.Should().Be(RoomDecisionMode.Off);
        grade.DecisionScheduling.Should().Be(RoomDecisionMode.Off);
    }

    [Theory]
    [InlineData("{\"decisionScheduling\":\"Questions\"}", RoomDecisionMode.Questions)]
    [InlineData("{\"decisionScheduling\":\"Adaptive\"}", RoomDecisionMode.Adaptive)]
    [InlineData("{\"decisionScheduling\":\"Off\"}", RoomDecisionMode.Off)]
    public void The_new_key_round_trips_as_a_string_enum(string json, RoomDecisionMode expected)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        JsonSerializer.Deserialize<EditRoomStepConfig>(json, options)!.DecisionScheduling.Should().Be(expected);
        JsonSerializer.Deserialize<GraphicsRoomStepConfig>(json, options)!.DecisionScheduling.Should().Be(expected);
        JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(json, options)!.DecisionScheduling.Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // Off / no-provider parity with the untouched schedule and terminator
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Off_asks_nothing_even_when_a_client_is_supplied_and_leaves_the_room_untouched()
    {
        // A client IS supplied here on purpose: the mode gate alone must be what suppresses every
        // call, not the absence of one.
        var client = new CapturingDecisionClient(_ => throw new InvalidOperationException("Off must never call DecideAsync"));

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Off), BuildParticipants(), client);

        client.Calls.Should().BeEmpty();
        manager.DecisionRecords.Should().BeEmpty();
        manager.LastNextSpeaker.Should().BeNull();
        manager.TerminationReason.Should().BeNull("FixedTurns never sets a termination reason — only the ceiling ends the room");
        Speakers(transcript).Should().Equal(ExpectedParticipantSequence);
    }

    [Fact]
    public async Task Questions_without_a_decision_provider_behaves_exactly_like_Off()
    {
        (IReadOnlyList<ChatMessage> offTranscript, EditRoomGroupChatManager offManager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Off), BuildParticipants());

        (IReadOnlyList<ChatMessage> questionsTranscript, EditRoomGroupChatManager questionsManager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Questions), BuildParticipants(), decisionClient: null);

        Speakers(questionsTranscript).Should().Equal(Speakers(offTranscript));
        Speakers(questionsTranscript).Should().Equal(ExpectedParticipantSequence);

        questionsManager.TerminationReason.Should().Be(offManager.TerminationReason);
        questionsManager.TerminationReason.Should().BeNull();

        questionsManager.DecisionRecords.Should().BeEmpty();
        questionsManager.LastNextSpeaker.Should().BeNull();
    }

    /// <summary>
    /// The same parity claim as <see cref="Questions_without_a_decision_provider_behaves_exactly_like_Off"/>,
    /// for the other two rooms. Both rooms drive the identical base-class code path (the mode gate
    /// plus the null-client gate), and the three existing manager suites exercise <c>Off</c> against
    /// their own fixtures unmodified — this adds the "Questions + no provider" half on each room's
    /// own seat layout.
    /// </summary>
    [Theory]
    [InlineData("graphics")]
    [InlineData("colorGrade")]
    public async Task Questions_without_a_provider_is_identical_to_Off_for_the_other_two_rooms(string room)
    {
        static (IRoomStepConfig Config, Func<IReadOnlyList<AIAgent>, IRoomStepConfig, IDecisionClient?, RoomGroupChatManager> ManagerFor) For(
            string room, RoomDecisionMode mode)
        {
            if (room == "graphics")
            {
                var config = new GraphicsRoomStepConfig(Rounds: 1, MaxTurns: 6, Termination: EditRoomTerminationMode.FixedTurns, DecisionScheduling: mode);
                return (config, (agents, c, client) => new GraphicsRoomGroupChatManager(agents, (GraphicsRoomStepConfig)c, OfferedIds, "Director", client));
            }
            else
            {
                var config = new ColorGradeRoomStepConfig(Rounds: 1, MaxTurns: 6, Termination: EditRoomTerminationMode.FixedTurns, DecisionScheduling: mode);
                return (config, (agents, c, client) => new ColorGradeRoomGroupChatManager(agents, (ColorGradeRoomStepConfig)c, OfferedIds, "Director", client));
            }
        }

        async Task<(IReadOnlyList<ChatMessage> Transcript, RoomGroupChatManager Manager)> RunAsync(RoomDecisionMode mode, IDecisionClient? client)
        {
            (IRoomStepConfig config, Func<IReadOnlyList<AIAgent>, IRoomStepConfig, IDecisionClient?, RoomGroupChatManager> managerFor) = For(room, mode);

            List<AIAgent> participants = [];
            foreach (EditRoomSeat seat in config.EffectiveSeats)
            {
                for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
                    participants.Add(new FakeAgent(seat.Name, $"arguing as {seat.Name}."));
            }

            for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
                participants.Add(new FakeAgent("Director", "moderating"));

            return await RunWithManagerFactoryAsync(config, participants, managerFor, client);
        }

        (IReadOnlyList<ChatMessage> offTranscript, RoomGroupChatManager offManager) = await RunAsync(RoomDecisionMode.Off, null);
        (IReadOnlyList<ChatMessage> questionsTranscript, RoomGroupChatManager questionsManager) =
            await RunAsync(RoomDecisionMode.Questions, null);

        questionsTranscript.Count.Should().BeGreaterThan(1, "the room must actually have run");
        Speakers(offTranscript).Should().HaveCount(6, "1 round of seats (4 in the graphics room, 3 in the grade room) plus the ceiling's remaining director turns");
        Speakers(offTranscript).Should().Contain("Director", "the director must have spoken, or the question path had nothing to trigger on");
        Speakers(questionsTranscript).Should().Equal(Speakers(offTranscript));
        questionsManager.TerminationReason.Should().Be(offManager.TerminationReason);
        questionsManager.DecisionRecords.Should().BeEmpty();
        questionsManager.LastNextSpeaker.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Questions mode: what is asked, and what is recorded
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Questions_asks_both_questions_in_one_call_after_each_director_turn_and_records_both()
    {
        var client = new CapturingDecisionClient(_ => new DecisionResult(
            "stub",
            new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.91, null,
                        new Dictionary<string, double> { ["yes"] = 0.91, ["no"] = 0.09 }),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat2", null, null, 0.77,
                        new Dictionary<string, double> { ["Seat2"] = 0.77 })
            },
            10,
            5));

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Questions), BuildParticipants(), client);

        List<string?> speakers = Speakers(transcript);
        speakers.Should().Equal(ExpectedParticipantSequence, "the answers are advisory — the schedule is unchanged");

        int directorTurns = speakers.Count(s => string.Equals(s, "Director", StringComparison.Ordinal));
        directorTurns.Should().Be(2);

        // Exactly ONE call per director turn, never per seat turn (the 6 seat turns produce none).
        // One boundary is imposed by the FRAMEWORK, not by this feature: GroupChatHost.TakeTurnAsync
        // checks ShouldTerminateAsync BEFORE calling UpdateHistoryAsync, so the last turn of a room
        // that ends at the ceiling is never surfaced to the hook and is never asked about. The
        // buffer of observed turns is the ground truth for what WAS surfaced.
        int observedDirectorTurns = manager.ObservedTurns.Count(t => string.Equals(t.AuthorName, "Director", StringComparison.Ordinal));
        observedDirectorTurns.Should().Be(directorTurns - 1, "the ceiling-terminating turn is never handed to UpdateHistoryAsync");
        client.Calls.Should().HaveCount(observedDirectorTurns);
        client.Calls.Should().NotBeEmpty();

        foreach (var call in client.Calls)
        {
            call.Questions.Should().HaveCount(2, "one call carries both questions");
            call.Questions.Select(q => q.Name).Should().Equal(
                RoomGroupChatManager.ConvergedQuestionName, RoomGroupChatManager.NextSpeakerQuestionName);

            DecisionQuestion converged = call.Questions[0];
            converged.Kind.Should().Be(DecisionKind.Noul);
            converged.Instructions.Should().NotBeNullOrWhiteSpace();
            converged.Options.Should().BeEmpty("a Noul question carries no options");

            DecisionQuestion nextSpeaker = call.Questions[1];
            nextSpeaker.Kind.Should().Be(DecisionKind.Choice);
            nextSpeaker.Instructions.Should().NotBeNullOrWhiteSpace();
            nextSpeaker.Options.Should().BeEquivalentTo(["Seat0", "Seat1", "Seat2"],
                "the option set is exactly the seat names this room registered");
            nextSpeaker.Options.Should().NotContain("Director", "the director moderates and is never a candidate");

            // The state is a bounded rendering of the room so far, ending in the director turn that
            // triggered the question — never the unbounded transcript.
            call.State.Content.Should().NotBeNullOrWhiteSpace();
            call.State.Content.Length.Should().BeLessThanOrEqualTo(4000);
            call.State.Content.Should().Contain("Seat0: I think we should keep s1.",
                "the state covers the room's turns, not just the one that triggered the question");
            call.State.Content.Should().EndWith("Director: moderating");
        }

        manager.DecisionRecords.Should().HaveCount(2 * observedDirectorTurns);
        List<string> expectedQuestionOrder = [];
        for (int i = 0; i < observedDirectorTurns; i++)
            expectedQuestionOrder.AddRange([RoomGroupChatManager.ConvergedQuestionName, RoomGroupChatManager.NextSpeakerQuestionName]);

        manager.DecisionRecords.Select(r => r.Question).Should().Equal(expectedQuestionOrder);

        manager.DecisionRecords.Where(r => r.Question == RoomGroupChatManager.ConvergedQuestionName)
            .Should().OnlyContain(r => r.Kind == DecisionKind.Noul && r.Noul == 0.91 && r.Choice == null);
        manager.DecisionRecords.Where(r => r.Question == RoomGroupChatManager.NextSpeakerQuestionName)
            .Should().OnlyContain(r => r.Kind == DecisionKind.Choice && r.Choice == "Seat2" && r.Noul == null);

        // TurnIndex points at the director turn each answer is about, in ascending order, with no
        // repeated turn across the two questions of one call.
        manager.DecisionRecords.Select(r => r.TurnIndex).Distinct().Should().HaveCount(observedDirectorTurns);

        manager.LastNextSpeaker.Should().Be("Seat2", "the answer is surfaced for a future adaptive scheduler");
    }

    [Fact]
    public async Task Adaptive_is_declared_but_behaves_identically_to_Questions()
    {
        var client = new CapturingDecisionClient(_ => new DecisionResult(
            "stub",
            new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.99, null,
                        new Dictionary<string, double>()),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat1", null, null, null,
                        new Dictionary<string, double>())
            },
            1,
            1));

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Adaptive), BuildParticipants(), client);

        Speakers(transcript).Should().Equal(ExpectedParticipantSequence,
            "Adaptive is wave 2: today it must not act on next_speaker (which says Seat1 every time)");
        manager.LastNextSpeaker.Should().Be("Seat1");
        client.Calls.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------------
    // Soft-failure: nothing a decision model does may fail or shorten a room
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_throwing_decision_client_leaves_the_room_byte_identical_and_warns()
    {
        var client = new CapturingDecisionClient(_ => throw new TimeoutException("provider is down"));
        var logger = new CollectingLogger();

        (IReadOnlyList<ChatMessage> offTranscript, EditRoomGroupChatManager offManager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Off), BuildParticipants());

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Questions), BuildParticipants(), client, logger);

        client.Calls.Should().NotBeEmpty("the question round was attempted");
        Speakers(transcript).Should().Equal(Speakers(offTranscript));
        Speakers(transcript).Should().Equal(ExpectedParticipantSequence);
        manager.TerminationReason.Should().Be(offManager.TerminationReason);

        manager.DecisionRecords.Should().BeEmpty();
        manager.LastNextSpeaker.Should().BeNull();
        logger.Warnings.Should().NotBeEmpty("the degradation is logged, not silent");
    }

    [Theory]
    // Missing the converged answer entirely.
    [InlineData("empty")]
    // Wrong kind for the asked question.
    [InlineData("wrong-kind")]
    // next_speaker answered with something this room never offered.
    [InlineData("invented-option")]
    // next_speaker answered with the director's own name.
    [InlineData("director-option")]
    public async Task A_malformed_answer_records_nothing(string shape)
    {
        var client = new CapturingDecisionClient(_ => shape switch
        {
            "empty" => new DecisionResult("stub", new Dictionary<string, DecisionAnswer>(), 1, 1),
            "wrong-kind" => new DecisionResult("stub", new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Choice, "yes", null, null, null,
                        new Dictionary<string, double>()),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat1", null, null, null,
                        new Dictionary<string, double>())
            }, 1, 1),
            "invented-option" => new DecisionResult("stub", new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.9, null,
                        new Dictionary<string, double>()),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat9", null, null, null,
                        new Dictionary<string, double>())
            }, 1, 1),
            _ => new DecisionResult("stub", new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.9, null,
                        new Dictionary<string, double>()),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Director", null, null, null,
                        new Dictionary<string, double>())
            }, 1, 1)
        });

        var logger = new CollectingLogger();

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunRoomAsync(BuildConfig(RoomDecisionMode.Questions), BuildParticipants(), client, logger);

        Speakers(transcript).Should().Equal(ExpectedParticipantSequence);
        manager.DecisionRecords.Should().BeEmpty();
        manager.LastNextSpeaker.Should().BeNull();
        logger.Warnings.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------------
    // The alias-pair invariant (plan.md §7 item 4)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>Microsoft.Agents.AI.Workflows</c> 1.22.0's <c>GroupChatHost.TakeTurnAsync</c> silently ends
    /// a room when <c>SelectNextAgentAsync</c> returns the same registered participant twice in a
    /// row — see <see cref="RoomGroupChatManager"/>'s constructor remarks. The scheduler upholds the
    /// invariant arithmetically (<c>2·logical(i) + (i mod 2)</c>), and this test asserts it on the
    /// REAL engine: every alias is a distinct instance, so if two consecutive selections had returned
    /// the same registration, the second turn would have been skipped, truncating the run below the
    /// full turn count AND leaving two ReferenceEquals neighbours in the invocation log.
    /// <para>
    /// The <c>Questions</c> arm uses a stub whose <c>next_speaker</c> answer is the SAME seat every
    /// time (Seat1, never the schedule's own choice) precisely because reordering the schedule for
    /// <c>next_speaker</c> is how the invariant would get broken — and it does not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(RoomDecisionMode.Off)]
    [InlineData(RoomDecisionMode.Questions)]
    [InlineData(RoomDecisionMode.Adaptive)]
    public async Task No_two_consecutive_selections_return_the_same_registration_instance(RoomDecisionMode mode)
    {
        // 6 seat turns then 4 director turns: the director phase repeats ONE participant for four
        // consecutive turns, which is exactly the shape that ends a room early without the alias
        // alternation.
        EditRoomStepConfig config = BuildConfig(mode, rounds: 2, maxTurns: 10);
        string[] expectedSequence =
            ["Seat0", "Seat1", "Seat2", "Seat0", "Seat1", "Seat2", "Director", "Director", "Director", "Director"];

        List<AIAgent> participants = [];
        List<AIAgent> invocations = [];
        foreach (string name in new[]
                 {
                     "Seat0", "Seat0", "Seat1", "Seat1", "Seat2", "Seat2", "Director", "Director"
                 })
        {
            participants.Add(new RecordingAgent(name, "fine.", invocations));
        }

        IDecisionClient client = mode == RoomDecisionMode.Off
            ? new CapturingDecisionClient(_ => throw new InvalidOperationException("Off must never call DecideAsync"))
            : new CapturingDecisionClient(_ => new DecisionResult(
                "stub",
                new Dictionary<string, DecisionAnswer>
                {
                    [RoomGroupChatManager.ConvergedQuestionName] =
                        new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.5, null,
                            new Dictionary<string, double>()),
                    [RoomGroupChatManager.NextSpeakerQuestionName] =
                        new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat1", null, null, null,
                            new Dictionary<string, double>())
                },
                1,
                1));

        (IReadOnlyList<ChatMessage> transcript, _) = await RunRoomAsync(config, participants, client);

        invocations.Should().HaveCount(expectedSequence.Length,
            "a repeated registration would have ended the room early and shortened the run");
        invocations.Select(a => a.Name).Should().Equal(expectedSequence);

        for (int i = 1; i < invocations.Count; i++)
        {
            ReferenceEquals(invocations[i], invocations[i - 1]).Should().BeFalse(
                $"turn {i} ({invocations[i].Name}) must not reuse the registration of turn {i - 1}");
        }

        Speakers(transcript).Should().Equal(expectedSequence);
    }

    // ---------------------------------------------------------------------
    // Termination parity for the reasons that ACTUALLY end a room early
    // ---------------------------------------------------------------------

    /// <summary>
    /// The parity claim of <see cref="Questions_without_a_decision_provider_behaves_exactly_like_Off"/>
    /// only means something for the termination reasons a room can really produce. The tests above all
    /// use <see cref="EditRoomTerminationMode.FixedTurns"/>, where <c>TerminationReason</c> is
    /// <c>null</c> by construction and the room always runs to the ceiling — so they cannot observe a
    /// changed sentinel or convergence decision at all. The decision questions run INSIDE
    /// <c>UpdateHistoryAsync</c>, which 1.22.0 calls immediately before the <c>ShouldTerminateAsync</c>
    /// that reads them, so early termination is exactly the behaviour a slow, throwing or
    /// nonsense-answering client could perturb. This pins both real reasons, with and without a
    /// client.
    /// </summary>
    [Theory]
    [InlineData("no-client")]
    [InlineData("answering-client")]
    [InlineData("throwing-client")]
    public async Task Sentinel_termination_is_identical_with_and_without_decision_questions(string clientKind)
    {
        (IReadOnlyList<ChatMessage> offTranscript, EditRoomGroupChatManager offManager) = await RunSentinelRoomAsync(
            RoomDecisionMode.Off, client: null);

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) = await RunSentinelRoomAsync(
            RoomDecisionMode.Questions, ClientFor(clientKind));

        offManager.TerminationReason.Should().Be("sentinel", "the fixture must actually exercise the early stop");
        manager.TerminationReason.Should().Be(offManager.TerminationReason);

        // Identical SEQUENCE and identical LENGTH: the room stops on the same turn, not merely with
        // the same label. A decision round that added a turn would show up here.
        Speakers(transcript).Should().Equal(Speakers(offTranscript));
        transcript.Should().HaveCount(offTranscript.Count);
        transcript.Should().HaveCountLessThan(MaxTurns + 1, "the sentinel ends the room well before the ceiling");
    }

    [Theory]
    [InlineData("no-client")]
    [InlineData("answering-client")]
    [InlineData("throwing-client")]
    public async Task Converged_termination_is_identical_with_and_without_decision_questions(string clientKind)
    {
        (IReadOnlyList<ChatMessage> offTranscript, EditRoomGroupChatManager offManager) =
            await RunConvergedRoomAsync(RoomDecisionMode.Off, client: null);

        (IReadOnlyList<ChatMessage> transcript, EditRoomGroupChatManager manager) =
            await RunConvergedRoomAsync(RoomDecisionMode.Questions, ClientFor(clientKind));

        offManager.TerminationReason.Should().Be("converged", "the fixture must actually converge before the ceiling");
        manager.TerminationReason.Should().Be(offManager.TerminationReason);
        Speakers(transcript).Should().Equal(Speakers(offTranscript));
        transcript.Should().HaveCount(offTranscript.Count);
    }

    // ---------------------------------------------------------------------
    // The alias-pair invariant (plan.md §7 item 4)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <see cref="No_two_consecutive_selections_return_the_same_registration_instance"/> covers the
    /// edit room's three-seat layout. The scheduler is shared by all three rooms, but each room
    /// registers a DIFFERENT number of seats (the graphics room has four, the grade room three), and
    /// seat count is exactly what the <c>2·logical(i) + (i mod 2)</c> stride arithmetic has to keep
    /// working — the director phase in particular repeats one logical participant for several turns in
    /// a row. Asserted here rather than assumed, from the test side.
    /// </summary>
    [Theory]
    [InlineData("graphics", RoomDecisionMode.Off)]
    [InlineData("graphics", RoomDecisionMode.Questions)]
    [InlineData("colorGrade", RoomDecisionMode.Off)]
    [InlineData("colorGrade", RoomDecisionMode.Questions)]
    public async Task No_two_consecutive_selections_reuse_a_registration_in_the_other_two_rooms(
        string room, RoomDecisionMode mode)
    {
        IRoomStepConfig config = room == "graphics"
            ? new GraphicsRoomStepConfig(Rounds: 1, MaxTurns: 8, Termination: EditRoomTerminationMode.FixedTurns, DecisionScheduling: mode)
            : new ColorGradeRoomStepConfig(Rounds: 1, MaxTurns: 8, Termination: EditRoomTerminationMode.FixedTurns, DecisionScheduling: mode);

        List<AIAgent> participants = [];
        List<AIAgent> invocations = [];
        foreach (EditRoomSeat seat in config.EffectiveSeats)
        {
            for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
                participants.Add(new RecordingAgent(seat.Name, $"arguing as {seat.Name}.", invocations));
        }

        for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
            participants.Add(new RecordingAgent("Director", "moderating", invocations));

        IDecisionClient client = mode == RoomDecisionMode.Off
            ? new CapturingDecisionClient(_ => throw new InvalidOperationException("Off must never call DecideAsync"))
            : ClientFor("answering-client")!;

        Func<IReadOnlyList<AIAgent>, IRoomStepConfig, IDecisionClient?, RoomGroupChatManager> managerFor = room == "graphics"
            ? (agents, c, dc) => new GraphicsRoomGroupChatManager(agents, (GraphicsRoomStepConfig)c, OfferedIds, "Director", dc)
            : (agents, c, dc) => new ColorGradeRoomGroupChatManager(agents, (ColorGradeRoomStepConfig)c, OfferedIds, "Director", dc);

        (IReadOnlyList<ChatMessage> transcript, _) =
            await RunWithManagerFactoryAsync(config, participants, managerFor, client);

        invocations.Should().HaveCount(config.ClampedMaxTurns,
            "a repeated registration would have ended the room early and shortened the run");
        for (int i = 1; i < invocations.Count; i++)
        {
            ReferenceEquals(invocations[i], invocations[i - 1]).Should().BeFalse(
                $"turn {i} ({invocations[i].Name}) must not reuse the registration of turn {i - 1}");
        }

        Speakers(transcript).Should().Equal(invocations.Select(a => a.Name));
    }

    // ---------------------------------------------------------------------
    // Termination fixtures shared by the parity tests above
    // ---------------------------------------------------------------------

    /// <summary>
    /// The sentinel fixture from <c>EditRoomGroupChatManagerTests.Sentinel_in_the_directors_turn_ends_the_room_early</c>:
    /// one round of seats, then a director turn carrying <c>ROOM_DECIDED</c>.
    /// </summary>
    private static Task<(IReadOnlyList<ChatMessage> Transcript, EditRoomGroupChatManager Manager)> RunSentinelRoomAsync(
        RoomDecisionMode mode, IDecisionClient? client) =>
        RunRoomAsync(
            BuildConfig(mode, rounds: 1, maxTurns: MaxTurns, termination: EditRoomTerminationMode.SentinelOnly),
            BuildParticipants("Great work, ROOM_DECIDED — keep s1 and s2."),
            client);

    /// <summary>
    /// The convergence fixture from
    /// <c>EditRoomGroupChatManagerTests.Identical_offered_id_mentions_across_consecutive_rounds_converge_before_the_ceiling</c>:
    /// every seat repeats the same offered ids, so convergence fires early.
    /// </summary>
    private static Task<(IReadOnlyList<ChatMessage> Transcript, EditRoomGroupChatManager Manager)> RunConvergedRoomAsync(
        RoomDecisionMode mode, IDecisionClient? client)
    {
        EditRoomStepConfig config = new(
            Seats:
            [
                new EditRoomSeat("Seat0", "first"),
                new EditRoomSeat("Seat1", "second"),
                new EditRoomSeat("Seat2", "third")
            ],
            Rounds: 4,
            MaxTurns: 20,
            Termination: EditRoomTerminationMode.Converged,
            MinConvergenceRounds: 2,
            DecisionScheduling: mode);

        List<AIAgent> participants = [];
        foreach ((string name, string text) in new[]
                 {
                     ("Seat0", "Keep s1 through the intro."),
                     ("Seat1", "And s2 for the close."),
                     ("Seat2", "Agreed on s1 and s2.")
                 })
        {
            for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
                participants.Add(new FakeAgent(name, text));
        }

        for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
            participants.Add(new FakeAgent("Director", "Still discussing."));

        return RunRoomAsync(config, participants, client);
    }

    /// <summary>
    /// The three client shapes a parity claim has to survive: no client at all (the install's real
    /// state), a well-formed answer, and a provider that is down.
    /// </summary>
    private static IDecisionClient? ClientFor(string kind) => kind switch
    {
        "no-client" => null,
        "answering-client" => new CapturingDecisionClient(_ => new DecisionResult(
            "stub",
            new Dictionary<string, DecisionAnswer>
            {
                [RoomGroupChatManager.ConvergedQuestionName] =
                    new(RoomGroupChatManager.ConvergedQuestionName, DecisionKind.Noul, null, null, 0.97, null,
                        new Dictionary<string, double> { ["yes"] = 0.97, ["no"] = 0.03 }),
                [RoomGroupChatManager.NextSpeakerQuestionName] =
                    new(RoomGroupChatManager.NextSpeakerQuestionName, DecisionKind.Choice, "Seat1", null, null, 0.8,
                        new Dictionary<string, double> { ["Seat1"] = 0.8 })
            },
            1,
            1)),
        _ => new CapturingDecisionClient(_ => throw new TimeoutException("provider is down"))
    };

    // ---------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------

    /// <summary>One captured <see cref="IDecisionClient.DecideAsync"/> invocation.</summary>
    private sealed record CapturedCall(
        DecisionState State,
        IReadOnlyList<DecisionQuestion> Questions);

    /// <summary>
    /// Records every call (state + questions) and answers through the supplied factory — which may
    /// throw, i.e. this doubles as the "provider is down" stub.
    /// </summary>
    private sealed class CapturingDecisionClient : IDecisionClient
    {
        private readonly Func<DecisionState, DecisionResult> _respond;

        public CapturingDecisionClient(Func<DecisionState, DecisionResult> respond) => _respond = respond;

        public List<CapturedCall> Calls { get; } = [];

        public Task<DecisionResult> DecideAsync(DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
        {
            Calls.Add(new CapturedCall(state, questions));
            return Task.FromResult(_respond(state));
        }
    }

    /// <summary>Minimal <see cref="ILogger"/> that keeps the warning messages this path emits.</summary>
    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Minimal concrete <see cref="AgentSession"/> — only needs to satisfy <see cref="AIAgent"/>'s abstract methods.</summary>
    private sealed class FakeSession : AgentSession
    {
    }

    /// <summary>
    /// Trivial concrete <see cref="AIAgent"/> returning fixed, instant text (zero network calls) and
    /// appending ITSELF to the shared list on every turn it is asked to take — which makes the
    /// registration identity the scheduler picked observable from a test.
    /// </summary>
    private sealed class RecordingAgent : AIAgent
    {
        private readonly string _name;
        private readonly string _text;
        private readonly List<AIAgent> _invocations;

        public RecordingAgent(string name, string text, List<AIAgent> invocations)
        {
            _name = name;
            _text = text;
            _invocations = invocations;
        }

        public override string Name => _name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonElement());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        {
            _invocations.Add(this);
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _text) { AuthorName = _name }));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _invocations.Add(this);
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, _text) { AuthorName = _name };
        }
    }

    /// <summary>Trivial concrete <see cref="AIAgent"/> that returns fixed, instant text — zero network/model calls.</summary>
    private sealed class FakeAgent : AIAgent
    {
        private readonly string _name;
        private readonly string _text;

        public FakeAgent(string name, string text)
        {
            _name = name;
            _text = text;
        }

        public override string Name => _name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JsonElement());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _text) { AuthorName = _name }));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, _text) { AuthorName = _name };
        }
    }
}
