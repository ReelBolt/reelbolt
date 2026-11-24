using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// STRUCTURAL invariants of the room convening gate — the three constraints this initiative treats
/// as non-negotiable, asserted from OUTSIDE the feature's own fixtures so a future change cannot
/// satisfy them by accident:
/// <list type="number">
/// <item>the gate defaults <see cref="ConveneGateMode.Off"/> everywhere, including in a room config
/// that does not exist yet;</item>
/// <item>nothing shipped turns it on — no <c>appsettings*.json</c>, no ported workflow template,
/// no config record;</item>
/// <item>a room with the gate <c>Off</c>, or with <c>Shadow</c>/<c>Gate</c> on but no usable
/// Decision answer, behaves byte-identically to the room that existed before the knob did.</item>
/// </list>
///
/// <para>
/// Deliberately independent of <c>RoomConveneGateTests</c> (the implementation's own suite) and of
/// every other room suite: this file builds its own fixtures, its own mocks and its own canned
/// <see cref="IChatClient"/>, and it reaches for reflection and the filesystem where the
/// implementation's tests reach for hand-written per-room assertions. That is the entire point —
/// the three hand-written config tests can never notice a FOURTH room config that arrives without
/// the <c>Off</c> default, and no unit fixture can notice a template or an appsettings key that
/// quietly enables the gate.
/// </para>
///
/// <para>
/// <b>Never</b> a real <see cref="DecisionGate"/>, a live provider, or a real
/// <see cref="IDecisionClient"/>. Every gate used here is a <see cref="Mock{T}"/> and the
/// "no Decision provider configured" case is the default of every fixture in this file — the normal
/// local case, and the only one this environment can exercise (no <c>Decision</c> provider row, zero
/// rows in <c>decision_observations</c>).
/// </para>
///
/// <para>
/// <b>Thresholds.</b> 0.85/0.25 appear here exactly once, in
/// <see cref="Every_IRoomStepConfig_implementation_defaults_the_convening_gate_off"/>, and only as
/// DEFAULTS. No test in this file asserts that any specific threshold is the <i>correct</i> one —
/// phase 1 has no live calibration data to tune them from, so a test that did would be pinning a
/// guess.
/// </para>
/// </summary>
public class RoomConveneGateInvariantTests
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid ProjectId = Guid.NewGuid();

    // =================================================================================
    // 1. The gate defaults Off — reflection over every IRoomStepConfig implementation.
    // =================================================================================

    /// <summary>
    /// Enumerates every non-abstract type implementing <see cref="IRoomStepConfig"/> and constructs
    /// each through its LONGEST all-defaulted constructor, so the assertions read the type's own
    /// declaration rather than anything this test knows about the three rooms that exist today.
    ///
    /// <para>
    /// This is the assertion no hand-written per-room test can make: add a fourth room config
    /// without <c>ConveneGate = ConveneGateMode.Off</c> and this test fails by name, while the three
    /// existing config suites stay green.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_IRoomStepConfig_implementation_defaults_the_convening_gate_off()
    {
        List<Type> implementations = [.. typeof(IRoomStepConfig).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(IRoomStepConfig).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];

        implementations.Should().HaveCountGreaterThanOrEqualTo(3,
            "the three shipped rooms must be discoverable by reflection, or this test is vacuous");
        implementations.Should().Contain(t => t.Name == "EditRoomStepConfig");
        implementations.Should().Contain(t => t.Name == "GraphicsRoomStepConfig");
        implementations.Should().Contain(t => t.Name == "ColorGradeRoomStepConfig");

        List<string> failures = [];

        foreach (Type type in implementations)
        {
            IRoomStepConfig config = ConstructWithDefaults(type, failures);
            if (config is null)
                continue;

            if (config.ConveneGate != ConveneGateMode.Off)
            {
                failures.Add(
                    $"{type.FullName}: ConveneGate defaults to {config.ConveneGate}, not ConveneGateMode.Off — " +
                    "a room must never convene-gate itself on merely by existing.");
            }

            // DEFAULTS, not tuned thresholds. See this class's remarks: phase 1 has no calibration
            // data, so nothing here claims 0.85/0.25 are the RIGHT numbers — only that a config
            // left alone arrives at exactly the documented defaults.
            if (config.ConveneAcceptAt != 0.85)
            {
                failures.Add(
                    $"{type.FullName}: ConveneAcceptAt defaults to {config.ConveneAcceptAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}, not the documented default 0.85.");
            }

            if (config.ConveneMinMargin != 0.25)
            {
                failures.Add(
                    $"{type.FullName}: ConveneMinMargin defaults to {config.ConveneMinMargin.ToString(System.Globalization.CultureInfo.InvariantCulture)}, not the documented default 0.25.");
            }

            if (config.ConveneProviderId is not null)
            {
                failures.Add(
                    $"{type.FullName}: ConveneProviderId defaults to {config.ConveneProviderId}, not null — " +
                    "a room must never ship pointing at a Decision provider.");
            }
        }

        failures.Should().BeEmpty(
            "every IRoomStepConfig must default its convening gate off; offending type(s):\n" +
            string.Join("\n", failures));
    }

    /// <summary>
    /// Constructs <paramref name="type"/> through the constructor with the MOST parameters, which
    /// must be entirely defaulted — the all-defaults shape every room config record ships. A
    /// non-defaulted parameter is itself a finding: it means the type cannot be built "left alone".
    /// </summary>
    private static IRoomStepConfig? ConstructWithDefaults(Type type, List<string> failures)
    {
        ConstructorInfo? ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
        {
            failures.Add($"{type.FullName}: has no public constructor at all.");
            return null;
        }

        ParameterInfo[] parameters = ctor.GetParameters();
        ParameterInfo[] required = [.. parameters.Where(p => !p.HasDefaultValue)];
        if (required.Length > 0)
        {
            failures.Add(
                $"{type.FullName}: its longest constructor ({parameters.Length} parameters) is not all-defaulted — " +
                $"'{required[0].Name}' has no default, so the type cannot be constructed at its defaults.");
            return null;
        }

        object?[] arguments = [.. parameters.Select(DefaultArgumentFor)];
        object? instance = ctor.Invoke(arguments);

        return instance as IRoomStepConfig;
    }

    private static object? DefaultArgumentFor(ParameterInfo parameter)
    {
        object? value = parameter.DefaultValue;

        if (parameter.ParameterType.IsEnum && value is not null && !parameter.ParameterType.IsInstanceOfType(value))
            return Enum.ToObject(parameter.ParameterType, value);

        return value;
    }

    // =================================================================================
    // 2. ConveneGateMode's declared shape.
    // =================================================================================

    /// <summary>
    /// The enum declares exactly three members, in order, and <see cref="ConveneGateMode.Off"/> is
    /// ordinal 0 — which is what makes "no value supplied" mean "off" for every serializer,
    /// every <c>default(ConveneGateMode)</c>, and every config record's field initializer alike.
    /// A fourth state, or a reordering that moved <c>Off</c> off zero, would silently change the
    /// meaning of every persisted config blob that omits the key.
    /// </summary>
    [Fact]
    public void ConveneGateMode_declares_exactly_Off_Shadow_Gate_with_Off_at_ordinal_zero()
    {
        Enum.GetNames<ConveneGateMode>().Should().Equal(
            [nameof(ConveneGateMode.Off), nameof(ConveneGateMode.Shadow), nameof(ConveneGateMode.Gate)],
            "the enum's members and their declaration order are a persisted JSON contract (JsonStringEnumConverter)");

        Enum.GetValues<ConveneGateMode>().Select(v => (int)v).Should().Equal([0, 1, 2]);

        ((int)ConveneGateMode.Off).Should().Be(0,
            "Off must be ordinal 0 so a default(ConveneGateMode) — a missing JSON key, an unset field — is Off");

        default(ConveneGateMode).Should().Be(ConveneGateMode.Off);
    }

    // =================================================================================
    // 3. Nothing shipped enables the gate.
    // =================================================================================

    /// <summary>
    /// Walks the repository's shipped configuration and room-config source and asserts that nothing
    /// assigns <c>conveneGate</c>/<c>ConveneGate</c> anything other than <c>Off</c>/<c>off</c>,
    /// unless the occurrence is inside a COMMENT (a commented-out example is exactly how this
    /// initiative ships documentation of the knob without enabling it).
    ///
    /// <para>
    /// A value is flagged when it names an enabling mode (<c>Shadow</c> or <c>Gate</c>, word-bounded
    /// so the type name <c>ConveneGateMode</c> is not confused with the <c>Gate</c> member) or when
    /// it is any other literal that is not <c>Off</c>. The one value deliberately NOT judged is an
    /// expression-bodied member that simply forwards to the backing property
    /// (<c>ConveneGateMode IRoomStepConfig.ConveneGate =&gt; ConveneGate;</c>) — that is the
    /// explicit-interface plumbing every room config carries, and it assigns no mode.
    /// </para>
    ///
    /// <para>
    /// This is the repo-wide half the unit fixtures can never cover: a key added to
    /// <c>appsettings.json</c>, or a ported template that turns the gate on, lives in a file no room
    /// test opens. The gate is a spend-affecting, output-affecting switch, and shipping it enabled
    /// anywhere would be discovered — expensively — in production.
    /// </para>
    ///
    /// <para>
    /// Scanned: every <c>appsettings*.json</c> in the repository, every <c>.cs</c> under
    /// <c>inference/src/ReelBolt.Shared/Workflows/</c> (where every room config record lives), and
    /// <c>WorkflowTemplateCatalog.cs</c> wherever it currently sits. The failure message names
    /// <c>file:line</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void No_shipped_configuration_enables_the_convening_gate()
    {
        if (!TryResolveRepositoryRoot(out string root))
        {
            // NEVER a silent pass. This project's test project has no dynamic-skip assertion
            // available, so an unlocatable repository root fails loudly with the reason instead.
            throw new InvalidOperationException(
                $"Could not locate the repository root (inference/ReelBolt.sln) above {AppContext.BaseDirectory}, " +
                "so the repo-wide 'nothing ships the gate enabled' scan cannot run. This is a source-tree " +
                "assumption failing, not a passing test.");
        }

        List<string> violations = [];
        List<string> scanned = [];

        foreach (string file in CollectScannedFiles(root))
        {
            scanned.Add(Path.GetRelativePath(root, file));
            ScanFileForAnEnabledGate(file, violations);
        }

        scanned.Should().Contain("inference/src/ReelBolt.WorkflowEngine/appsettings.json",
            "the scan must actually have opened the shipped service configuration");
        scanned.Should().Contain("inference/src/ReelBolt.Shared/Workflows/RoomStepConfig.cs",
            "the scan must actually have opened the room config source");
        scanned.Should().Contain("inference/src/ReelBolt.Shared/Workflows/WorkflowTemplateCatalog.cs",
            "the scan must actually have opened the workflow template catalog");
        scanned.Should().Contain("inference/src/ReelBolt.WorkflowEngine/appsettings.Development.json",
            "the scan must actually have opened the development override, not just the base appsettings");

        violations.Should().BeEmpty(
            "no shipped configuration may enable the room convening gate — every example must be commented out and Off:\n" +
            string.Join("\n", violations));
    }

    // =================================================================================
    // 4. Off (explicit) is the same run as the member left at its default.
    // =================================================================================

    /// <summary>
    /// For each room: a step whose config says <c>"conveneGate": "Off"</c> must produce the same
    /// JSON KEY SET and the same <c>room.terminationReason</c> as the same step whose config omits
    /// the key entirely. Asserted on the PARSED JSON (a key-set comparison and a property read), not
    /// on a substring, so a reordered or re-spaced envelope cannot pass by coincidence.
    /// </summary>
    [Theory]
    [InlineData(RoomKey.EditRoom)]
    [InlineData(RoomKey.GraphicsRoom)]
    [InlineData(RoomKey.ColorGradeRoom)]
    public async Task Explicit_off_is_the_same_run_as_leaving_the_member_at_its_default(string room)
    {
        StepExecutionResult explicitOff = await RunRoomAsync(
            room, WithGateMode(BuildConfigJson(room), "Off"), gate: null);

        StepExecutionResult defaulted = await RunRoomAsync(
            room, WithoutConveneGate(BuildConfigJson(room)), gate: null);

        explicitOff.Status.Should().Be(StepStatus.Completed, because: explicitOff.ErrorDetails ?? explicitOff.Output);
        defaulted.Status.Should().Be(StepStatus.Completed, because: defaulted.ErrorDetails ?? defaulted.Output);

        AssertSameEnvelopeShape(explicitOff, defaulted,
            "an explicit Off and an omitted conveneGate must be the same configuration");
    }

    // =================================================================================
    // 5. Shadow + a throwing gate + no Decision provider: still today's room.
    // =================================================================================

    /// <summary>
    /// For each room: <c>ConveneGate = Shadow</c>, an <see cref="IDecisionGate"/> whose
    /// <see cref="IDecisionGate.RunShadowAsync"/> THROWS, and no Decision provider registered
    /// anywhere. The room must complete <see cref="StepStatus.Completed"/> with an Output
    /// byte-identical to the same room at <c>Off</c>.
    ///
    /// <para>
    /// The throwing mock is the point: it pins the soft-failure contract from OUTSIDE. Shadow is a
    /// calibration side-channel, so no failure inside it — and no absent provider — may reach the
    /// room's status, decision or transcript. The grade room genuinely reaches the gate here (its
    /// shadow seam returns a question set); the edit and graphics rooms are an explicit no-op whose
    /// seams return <c>null</c>, so their gate is never touched at all — asserted, not assumed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(RoomKey.EditRoom)]
    [InlineData(RoomKey.GraphicsRoom)]
    [InlineData(RoomKey.ColorGradeRoom)]
    public async Task Shadow_with_a_throwing_gate_completes_byte_identically_to_off(string room)
    {
        StepExecutionResult baseline = await RunRoomAsync(
            room, WithGateMode(BuildConfigJson(room), "Off"), gate: null);
        baseline.Status.Should().Be(StepStatus.Completed, because: baseline.ErrorDetails ?? baseline.Output);

        var throwingGate = new Mock<IDecisionGate>(MockBehavior.Strict);
        throwingGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(),
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionGateQuestion>>(),
                It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("shadow hook exploded"));
        throwingGate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("shadow run must never reach the gate arm"));

        StepExecutionResult shadowed = await RunRoomAsync(
            room, WithGateMode(BuildConfigJson(room), "Shadow"), throwingGate.Object);

        shadowed.Status.Should().Be(StepStatus.Completed,
            "a throwing shadow hook is a soft failure by contract — it can never fail the room");
        shadowed.Output.Should().Be(baseline.Output,
            "Shadow is strictly additive: it may change no byte of the room's decision or its room meta");

        // The grade room is the one room with a real (non-null) shadow-question seam, so it is the
        // one that actually exercises the throwing path rather than short-circuiting before it.
        int expectedShadowCalls = room == RoomKey.ColorGradeRoom ? 1 : 0;
        throwingGate.Verify(
            g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(),
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionGateQuestion>>(),
                It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Exactly(expectedShadowCalls),
            expectedShadowCalls == 1
                ? "the grade room's shadow seam returns questions, so the throwing hook must really have been called"
                : "the edit/graphics rooms' shadow seam returns null by design — the gate must not be touched");

        throwingGate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Shadow never asks the gate whether to convene");
    }

    // =================================================================================
    // 6. Edit/Graphics at Gate: inert, and provably never consulted.
    // =================================================================================

    /// <summary>
    /// The edit and graphics rooms cannot decompose their decisions into a fixed option set
    /// (decisions.md §7), so their convene seams return <c>null</c> and <c>ConveneGate = Gate</c> is
    /// an explicit no-op rather than an error. With the gate ARM now live, this is load-bearing: the
    /// room stays inert PURELY because those seams return <c>null</c>, so a <see cref="MockBehavior.Strict"/>
    /// gate recording ZERO invocations is the assertion that proves it — any invocation at all would
    /// throw out of the strict mock and fail the run.
    /// </summary>
    [Theory]
    [InlineData(RoomKey.EditRoom)]
    [InlineData(RoomKey.GraphicsRoom)]
    public async Task Gate_on_a_room_without_a_question_set_is_a_no_op_that_never_calls_the_gate(string room)
    {
        StepExecutionResult baseline = await RunRoomAsync(
            room, WithGateMode(BuildConfigJson(room), "Off"), gate: null);
        baseline.Status.Should().Be(StepStatus.Completed, because: baseline.ErrorDetails ?? baseline.Output);

        var strictGate = new Mock<IDecisionGate>(MockBehavior.Strict);

        StepExecutionResult gated = await RunRoomAsync(
            room, WithGateMode(BuildConfigJson(room), "Gate"), strictGate.Object);

        gated.Status.Should().Be(StepStatus.Completed,
            "turning the gate on for a room that supplies no questions is a no-op, never an error");
        gated.Output.Should().Be(baseline.Output,
            "a room with no convene question set must behave byte-identically to the gate being Off");

        strictGate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the room's convene questions are null, so the gate must not be consulted at all");
        strictGate.VerifyNoOtherCalls();
    }

    // =================================================================================
    // 7. ColorGrade at Gate: any gate failure or no-decision still convenes. (Boundary: T-104
    //    owns the arm's own behavioural matrix; this is the same contract seen from outside.)
    // =================================================================================

    /// <summary>
    /// The grade room DOES supply a convene question set, so <c>Gate</c> reaches the live
    /// <see cref="IDecisionGate.DecideAsync"/> arm. Both contract edges from the outside:
    /// <list type="bullet">
    /// <item><b>throwing</b> — the gate fails, the room convenes exactly as if it were Off;</item>
    /// <item><b>NoDecision</b> — no provider resolved / the deadline fired, which is the normal
    /// local case, and again the room convenes with no visible difference.</item>
    /// </list>
    /// In both cases: <see cref="StepStatus.Completed"/> and an Output byte-identical to the same
    /// step at <c>Off</c> — the room acquires no failure mode and no agenda it did not earn.
    /// </summary>
    [Theory]
    [InlineData("throws")]
    [InlineData("no-decision")]
    public async Task ColorGrade_gate_failure_or_no_decision_convenes_byte_identically_to_off(string outcome)
    {
        StepExecutionResult baseline = await RunRoomAsync(
            RoomKey.ColorGradeRoom, WithGateMode(BuildConfigJson(RoomKey.ColorGradeRoom), "Off"), gate: null);
        baseline.Status.Should().Be(StepStatus.Completed, because: baseline.ErrorDetails ?? baseline.Output);

        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);
        if (outcome == "throws")
        {
            gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("decision provider unreachable"));
        }
        else
        {
            gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DecisionGateOutcome.NoDecision);
        }

        StepExecutionResult gated = await RunRoomAsync(
            RoomKey.ColorGradeRoom, WithGateMode(BuildConfigJson(RoomKey.ColorGradeRoom), "Gate"), gate.Object);

        gated.Status.Should().Be(StepStatus.Completed,
            "any gate failure and any no-decision outcome must convene, never fail the step");
        gated.Output.Should().Be(baseline.Output,
            "a gate that produced no usable answer must leave the room byte-identical to Off");

        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the grade room supplies questions, so Gate must actually have asked them");
    }

    // =================================================================================
    // Room fixtures — independent of the implementation's own suites.
    // =================================================================================

    private static class RoomKey
    {
        public const string EditRoom = "EditRoom";
        public const string GraphicsRoom = "GraphicsRoom";
        public const string ColorGradeRoom = "ColorGradeRoom";
    }

    private static AgentType DirectorType(string room) => room switch
    {
        RoomKey.EditRoom => AgentType.VideoEditDirector,
        RoomKey.GraphicsRoom => AgentType.MotionGraphicsDirector,
        RoomKey.ColorGradeRoom => AgentType.ColorGradeDirector,
        _ => throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key")
    };

    private static StepType StepTypeFor(string room) => room switch
    {
        RoomKey.EditRoom => StepType.EditRoom,
        RoomKey.GraphicsRoom => StepType.GraphicsRoom,
        RoomKey.ColorGradeRoom => StepType.ColorGradeRoom,
        _ => throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key")
    };

    /// <summary>
    /// One offered id per room, named in the canned room-participant turn so the room's
    /// offered-id-mention bookkeeping has something to find — the same trick every room suite uses,
    /// reproduced here rather than shared.
    /// </summary>
    private static string MentionedId(string room) => room switch
    {
        RoomKey.GraphicsRoom => "p1",
        _ => "s1"
    };

    private static string[] OfferedIds(string room) => room switch
    {
        RoomKey.GraphicsRoom => ["p0", "p1"],
        _ => ["s0", "s1"]
    };

    /// <summary>
    /// The smallest DETERMINISTIC room shape: one seat, one round, a two-turn ceiling and
    /// FixedTurns (which ignores the sentinel/convergence checks). Every run in this file uses it,
    /// so two runs of the same room are comparable byte for byte.
    /// </summary>
    private static string BuildConfigJson(string room)
    {
        string json = room switch
        {
            RoomKey.EditRoom => JsonSerializer.Serialize(
                new EditRoomStepConfig(
                    View: new ExtractInputRef(ExtractInputSource.Previous),
                    Seats: [new EditRoomSeat("Seat0", "first")],
                    Rounds: 1,
                    MaxTurns: 2,
                    Termination: EditRoomTerminationMode.FixedTurns,
                    RoomTimeoutSeconds: 20,
                    PersistTranscript: false),
                ConfigOptions),
            RoomKey.GraphicsRoom => JsonSerializer.Serialize(
                new GraphicsRoomStepConfig(
                    View: new ExtractInputRef(ExtractInputSource.Previous),
                    Seats: [new EditRoomSeat("Seat0", "first")],
                    Rounds: 1,
                    MaxTurns: 2,
                    Termination: EditRoomTerminationMode.FixedTurns,
                    RoomTimeoutSeconds: 20,
                    PersistTranscript: false),
                ConfigOptions),
            RoomKey.ColorGradeRoom => JsonSerializer.Serialize(
                new ColorGradeRoomStepConfig(
                    View: new ExtractInputRef(ExtractInputSource.Previous),
                    Seats: [new EditRoomSeat("Seat0", "first")],
                    Rounds: 1,
                    MaxTurns: 2,
                    Termination: EditRoomTerminationMode.FixedTurns,
                    RoomTimeoutSeconds: 20,
                    PersistTranscript: false),
                ConfigOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key")
        };

        return json;
    }

    private static string WithGateMode(string configJson, string mode)
    {
        JsonObject node = JsonNode.Parse(configJson)!.AsObject();
        node["conveneGate"] = mode;
        return node.ToJsonString(ConfigOptions);
    }

    private static string WithoutConveneGate(string configJson)
    {
        JsonObject node = JsonNode.Parse(configJson)!.AsObject();
        node.Remove("conveneGate");
        return node.ToJsonString(ConfigOptions);
    }

    /// <summary>
    /// The bounded <c>{view, meta}</c> envelope each room reads, in the id vocabulary that room
    /// actually deliberates over: <c>s{n}</c> shots for edit/grade, <c>p{n}</c> overlay placements
    /// for graphics.
    /// </summary>
    private static string BuildViewJson(string room)
    {
        string[] ids = OfferedIds(room);

        object view = room switch
        {
            RoomKey.GraphicsRoom => new
            {
                placements = ids.Select((id, i) => new
                {
                    id,
                    region = "LowerThird",
                    startSec = i * 2.0,
                    endSec = (i * 2.0) + 2.0,
                    fit = 80,
                    text = "Light"
                }),
                insertRegions = Array.Empty<object>()
            },
            RoomKey.ColorGradeRoom => new
            {
                shots = ids.Select((id, i) => new
                {
                    id,
                    startSec = i * 2.0,
                    endSec = (i * 2.0) + 2.0,
                    v = new { temp = "Cool", tone = "Balanced", sat = "Natural" }
                })
            },
            _ => new
            {
                shots = ids.Select((id, i) => new
                {
                    id,
                    startSec = i * 2.0,
                    endSec = (i * 2.0) + 2.0,
                    durationSec = 2.0
                }),
                silences = Array.Empty<object>(),
                segments = Array.Empty<object>()
            }
        };

        return JsonSerializer.Serialize(new { view, meta = new { offeredIdCount = ids.Length } });
    }

    /// <summary>
    /// The canned director synthesis output, one per room, in the schema that room actually emits.
    /// Kept a fixed string so two runs of the same room are byte-comparable.
    /// </summary>
    private static string BuildDecisionJson(string room) => room switch
    {
        RoomKey.EditRoom => JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s1", toId = "s1", reason = "room agreed" } },
            editRationale = "room synthesis",
            suggestedTitle = "Room Edit"
        }),
        RoomKey.GraphicsRoom => JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new
                {
                    placementId = "p1",
                    kind = "LowerThird",
                    text = "Hello",
                    subtext = "",
                    duration = "Short",
                    emphasis = "Normal",
                    renderedAssetStorageKey = "",
                    reason = "room agreed"
                }
            },
            planRationale = "room synthesis"
        }),
        RoomKey.ColorGradeRoom => JsonSerializer.Serialize(new
        {
            look = "Warm",
            strength = "Subtle",
            shadowTone = "Neutral",
            highlightTone = "Neutral",
            reason = "room synthesis",
            planRationale = "room synthesis"
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key")
    };

    private static StepExecutionContext CreateContext(string room, string configJson)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepTypeFor(room)
        };

        switch (room)
        {
            case RoomKey.EditRoom:
                step.EditRoomConfigJson = configJson;
                break;
            case RoomKey.GraphicsRoom:
                step.GraphicsRoomConfigJson = configJson;
                break;
            case RoomKey.ColorGradeRoom:
                step.ColorGradeRoomConfigJson = configJson;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key");
        }

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(1, "Analyze", BuildViewJson(room))],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "room-convene-gate-invariants",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>
    /// Runs one room end to end with a canned room-participant <see cref="IChatClient"/> and a
    /// canned director synthesis agent, and an optional <see cref="IDecisionGate"/> — <c>null</c>
    /// meaning "no Decision provider configured", this environment's only real state.
    ///
    /// <para>
    /// Every mock is built fresh per call so no two runs in this file share state: two invocations
    /// of the same room with the same config must be independently reproducible byte for byte.
    /// </para>
    /// </summary>
    private static async Task<StepExecutionResult> RunRoomAsync(string room, string configJson, IDecisionGate? gate)
    {
        var chatClients = new Mock<IAgentChatClientProvider>();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient($"{MentionedId(room)} is the one that matters here."));

        var toolProvider = new Mock<IAgentToolProvider>();
        toolProvider.Setup(t => t.GetTools(It.IsAny<AgentType>())).Returns(Array.Empty<AIFunction>());

        var agentRegistry = new Mock<IAgentRegistry>();
        var director = new Mock<IReelBoltAgent>();
        director
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = BuildDecisionJson(room), Success = true });
        agentRegistry.Setup(r => r.GetByType(DirectorType(room), null)).Returns(director.Object);

        var workspace = new Mock<IProjectFileWorkspace>();

        // The real (not mocked) accessor: a pure AsyncLocal wrapper, and using it means these runs
        // exercise BeginScope actually opening — the missing piece a live run once found.
        IWorkflowExecutionContextAccessor executionContextAccessor = new WorkflowExecutionContextAccessor();

        IStepExecutor executor = room switch
        {
            RoomKey.EditRoom => new EditRoomStepExecutor(
                chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
                executionContextAccessor, NullLogger<EditRoomStepExecutor>.Instance, null!, gate),
            RoomKey.GraphicsRoom => new GraphicsRoomStepExecutor(
                chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
                executionContextAccessor, CreateNoOpAnnotator(), NullLogger<GraphicsRoomStepExecutor>.Instance, gate),
            RoomKey.ColorGradeRoom => new ColorGradeRoomStepExecutor(
                chatClients.Object, toolProvider.Object, agentRegistry.Object, workspace.Object,
                executionContextAccessor, NullLogger<ColorGradeRoomStepExecutor>.Instance, gate),
            _ => throw new ArgumentOutOfRangeException(nameof(room), room, "unknown room key")
        };

        return await executor.ExecuteAsync(CreateContext(room, configJson));
    }

    private static IMotionGraphicsPlacementAnnotator CreateNoOpAnnotator()
    {
        // Soft-fails to a no-op by its own contract; a canned no-op keeps the graphics room's run
        // deterministic without reaching into its own annotator test fixture.
        var annotator = new Mock<IMotionGraphicsPlacementAnnotator>();
        annotator
            .Setup(a => a.AnnotateAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return annotator.Object;
    }

    // =================================================================================
    // Assertions over the emitted envelope.
    // =================================================================================

    /// <summary>
    /// Compares two results on the two things the acceptance names: the top-level JSON KEY SET and
    /// the <c>room.terminationReason</c> — both read from the parsed document.
    /// </summary>
    private static void AssertSameEnvelopeShape(StepExecutionResult actual, StepExecutionResult expected, string because)
    {
        using JsonDocument actualDoc = JsonDocument.Parse(actual.Output);
        using JsonDocument expectedDoc = JsonDocument.Parse(expected.Output);

        string[] actualKeys = [.. actualDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];
        string[] expectedKeys = [.. expectedDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];

        actualKeys.Should().Equal(expectedKeys, because);

        actualDoc.RootElement.GetProperty("room").GetProperty("terminationReason").GetString().Should()
            .Be(expectedDoc.RootElement.GetProperty("room").GetProperty("terminationReason").GetString(), because);
    }

    // =================================================================================
    // The repo-wide source scan.
    // =================================================================================

    /// <summary>
    /// Directory names never scanned: VCS metadata, sibling agent worktrees (other branches — not
    /// this tree's shipped source), dependency trees and build output.
    /// </summary>
    private static readonly string[] ExcludedSegments =
        [".git", ".claude", "node_modules", "bin", "obj", ".nuget-packages", ".nuget-http-cache"];

    /// <summary>
    /// Walks up from the test assembly's output directory to the repository root, identified by
    /// <c>inference/ReelBolt.sln</c> — the same marker-walk-up discipline an existing test in this
    /// project uses to reach real repository content, rather than an invented relative path.
    /// </summary>
    private static bool TryResolveRepositoryRoot(out string root)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "inference", "ReelBolt.sln")))
            {
                root = dir.FullName;
                return true;
            }

            dir = dir.Parent;
        }

        root = string.Empty;
        return false;
    }

    private static IEnumerable<string> CollectScannedFiles(string root)
    {
        SortedSet<string> files = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.AllDirectories))
        {
            if (!IsExcluded(root, path))
                files.Add(path);
        }

        string workflowsDir = Path.Combine(root, "inference", "src", "ReelBolt.Shared", "Workflows");
        if (Directory.Exists(workflowsDir))
        {
            foreach (string path in Directory.EnumerateFiles(workflowsDir, "*.cs", SearchOption.AllDirectories))
                files.Add(path);
        }

        // Located by name rather than by a hard-coded path, so moving the catalog does not silently
        // drop it out of the scan.
        foreach (string path in Directory.EnumerateFiles(root, "WorkflowTemplateCatalog.cs", SearchOption.AllDirectories))
        {
            if (!IsExcluded(root, path))
                files.Add(path);
        }

        return files;
    }

    private static bool IsExcluded(string root, string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ExcludedSegments.Contains(segment, StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>\bconveneGate\b</c>, case-insensitive. The word boundaries matter: they match the config
    /// MEMBER and the JSON KEY, while excluding the type name <c>ConveneGateMode</c> and every
    /// identifier that merely contains the phrase (<c>BuildConveneQuestions</c>,
    /// <c>EvaluateConveneGateAsync</c>, ...).
    /// </summary>
    private static readonly Regex GateMember = new(@"\bconveneGate\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// A mode name that ENABLES the gate. Word-bounded, so <c>ConveneGateMode</c> — where
    /// <c>Gate</c> sits between word characters — is not mistaken for the <c>Gate</c> member, while
    /// <c>ConveneGateMode.Gate</c>, <c>"Gate"</c> and <c>"gate"</c> all match.
    /// </summary>
    private static readonly Regex EnabledModeName = new(@"\b(?:Shadow|Gate)\b", RegexOptions.IgnoreCase);

    private static void ScanFileForAnEnabledGate(string file, List<string> violations)
    {
        string[] lines = File.ReadAllLines(file);
        string?[] codePortions = new string?[lines.Length];

        bool inBlockComment = false;
        for (int i = 0; i < lines.Length; i++)
        {
            codePortions[i] = ExtractCodePortion(lines[i], ref inBlockComment);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string? code = codePortions[i];
            if (code is null)
                continue;

            foreach (System.Text.RegularExpressions.Match match in GateMember.Matches(code))
            {
                int separator = code.IndexOfAny(['=', ':'], match.Index + match.Length);

                // No `=`/`:` after the member on this line means no value is being assigned here —
                // e.g. the interface declaration `ConveneGateMode ConveneGate { get; }`.
                if (separator < 0)
                    continue;

                string valueText = code[(separator + 1)..].Trim();

                // A value on the next line is legal JSON/C# formatting; the key is still on this
                // line, so the value must still be read and judged.
                for (int j = i + 1; valueText.Length == 0 && j < lines.Length; j++)
                {
                    string? next = codePortions[j];
                    if (next is null)
                        continue;

                    valueText = next.Trim();
                }

                string value = NormalizeGateValue(valueText);
                bool forwardsToTheProperty = value
                    .Split('.')
                    .Last()
                    .Equals("ConveneGate", StringComparison.OrdinalIgnoreCase);

                // Two ways to enable the gate, both caught textually:
                bool namesAnEnabledMode = EnabledModeName.IsMatch(valueText);
                bool isAnUnrecognisedLiteral =
                    !forwardsToTheProperty && !value.Equals("Off", StringComparison.OrdinalIgnoreCase);

                if (namesAnEnabledMode || isAnUnrecognisedLiteral)
                {
                    violations.Add(
                        $"{file}:{i + 1}: conveneGate is set to '{valueText.Trim()}' — only 'Off' may appear outside a comment");
                }
            }
        }
    }

    /// <summary>
    /// Returns the CODE part of one line, or <c>null</c> when the whole line is a comment. Covers
    /// line comments (<c>//</c>, also the JSONC spelling used by .NET configuration), the
    /// <c>/* ... */</c> block form spanning lines, and — importantly — does not mistake a
    /// <c>//</c> inside a string literal (a URL, say) for the start of a comment.
    /// </summary>
    private static string? ExtractCodePortion(string line, ref bool inBlockComment)
    {
        int start = 0;
        if (inBlockComment)
        {
            int close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
                return null;

            inBlockComment = false;
            start = close + 2;
        }

        bool inString = false;
        bool escaped = false;

        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    return line[start..i].Trim();

                if (line[i + 1] == '*')
                {
                    inBlockComment = true;
                    return line[start..i].Trim();
                }
            }
        }

        return line[start..].Trim();
    }

    /// <summary>
    /// Reduces a raw value token to the bare mode name: drops an expression-bodied <c>=&gt;</c>,
    /// trailing separators, the qualified <c>ConveneGateMode.</c> prefix and surrounding quotes, so
    /// <c>"Off"</c>, <c>'off'</c> and <c>ConveneGateMode.Off</c> all normalize to <c>Off</c>.
    /// </summary>
    private static string NormalizeGateValue(string raw)
    {
        // The separator itself was consumed by the caller, so an expression-bodied member arrives
        // here as "> ConveneGate" (the "=" of "=>" is the separator) and an assignment as
        // "ConveneGateMode.Off". Both leading forms are stripped.
        string value = raw.TrimStart('=', '>').Trim();

        value = value.TrimEnd(',', ';').Trim();

        const string prefix = "ConveneGateMode.";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
            value = value[prefix.Length..];

        return value.Trim('"', '\'').Trim();
    }

    /// <summary>
    /// Trivial canned <see cref="IChatClient"/> — one fixed assistant text on both paths (the group
    /// chat host drives participants through the streaming one). Zero network calls.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _text;

        public FakeChatClient(string text) => _text = text;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
