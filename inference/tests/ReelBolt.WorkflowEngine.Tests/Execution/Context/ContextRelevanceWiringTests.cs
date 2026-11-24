using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Execution.Context;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Context;

/// <summary>
/// Proves the DI half of the context-relevance feature against the WorkflowEngine's REAL
/// registrations (<see cref="WorkflowEngineServiceCollectionExtensions.AddWorkflowEngineServices"/>,
/// the exact method Program.cs calls) — the half no amount of unit testing on a hand-built options
/// instance can cover.
///
/// <para>
/// Two things have to be true for the feature to be reachable in production at all: the
/// <c>WorkflowEngine:ContextBudget:Relevance</c> section must bind onto the budget options, and the
/// resolved <see cref="IContextRelevanceSelector"/> must end up attached to that same instance —
/// the latter surviving section binding, which is the one real risk of carrying a service on an
/// options object. Both are asserted here, plus the default-off contract.
/// </para>
/// </summary>
public class ContextRelevanceWiringTests
{
    private static CompositionRootHarness BuildEngine(IDictionary<string, string?>? extraConfig = null) =>
        CompositionRootHarness.Build(
            "workflow-engine-relevance",
            (services, configuration) => services.AddWorkflowEngineServices(configuration),
            extraConfig);

    [Fact]
    public void The_relevance_section_binds_onto_the_budget_options_and_defaults_to_disabled()
    {
        using CompositionRootHarness root = BuildEngine();

        AgentInputBudgetOptions options = root.Services
            .GetRequiredService<IOptions<AgentInputBudgetOptions>>()
            .Value;

        options.Relevance.Should().NotBeNull();
        options.Relevance.Enabled.Should().BeFalse("context relevance is opt-in and off by default");
        options.Relevance.ProviderId.Should().BeNull();
        options.Relevance.MaxCandidates.Should().Be(12);
        options.Relevance.MaxStateChars.Should().Be(4_000);
    }

    [Fact]
    public void The_relevance_section_is_bound_at_WorkflowEngine_ContextBudget_Relevance()
    {
        using CompositionRootHarness root = BuildEngine(new Dictionary<string, string?>
        {
            ["WorkflowEngine:ContextBudget:Relevance:Enabled"] = "true",
            ["WorkflowEngine:ContextBudget:Relevance:AcceptAt"] = "0.75",
            ["WorkflowEngine:ContextBudget:Relevance:MaxCandidates"] = "5",
            ["WorkflowEngine:ContextBudget:Relevance:MaxStateChars"] = "2048",
            ["WorkflowEngine:ContextBudget:Relevance:ProviderId"] = "77777777-7777-7777-7777-777777777777"
        });

        ContextRelevanceOptions options = root.Services
            .GetRequiredService<IOptions<AgentInputBudgetOptions>>()
            .Value
            .Relevance;

        options.Enabled.Should().BeTrue();
        options.AcceptAt.Should().Be(0.75);
        options.MaxCandidates.Should().Be(5);
        options.MaxStateChars.Should().Be(2_048);
        options.ProviderId.Should().Be(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    }

    [Fact]
    public void The_selector_is_attached_to_the_same_budget_options_every_execution_context_receives()
    {
        using CompositionRootHarness root = BuildEngine(new Dictionary<string, string?>
        {
            ["WorkflowEngine:ContextBudget:Relevance:Enabled"] = "true"
        });

        using IServiceScope scope = root.Services.CreateScope();

        AgentInputBudgetOptions options = scope.ServiceProvider
            .GetRequiredService<IOptions<AgentInputBudgetOptions>>()
            .Value;

        // Section binding must not have clobbered it: the selector is internal-set precisely so the
        // configuration binder never sees a bindable property, and it is attached post-configure.
        options.RelevanceSelector.Should().NotBeNull();
        options.RelevanceSelector.Should().BeOfType<ContextRelevanceSelector>();
        options.RelevanceSelector.Should().BeSameAs(root.Services.GetRequiredService<IContextRelevanceSelector>());
    }
}
