#nullable enable
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DashboardControllerTests
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InferenceApiDbContext, TestTenancy.MutableTenant> Tenants = new();

    // Seeding runs in a system scope; Get(...) then narrows the same context to the caller's organization,
    // so the assertions exercise the real tenant query filter rather than an unfiltered context.
    private static InferenceApiDbContext CreateContext()
    {
        var tenant = new TestTenancy.MutableTenant();
        var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        Tenants.Add(db, tenant);
        return db;
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static Project NewProject(Guid owner, string name) =>
        new() { Id = Guid.NewGuid(), Name = name, OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow.AddDays(-30) };

    private static async Task<DashboardOverviewResponse> Get(InferenceApiDbContext db, Guid user)
    {
        FakeCurrentUser caller = new(user);
        Tenants.TryGetValue(db, out TestTenancy.MutableTenant? tenant);
        tenant!.ActAs(user);
        DashboardController c = new(db, caller, TestTenancy.Access(db, caller));
        ActionResult<DashboardOverviewResponse> r = await c.Overview(CancellationToken.None);
        return r.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DashboardOverviewResponse>().Subject;
    }

    [Fact]
    public async Task Overview_IsEmpty_ForUserWithoutProjects()
    {
        using InferenceApiDbContext db = CreateContext();
        DashboardOverviewResponse res = await Get(db, Guid.NewGuid());
        res.Projects.Should().BeEmpty();
        res.Totals.Projects.Should().Be(0);
    }

    [Fact]
    public async Task Overview_NeverLeaksAnotherUsersData()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid(), other = Guid.NewGuid();
        Project mine = NewProject(me, "mine"), theirs = NewProject(other, "theirs");
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = theirs.Id, Name = "wf" };
        db.Projects.AddRange(mine, theirs);
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = theirs.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        DashboardOverviewResponse res = await Get(db, me);

        res.Projects.Select(p => p.ProjectId).Should().Equal(mine.Id);
        res.ActiveExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Overview_AggregatesFilesExecutionsAndOutputs()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project p = NewProject(me, "reel");
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = p.Id, Name = "derush" };
        WorkflowExecution failedOld = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddHours(-3), CompletedAt = DateTime.UtcNow.AddHours(-3), ErrorMessage = "boom" };
        WorkflowExecution passed = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow.AddHours(-1) };
        WorkflowExecution running = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow };
        db.Projects.Add(p);
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowExecutions.AddRange(failedOld, passed, running);
        db.ProjectFiles.AddRange(
            new ProjectFile { Id = Guid.NewGuid(), ProjectId = p.Id, OriginalFileName = "a.mp4", MimeType = "video/mp4", SizeBytes = 100, Category = "userFiles" },
            new ProjectFile { Id = Guid.NewGuid(), ProjectId = p.Id, OriginalFileName = "b.wav", MimeType = "audio/wav", SizeBytes = 50, Category = "userFiles" },
            new ProjectFile { Id = Guid.NewGuid(), ProjectId = p.Id, OriginalFileName = "out.mp4", MimeType = "video/mp4", SizeBytes = 10, Category = "outputFiles" });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = passed.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputStorageKey = $"projects/{p.Id}/outputFiles/{passed.Id}/final.mp4",
            CompletedAt = DateTime.UtcNow,
        });
        // A key outside this project's prefix must not surface as a render.
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = passed.Id,
            WorkflowStepId = Guid.NewGuid(),
            OutputStorageKey = $"projects/{Guid.NewGuid()}/outputFiles/x/evil.mp4",
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        DashboardOverviewResponse res = await Get(db, me);

        ProjectOverview o = res.Projects.Single();
        o.SourceFileCount.Should().Be(2);
        o.VideoCount.Should().Be(1); // the outputFiles render row is not source media
        o.AudioCount.Should().Be(1);
        o.WorkflowCount.Should().Be(1);
        o.RenderCount.Should().Be(1);
        o.ActiveExecutionCount.Should().Be(1);
        o.LatestOutput!.FileName.Should().Be("final.mp4");
        res.ActiveExecutions.Should().ContainSingle().Which.Id.Should().Be(running.Id);
        res.RecentExecutions.First().Id.Should().Be(passed.Id);
        res.RecentExecutions.First().OutputFileName.Should().Be("final.mp4");
        // The newer pass supersedes the older failure for this workflow.
        res.NeedsAttention.Should().BeEmpty();
        res.RecentOutputs.Should().ContainSingle();
    }

    [Fact]
    public async Task Overview_FlagsWorkflowWhoseLatestRunFailed()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project p = NewProject(me, "reel");
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = p.Id, Name = "derush" };
        db.Projects.Add(p);
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow.AddHours(-5), CompletedAt = DateTime.UtcNow.AddHours(-5) });
        WorkflowExecution failed = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow.AddHours(-1), ErrorMessage = "provider timeout" };
        db.WorkflowExecutions.Add(failed);
        await db.SaveChangesAsync();

        DashboardOverviewResponse res = await Get(db, me);

        res.NeedsAttention.Should().ContainSingle().Which.ErrorMessage.Should().Be("provider timeout");
        res.Totals.FailedLast7Days.Should().Be(1);
    }

    [Fact]
    public async Task Overview_CarriesRunAttention_OnlyForAPassedRun()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project p = NewProject(me, "reel");
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = p.Id, Name = "derush" };
        const string attention = """{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""";
        db.Projects.Add(p);
        db.WorkflowDefinitions.Add(wf);
        WorkflowExecution passed = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow.AddHours(-3), CompletedAt = DateTime.UtcNow.AddHours(-3), AttentionJson = attention };
        WorkflowExecution failed = new() { Id = Guid.NewGuid(), ProjectId = p.Id, WorkflowDefinitionId = wf.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow.AddHours(-1), AttentionJson = attention };
        db.WorkflowExecutions.AddRange(passed, failed);
        await db.SaveChangesAsync();

        DashboardOverviewResponse res = await Get(db, me);

        res.RecentExecutions.Single(r => r.Id == passed.Id).RunAttention.Should().NotBeNull();
        res.RecentExecutions.Single(r => r.Id == passed.Id).RunAttention!.Score.Should().Be(5);
        res.RecentExecutions.Single(r => r.Id == failed.Id).RunAttention.Should().BeNull();
    }
}
