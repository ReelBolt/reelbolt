#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// QA-side evidence for T-502's carried-in defect #1 — "explicit <see cref="DecisionGateMode"/>
/// handling with an unrecognised value inert".
///
/// <para>
/// The <b>value</b> half is closed and already pinned by
/// <see cref="DecisionGateTests.DecideAsync_WithUnrecognisedMode_PerformsZeroOutboundCalls"/>. What
/// this file added was the half that defect's own documentation claims but did not hold: the enum's
/// XML comment names "a bad appsettings binding" as one of the ways an unrecognised VALUE arises
/// and promises it means <i>do nothing</i>. It did not. A non-numeric, non-member string never
/// became an unrecognised value at all — the configuration binder threw
/// <see cref="InvalidOperationException"/> before the gate was ever reached, so the promise was
/// unreachable for that vector.
/// </para>
///
/// <para>
/// <b>T-014 follows this file rather than contradicting it.</b> The throw was recorded here as a
/// defect the gate's "never fails a step" contract did not cover, and T-014 removed it: the mode is
/// now read through <see cref="DecisionGateAgentConfig.ReadMode"/>, so a typo fails to
/// <see cref="DecisionGateMode.Off"/> and warns. The assertion in §1 below is therefore inverted to
/// pin the fixed behaviour, and the enum's documentation promise now holds for this vector too.
/// </para>
/// </summary>
public sealed class QaCarriedInDefectTests
{
    private const string Colorist = "Colorist";
    private const string ModeKey = "Agents:Colorist:DecisionGate:Mode";

