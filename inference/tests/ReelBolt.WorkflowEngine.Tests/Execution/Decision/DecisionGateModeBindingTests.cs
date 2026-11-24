#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Coverage for the <c>Agents:&lt;Agent&gt;:DecisionGate:Mode</c> config-key → enum binding, plus the
/// per-site threshold ladder and the shipped-file guard.
///
/// <para>
/// <b>Why this file exists.</b> <c>Mode</c> decides whether an agent's real output is used at all,
/// and it lives in appsettings and in no step config blob — so a typo'd value is not caught by any
/// compiler, any schema, or any step validation. Every test here goes through a real
/// <see cref="IConfiguration"/> (in-memory or the shipped JSON file) into the real
/// <see cref="AgentStepExecutor.ResolveDecisionGateConfig"/>, then into the real
/// <see cref="DecisionGate"/>; none of them hand-build a <see cref="DecisionGateConfig"/> and
/// assert it against itself.
/// </para>
///
/// <para>
/// <b>What the binding actually does</b> — measured, not assumed, because the intuitive answer for
/// three of these is wrong:
/// </para>
/// <list type="bullet">
/// <item><c>"Off"</c> / <c>"Shadow"</c> / <c>"Gate"</c> bind to their own members. The intended case.</item>
/// <item>An <b>empty</b> value (<c>Mode: ""</c>) reads as <b>absent</b> and resolves to
/// <see cref="DecisionGateMode.Off"/> — the fail-to-Off outcome.</item>
/// <item>A <b>case or whitespace variant</b> (<c>"gate"</c>, <c>"GATE"</c>, <c>"Gate "</c>) is
/// <b>recognised</b>, not inert: the resolver parses case-insensitively and trims around the token,
/// exactly as .NET's enum converter did before it. A lowercase <c>gate</c> therefore <b>enables</b>
/// the gate. These are pinned as recognised so nobody "hardens" them into an expectation the
/// runtime does not have.</item>
/// <item>A <b>numeric</b> value binds by ordinal position, so <c>Mode: "1"</c> silently means
/// <see cref="DecisionGateMode.Shadow"/> and <c>"2"</c> means <see cref="DecisionGateMode.Gate"/>;
/// an out-of-range number (<c>"99"</c>) binds to an <i>undefined</i> enum value, which is exactly the
/// "unrecognised value" <see cref="DecisionGateMode"/>'s own documentation promises is inert.</item>
/// <item>Any other <b>non-numeric, non-member text</b> (<c>"Enabled"</c>, <c>"Offf"</c>) resolves to
/// <see cref="DecisionGateMode.Off"/> and logs a warning. It used to make the binder <b>throw</b>
/// <see cref="InvalidOperationException"/> out of the step executor; see
/// <see cref="DecisionGateAgentConfig.ReadMode"/> for why that was a run-killing defect rather than
/// a loud-but-safe failure, and the <c>T-014</c> region below for the regression tests.</item>
/// </list>
/// </summary>
public sealed class DecisionGateModeBindingTests
{
    private const string Colorist = "Colorist";
    private const string MusicSupervisor = "MusicSupervisor";

    // Question names exactly as BuildColoristQuestions / BuildMusicSupervisorQuestions pose them —
    // a site key is keyed on this name, never on the Site string.
    private const string Look = "Look";
    private const string Strength = "Strength";
    private const string HighlightTone = "HighlightTone";
    private const string Intensity = "Intensity";

    private static string AppSettingsPath() =>
        Path.Combine(AppContext.BaseDirectory, "WorkflowEngine.appsettings.json");

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    private static IConfiguration EmptyConfig() => Config();

    private static string ModeKey(string agentConfigName) => $"Agents:{agentConfigName}:DecisionGate:Mode";

    private static DecisionGateConfig Resolve(IConfiguration configuration, string agent, string? questionName = null) =>
        AgentStepExecutor.ResolveDecisionGateConfig(configuration, agent, questionName);

    // ---------------------------------------------------------------------
    // The mapping itself: every declared mode, through the real binder
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Off", DecisionGateMode.Off, 0)]
    [InlineData("Shadow", DecisionGateMode.Shadow, 1)]
    [InlineData("Gate", DecisionGateMode.Gate, 2)]
    public void Mode_key_binds_each_declared_mode_to_its_own_enum_member(
        string configuredValue, DecisionGateMode expected, int expectedOrdinal)
    {
        DecisionGateConfig resolved = Resolve(Config((ModeKey(Colorist), configuredValue)), Colorist);

        resolved.Mode.Should().Be(expected);
        ((int)resolved.Mode).Should().Be(expectedOrdinal,
            "the mapping is positional; pinning the ordinal catches a future reordering of the enum, which would silently reinterpret every shipped value");
    }

