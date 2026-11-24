using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// WP A14, the engine's own half: <b>the WorkflowEngine's read-only view of the Inference API's
/// provider tables, and the run-time agent guard an already-stored workflow row still meets.</b>
///
/// <para>
/// The guard itself is covered by <c>AgentStepExecutorOrganizationScopeTests</c>; what is added here
/// is the case that suite does not have — an execution with <b>no</b> organization at all, which is
/// what a run created before the tenancy migration (or by any future caller that forgets to stamp it)
/// looks like. That case is the one where failing open would be invisible, so it is asserted directly
/// rather than left to the reasoning in the guard's comment.
/// </para>
///
/// <para>
/// The store half is uncovered anywhere: <c>InferenceProviderResolverTests</c> fakes the store out, and
/// the engine's own implementation reads the tables through its read-only, excluded-from-migrations
/// mapping — a different context, a different set of filters, and therefore a second chance to get the
/// scope wrong.
/// </para>
/// </summary>
public sealed class CrossTenantEngineTests : IDisposable
{
    private readonly WorkflowEngineDbContext _engineDb;
    private readonly Guid _orgA = Guid.NewGuid();
    private readonly Guid _orgB = Guid.NewGuid();

    public CrossTenantEngineTests()
    {
        _engineDb = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"cross-tenant-engine-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _engineDb.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // The engine's provider store
    // ---------------------------------------------------------------------------------------------------

    private ModelCapabilityView Provider(string name, Guid? organizationId, bool isDefault, bool isEnabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.OpenAICompatible,
        Capability = InferenceProviderCapability.Chat,
        OrganizationId = organizationId,
        Endpoint = "https://provider.example/v1",
        ModelName = "m",
        IsEnabled = isEnabled,
        IsDefault = isDefault,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task The_engine_store_returns_platform_rows_plus_only_the_executions_organization()
    {
        ModelCapabilityView platform = Provider("platform", organizationId: null, isDefault: true);
        ModelCapabilityView orgA = Provider("org-a", _orgA, isDefault: true);
        ModelCapabilityView orgB = Provider("org-b", _orgB, isDefault: true);
        ModelCapabilityView disabledOrgB = Provider("org-b-disabled", _orgB, isDefault: false, isEnabled: false);
        _engineDb.AddFlat(platform, orgA, orgB, disabledOrgB);
        await _engineDb.SaveChangesAsync();

        var store = new WorkflowEngineProviderStore(_engineDb);

        (await store.LoadEnabledAsync(_orgA, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform", "org-a");

        // An execution may never see another organization's row — and a disabled row is not a
        // configuration the engine falls back to either.
        (await store.LoadEnabledAsync(_orgB, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform", "org-b");

        // No execution scope means the platform-only view.
        (await store.LoadEnabledAsync(null, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform");
    }

    [Fact]
    public async Task The_engine_store_scopes_every_override_loader_to_the_executions_organization()
    {
        AgentDefinition agentA = CustomAgent(_orgA, null);
        AgentDefinition agentB = CustomAgent(_orgB, null);
        _engineDb.AgentDefinitions.AddRange(agentA, agentB);
        _engineDb.OrganizationAgentProviderOverrides.AddRange(
            new OrganizationAgentProviderOverride { OrganizationId = _orgA, AgentType = AgentType.Colorist },
            new OrganizationAgentProviderOverride { OrganizationId = _orgB, AgentType = AgentType.Colorist });
        await _engineDb.SaveChangesAsync();

        var store = new WorkflowEngineProviderStore(_engineDb);

        (await store.LoadAgentOverridesAsync(_orgA, CancellationToken.None)).Keys.Should().Equal(agentA.Id);
        (await store.LoadAgentOverridesAsync(_orgB, CancellationToken.None)).Keys.Should().Equal(agentB.Id);
        (await store.LoadAgentOverridesAsync(null, CancellationToken.None)).Should().BeEmpty();
        (await store.LoadOrgAgentOverridesAsync(_orgA, CancellationToken.None)).Should().ContainKey(AgentType.Colorist);
        (await store.LoadOrgAgentOverridesAsync(null, CancellationToken.None)).Should().BeEmpty();

        // An organization-scoped override stamped by a built-in agent type is still the ORGANIZATION's
        // row: the platform-wide built-in loader must not pick it up.
        (await store.LoadBuiltInAgentOverridesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static AgentDefinition CustomAgent(Guid organizationId, Guid? providerOverride) => new()
    {
        Id = Guid.NewGuid(),
        Name = "custom",
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.Custom,
        IsBuiltIn = false,
        OwnerId = Guid.NewGuid(),
        OrganizationId = organizationId,
        InferenceProviderId = providerOverride,
        CreatedAt = DateTime.UtcNow
    };

    // ---------------------------------------------------------------------------------------------------
    // The run-time agent guard
    // ---------------------------------------------------------------------------------------------------

    private static AgentStepExecutor CreateExecutor(IReelBoltAgent? agent)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        return new AgentStepExecutor(
            registry.Object,
            accessor.Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static StepExecutionContext CreateContext(
        AgentType agentType, bool isBuiltIn, Guid? agentOrganizationId, Guid executionOrganizationId)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "a",
                AgentType = agentType,
                IsBuiltIn = isBuiltIn,
                OrganizationId = agentOrganizationId
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = executionOrganizationId
            },
            Step = step,
            AllSteps = new List<WorkflowStep> { step },
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>
    /// The case the existing suite does not have: an execution whose organization is <see cref="Guid.Empty"/>
    /// — what a row written before the tenancy migration, or by a caller that forgot to stamp it, looks
    /// like (<c>WorkflowExecution.OrganizationId</c> is a non-nullable column, so "unscoped" is Empty
    /// rather than null).
    ///
    /// <para>
    /// The registry resolves NOTHING for this agent type, so the two possible outcomes are
    /// distinguishable by status alone: <c>Skipped</c> is what the executor does for an agent it cannot
    /// find, and <c>Failed</c> is the organization refusal. An unscoped run must not be able to reach an
    /// owned agent, and it must not be able to masquerade as a successful run that simply did nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_execution_with_no_organization_fails_the_step_rather_than_skipping_it()
    {
        Guid agentOrganization = Guid.NewGuid();
        AgentStepExecutor executor = CreateExecutor(agent: null);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Custom, isBuiltIn: false, agentOrganization, executionOrganizationId: Guid.Empty));

        result.Status.Should().Be(StepStatus.Failed,
            "the guard sits BEFORE the registry lookup on purpose: an unresolvable agent is SKIPPED, "
            + "and a silently skipped step would let an unscoped run look successful while doing "
            + "nothing");
        result.ErrorDetails.Should().Contain(agentOrganization.ToString());
    }

    [Fact]
    public async Task A_custom_agent_of_another_organization_fails_and_never_reaches_the_registry()
    {
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.AgentType).Returns(AgentType.Custom);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        AgentStepExecutor executor = CreateExecutor(agent.Object);

        Guid agentOrganization = Guid.NewGuid();
        Guid executionOrganization = Guid.NewGuid();

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Custom, isBuiltIn: false, agentOrganization, executionOrganization));

        result.Status.Should().Be(StepStatus.Failed);
        result.TokensUsed.Should().Be(0);
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The other direction, so the guard cannot be satisfied by refusing every custom agent: an agent
    /// whose organization matches the execution's is allowed through to the registry lookup.
    /// </summary>
    [Fact]
    public async Task A_custom_agent_of_the_executions_own_organization_is_not_refused_by_the_guard()
    {
        Guid organization = Guid.NewGuid();
        AgentStepExecutor executor = CreateExecutor(agent: null);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Custom, isBuiltIn: false, organization, organization));

        result.Status.Should().Be(StepStatus.Skipped,
            "the step is skipped because no AgentType.Custom implementation is registered in this test, "
            + "which is the point: the guard let it through");
    }
}
