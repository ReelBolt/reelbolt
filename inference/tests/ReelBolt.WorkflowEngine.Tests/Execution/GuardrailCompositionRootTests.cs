using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Guardrails;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// The composition-root half of the guardrail and decision surface: everything in it lands through
/// OPTIONAL, NULLABLE trailing constructor parameters, which is what lets the existing
/// three-argument construction sites (<c>ToolScopingDriftGuardTests</c> above all) keep compiling
/// unchanged. The cost of that convenience is that a container resolving none of them falls back to
/// <c>null</c> — and the feature then looks wired while doing exactly nothing.
/// </summary>
/// <remarks>
/// <para>
/// These tests resolve the REAL registrations — <c>AddWorkflowEngineServices</c>, the method
/// <c>Program.cs</c> calls — not a hand-built <see cref="ServiceCollection"/>. That distinction is
/// the whole point: <c>AgentToolProviderScreeningTests.The_dependency_injection_container_injects_the_screener_into_the_new_constructor</c>
/// already proves the mechanism works WHEN a screener is registered, but it registers one itself, so
/// it cannot notice a production registration that never reaches the provider.
/// </para>
/// <para>
/// The same technique <c>RoomDecisionModeTests</c> uses for the three room executors, applied to the
/// two surfaces it does not cover: <see cref="AgentToolProvider"/> (tool-call screening) and
/// <see cref="VideoAnalyzeStepExecutor"/> (the derush pre-pass and input screening).
/// </para>
/// </remarks>
public sealed class GuardrailCompositionRootTests
{
    private static CompositionRootHarness BuildEngine() =>
        CompositionRootHarness.Build(
            "workflow-engine-guardrail-wiring",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

    [Fact]
    public void The_real_registrations_inject_both_optional_guardrail_dependencies_into_the_agent_tool_provider()
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<IAgentToolProvider>();
        provider.Should().BeOfType<AgentToolProvider>();

        // A null screener here is the silent-unscreened-tool-surface failure mode: every call would
        // behave exactly as it did before guardrails existed, with nothing at all to say so.
        Field("_guardrailScreener", typeof(AgentToolProvider)).GetValue(provider).Should().NotBeNull(
            "AgentToolProvider must receive the IGuardrailScreener singleton; an optional nullable " +
            "parameter the container fails to resolve silently disables screening");

        Field("_guardrailStateMaxChars", typeof(AgentToolProvider)).GetValue(provider).Should().Be(
            GuardrailConfig.DefaultStateMaxChars,
            "the arguments-summary bound must come from the bound IOptions<GuardrailConfig> — the " +
            "shipped configuration sets StateMaxChars to 4000 — not from the constructor's fallback");
    }

    /// <summary>
    /// The end-to-end form of the same claim, and the one a non-null field cannot fake: through the
    /// real container, every tool an agent is handed that belongs to the screened set IS wrapped —
    /// and no tool outside that set is.
    /// </summary>
    [Theory]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.MotionGraphicsPlanner)]
    [InlineData(AgentType.MotionGraphicsDirector)]
    public void The_production_wiring_wraps_every_screened_tool_the_agent_is_granted(AgentType agentType)
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<IAgentToolProvider>();
        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = provider.GetTools(agentType);

        List<string> wrapped = tools
            .Where(t => t is AgentToolProvider.ScreenedAIFunction)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // What the provider SHOULD have wrapped: exactly the intersection of what this agent is
        // granted with the five screened names. If the container had missed the screener this list
        // would be non-empty while `wrapped` was empty — which is the failure being hunted.
        List<string> expected = tools
            .Select(t => t.Name)
            .Where(AgentToolProvider.ScreenedToolNames.Contains)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        expected.Should().NotBeEmpty(
            $"{agentType} is one of the agents holding the sandbox-write/render grant that the " +
            "documented MotionGraphicsPlanner prompt-injection tradeoff is bounded by");

        wrapped.Should().Equal(expected,
            "a screened tool that reached an agent unwrapped is invisible everywhere else");
    }

    [Fact]
    public void The_real_registrations_inject_the_decision_plumbing_into_the_video_analyze_executor()
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        var executor = scope.ServiceProvider.GetServices<IStepExecutor>()
            .OfType<VideoAnalyzeStepExecutor>()
            .Single();

        // Three optional nullable parameters, three silent no-ops if the container misses them: the
        // derush pre-pass declines to run without the memoizer or the client factory, and input
        // screening declines without the span screener.
        Field("_decisionMemoizer", typeof(VideoAnalyzeStepExecutor)).GetValue(executor).Should().NotBeNull(
            "the derush pre-pass cannot run — nor honour its per-execution memo contract — without IDecisionQuestionMemoizer");
        Field("_decisionClientFactory", typeof(VideoAnalyzeStepExecutor)).GetValue(executor).Should().NotBeNull(
            "DerushPrePass=Prior would be a permanent no-op without IDecisionClientFactory");
        Field("_inputSpanScreener", typeof(VideoAnalyzeStepExecutor)).GetValue(executor).Should().NotBeNull(
            "ScreenInstructionLikeSpans=true would be a permanent no-op without IInputSpanScreener");
    }

    [Fact]
    public void The_screener_services_resolve_to_their_shipped_implementations_and_the_memoizer_is_a_singleton()
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGuardrailScreener>().Should().BeOfType<GuardrailScreener>();
        scope.ServiceProvider.GetRequiredService<IInputSpanScreener>().Should().BeOfType<InputSpanScreener>();

        // The memoizer is deliberately a SINGLETON, keyed by execution id: a scoped registration
        // would live and die with the message scope, so a ReviewLoop iteration would re-ask — and
        // re-bill — the same unchanged per-item questions.
        IDecisionQuestionMemoizer first = scope.ServiceProvider.GetRequiredService<IDecisionQuestionMemoizer>();
        IDecisionQuestionMemoizer second = scope.ServiceProvider.GetRequiredService<IDecisionQuestionMemoizer>();
        first.Should().BeSameAs(second, "the memoizer must be one instance per process, not per scope");
    }

    private static FieldInfo Field(string name, Type declaring)
    {
        FieldInfo? field = declaring.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"{declaring.Name} must declare a private field '{name}'");
        return field!;
    }
}