    [Fact]
    public void The_three_declared_modes_bind_to_three_distinct_members()
    {
        DecisionGateMode off = Resolve(Config((ModeKey(Colorist), "Off")), Colorist).Mode;
        DecisionGateMode shadow = Resolve(Config((ModeKey(Colorist), "Shadow")), Colorist).Mode;
        DecisionGateMode gate = Resolve(Config((ModeKey(Colorist), "Gate")), Colorist).Mode;

        new[] { off, shadow, gate }.Distinct().Should().HaveCount(3,
            "a binder that collapsed two modes onto one member would be invisible to any single-value test — this is the guard against 'everything binds to Off' and against 'everything binds to Gate'");

        off.Should().Be(DecisionGateMode.Off);
        shadow.Should().Be(DecisionGateMode.Shadow);
        gate.Should().Be(DecisionGateMode.Gate);
    }

    [Fact]
    public void Mode_is_namespaced_by_agent_and_is_not_inherited_across_agents()
    {
        IConfiguration configuration = Config((ModeKey(Colorist), "Gate"));

        Resolve(configuration, Colorist).Mode.Should().Be(DecisionGateMode.Gate);
        Resolve(configuration, MusicSupervisor).Mode.Should().Be(DecisionGateMode.Off,
            "the two gate sites resolve their own key; a mode set for one must not gate the other");
    }

    [Fact]
    public void A_Mode_key_outside_the_DecisionGate_section_is_not_read_at_all()
    {
        // The path is as load-bearing as the value, and its failure mode is the OPPOSITE of a bad
        // value's: a misplaced key is silently inert rather than throwing. That asymmetry is worth a
        // test of its own — it is the shape of a real appsettings mistake.
        IConfiguration configuration = Config(($"Agents:{Colorist}:Mode", "Gate"));

        Resolve(configuration, Colorist).Mode.Should().Be(DecisionGateMode.Off,
            "only Agents:<name>:DecisionGate:Mode is read; a Mode key one level up is a no-op, not a gate");
    }

    [Theory]
    [InlineData(MusicSupervisor, "Gate")]
    [InlineData("Colorist", "Gate")]
    public void Both_gate_sites_read_their_own_Mode_key(string agentConfigName, string expected)
    {
        DecisionGateConfig resolved =
            Resolve(Config((ModeKey(agentConfigName), expected)), agentConfigName);

        resolved.Mode.Should().Be(DecisionGateMode.Gate);
    }

    // ---------------------------------------------------------------------
    // Unrecognised values — the three real outcomes, each pinned to what it is
    // ---------------------------------------------------------------------

    // Outcome 1: fail to Off. An EMPTY value is read as absent, so it resolves to Off — the inert,
    // fail-closed outcome the ticket is about. Verified end-to-end: the real gate is handed the
    // config this binding produced and is shown to do nothing with it.
    [Fact]
    public async Task An_empty_mode_value_resolves_to_Off_which_is_the_fail_to_Off_outcome()
    {
        DecisionGateConfig resolved = Resolve(Config((ModeKey(Colorist), string.Empty)), Colorist);

        resolved.Mode.Should().Be(DecisionGateMode.Off,
            "an empty string is an absent key to the configuration system, and an absent key is Off");

        await AssertGateIsInertAsync(resolved);
    }

    // Outcome 2: a typo used to THROW. Since T-014 it fails to Off and warns, which is the outcome
    // that makes the step complete. The former assertion here (`Should().Throw<InvalidOperationException>()`)
    // pinned the defect itself; it is deliberately replaced rather than deleted, so the flip is
    // visible in the history of this file. The end-to-end consequence — step Completed, zero
    // outbound calls — is asserted in the T-014 region further down.
    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    [InlineData("On")]
    [InlineData("Offf")]
    [InlineData("true")]
    public void An_unparseable_mode_string_resolves_to_Off_and_never_reaches_the_gate(string configuredValue)
    {
        IConfiguration configuration = Config((ModeKey(Colorist), configuredValue));

        DecisionGateConfig resolved = configuration
            .Invoking(c => Resolve(c, Colorist))
            .Should().NotThrow(
                "a typo in one appsettings key must be inert: this call sits outside RunPreAgentGateAsync's try, so a throw here escapes the step executor and WorkflowExecutorService — which excludes InvalidOperationException from retry — marks the step Failed and aborts the execution")
            .Subject;

        resolved.Mode.Should().Be(DecisionGateMode.Off,
            "fail to Off, not to Shadow and certainly not to Gate: the value decides whether an agent's real output is used at all");
    }

