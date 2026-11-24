#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Editing.Integration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// The orphan reaper for hidden render workflows: every render attempt that reached the controller
/// before the transactional start persisted a <c>Render: &lt;name&gt;</c> workflow with no execution
/// (audit report §8). Only that shape is deleted: <c>Kind = TimelineRender</c>, no execution, older
/// than the age floor.
/// </summary>
public sealed class TimelineRenderOrphanReaperTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Selects_only_old_TimelineRender_workflows_without_an_execution()
    {
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid project = Guid.NewGuid();
        WorkflowDefinition orphan = Workflow(project, WorkflowDefinitionKind.TimelineRender, Now.AddHours(-2));
        WorkflowDefinition young = Workflow(project, WorkflowDefinitionKind.TimelineRender, Now.AddMinutes(-30));
        WorkflowDefinition ran = Workflow(project, WorkflowDefinitionKind.TimelineRender, Now.AddDays(-3));
        WorkflowDefinition standard = Workflow(project, WorkflowDefinitionKind.Standard, Now.AddDays(-3));
        db.WorkflowDefinitions.AddRange(orphan, young, ran, standard);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = ran.Id,
            ProjectId = project,
            Status = ExecutionStatus.Passed,
            CorrelationId = "c"
        });
        await db.SaveChangesAsync();

        List<Guid> selected = await TimelineRenderOrphanReaper
            .SelectOrphans(db.WorkflowDefinitions, Now - TimeSpan.FromHours(1))
            .Select(w => w.Id)
            .ToListAsync();

        selected.Should().Equal(orphan.Id);
    }

    [Fact]
    public async Task Selects_old_AudioCleanup_workflows_without_an_execution_after_the_one_hour_floor()
    {
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid project = Guid.NewGuid();
        WorkflowDefinition orphan = Workflow(project, WorkflowDefinitionKind.AudioCleanup, Now.AddHours(-2));
        WorkflowDefinition young = Workflow(project, WorkflowDefinitionKind.AudioCleanup, Now.AddMinutes(-30));
        WorkflowDefinition ran = Workflow(project, WorkflowDefinitionKind.AudioCleanup, Now.AddDays(-3));
        db.WorkflowDefinitions.AddRange(orphan, young, ran);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = ran.Id,
            ProjectId = project,
            Status = ExecutionStatus.Passed,
            CorrelationId = "c"
        });
        await db.SaveChangesAsync();

        List<Guid> selected = await TimelineRenderOrphanReaper
            .SelectOrphans(db.WorkflowDefinitions, Now - TimeSpan.FromHours(1))
            .Select(w => w.Id)
            .ToListAsync();

        selected.Should().Equal(orphan.Id);
    }

    [Fact]
    public async Task A_disabled_reaper_never_touches_the_database()
    {
        ServiceCollection services = new();
        bool resolved = false;
        services.AddScoped<InferenceApiDbContext>(_ =>
        {
            resolved = true;
            throw new InvalidOperationException("the disabled reaper must not open a context");
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        TimelineRenderOrphanReaper reaper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TimelineRenderOrphanReaperOptions { Enabled = false }),
            NullLogger<TimelineRenderOrphanReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);
        await (reaper.ExecuteTask ?? Task.CompletedTask);
        await reaper.StopAsync(CancellationToken.None);

        resolved.Should().BeFalse();
    }

    [Fact]
    public async Task Deletes_orphans_and_their_steps_on_a_real_Postgres_and_is_a_no_op_the_second_time()
    {
        await RenderStartRealPostgresTests.RealPostgres.RunAsync(async pg =>
        {
            RenderStartRealPostgresTests.Seeded seeded = await pg.SeedAsync(timelineName: "doc");
            Guid agent;
            await using (InferenceApiDbContext db = new(pg.ApiOptions))
            {
                agent = await db.AgentDefinitions.Select(a => a.Id).FirstAsync();
            }

            DateTime now = DateTime.UtcNow;
            Guid orphan = await InsertAsync(pg, seeded.ProjectId, agent, WorkflowDefinitionKind.TimelineRender, now.AddHours(-2), withExecution: false);
            Guid young = await InsertAsync(pg, seeded.ProjectId, agent, WorkflowDefinitionKind.TimelineRender, now.AddMinutes(-10), withExecution: false);
            Guid ran = await InsertAsync(pg, seeded.ProjectId, agent, WorkflowDefinitionKind.TimelineRender, now.AddDays(-2), withExecution: true);
            Guid standard = await InsertAsync(pg, seeded.ProjectId, agent, WorkflowDefinitionKind.Standard, now.AddDays(-2), withExecution: false);

            int first;
            int second;
            await using (InferenceApiDbContext db = new(pg.ApiOptions))
            {
                first = await TimelineRenderOrphanReaper.ReapOnceAsync(db, now, TimeSpan.FromHours(1), CancellationToken.None);
            }

            await using (InferenceApiDbContext db = new(pg.ApiOptions))
            {
                second = await TimelineRenderOrphanReaper.ReapOnceAsync(db, now, TimeSpan.FromHours(1), CancellationToken.None);
            }

            first.Should().Be(1);
            second.Should().Be(0);
            (await pg.ScalarAsync($"SELECT count(*) FROM workflow_definitions WHERE id = '{orphan}'")).Should().Be(0);
            (await pg.ScalarAsync($"SELECT count(*) FROM workflow_steps WHERE workflow_definition_id = '{orphan}'")).Should().Be(0);
            (await pg.ScalarAsync($"SELECT count(*) FROM workflow_definitions WHERE id IN ('{young}','{ran}','{standard}')")).Should().Be(3);
            (await pg.ScalarAsync("SELECT count(*) FROM workflow_steps")).Should().Be(3);
        });
    }

    private static WorkflowDefinition Workflow(Guid project, WorkflowDefinitionKind kind, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = project,
        Name = "Render: doc",
        Kind = kind,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private static async Task<Guid> InsertAsync(
        RenderStartRealPostgresTests.RealPostgres pg, Guid project, Guid agent, WorkflowDefinitionKind kind, DateTime createdAt, bool withExecution)
    {
        await using InferenceApiDbContext db = new(pg.ApiOptions);
        WorkflowDefinition workflow = Workflow(project, kind, createdAt);
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent,
            StepOrder = 1,
            Label = "Render Master",
            StepType = StepType.TimelineRender,
            CacheMode = StepCacheMode.Never
        });
        db.WorkflowDefinitions.Add(workflow);
        if (withExecution)
        {
            Guid org = await db.Projects.Where(p => p.Id == project).Select(p => p.OrganizationId).FirstAsync();
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                ProjectId = project,
                OrganizationId = org,
                Status = ExecutionStatus.Passed,
                CorrelationId = "c",
                QueuedAt = createdAt
            });
        }

        await db.SaveChangesAsync();
        return workflow.Id;
    }
}
