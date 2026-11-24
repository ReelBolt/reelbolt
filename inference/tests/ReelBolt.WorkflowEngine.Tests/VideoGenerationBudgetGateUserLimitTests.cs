using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The daily video limit is the USER's own setting (docs/design/video-spend-limit.md): the gate counts the owner's rows of
/// the UTC day, refuses naming that limit and where to change it, and no platform figure limits a normal day.
/// </summary>
public class VideoGenerationBudgetGateUserLimitTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static VideoGenerationBudgetGate NewGate(SharedInMemoryDbFactory factory, VideoGenerationOptions? options = null)
        => new(factory, Options.Create(options ?? new VideoGenerationOptions()));

    private static async Task SeedExecutionAsync(SharedInMemoryDbFactory factory, Guid executionId, Guid projectId, Guid? userId)
    {
        await using var db = factory.CreateDbContext();
        if (!db.Projects.Any(p => p.Id == projectId))
        {
            db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = Guid.NewGuid() });
        }

        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            WorkflowDefinitionId = Guid.NewGuid(),
            InitiatedByUserId = userId
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLimitAsync(SharedInMemoryDbFactory factory, Guid userId, decimal? limit)
    {
        await using var db = factory.CreateDbContext();
        db.UserVideoSpendLimits.Add(new UserVideoSpendLimit { UserId = userId, DailyLimitCredits = limit });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSpendAsync(
        SharedInMemoryDbFactory factory, Guid executionId, Guid projectId, decimal credits, string clipId = "v1")
    {
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepResultId = Guid.NewGuid(),
            ProjectId = projectId,
            ClipId = clipId,
            RequestHash = Guid.NewGuid().ToString(),
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = credits,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static VideoGenerationReservationRequest Request(Guid projectId, Guid executionId, decimal cost)
        => new(projectId, executionId, Guid.NewGuid(), Guid.NewGuid(), 1_000_000m,
            new[] { new VideoGenerationTakeRequest("v1", "hash-1", cost) });

    [Fact]
    public async Task With_no_limit_set_a_big_day_is_not_refused_by_any_shipped_figure()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), earlier = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, earlier, project, Owner);
        await SeedExecutionAsync(factory, now, project, Owner);
        await SeedSpendAsync(factory, earlier, project, 5000m); // far above the old 3333-credit per-project default

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 100m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task A_user_limit_refuses_naming_only_that_a_personal_limit_was_reached_and_where_to_change_it()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), earlier = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, earlier, project, Owner);
        await SeedExecutionAsync(factory, now, project, Owner);
        await SeedLimitAsync(factory, Owner, 100m);
        await SeedSpendAsync(factory, earlier, project, 90m);

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 20m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Refusal!.Cap.Should().Be(BudgetCapKind.UserDaily);
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:")
            .And.Contain("personal daily video limit")
            .And.Contain("Settings > Usage");
        // The message embeds a live reset countdown ("(in 20 h 42 min)") whose digits vary with the wall clock;
        // strip only that fragment so the figure checks below stay deterministic.
        var withoutCountdown = System.Text.RegularExpressions.Regex.Replace(
            result.DenyReason!, @"\(in [^)]*\)", string.Empty);
        withoutCountdown.Should()
            .NotContain("platform")
            .And.NotContain("100")
            .And.NotContain("90")
            .And.NotContain("20")
            .And.NotContain("left")
            .And.NotContain("already used");
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Count(j => j.ExecutionId == now).Should().Be(0);
    }

    [Fact]
    public async Task A_user_limit_with_room_left_allows_the_reservation()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), earlier = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, earlier, project, Owner);
        await SeedExecutionAsync(factory, now, project, Owner);
        await SeedLimitAsync(factory, Owner, 100m);
        await SeedSpendAsync(factory, earlier, project, 70m);

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 30m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task Another_users_spend_does_not_count_against_my_limit()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), theirs = Guid.NewGuid(), mine = Guid.NewGuid();
        await SeedExecutionAsync(factory, theirs, project, Guid.NewGuid());
        await SeedExecutionAsync(factory, mine, project, Owner);
        await SeedLimitAsync(factory, Owner, 100m);
        await SeedSpendAsync(factory, theirs, project, 500m);

        var result = await NewGate(factory).ReserveAsync(Request(project, mine, 50m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task A_cleared_limit_means_no_limit()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), earlier = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, earlier, project, Owner);
        await SeedExecutionAsync(factory, now, project, Owner);
        await SeedLimitAsync(factory, Owner, null);
        await SeedSpendAsync(factory, earlier, project, 900m);

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 50m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task Audio_rows_do_not_count_toward_the_user_limit()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), earlier = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, earlier, project, Owner);
        await SeedExecutionAsync(factory, now, project, Owner);
        await SeedLimitAsync(factory, Owner, 100m);
        await SeedSpendAsync(factory, earlier, project, 95m, clipId: "music-1");

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 50m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task A_run_with_no_initiating_user_has_no_user_limit()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, now, project, userId: null);
        await SeedLimitAsync(factory, Owner, 1m);

        var result = await NewGate(factory).ReserveAsync(Request(project, now, 50m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Fact]
    public async Task The_operator_safety_ceiling_still_stops_runaway_spend_without_naming_a_figure()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid project = Guid.NewGuid(), now = Guid.NewGuid();
        await SeedExecutionAsync(factory, now, project, Owner);

        var result = await NewGate(factory, new VideoGenerationOptions { GlobalDailyBudgetCredits = 100m })
            .ReserveAsync(Request(project, now, 150m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Refusal!.Cap.Should().Be(BudgetCapKind.GlobalDaily);
        new VideoGenerationOptions().GlobalDailyBudgetCredits.Should().BeGreaterThan(500_000m, "the shipped ceiling is a runaway guard, not a limit");
        new VideoGenerationOptions().DailyBudgetCredits.Should().BeNull("no per-project figure ships");
    }
}