    // Outcome 3: RECOGNISED, despite looking like the ticket's example of an unrecognised value.
    // .NET's enum converter parses case-insensitively and tolerates surrounding whitespace, so these
    // do not fail to Off and do not throw — "gate" ENABLES the gate. Pinned deliberately: an
    // expectation that case variants are inert would be a test that passes while the gate is on.
    [Theory]
    [InlineData("gate", DecisionGateMode.Gate)]
    [InlineData("GATE", DecisionGateMode.Gate)]
    [InlineData("gAtE", DecisionGateMode.Gate)]
    [InlineData("Gate ", DecisionGateMode.Gate)]
    [InlineData(" Gate", DecisionGateMode.Gate)]
    [InlineData("\tGate\t", DecisionGateMode.Gate)]
    [InlineData("off", DecisionGateMode.Off)]
    [InlineData("shadow", DecisionGateMode.Shadow)]
    [InlineData(" Shadow ", DecisionGateMode.Shadow)]
    public void A_case_or_whitespace_variant_of_a_declared_mode_is_RECOGNISED_not_inert(
        string configuredValue, DecisionGateMode expected)
    {
        Resolve(Config((ModeKey(Colorist), configuredValue)), Colorist).Mode.Should().Be(expected,
            "the enum converter is case-insensitive and trims, so case/whitespace variants resolve to a real mode — 'Gate ' and 'gate' both turn the gate ON");
    }

    // Outcome 4: an undefined enum VALUE, reached through a real appsettings string. This is the case
    // DecisionGateMode's own documentation names ("a bad appsettings binding, a cast") when it
    // promises an unrecognised value does nothing — and here that promise holds: the gate's
    // exhaustive switch falls to its default arm and no provider is resolved.
    [Fact]
    public async Task An_out_of_range_numeric_mode_binds_to_an_undefined_value_and_the_gate_is_inert()
    {
        DecisionGateConfig resolved = Resolve(Config((ModeKey(Colorist), "99")), Colorist);

        resolved.Mode.Should().Be((DecisionGateMode)99,
            "a number binds to the enum's underlying value without a membership check, so an out-of-range number produces a value no member declares");

        await AssertGateIsInertAsync(resolved);
    }

    [Theory]
    [InlineData("0", DecisionGateMode.Off)]
    [InlineData("1", DecisionGateMode.Shadow)]
    [InlineData("2", DecisionGateMode.Gate)]
    public void A_numeric_mode_binds_by_ordinal_position(string configuredValue, DecisionGateMode expected)
    {
        Resolve(Config((ModeKey(Colorist), configuredValue)), Colorist).Mode.Should().Be(expected,
            "Mode: \"2\" silently enables the gate and Mode: \"1\" silently enables Shadow — no member name is required for the gate to arm");
    }

    // ---------------------------------------------------------------------
    // The per-site threshold ladder (decisions.md §6): site → agent → record default
    // ---------------------------------------------------------------------

    [Fact]
    public void Level_1_the_site_key_wins_over_the_agent_key_for_its_own_question_only()
    {
        IConfiguration configuration = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.70"),
            ("Agents:Colorist:DecisionGate:MinMargin", "0.15"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"),
            ("Agents:Colorist:DecisionGate:Sites:Look:MinMargin", "0.05"));

        DecisionGateConfig site = Resolve(configuration, Colorist, Look);
        site.AcceptAt.Should().Be(0.95);
        site.MinMargin.Should().Be(0.05);

        DecisionGateConfig sibling = Resolve(configuration, Colorist, Strength);
        sibling.AcceptAt.Should().Be(0.70, "the site layer is scoped to one question, never to the call");
        sibling.MinMargin.Should().Be(0.15);
    }

    [Fact]
    public void Level_2_the_agent_key_wins_over_the_record_defaults_when_no_site_key_exists()
    {
        DecisionGateConfig defaults = new();

        IConfiguration configuration = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.70"),
            ("Agents:Colorist:DecisionGate:MinMargin", "0.15"));

        DecisionGateConfig resolved = Resolve(configuration, Colorist, Strength);

        resolved.AcceptAt.Should().Be(0.70);
        resolved.MinMargin.Should().Be(0.15);