    private static IConfiguration Config(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static DecisionGateConfig ResolveFromBoundString(string value) =>
        AgentStepExecutor.ResolveDecisionGateConfig(Config(ModeKey, value), Colorist);

    // ---------------------------------------------------------------------
    // 1. A typo'd Mode STRING is inert — the binder no longer throws.
    //
    // FLIPPED BY T-014. Until that commit this theory asserted
    // `Should().Throw<InvalidOperationException>()`, i.e. it pinned the carried-in defect itself.
    // T-014's whole subject is that this throw was NOT the safe direction: the read sits outside
    // RunPreAgentGateAsync's try block, so it escaped AgentStepExecutor.ExecuteAsync and
    // WorkflowExecutorService — which excludes InvalidOperationException from retry — marked the
    // step Failed and aborted the execution. The value now fails to Off instead. The assertion is
    // inverted rather than deleted so the flip is visible in this file's history.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Bogus")]
    [InlineData("Offf")]
    [InlineData("Enabled")]
    [InlineData("Enabled ")]
    [InlineData("true")]
    public void An_unrecognised_Mode_string_reads_as_Off_instead_of_throwing(string value)
    {
        Action act = () => ResolveFromBoundString(value);

        act.Should().NotThrow(
            "a typo in one appsettings key must be inert — a throw here left the step executor and killed the whole run (T-014)");

        ResolveFromBoundString(value).Mode.Should().Be(DecisionGateMode.Off,
            "fail to Off, never to Shadow and never to Gate: this value decides whether an agent's real output is used at all");
    }

    [Fact]
    public void An_empty_Mode_string_is_the_fail_to_Off_outcome()
    {
        // The one non-member string that DOES read as Off — an empty value binds to null, which the
        // resolver's `?? defaults.Mode` turns into Off.
        ResolveFromBoundString(string.Empty).Mode.Should().Be(DecisionGateMode.Off);
    }

    // ---------------------------------------------------------------------
    // 2. A numeric or case-variant string IS recognised — sometimes not as intended.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Shadow", DecisionGateMode.Shadow)]
    [InlineData("shadow", DecisionGateMode.Shadow)]
    [InlineData("GATE", DecisionGateMode.Gate)]
    [InlineData("1", DecisionGateMode.Shadow)]
    [InlineData("2", DecisionGateMode.Gate)]
    [InlineData("0", DecisionGateMode.Off)]
    public void A_case_variant_or_numeric_Mode_binds_by_ordinal_position(string value, DecisionGateMode expected)
    {
        // Pinned, not endorsed: `Mode: "2"` silently ENABLES the gate, and a lowercase `gate` enables
        // it too. Neither is a typo the operator will notice from the config file alone.
        ResolveFromBoundString(value).Mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task A_out_of_range_numeric_Mode_binds_to_an_undefined_value_and_that_value_is_inert(string value)
    {
        // The only configuration strings that really do produce an unrecognised VALUE are out-of-range
        // NUMBERS — including a negative one, which the binder accepts as numeric rather than
        // rejecting as unparseable — and for those the exhaustive switch does exactly what its
        // documentation promises: nothing.
        DecisionGateConfig config = ResolveFromBoundString(value);

        config.Mode.Should().NotBe(DecisionGateMode.Off);
        config.Mode.Should().NotBe(DecisionGateMode.Shadow);
        config.Mode.Should().NotBe(DecisionGateMode.Gate);

        // A scope factory that throws if it is ever asked for one: recording an observation is the
        // only thing that needs a scope, so this is a second, independent tripwire on "no work done".
        var resolver = new Mock<IInferenceProviderResolver>();
        var factory = new Mock<IDecisionClientFactory>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(sf => sf.CreateScope()).Throws(new InvalidOperationException("the unchecked path created a scope"));

        var sut = new DecisionGate(
            resolver.Object, factory.Object, scopeFactory.Object, new Mock<ILogger<DecisionGate>>().Object);

        DecisionGateOutcome outcome = await sut.DecideAsync(
            new DecisionGateRequest(
                Guid.NewGuid(), 7, AgentType.Colorist, "Colorist.step",
                new DecisionState("descriptor"),
                [new DecisionGateQuestion("Colorist.Look", "Look", DecisionKind.Choice, "which look", ["Warm", "Cool"], "Warm")],
                config),
            CancellationToken.None);

        outcome.HasDecision.Should().BeFalse();
        outcome.Accepted.Should().BeFalse();
        outcome.Result.Should().BeNull();
        resolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        factory.Verify(f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // 3. The same value is NOT inert to the step-cache rule.
    // ---------------------------------------------------------------------

    [Fact]
    public void An_unrecognised_Mode_value_makes_the_step_non_cacheable()
    {
        // `HasNoActiveDecisionGate` asks "is the mode Off?" — and an undefined value is not Off, so
        // the step is withheld from the cache. Conservative and safe (the alternative would be to
        // cache a step whose configuration nobody can interpret), but it does mean an unrecognised
        // value has one observable effect rather than being inert end to end.
        var policy = new StepCachePolicy(Config(ModeKey, "99"));

        var step = new WorkflowStep
        {
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.Colorist }
        };

        policy.IsCacheable(step).Should().BeFalse();
    }

    [Fact]
    public void The_two_documented_cache_residuals_are_reachable_and_pinned_by_their_own_tests()
    {
        // Not a re-assertion of StepCachePolicyDecisionGateTests (which owns these); a pointer that
        // they exist, kept here so the T-502 report's "residual holes still hold" claim is traceable
        // to a test name rather than to prose.
        var withMode = new StepCachePolicy(Config(ModeKey, "Gate"));

        var alwaysStep = new WorkflowStep
        {
            StepType = StepType.Agent,
            CacheMode = StepCacheMode.Always,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.Colorist }
        };

        var defaultStep = new WorkflowStep
        {
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.Colorist }
        };

        // Residual 1: CacheMode = Always still forces caching on a gate-configured step.
        withMode.IsCacheable(alwaysStep).Should().BeTrue();

        // Residual 2: a policy with no IConfiguration reads the absent key as Off, so it is cacheable.
        new StepCachePolicy().IsCacheable(defaultStep).Should().BeTrue();

        // The rule itself, for contrast.
        withMode.IsCacheable(defaultStep).Should().BeFalse();
    }
}