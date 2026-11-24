using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// A change request (or composition revision) runs a hidden CompositionReplay workflow whose steps the replay plan
/// decides. Retry is refused (REVISION_RETRY_NOT_SUPPORTED); Resume is refused too, and the recovery sweeper never
/// auto-resumes such a run, because a resume would copy the reused steps again and re-run the revised step.
/// </summary>
public class ChangeRequestResumeTests
{
    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    }

    private static (SqliteConnection, ServiceProvider, ExecutionRecoverySweeper) Build()
    {
        SqliteConnection conn = new("Data Source=:memory:;Foreign Keys=False");
        conn.Open();
        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton(new Mock<IWorkflowEventPublisher>().Object);
        services.AddSingleton(new Mock<IPlatformFailureHook>().Object);
        services.AddSingleton(new Mock<IPublishEndpoint>().Object);
        ServiceProvider sp = services.BuildServiceProvider();
        using (IServiceScope scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>().Database.EnsureCreated();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-a" }).Build();
        FakeTime time = new();
        IServiceScopeFactory factory = sp.GetRequiredService<IServiceScopeFactory>();
        ExecutionLeaseService leases = new(factory, config, NullLogger<ExecutionLeaseService>.Instance, time);
        return (conn, sp, new ExecutionRecoverySweeper(factory, leases, NullLogger<ExecutionRecoverySweeper>.Instance, time));
    }

    private static Guid AddLostRun(ServiceProvider sp, WorkflowDefinitionKind kind)
    {
        using WorkflowEngineDbContext db = sp.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        DateTime now = new FakeTime().GetUtcNow().UtcDateTime;
        Guid projectId = Guid.NewGuid();
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), Name = "wf", ProjectId = projectId, Kind = kind };
        db.WorkflowDefinitions.Add(wf);
        WorkflowExecution e = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = wf.Id,
            ProjectId = projectId,
            OrganizationId = Guid.NewGuid(),
            Status = ExecutionStatus.Running,
            CorrelationId = "c-" + Guid.NewGuid().ToString("N")[..6],
            StartedAt = now.AddMinutes(-20),
            QueuedAt = now.AddMinutes(-21),
            LeaseOwner = "engine-dead",
            LeaseExpiresAt = now.AddSeconds(-5)
        };
        db.WorkflowExecutions.Add(e);
        db.SaveChanges();
        WorkflowDefinition snap = new() { Id = Guid.NewGuid(), Name = "snapshot", ProjectId = projectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
        db.WorkflowDefinitions.Add(snap);
        db.Entry(snap).Property<Guid?>(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = e.Id;
        e.SnapshotDefinitionId = snap.Id;
        db.SaveChanges();
        return e.Id;
    }

    [Fact]
    public async Task A_lost_replay_run_is_failed_not_auto_resumed()
    {
        (SqliteConnection conn, ServiceProvider sp, ExecutionRecoverySweeper sweeper) = Build();
        using (conn)
        {
            Guid id = AddLostRun(sp, WorkflowDefinitionKind.CompositionReplay);
            (int failed, int republished) = await sweeper.SweepOnceAsync(CancellationToken.None);
            failed.Should().Be(1);
            republished.Should().Be(0);
            using WorkflowEngineDbContext db = sp.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            WorkflowExecution e = db.WorkflowExecutions.AsNoTracking().Single(x => x.Id == id);
            e.Status.Should().Be(ExecutionStatus.Failed);
            e.AutoResumeCount.Should().Be(0);
            e.PendingResume.Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_lost_ordinary_run_is_still_auto_resumed()
    {
        (SqliteConnection conn, ServiceProvider sp, ExecutionRecoverySweeper sweeper) = Build();
        using (conn)
        {
            Guid id = AddLostRun(sp, WorkflowDefinitionKind.Standard);
            (await sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(0);
            using WorkflowEngineDbContext db = sp.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            WorkflowExecution e = db.WorkflowExecutions.AsNoTracking().Single(x => x.Id == id);
            e.AutoResumeCount.Should().Be(1);
            e.PendingResume.Should().BeTrue();
        }
    }

    [Fact]
    public void Resume_refusal_is_a_plain_code_and_message()
    {
        ExecutionResumability.ReplayResumeCode.Should().Be("REVISION_RESUME_NOT_SUPPORTED");
        ExecutionResumability.ReplayResumeMessage.Should().Contain("again");
    }
}
