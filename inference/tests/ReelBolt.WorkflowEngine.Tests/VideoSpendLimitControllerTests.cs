using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The caller's own daily video limit: GET/PUT validate, store and read back (docs/design/video-spend-limit.md).</summary>
public class VideoSpendLimitControllerTests
{
    private sealed class FakeUser(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private static InferenceApiDbContext NewDb(string? name = null) => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Nothing_set_reads_as_no_limit_with_todays_use()
    {
        using var db = NewDb();
        var controller = new VideoSpendLimitController(db, new FakeUser(Guid.NewGuid()));

        var view = ((OkObjectResult)(await controller.Get(CancellationToken.None)).Result!).Value as UserVideoLimitView;

        view!.DailyLimitCredits.Should().BeNull();
        view.RemainingTodayCredits.Should().BeNull();
        view.LimitReached.Should().BeFalse();
    }

    [Fact]
    public async Task Put_stores_the_limit_for_the_caller_only_and_get_reads_it_back_with_the_room_left()
    {
        string name = Guid.NewGuid().ToString();
        Guid me = Guid.NewGuid(), other = Guid.NewGuid();
        using var db = NewDb(name);
        var controller = new VideoSpendLimitController(db, new FakeUser(me));

        var put = await controller.Put(new SaveVideoSpendLimitRequest(300m, 200m), CancellationToken.None);
        var view = (UserVideoLimitView)((OkObjectResult)put.Result!).Value!;

        view.DailyLimitCredits.Should().Be(300m);
        view.AlertThresholdCredits.Should().Be(200m);
        view.RemainingTodayCredits.Should().Be(300m);
        (await db.UserVideoSpendLimits.CountAsync()).Should().Be(1);
        (await db.UserVideoSpendLimits.AnyAsync(l => l.UserId == other)).Should().BeFalse();

        // A second PUT replaces both fields; null clears the limit.
        await controller.Put(new SaveVideoSpendLimitRequest(null, null), CancellationToken.None);
        (await db.UserVideoSpendLimits.SingleAsync()).DailyLimitCredits.Should().BeNull();
    }

    [Theory]
    [InlineData(-1.0, null, "invalid_daily_limit")]
    [InlineData(null, -5.0, "invalid_alert_threshold")]
    [InlineData(100.0, 150.0, "alert_above_limit")]
    [InlineData(200000000.0, null, "invalid_daily_limit")]
    public async Task Invalid_bodies_are_refused_and_nothing_is_stored(double? limit, double? alert, string error)
    {
        using var db = NewDb();
        var controller = new VideoSpendLimitController(db, new FakeUser(Guid.NewGuid()));

        var result = await controller.Put(
            new SaveVideoSpendLimitRequest((decimal?)limit, (decimal?)alert), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { error });
        (await db.UserVideoSpendLimits.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_view_counts_only_my_runs_video_spend_today_and_flags_the_alert()
    {
        Guid me = Guid.NewGuid(), run = Guid.NewGuid(), theirRun = Guid.NewGuid();
        using var db = NewDb();
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = run, ProjectId = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), InitiatedByUserId = me });
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = theirRun, ProjectId = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), InitiatedByUserId = Guid.NewGuid() });
        foreach ((Guid exec, decimal cost, string clip) in new[] { (run, 60m, "v1"), (theirRun, 400m, "v1"), (run, 30m, "sfx-1") })
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = exec,
                StepResultId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                ClipId = clip,
                RequestHash = Guid.NewGuid().ToString(),
                ProviderId = Guid.NewGuid(),
                Status = ExternalGenerationJobStatus.Succeeded,
                CostEstimate = cost,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        db.UserVideoSpendLimits.Add(new UserVideoSpendLimit { UserId = me, DailyLimitCredits = 100m, AlertThresholdCredits = 50m });
        await db.SaveChangesAsync();

        UserVideoLimitView view = await VideoBudgetSnapshot.ForUserAsync(db, me, CancellationToken.None);

        view.UsedTodayCredits.Should().Be(60m, "another user's 400 and my audio take do not count");
        view.RemainingTodayCredits.Should().Be(40m);
        view.AlertReached.Should().BeTrue();
        view.LimitReached.Should().BeFalse();
    }

    [Fact]
    public async Task The_run_budget_names_the_users_own_limit_as_the_binding_cap()
    {
        Guid me = Guid.NewGuid(), run = Guid.NewGuid();
        using var db = NewDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OrganizationId = Guid.NewGuid() };
        db.Projects.Add(project);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = run, ProjectId = project.Id, WorkflowDefinitionId = Guid.NewGuid(), InitiatedByUserId = me });
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = run,
            StepResultId = Guid.NewGuid(),
            ProjectId = project.Id,
            ClipId = "v1",
            RequestHash = "h",
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 60m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.UserVideoSpendLimits.Add(new UserVideoSpendLimit { UserId = me, DailyLimitCredits = 100m });
        await db.SaveChangesAsync();

        var view = await VideoBudgetSnapshot.ForRunAsync(db, null, project, run, me, CancellationToken.None);

        view["limitedBy"].Should().Be("user");
        view["dailyCapCredits"].Should().Be(100m);
        view["remainingTodayCredits"].Should().Be(40m);
    }

    [Fact]
    public async Task With_no_limit_the_run_budget_reports_no_cap_instead_of_a_platform_number()
    {
        Guid me = Guid.NewGuid(), run = Guid.NewGuid();
        using var db = NewDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OrganizationId = Guid.NewGuid() };
        db.Projects.Add(project);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = run, ProjectId = project.Id, WorkflowDefinitionId = Guid.NewGuid(), InitiatedByUserId = me });
        await db.SaveChangesAsync();

        var view = await VideoBudgetSnapshot.ForRunAsync(db, null, project, run, me, CancellationToken.None);

        view["limitedBy"].Should().BeNull();
        view["dailyCapCredits"].Should().BeNull();
        view["remainingTodayCredits"].Should().BeNull();
    }

    [Fact]
    public async Task Another_member_viewing_the_run_sees_none_of_the_initiators_personal_limit_or_use()
    {
        Guid initiator = Guid.NewGuid(), viewer = Guid.NewGuid(), run = Guid.NewGuid();
        using var db = NewDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OrganizationId = Guid.NewGuid() };
        db.Projects.Add(project);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = run, ProjectId = project.Id, WorkflowDefinitionId = Guid.NewGuid(), InitiatedByUserId = initiator });
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = run,
            StepResultId = Guid.NewGuid(),
            ProjectId = project.Id,
            ClipId = "v1",
            RequestHash = "h",
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 60m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.UserVideoSpendLimits.Add(new UserVideoSpendLimit { UserId = initiator, DailyLimitCredits = 100m, AlertThresholdCredits = 50m });
        await db.SaveChangesAsync();

        var seen = await VideoBudgetSnapshot.ForRunAsync(db, null, project, run, viewer, CancellationToken.None);

        seen["limitedBy"].Should().BeNull();
        seen["dailyCapCredits"].Should().BeNull();
        seen["remainingTodayCredits"].Should().BeNull();
        seen["alertThresholdCredits"].Should().BeNull();
        seen["usedTodayCredits"].Should().Be(0m);
        System.Text.Json.JsonSerializer.Serialize(seen).Should().NotContain("100").And.NotContain("60");

        var own = await VideoBudgetSnapshot.ForRunAsync(db, null, project, run, initiator, CancellationToken.None);
        own["limitedBy"].Should().Be("user");
    }
}
