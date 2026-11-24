using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the decision-gate arm of <see cref="StepCachePolicy.IsCacheable"/>: an Agent step
/// whose resolved <c>Agents:&lt;name&gt;:DecisionGate:Mode</c> is anything other than
/// <see cref="DecisionGateMode.Off"/> is not cacheable.
///
/// <para>
/// The hazard being closed: the step-cache key is built by <c>WorkflowExecutorService</c>
/// <b>before</b> the step runs, from the pre-execution prompt, and the gate's
/// <c>Mode</c>/<c>AcceptAt</c>/<c>MinMargin</c>/<c>ProviderId</c> come from appsettings — so they are
/// in no step config blob and in no key component. Both gated agent types (<c>Colorist</c>,
/// <c>MusicSupervisor</c>) are otherwise cacheable, so without this rule a run with the gate
/// <c>Off</c> could hash to the same key as a run the decision model answered and be served a plan
/// only the decision model ever produced. <c>AgentStepExecutor</c>'s former claim that the recorded
/// resolved input protected the key was false: <c>RecordResolvedInput</c> feeds
/// <c>WorkflowStepResult.InputJson</c>, not the key.
/// </para>
///
/// <para>
/// The two tests that pin <b>today's</b> behaviour are the important ones: the local install
/// configures no <c>DecisionGate</c> key at all, and this rule must be completely inert there.
/// </para>
/// </summary>
public sealed class StepCachePolicyDecisionGateTests
{
    private const string Colorist = "Colorist";
    private const string MusicSupervisor = "MusicSupervisor";

    private static WorkflowStep AgentStep(AgentType agentType, StepCacheMode? cacheMode = null) => new()
    {
        StepType = StepType.Agent,
        CacheMode = cacheMode,
        AgentDefinition = new AgentDefinition { AgentType = agentType }
    };

    /// <summary>A policy over a real <see cref="IConfiguration"/> built from an in-memory collection
    /// — the same shape the executor itself reads, so this exercises the real key binding.</summary>
    private static IStepCachePolicy Policy(params (string Key, string Value)[] entries) =>
        new StepCachePolicy(
            new ConfigurationBuilder()
                .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
                .Build());

    /// <summary>The policy as DI composes it: a real, fully populated configuration with no
    /// DecisionGate key anywhere under <c>Agents</c> — the actual local install.</summary>
    private static IStepCachePolicy UnconfiguredPolicy() =>
        Policy(("Agents:AuthorAgent:SystemPrompt", "irrelevant"), ("WorkflowEngine:MaxConcurrency", "2"));

    /// <summary>A policy handed no configuration at all (the optional-constructor path).</summary>
    private static IStepCachePolicy NullConfigurationPolicy() => new StepCachePolicy();

    private static (string Key, string Value) Mode(string agentConfigName, string mode) =>
        ($"Agents:{agentConfigName}:DecisionGate:Mode", mode);

    // ---------------------------------------------------------------------
    // Today's behaviour, pinned: no DecisionGate config, and Mode = Off
    // ---------------------------------------------------------------------

    [Fact]
    public void Colorist_is_cacheable_with_no_DecisionGate_config_at_all()
    {
        UnconfiguredPolicy().IsCacheable(AgentStep(AgentType.Colorist)).Should().BeTrue(
            because: "absence is the normal local case — the shipped appsettings.json keeps every DecisionGate example commented out — and the rule must be inert there");
    }

