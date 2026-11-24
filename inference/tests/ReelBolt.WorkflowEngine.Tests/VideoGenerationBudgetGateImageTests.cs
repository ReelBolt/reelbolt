using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// ImageGenerate shares the generation ledger but never the video-generation daily caps, like AudioGenerate. Its rows are
/// recognised by their clip id (<c>img-{k}</c>), the video sums leave them out, and an image reservation is limited only by
/// its step cap (MaxSpendCredits) and the credit hold.
/// </summary>
public class VideoGenerationBudgetGateImageTests
{
    private static VideoGenerationBudgetGate NewGate(SharedInMemoryDbFactory factory, bool videoGenAllowed = true)
        => new(
            factory,
            Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 100m, GlobalDailyBudgetCredits = 100m }),
            new ConfigOrganizationEntitlements(new TenancyOptions
            {
                DefaultEntitlements = new OrganizationEntitlementSet { VideoGenAllowed = videoGenAllowed }
            }));

    private static async Task<(Guid Project, Guid Org)> SeedProjectAsync(SharedInMemoryDbFactory factory)
    {
        Guid project = Guid.NewGuid(), org = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        db.Projects.Add(new Project { Id = project, Name = "p", OrganizationId = org });
        await db.SaveChangesAsync();
        return (project, org);
    }

    private static async Task AddRowAsync(SharedInMemoryDbFactory factory, Guid project, Guid org, string clipId, decimal cost)
    {
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = project,
            OrganizationId = org,
            ClipId = clipId,
            RequestHash = Guid.NewGuid().ToString("N"),
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = cost,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static VideoGenerationReservationRequest Video(Guid project, decimal cost, string clipId = "v1")
        => new(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m,
            [new VideoGenerationTakeRequest(clipId, Guid.NewGuid().ToString("N"), cost)]);

    private static VideoGenerationReservationRequest Image(Guid project, decimal cost, decimal stepCap = 1000m, string clipId = "img-1")
        => new(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), stepCap,
            [new VideoGenerationTakeRequest(clipId, Guid.NewGuid().ToString("N"), cost)],
            Scope: VideoGenerationBudgetScope.Image);

    [Fact]
    public async Task Image_rows_consume_no_video_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, org) = await SeedProjectAsync(factory);
        await AddRowAsync(factory, project, org, "img-1", 90m);
        await AddRowAsync(factory, project, org, "img-2", 90m);

        var result = await NewGate(factory).ReserveAsync(Video(project, 60m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
        result.ProjectSpentToday.Should().Be(0m);
        result.GlobalSpentToday.Should().Be(0m);
    }

    [Fact]
    public async Task An_exhausted_video_cap_does_not_block_images_and_a_plan_without_a_video_budget_does_not_either()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, org) = await SeedProjectAsync(factory);
        await AddRowAsync(factory, project, org, "v1", 100m);

        (await NewGate(factory).ReserveAsync(Video(project, 1m), CancellationToken.None)).Allowed.Should().BeFalse();

        var image = await NewGate(factory, videoGenAllowed: false).ReserveAsync(Image(project, 50m), CancellationToken.None);
        image.Allowed.Should().BeTrue(image.DenyReason);
        image.Takes.Should().ContainSingle();
    }

    [Fact]
    public async Task An_image_is_still_bounded_by_its_step_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, _) = await SeedProjectAsync(factory);

        var image = await NewGate(factory).ReserveAsync(Image(project, 50m, stepCap: 40m), CancellationToken.None);

        image.Allowed.Should().BeFalse();
        image.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Any().Should().BeFalse();
    }

    [Theory]
    [InlineData(VideoGenerationBudgetScope.Image, "v1")]
    [InlineData(VideoGenerationBudgetScope.Image, "music-1")]
    [InlineData(VideoGenerationBudgetScope.Audio, "img-1")]
    [InlineData(VideoGenerationBudgetScope.Video, "img-1")]
    public async Task A_clip_id_outside_its_scope_is_refused_so_no_row_can_escape_the_video_caps(VideoGenerationBudgetScope scope, string clipId)
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, _) = await SeedProjectAsync(factory);
        var request = new VideoGenerationReservationRequest(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m,
            [new VideoGenerationTakeRequest(clipId, "h", 10m)], Scope: scope);

        var result = await NewGate(factory).ReserveAsync(request, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
    }

    [Fact]
    public async Task An_image_abandoned_after_delivery_failed_keeps_counting_toward_the_step_cap_of_a_later_attempt()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, org) = await SeedProjectAsync(factory);
        Guid execution = Guid.NewGuid(), step = Guid.NewGuid(), earlier = Guid.NewGuid(), current = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = execution, WorkflowDefinitionId = Guid.NewGuid(), ProjectId = project, Status = ExecutionStatus.Running });
            db.WorkflowSteps.Add(new WorkflowStep { Id = step, WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.ImageGenerate });
            db.WorkflowStepResults.Add(new WorkflowStepResult { Id = earlier, WorkflowExecutionId = execution, WorkflowStepId = step, Status = StepStatus.Failed });
            db.WorkflowStepResults.Add(new WorkflowStepResult { Id = current, WorkflowExecutionId = execution, WorkflowStepId = step, Status = StepStatus.Running });
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution,
                StepResultId = earlier,
                ProjectId = project,
                OrganizationId = org,
                ClipId = "img-1",
                RequestHash = "old",
                ProviderId = Guid.NewGuid(),
                Status = ExternalGenerationJobStatus.Abandoned,
                CostEstimate = 10m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        VideoGenerationReservationRequest Ask(decimal cap) => new(project, execution, current, Guid.NewGuid(), cap,
            [new VideoGenerationTakeRequest("img-1", "new", 10m)], Scope: VideoGenerationBudgetScope.Image);

        // 10 credits already spent by the abandoned picture + 10 more = 20, over a 15 cap: refused.
        var refused = await NewGate(factory).ReserveAsync(Ask(15m), CancellationToken.None);
        refused.Allowed.Should().BeFalse();
        refused.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");

        (await NewGate(factory).ReserveAsync(Ask(25m), CancellationToken.None)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void The_accounting_counts_no_image_row()
    {
        ExternalGenerationJob Row(string clip) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            ClipId = clip,
            RequestHash = "h",
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 10m,
            CreatedAt = DateTime.UtcNow,
        };

        var rows = new[] { Row("v1"), Row("v2t1"), Row("music-1"), Row("img-1"), Row("img-4") }.AsQueryable();

        VideoBudgetAccounting.TodayJobs(rows, ExternalGenerationJob.CreditsUnit, DateTime.UtcNow.Date).Sum(VideoBudgetAccounting.ChargeOf).Should().Be(20m);
        VideoBudgetAccounting.IsImageClipId("img-1").Should().BeTrue();
        VideoBudgetAccounting.IsImageClipId("v1").Should().BeFalse();
        VideoBudgetAccounting.ImageClipId(3).Should().Be("img-3");
    }
}
