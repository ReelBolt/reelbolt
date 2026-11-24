using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// T-012: a run started by an execute route that classified under the guard's lock carries the bucket it was
/// admitted under to the engine; every other start (human REST, retries, revisions) carries null.
/// </summary>
public class WorkflowExecutionServiceAdmittedBucketTests
{
    private sealed class Rig
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public Guid User { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public List<WorkflowExecutionRequested> Published { get; } = new();
        public WorkflowExecutionService Service { get; }

        public Rig(bool publishFails = false)
        {
            Db.Projects.Add(new Project { Id = ProjectId, Name = "P", OwnerId = User, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = WorkflowId, Name = "W", ProjectId = ProjectId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.SaveChanges();
            var publish = new Mock<IPublishEndpoint>();
            publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecutionRequested, CancellationToken>((m, _) => Published.Add(m))
                .Returns(publishFails ? Task.FromException(new InvalidOperationException("broker unreachable")) : Task.CompletedTask);
            Service = new WorkflowExecutionService(Db, publish.Object, NullLogger<WorkflowExecutionService>.Instance);
        }

        public async Task<Guid?> StartAsync(ExecutionInitiator initiator, Guid? workflowId = null) =>
            (await Service.ExecuteAsync(ProjectId, workflowId ?? WorkflowId, User, null, initiator, CancellationToken.None)).Id;
    }

    [Theory]
    [InlineData(RunRateBucket.Free, "free")]
    [InlineData(RunRateBucket.Paid, "paid")]
    public async Task A_run_started_under_the_classify_overload_carries_its_admitted_bucket(RunRateBucket bucket, string wire)
    {
        var rig = new Rig();
        var guard = new AssistantExecutionGuard(rig.Db, new ConfigurationBuilder().Build());

        RunRateDecision decision = await guard.TryAcquireAsync(rig.User, rig.ProjectId, () => Task.FromResult(bucket),
            RunRateEvent.ExecuteSource, () => rig.StartAsync(ExecutionInitiator.Assistant), CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        rig.Published.Should().ContainSingle().Which.AdmittedRunRateBucket.Should().Be(wire);
        // T-015: the row carries it too, so the engine's re-check survives a retry or a recovery re-publish.
        (await rig.Db.WorkflowExecutions.AsNoTracking().SingleAsync()).AdmittedRunRateBucket.Should().Be(wire);
    }

    [Fact]
    public async Task A_run_started_outside_the_guard_carries_no_admitted_bucket()
    {
        var rig = new Rig();

        await rig.StartAsync(ExecutionInitiator.Human);

        rig.Published.Should().ContainSingle().Which.AdmittedRunRateBucket.Should().BeNull();
        (await rig.Db.WorkflowExecutions.AsNoTracking().SingleAsync()).AdmittedRunRateBucket.Should().BeNull();
    }

    [Fact]
    public async Task A_run_started_under_the_fixed_bucket_overload_carries_no_admitted_bucket()
    {
        var rig = new Rig();
        var guard = new AssistantExecutionGuard(rig.Db, new ConfigurationBuilder().Build());

        await guard.TryAcquireAsync(rig.User, rig.ProjectId, RunRateBucket.Paid, RunRateEvent.RevisionSource,
            () => rig.StartAsync(ExecutionInitiator.Human), CancellationToken.None);

        rig.Published.Should().ContainSingle().Which.AdmittedRunRateBucket.Should().BeNull();
    }

    /// <summary>S-11 (round 2): the Queued row is committed and the recovery sweeper re-publishes it, so a publish
    /// failure after that point must not uncount the run.</summary>
    [Fact]
    public async Task A_publish_failure_after_the_execution_row_is_committed_keeps_the_run_counted()
    {
        var rig = new Rig(publishFails: true);
        var guard = new AssistantExecutionGuard(rig.Db, new ConfigurationBuilder().Build());

        Func<Task> act = () => guard.TryAcquireAsync(rig.User, rig.ProjectId, () => Task.FromResult(RunRateBucket.Paid),
            RunRateEvent.ExecuteSource, () => rig.StartAsync(ExecutionInitiator.Assistant), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        WorkflowExecution row = await rig.Db.WorkflowExecutions.AsNoTracking().SingleAsync();
        RunRateEvent counted = await rig.Db.RunRateEvents.AsNoTracking().SingleAsync();
        counted.ExecutionId.Should().Be(row.Id, "the sweeper will run this row, so its event stays and points at it");
    }

    /// <summary>S-11 (round 2): a failure before any execution row is written started nothing, so nothing is counted.</summary>
    [Fact]
    public async Task A_failure_before_the_execution_row_is_written_leaves_no_event()
    {
        var rig = new Rig(publishFails: true);
        var guard = new AssistantExecutionGuard(rig.Db, new ConfigurationBuilder().Build());

        Func<Task> act = () => guard.TryAcquireAsync(rig.User, rig.ProjectId, () => Task.FromResult(RunRateBucket.Paid),
            RunRateEvent.ExecuteSource, () => rig.StartAsync(ExecutionInitiator.Assistant, Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
        (await rig.Db.WorkflowExecutions.AsNoTracking().CountAsync()).Should().Be(0);
        (await rig.Db.RunRateEvents.AsNoTracking().CountAsync()).Should().Be(0, "no row was committed, so no run will ever start");
    }
}