    [Theory]
    [InlineData(AgentType.Colorist, Colorist)]
    [InlineData(AgentType.MusicSupervisor, MusicSupervisor)]
    public void Gated_agent_type_is_cacheable_when_its_mode_is_Off(AgentType agentType, string configName)
    {
        Policy(Mode(configName, "Off")).IsCacheable(AgentStep(agentType)).Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentType.Colorist)]
    [InlineData(AgentType.MusicSupervisor)]
    public void Gated_agent_type_is_cacheable_when_no_configuration_is_supplied_at_all(AgentType agentType)
    {
        NullConfigurationPolicy().IsCacheable(AgentStep(agentType)).Should().BeTrue(
            because: "no configuration reads as an absent key, which reads as Off");
    }

    // ---------------------------------------------------------------------
    // The rule itself: any mode other than Off is not cacheable
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AgentType.Colorist, Colorist)]
    [InlineData(AgentType.MusicSupervisor, MusicSupervisor)]
    public void Gated_agent_type_is_not_cacheable_in_Shadow_mode(AgentType agentType, string configName)
    {
        Policy(Mode(configName, "Shadow")).IsCacheable(AgentStep(agentType)).Should().BeFalse(
            because: "the key is built before the step runs and contains none of the gate's configuration");
    }

    [Theory]
    [InlineData(AgentType.Colorist, Colorist)]
    [InlineData(AgentType.MusicSupervisor, MusicSupervisor)]
    public void Gated_agent_type_is_not_cacheable_in_Gate_mode(AgentType agentType, string configName)
    {
        Policy(Mode(configName, "Gate")).IsCacheable(AgentStep(agentType)).Should().BeFalse(
            because: "in Gate mode the plan may be produced with no agent run at all, so a gate-OFF run must never be served it");
    }

    [Fact]
    public void The_mode_read_is_namespaced_by_agent_and_not_inherited_across_agents()
    {
        IStepCachePolicy policy = Policy(Mode(Colorist, "Gate"));

        policy.IsCacheable(AgentStep(AgentType.Colorist)).Should().BeFalse();
        policy.IsCacheable(AgentStep(AgentType.MusicSupervisor)).Should().BeTrue(
            because: "the rule must read Agents:MusicSupervisor:DecisionGate:Mode, never the Colorist block");
    }

    // ---------------------------------------------------------------------
    // Non-gate agents are unaffected by any DecisionGate configuration
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AgentType.ReviewAgent)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.SoundDesigner)]
    public void A_non_gated_Agent_type_stays_cacheable_with_mode_Gate_configured_for_Colorist(AgentType agentType)
    {
        IStepCachePolicy policy = Policy(Mode(Colorist, "Gate"), Mode(MusicSupervisor, "Gate"));

        policy.IsCacheable(AgentStep(agentType)).Should().BeTrue(
            because: $"{agentType} is not a decision-gate site, so no gate configuration can affect it");
    }

    [Fact]
    public void A_non_gated_Agent_type_excluded_by_its_tool_grant_is_unaffected_in_both_directions()
    {
        var step = AgentStep(AgentType.AuthorAgent);

        UnconfiguredPolicy().IsCacheable(step).Should().BeFalse();
        Policy(Mode(Colorist, "Gate")).IsCacheable(step).Should().BeFalse(
            because: "AuthorAgent is excluded by its own ProjectWrite/SandboxAuthoring grant — the gate rule neither rescues nor further restricts it");
    }

    [Fact]
    public void A_non_Agent_step_type_is_unaffected_by_any_DecisionGate_configuration()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.Extract,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.ExtractTransform }
        };

        Policy(Mode(Colorist, "Gate"), Mode(MusicSupervisor, "Gate")).IsCacheable(step).Should().BeTrue(
            because: "the rule lives on the Agent arm only — no non-Agent step type's cacheability changes");
    }

    // ---------------------------------------------------------------------
    // The CacheMode override still wins in both directions — and the
    // residual that leaves open is asserted, not hidden.
    // ---------------------------------------------------------------------

    [Fact]
    public void CacheMode_Never_still_wins_on_a_gate_configured_step()
    {
        var step = AgentStep(AgentType.Colorist, StepCacheMode.Never);

        Policy(Mode(Colorist, "Gate")).IsCacheable(step).Should().BeFalse();
    }

    [Fact]
    public void CacheMode_Always_still_forces_caching_on_a_gate_configured_step()
    {
        var step = AgentStep(AgentType.Colorist, StepCacheMode.Always);

        Policy(Mode(Colorist, "Gate")).IsCacheable(step).Should().BeTrue(
            because: "the per-step override is left above the built-in table deliberately; an author who sets CacheMode=Always on a gate-configured step re-opens the hazard the rule exists to close");
    }

    [Fact]
    public void CacheMode_Always_on_a_gate_configured_step_is_the_only_way_back_in()
    {
        IStepCachePolicy policy = Policy(Mode(Colorist, "Gate"));

        policy.IsCacheable(AgentStep(AgentType.Colorist, StepCacheMode.Default)).Should().BeFalse();
        policy.IsCacheable(AgentStep(AgentType.Colorist, StepCacheMode.Always)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // A typo'd mode is inert here too, and reads exactly like an absent key
    //
    // T-014: this reader used to hold its own `GetValue<DecisionGateMode?>`, which threw
    // InvalidOperationException on a non-empty unparseable string. Both call sites of
    // HasNoActiveDecisionGate sit inside a `catch (Exception)`, so the effect was "no cache" plus a
    // warning log rather than an aborted run — but it was the same defect as the executor's, and it
    // meant the two readers answered differently on exactly the inputs that mattered. Both now go
    // through DecisionGateAgentConfig.ReadMode.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Offf")]
    [InlineData("true")]
    [InlineData("On")]
    [InlineData("gated")]
    public void A_garbage_mode_value_reads_exactly_like_an_absent_key(string garbage)
    {
        bool withGarbage = Policy(Mode(Colorist, garbage)).IsCacheable(AgentStep(AgentType.Colorist));
        bool withAbsentKey = UnconfiguredPolicy().IsCacheable(AgentStep(AgentType.Colorist));

        withGarbage.Should().Be(withAbsentKey,
            "a typo must not change the cacheability of a step: the executor reads the same key through the same helper, and the two answers drifting is the bug this pins");
        withAbsentKey.Should().BeTrue(
            because: "the anti-vacuity guard — if absence itself were non-cacheable, the comparison above would pass while both arms were wrong");
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Offf")]
    public void A_garbage_mode_value_does_not_throw_out_of_the_policy(string garbage)
    {
        IStepCachePolicy policy = Policy(Mode(Colorist, garbage));

        policy.Invoking(p => p.IsCacheable(AgentStep(AgentType.Colorist))).Should().NotThrow(
            "the throw was absorbed by WorkflowExecutorService's catch, so it degraded to 'no cache' rather than aborting — but a configuration read that throws is a defect wherever it is called from");
    }

    [Fact]
    public void The_cache_reader_also_warns_on_a_garbage_mode_value()
    {
        // The ticket's "both callers must get the warning". The executor path logs through
        // AgentStepExecutor's logger; this is the other one, and without it a typo would be
        // discoverable only when the step happened to be a Gate site.
        var logger = new RecordingLogger();

        new StepCachePolicy(RawConfig(Mode(Colorist, "Enabled")), logger)
            .IsCacheable(AgentStep(AgentType.Colorist));

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Should().Contain("Enabled");
        logger.Entries[0].Should().Contain("Agents:Colorist:DecisionGate:Mode");
    }

    [Fact]
    public void The_cache_reader_stays_silent_on_an_absent_or_valid_mode()
    {
        var absent = new RecordingLogger();
        var valid = new RecordingLogger();

        new StepCachePolicy(RawConfig(), absent).IsCacheable(AgentStep(AgentType.Colorist));
        new StepCachePolicy(RawConfig(Mode(Colorist, "Gate")), valid)
            .IsCacheable(AgentStep(AgentType.Colorist));

        absent.Entries.Should().BeEmpty("an absent key is not a mistake");
        valid.Entries.Should().BeEmpty("a recognised value is not a mistake");
    }

    private static IConfigurationRoot RawConfig(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    private sealed class RecordingLogger : ILogger<StepCachePolicy>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    // ---------------------------------------------------------------------
    // The extraction: DecisionGateAgentConfig is the one AgentType -> name
    // mapping, and null means "not a gate site" rather than a guessed name.
    // ---------------------------------------------------------------------

    [Fact]
    public void DecisionGateAgentConfig_maps_exactly_the_two_gate_sites()
    {
        DecisionGateAgentConfig.ConfigNameFor(AgentType.Colorist).Should().Be("Colorist");
        DecisionGateAgentConfig.ConfigNameFor(AgentType.MusicSupervisor).Should().Be("MusicSupervisor");
    }

    [Theory]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.DirectorAgent)]
    [InlineData(AgentType.ReviewAgent)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.MotionGraphicsPlanner)]
    [InlineData(AgentType.ColorGradeDirector)]
    [InlineData(AgentType.SoundDesigner)]
    [InlineData(AgentType.Custom)]
    [InlineData(AgentType.ExtractTransform)]
    public void DecisionGateAgentConfig_returns_null_for_a_non_gate_agent_type(AgentType agentType)
    {
        DecisionGateAgentConfig.ConfigNameFor(agentType).Should().BeNull(
            because: "the policy must be able to ask \"is this even a gate site?\" without guessing a name");
    }
}