        resolved.AcceptAt.Should().NotBe(defaults.AcceptAt,
            because: "if this passed with the default the agent-level key would not have been read at all");
        resolved.MinMargin.Should().NotBe(defaults.MinMargin);
    }

    [Fact]
    public void Level_3_the_record_defaults_apply_when_neither_key_exists()
    {
        DecisionGateConfig defaults = new();

        DecisionGateConfig resolved = Resolve(EmptyConfig(), Colorist, Look);

        resolved.AcceptAt.Should().Be(defaults.AcceptAt);
        resolved.MinMargin.Should().Be(defaults.MinMargin);
        resolved.AcceptAt.Should().Be(0.85,
            "0.85/0.25 are the phase-1 fallback, NOT calibration — pinned as a literal so a change to the fallback is a deliberate edit, not a drift");
        resolved.MinMargin.Should().Be(0.25);
    }

    [Fact]
    public void An_unrelated_question_does_not_inherit_another_questions_site_override()
    {
        IConfiguration configuration = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.70"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"),
            ("Agents:Colorist:DecisionGate:Sites:Look:MinMargin", "0.05"));

        DecisionGateConfig unrelated = Resolve(configuration, Colorist, HighlightTone);

        unrelated.AcceptAt.Should().Be(0.70,
            "a site lookup keyed on the wrong name would hand Look's tighter threshold to every sibling, which is the failure the per-question resolution exists to prevent");
        unrelated.MinMargin.Should().Be(0.25,
            "MinMargin is resolved per key, so Look's site MinMargin must not reach a question that declares none");
    }

    [Fact]
    public void A_site_key_for_a_name_that_is_not_a_question_affects_no_question()
    {
        IConfiguration configuration = Config(
            ("Agents:Colorist:DecisionGate:Sites:SomethingElse:AcceptAt", "0.99"));

        Resolve(configuration, Colorist, Look).AcceptAt.Should().Be(0.85);
        Resolve(configuration, Colorist, Strength).AcceptAt.Should().Be(0.85);
        Resolve(configuration, MusicSupervisor, Intensity).AcceptAt.Should().Be(0.85);
    }

    [Fact]
    public void The_threshold_ladder_is_namespaced_by_agent()
    {
        IConfiguration configuration = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.70"),
            ("Agents:MusicSupervisor:DecisionGate:Sites:Intensity:AcceptAt", "0.55"));

        Resolve(configuration, Colorist, Look).AcceptAt.Should().Be(0.70);
        Resolve(configuration, MusicSupervisor, Intensity).AcceptAt.Should().Be(0.55);
        Resolve(configuration, MusicSupervisor, "Ducking").AcceptAt.Should().Be(0.85,
            "the agent key was never set for MusicSupervisor, so its other questions fall to the default");
    }

    // ---------------------------------------------------------------------
    // The shipped file stays inert
    // ---------------------------------------------------------------------

    [Fact]
    public void Shipped_appsettings_carries_no_active_Agents_DecisionGate_key()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath(), optional: false)
            .Build();

        AllLeafPaths(configuration.GetSection("Agents"))
            .Where(path => path.Contains("DecisionGate", StringComparison.Ordinal))
            .Should().BeEmpty(
                "the shipped file must expose no live DecisionGate key at all — a live Mode, AcceptAt or site key is a shipped enablement");
    }

    [Fact]
    public void Shipped_appsettings_writes_Off_as_its_only_Mode_value()
    {
        string[] lines = File.ReadAllLines(AppSettingsPath());

        string[] liveDecisionGateLines = lines
            .Select(line => line.Trim())
            .Where(line => line.Contains("DecisionGate", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();

        liveDecisionGateLines.Should().BeEmpty(
            "every DecisionGate line in the shipped file is documentation, so every one of them is commented out");

        string[] modeLines = lines
            .Where(line => line.Contains("\"Mode\":", StringComparison.Ordinal))
            .ToArray();

        modeLines.Should().NotBeEmpty(
            "the commented example block documents Mode; an empty set would make the assertion below vacuous");

        modeLines.Should().OnlyContain(
            line => line.Contains("\"Off\"", StringComparison.Ordinal),
            "the only Mode value written anywhere in the shipped file is Off, so an edit that turns the gate on fails a test instead of shipping");
    }

    // =====================================================================
    // T-014: DecisionGateAgentConfig.ReadMode — the reader's own contract
    //
    // Five cases, exactly as the ticket's acceptance states them: Off for a garbage string, Off for
    // an absent key, Shadow for "Shadow", Gate for "Gate" and for a differently-cased valid value —
    // plus the warning, which is the whole reason the bad value is not merely silent.
    // =====================================================================

    [Fact]
    public void ReadMode_returns_Off_for_a_garbage_string_never_an_exception()
    {
        var logger = new RecordingLogger();

        DecisionGateMode mode = DecisionGateAgentConfig.ReadMode(
            Config((ModeKey(Colorist), "Enabled")), Colorist, logger);

        mode.Should().Be(DecisionGateMode.Off);
    }

    [Fact]
    public void ReadMode_returns_Off_for_an_absent_key()
    {
        DecisionGateAgentConfig.ReadMode(EmptyConfig(), Colorist).Should().Be(DecisionGateMode.Off);
    }

    [Fact]
    public void ReadMode_returns_Off_for_an_absent_key_with_no_warning()
    {
        var logger = new RecordingLogger();

        DecisionGateAgentConfig.ReadMode(EmptyConfig(), Colorist, logger);

        logger.Entries.Should().BeEmpty(
            "absence is the ordinary state of every shipped install — the DecisionGate block ships commented out — so warning on it would train operators to ignore the message");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadMode_returns_Off_for_a_blank_value_with_no_warning(string blank)
    {
        var logger = new RecordingLogger();

        DecisionGateAgentConfig.ReadMode(Config((ModeKey(Colorist), blank)), Colorist, logger)
            .Should().Be(DecisionGateMode.Off);

        logger.Entries.Should().BeEmpty("a blank value is the absent case, and it is silent");
    }

    [Fact]
    public void ReadMode_returns_Shadow_for_Shadow()
    {
        DecisionGateAgentConfig.ReadMode(Config((ModeKey(Colorist), "Shadow")), Colorist)
            .Should().Be(DecisionGateMode.Shadow);
    }

    [Fact]
    public void ReadMode_returns_Gate_for_Gate()
    {
        DecisionGateAgentConfig.ReadMode(Config((ModeKey(Colorist), "Gate")), Colorist)
            .Should().Be(DecisionGateMode.Gate);
    }

    [Theory]
    [InlineData("gate", DecisionGateMode.Gate)]
    [InlineData("GATE", DecisionGateMode.Gate)]
    [InlineData("gAtE", DecisionGateMode.Gate)]
    [InlineData(" Gate ", DecisionGateMode.Gate)]
    [InlineData("shadow", DecisionGateMode.Shadow)]
    [InlineData("SHADOW", DecisionGateMode.Shadow)]
    [InlineData("off", DecisionGateMode.Off)]
    public void ReadMode_is_case_insensitive_exactly_as_the_previous_binder_was(
        string configuredValue, DecisionGateMode expected)
    {
        DecisionGateAgentConfig.ReadMode(Config((ModeKey(Colorist), configuredValue)), Colorist)
            .Should().Be(expected,
                "the previous binder was case-insensitive (EnumConverter → Enum.Parse(ignoreCase: true)); narrowing this would silently turn a lowercase 'gate' from an armed gate into an inert one");
    }

    [Fact]
    public void ReadMode_warns_naming_the_offending_key_and_value()
    {
        var logger = new RecordingLogger();

        DecisionGateAgentConfig.ReadMode(Config((ModeKey(Colorist), "Enabled")), Colorist, logger);

        logger.Entries.Should().ContainSingle("one bad key produces one warning, not a silent fallback");
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("Enabled", "the offending VALUE is what an operator has to find and fix");
        logger.Entries[0].Message.Should().Contain(ModeKey(Colorist),
            "and the offending KEY, which is the only way to tell two gate sites apart in the log");
    }

    [Fact]
    public void ReadMode_is_the_same_key_the_executor_reads()
    {
        // The key is built here, from the config name, so a caller cannot pass a full key and a
        // caller cannot pass the wrong prefix. Pinned against the literal the executor used before
        // this change and StepCachePolicy still builds its expectations from.
        DecisionGateAgentConfig.ModeKeyFor(Colorist).Should().Be("Agents:Colorist:DecisionGate:Mode");
        DecisionGateAgentConfig.ModeKeyFor(MusicSupervisor).Should().Be("Agents:MusicSupervisor:DecisionGate:Mode");
    }

    [Fact]
    public void ReadMode_tolerates_a_null_configuration()
    {
        DecisionGateAgentConfig.ReadMode(null, Colorist).Should().Be(DecisionGateMode.Off);
        DecisionGateAgentConfig.ReadMode(null, Colorist, new RecordingLogger()).Should().Be(DecisionGateMode.Off);
    }

    [Theory]
    [MemberData(nameof(GarbageModeValues))]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Off")]
    [InlineData("Shadow")]
    [InlineData("Gate")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("Off, Shadow")]
    [InlineData("!!!")]
    [InlineData("\u0000")]
    [InlineData("Ω")]
    public void ReadMode_never_throws_for_any_input(string configuredValue)
    {
        // The whole point of the helper. A NullReferenceException on a null configuration, an
        // OverflowException on a huge numeric string, or a FormatException would all be the same
        // run-killing bug wearing a different exception type, so this asserts the absence of ANY
        // exception rather than the absence of one particular one.
        Action act = () => DecisionGateAgentConfig.ReadMode(
            Config((ModeKey(Colorist), configuredValue)), Colorist, new RecordingLogger());

        act.Should().NotThrow();
    }

    /// <summary>
    /// A minimal capturing <see cref="ILogger"/>: the warning is part of the reader's contract ("the
    /// unrecognised value is discoverable without being fatal"), so it is asserted rather than
    /// assumed, and <see cref="NullLogger"/> would make that assertion vacuous.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // =====================================================================
    // T-014: a bad Mode key is INERT — the step completes and nothing is called
    //
    // This is the Security condition's regression test. It deliberately does NOT assert "does not
    // throw": a future change that turned a bad mode into Gate would also stop throwing, and a
    // throw-only assertion would pass while the gate made real outbound calls. Both halves are
    // asserted here — the step reaches StepStatus.Completed, AND no decision call was made.
    // =====================================================================

    /// <summary>Values a human actually types when they mean a mode and get it wrong.</summary>
    public static TheoryData<string> GarbageModeValues() => new()
    {
        "Enabled", "Disabled", "On", "Offf", "true", "False", "yes", "off ", "Shaddow", "Gated", "None",
    };

    [Theory]
    [MemberData(nameof(GarbageModeValues))]
    public async Task A_garbage_mode_string_completes_the_agent_step_and_makes_zero_decision_calls(string garbage)
    {
        var decisionGate = new Mock<IDecisionGate>();
        var agent = MockAgent(AgentType.Colorist, ColoristJson);
        AgentStepExecutor executor = CreateExecutor(agent.Object, decisionGate.Object, Config((ModeKey(Colorist), garbage)));

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Status.Should().Be(StepStatus.Completed,
            "a typo in one appsettings key must be inert — it used to throw InvalidOperationException out of ExecuteAsync, and WorkflowExecutorService excludes that exception type from retry, so the step was marked Failed and the whole execution aborted");
        result.Output.Should().Be(ColoristJson, "the fake agent runs exactly as it does with no gate configured");
        result.ErrorDetails.Should().BeNull();

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the agent is the System Two path a bad mode must fall back to");

        decisionGate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Gate must not arm on an unparseable value — this is the half that a 'does not throw' assertion would miss, since Mode = Gate would also not throw");

        // The phase-1 Shadow hook is unconditional for a gate AGENT TYPE: AgentStepExecutor always
        // offers the gate the observation, and DecisionGate.RunShadowAsync is what no-ops on a
        // non-Shadow mode. So the assertion is not "the gate object is untouched" — it is that the
        // gate is handed Mode = Off, which is precisely what makes its outbound call unreachable.
        // That the call really is unreachable is proved against a REAL gate in
        // A_garbage_mode_string_reaches_no_decision_provider_even_through_a_real_gate.
        decisionGate.Verify(
            g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(),
                It.Is<DecisionGateConfig>(c => c.Mode == DecisionGateMode.Off),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the gate is handed Off, never Shadow and never Gate");
    }

    [Theory]
    [MemberData(nameof(GarbageModeValues))]
    public async Task A_garbage_mode_string_reaches_no_decision_provider_even_through_a_real_gate(string garbage)
    {
        // The stronger half: a REAL DecisionGate with a provider that WOULD resolve and a fake client
        // that WOULD answer. Nothing in the chain is stubbed into silence, so an outbound call would
        // actually happen if the value were read as anything other than Off.
        var harness = new RecordingGateHarness();

        var agent = MockAgent(AgentType.Colorist, ColoristJson);
        AgentStepExecutor executor = CreateExecutor(agent.Object, harness.Gate, Config((ModeKey(Colorist), garbage)));

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be(ColoristJson);

        harness.Client.Invocations.Should().BeEmpty("zero outbound decision calls");
        (await harness.Db.DecisionObservations.CountAsync()).Should().Be(0,
            "inert means no observation row either, so not even a scope was opened to write one");
    }

    [Fact]
    public async Task The_garbage_mode_result_is_byte_identical_to_the_absent_key_result()
    {
        var garbageGate = new Mock<IDecisionGate>();
        var absentGate = new Mock<IDecisionGate>();

        StepExecutionResult garbage = await CreateExecutor(
                MockAgent(AgentType.Colorist, ColoristJson).Object, garbageGate.Object,
                Config((ModeKey(Colorist), "Enabled")))
            .ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        StepExecutionResult absent = await CreateExecutor(
                MockAgent(AgentType.Colorist, ColoristJson).Object, absentGate.Object, EmptyConfig())
            .ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        Describe(garbage).Should().Be(Describe(absent),
            "both read as Off, so the two runs must be indistinguishable — not merely both 'successful'");
    }

    [Fact]
    public void A_garbage_mode_string_is_not_a_gate_site_for_the_step_cache_either()
    {
        // The second reader. A typo must not make the step non-cacheable, because that would be an
        // observable behaviour change caused by a value the runtime treats as Off everywhere else.
        WorkflowStep step = AgentStep(AgentType.Colorist, StepCacheMode.Default);

        bool withGarbage = new StepCachePolicy(Config((ModeKey(Colorist), "Enabled"))).IsCacheable(step);
        bool withAbsentKey = new StepCachePolicy(EmptyConfig()).IsCacheable(step);

        withGarbage.Should().Be(withAbsentKey,
            "the cache reader and the executor reader must agree; while both called GetValue<DecisionGateMode?> the two answers could drift");
        withGarbage.Should().BeTrue("an absent key is cacheable, and garbage now reads as absent");
        withAbsentKey.Should().BeTrue(because: "the guard against a vacuous pass — if absence were non-cacheable this test would still pass");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private const string ColoristJson =
        """
        {"look":"Warm","strength":"Normal","shadowTone":"Lifted","highlightTone":"Neutral","reason":"r","planRationale":"pr"}
        """;

    private static Mock<IReelBoltAgent> MockAgent(AgentType agentType, string output)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(agentType.ToString());
        agent.Setup(a => a.AgentType).Returns(agentType);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns(typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = output, Success = true, TokensUsed = 10, InputTokens = 6, OutputTokens = 4 });
        return agent;
    }

    private static AgentStepExecutor CreateExecutor(
        IReelBoltAgent agent, IDecisionGate decisionGate, IConfiguration configuration)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        return new AgentStepExecutor(
            registry.Object,
            accessor.Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            configuration,
            decisionGate,
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static StepExecutionContext CreateContext(
        AgentType agentType, IReadOnlyList<StepOutputHistoryEntry>? history = null)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agentType, Name = agentType.ToString() }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history ?? [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static WorkflowStep AgentStep(AgentType agentType, StepCacheMode cacheMode) => new()
    {
        StepType = StepType.Agent,
        CacheMode = cacheMode,
        AgentDefinition = new AgentDefinition { AgentType = agentType }
    };

    /// <summary>
    /// An upstream <c>VideoAnalyze</c> <c>{view, meta}</c> envelope carrying the per-shot measured
    /// colour words a Colorist Gate-mode question set needs. Without it the pre-agent Gate exits on
    /// "no descriptor" before it would ever try to resolve a provider, which would make the
    /// zero-outbound-call assertion vacuous.
    /// </summary>
    private static IReadOnlyList<StepOutputHistoryEntry> DescriptorHistory() =>
    [
        new StepOutputHistoryEntry(1, "VideoAnalyze", """
        {"view":{
           "shots":[
             {"id":"s0","startSec":0.0,"endSec":4.0,"durationSec":4.0,"v":{"temp":"Warm","tone":"Neutral","sat":"Normal"}},
             {"id":"s1","startSec":4.0,"endSec":8.0,"durationSec":8.0,"v":{"temp":"Cool","tone":"Deepened","sat":"Muted"}}
           ]
         },
         "meta":{"version":2}}
        """)
    ];

    /// <summary>
    /// Every observable field of a <see cref="StepExecutionResult"/> except <c>DurationMs</c>, which
    /// is wall-clock rather than behaviour.
    /// </summary>
    private static string Describe(StepExecutionResult result) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            result.Output,
            result.NextStepIndex,
            result.NewIterationCount,
            result.TokensUsed,
            result.InputTokens,
            result.OutputTokens,
            result.ToolCalls,
            result.Reasoning,
            result.Status,
            result.ErrorDetails,
            result.IterationNumber,
            result.OutputStorageKey,
            result.ArtifactStorageKey,
            result.ChatTranscriptJson
        });

    /// <summary>
    /// A REAL <see cref="DecisionGate"/> over a fake <see cref="IDecisionClient"/> and an in-memory
    /// observation store, with a provider that resolves. "No call was made" is therefore a property
    /// of the mode, not of a harness that could never have made one.
    /// </summary>
    private sealed class RecordingGateHarness
    {
        public Mock<IDecisionClient> Client { get; } = new();

        public DecisionGate Gate { get; }

        public WorkflowEngineDbContext Db { get; }

        public RecordingGateHarness()
        {
            Db = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                    .UseInMemoryDatabase($"DecisionGateModeBinding_{Guid.NewGuid()}")
                    .Options);

            var provider = new ResolvedDecisionProvider(
                ProviderId: Guid.NewGuid(),
                Name: "mock-provider",
                Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://test",
                ModelName: "test-model",
                ApiKey: "key",
                TimeoutSeconds: 300);

            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);

            var factory = new Mock<IDecisionClientFactory>();
            factory.Setup(f => f.Get(provider)).Returns(Client.Object);

            var scopeServiceProvider = new Mock<IServiceProvider>();
            scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(Db);

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

            Gate = new DecisionGate(
                resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance);
        }
    }

    private static IEnumerable<string> AllLeafPaths(IConfigurationSection section) =>
        section.GetChildren().SelectMany(child =>
            child.Value is not null
                ? new[] { child.Path }
                : AllLeafPaths(child));

    /// <summary>
    /// Drives the <b>real</b> <see cref="DecisionGate"/> with a config the <b>real</b> binder
    /// produced from real configuration, and asserts the full inertness contract: no provider
    /// resolution, no client construction, no scope opened, no observation row, and the uniform
    /// "nothing happened" outcome the caller treats as "run the agent".
    ///
    /// <para>
    /// The non-vacuity of these assertions is what
    /// <see cref="Control_the_gate_resolved_from_real_config_binding_does_reach_provider_resolution"/>
    /// pins: the same helper's assertions are shown to fail on the live path.
    /// </para>
    /// </summary>
    private static async Task AssertGateIsInertAsync(DecisionGateConfig config)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        var factory = new Mock<IDecisionClientFactory>();
        var scopeFactory = new Mock<IServiceScopeFactory>();

        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"DecisionGateModeBindingTests_{Guid.NewGuid()}")
            .Options;
        await using WorkflowEngineDbContext db = new WorkflowEngineDbContext(options);

        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(db);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);
        scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        DecisionGate gate = new DecisionGate(
            resolver.Object, factory.Object, scopeFactory.Object, Mock.Of<ILogger<DecisionGate>>());

        DecisionGateOutcome outcome = await gate.DecideAsync(
            new DecisionGateRequest(
                Guid.NewGuid(),
                2,
                AgentType.Colorist,
                Site: "Colorist.step",
                State: new DecisionState("compiled-plan descriptor"),
                Questions:
                [
                    new DecisionGateQuestion(
                        "Colorist.step", "Look", DecisionKind.Choice, "pick a look",
                        new[] { "None", "Warm" }, "Warm")
                ],
                Config: config),
            CancellationToken.None);

        outcome.Should().BeSameAs(DecisionGateOutcome.NoDecision);

        resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "inert means no provider resolution");
        factory.Verify(f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never,
            "inert means no outbound call, and the client is only ever built to make one");
        scopeFactory.Verify(sf => sf.CreateScope(), Times.Never,
            "inert means no observation row, so the gate must not even open a scope to write one");

        (await db.DecisionObservations.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The positive control. Same path, same helper inputs, but a mode resolved from a real
    /// configuration that <i>does</i> gate: provider resolution is reached. Without this, every
    /// <c>Times.Never</c> above could be true for a reason unrelated to the binding.
    /// </summary>
    [Fact]
    public async Task Control_the_gate_resolved_from_real_config_binding_does_reach_provider_resolution()
    {
        DecisionGateConfig resolved = Resolve(Config((ModeKey(Colorist), "Gate")), Colorist);
        resolved.Mode.Should().Be(DecisionGateMode.Gate);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        DecisionGate gate = new DecisionGate(
            resolver.Object,
            new Mock<IDecisionClientFactory>().Object,
            new Mock<IServiceScopeFactory>().Object,
            Mock.Of<ILogger<DecisionGate>>());

        DecisionGateOutcome outcome = await gate.DecideAsync(
            new DecisionGateRequest(
                Guid.NewGuid(),
                2,
                AgentType.Colorist,
                Site: "Colorist.step",
                State: new DecisionState("compiled-plan descriptor"),
                Questions:
                [
                    new DecisionGateQuestion(
                        "Colorist.step", "Look", DecisionKind.Choice, "pick a look",
                        new[] { "None", "Warm" }, "Warm")
                ],
                Config: resolved),
            CancellationToken.None);

        resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a real Mode: Gate binding reaches the provider call path, so Times.Never elsewhere is a property of the mode and not of the harness");

        outcome.HasDecision.Should().BeFalse("no Decision provider is configured, so the gate degrades to the agent");
    }
}