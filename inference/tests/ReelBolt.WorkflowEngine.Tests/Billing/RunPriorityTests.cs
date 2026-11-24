using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B8c: the plan-driven run priority.
///
/// <para>
/// These tests pin the DECISION — which plan gets which class, and whether a run is published with a
/// priority at all — and they pin it against the SHIPPED plan catalog rather than a hand-written
/// number, because the whole point of this WP is that the plan catalog is the only place the value is
/// authored. What they cannot observe is the AMQP basic property itself (MassTransit stores it
/// internally and the test seam here is a mock endpoint); that half is proven against a real broker
/// in <c>RunPriorityQueueIntegrationTests</c>, which reads the property off the wire and shows the
/// queue's <c>x-max-priority</c> declaration is what makes it order anything.
/// </para>
/// </summary>
public class RunPriorityTests
{
    // ---- the catalog is the source -------------------------------------------------------------

    [Fact]
    public void The_shipped_catalog_grants_priority_to_the_paid_plans_only()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();

        // The four values the whole feature is built on: Trial and Creator are standard, Pro and Team
        // bought "priority rendering", Enterprise is dedicated. If a catalog edit changes one of
        // these, this test is the place that says so out loud rather than a queue silently
        // re-ordering itself.
        RunPriority.Of(PlanEntitlements.ToSet(plans["trial"])).Should().Be(RunPriority.Standard);
        RunPriority.Of(PlanEntitlements.ToSet(plans["creator"])).Should().Be(RunPriority.Standard);
        RunPriority.Of(PlanEntitlements.ToSet(plans["pro"])).Should().Be(1);
        RunPriority.Of(PlanEntitlements.ToSet(plans["team"])).Should().Be(1);
        RunPriority.Of(PlanEntitlements.ToSet(plans["enterprise"])).Should().Be(2);
    }

    [Fact]
    public void A_pro_plan_run_is_stamped_and_a_trial_plan_run_is_not()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();

        RunPriority.AmqpPriorityToStamp(RunPriority.Of(PlanEntitlements.ToSet(plans["pro"])))
            .Should().Be((byte)1);
        RunPriority.AmqpPriorityToStamp(RunPriority.Of(PlanEntitlements.ToSet(plans["team"])))
            .Should().Be((byte)1);

        // null, not 0: a trial run must keep the plain publish it had before B8c, so nothing about
        // its wire shape depends on the priority feature existing.
        RunPriority.AmqpPriorityToStamp(RunPriority.Of(PlanEntitlements.ToSet(plans["trial"])))
            .Should().BeNull();
        RunPriority.AmqpPriorityToStamp(RunPriority.Of(PlanEntitlements.ToSet(plans["creator"])))
            .Should().BeNull();
    }

    // ---- the scale -----------------------------------------------------------------------------

    [Fact]
    public void Self_host_is_standard_so_a_default_deployment_publishes_exactly_as_before()
    {
        RunPriority.Of(OrganizationEntitlementSet.Unlimited).Should().Be(RunPriority.Standard);
        RunPriority.AmqpPriorityToStamp(RunPriority.Of(OrganizationEntitlementSet.Unlimited)).Should().BeNull();
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(9999, RunPriority.Max)]
    public void The_class_is_clamped_into_the_byte_the_transport_can_carry(int authored, int expected)
    {
        // A catalog edit is data, and data must not be able to produce a value the publish path
        // cannot carry: AMQP priority is one byte, so anything above it saturates rather than
        // throwing somewhere deep in the transport.
        OrganizationEntitlementSet set = new() { Tier = "x", Priority = authored };

        RunPriority.Of(set).Should().Be(expected);
        RunPriority.ToAmqp(RunPriority.Of(set)).Should().Be((byte)expected);
    }

    // ---- the entitlement read ------------------------------------------------------------------

    [Fact]
    public async Task The_class_comes_from_the_orgs_entitling_subscription()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();
        Guid proOrg = Guid.NewGuid();
        Guid trialOrg = Guid.NewGuid();
        FakePlans source = new() { Subscribed = new Dictionary<Guid, Plan> { [proOrg] = plans["pro"] }, Fallback = plans["trial"] };
        PlanBackedOrganizationEntitlements entitlements = BuildEntitlements(source);

        (await RunPriority.OfAsync(entitlements, proOrg)).Should().Be(1);
        (await RunPriority.OfAsync(entitlements, trialOrg)).Should().Be(RunPriority.Standard);
    }

    [Fact]
    public async Task Billing_disabled_is_standard_for_every_org()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();
        FakePlans source = new() { Fallback = plans["pro"] };
        PlanBackedOrganizationEntitlements entitlements =
            new(source, Options.Create(new BillingOptions { Enabled = false }), new TenancyOptions());

        (await RunPriority.OfAsync(entitlements, Guid.NewGuid())).Should().Be(RunPriority.Standard);
    }

    // ---- the publish path ----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_publishes_a_pro_run_through_the_priority_overload()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();
        Guid orgId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        (Guid projectId, Guid workflowId) = SeedProject(db, orgId);
        Mock<IPublishEndpoint> endpoint = new();
        WorkflowExecutionService service = new(
            db, endpoint.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance,
            planGate: null,
            entitlements: BuildEntitlements(new FakePlans
            {
                Subscribed = new Dictionary<Guid, Plan> { [orgId] = plans["pro"] },
                Fallback = plans["trial"]
            }));

        await service.ExecuteAsync(projectId, workflowId, Guid.NewGuid(), null, ExecutionInitiator.Human, CancellationToken.None);

        // The priority-carrying overload, exactly once...
        endpoint.Verify(
            e => e.Publish(
                It.IsAny<WorkflowExecutionRequested>(),
                It.IsAny<IPipe<PublishContext<WorkflowExecutionRequested>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // ...and NOT the plain one.
        endpoint.Verify(e => e.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_publishes_a_trial_run_plainly()
    {
        Dictionary<string, Plan> plans = LoadShippedCatalog();
        Guid orgId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        (Guid projectId, Guid workflowId) = SeedProject(db, orgId);
        Mock<IPublishEndpoint> endpoint = new();
        WorkflowExecutionService service = new(
            db, endpoint.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance,
            planGate: null,
            entitlements: BuildEntitlements(new FakePlans { Fallback = plans["trial"] }));

        await service.ExecuteAsync(projectId, workflowId, Guid.NewGuid(), null, ExecutionInitiator.Human, CancellationToken.None);

        endpoint.Verify(e => e.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Once);
        endpoint.Verify(
            e => e.Publish(
                It.IsAny<WorkflowExecutionRequested>(),
                It.IsAny<IPipe<PublishContext<WorkflowExecutionRequested>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task An_unreadable_plan_still_queues_the_run_at_standard_priority()
    {
        Guid orgId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        (Guid projectId, Guid workflowId) = SeedProject(db, orgId);
        Mock<IPublishEndpoint> endpoint = new();
        Mock<IOrganizationEntitlements> broken = new();
        broken.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the plans table is unreachable"));
        WorkflowExecutionService service = new(
            db, endpoint.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance,
            planGate: null,
            entitlements: broken.Object);

        // A billing read must never be able to stop a run from being queued.
        await service.ExecuteAsync(projectId, workflowId, Guid.NewGuid(), null, ExecutionInitiator.Human, CancellationToken.None);

        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
        endpoint.Verify(e => e.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Dictionary<string, Plan> LoadShippedCatalog()
    {
        string path = PlanCatalogSeeder.DefaultPath;
        File.Exists(path).Should().BeTrue($"the shipped plan catalog should be next to the test assembly at {path}");

        return PlanCatalogSeeder.Load(File.ReadAllText(path)).ToDictionary(p => p.Key);
    }

    private static PlanBackedOrganizationEntitlements BuildEntitlements(IPlanSource plans)
        => new(plans, Options.Create(new BillingOptions { Enabled = true, EntitlementCacheSeconds = 0 }), new TenancyOptions());

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid ProjectId, Guid WorkflowId) SeedProject(InferenceApiDbContext db, Guid organizationId)
    {
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        db.Projects.Add(new Project
        {
            Id = projectId,
            OrganizationId = organizationId,
            Name = "B8c priority",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            ProjectId = projectId,
            Name = "priority-run",
            RequiresUserInput = false,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.SaveChanges();
        return (projectId, workflowId);
    }

    /// <summary>A plan source with a per-org subscription and one fallback ("trial") plan.</summary>
    private sealed class FakePlans : IPlanSource
    {
        public Dictionary<Guid, Plan> Subscribed { get; init; } = [];
        public Plan? Fallback { get; init; }

        public Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken)
            => Task.FromResult(Subscribed.TryGetValue(organizationId, out Plan? plan) ? plan : null);

        public Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(Fallback);
    }
}
