using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-017: a refused video reservation says which cap refused, what it is, what is used today, what
/// the request needs and when the day resets, in one plain sentence, and never says nothing.
/// </summary>
public class VideoBudgetMessageTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static VideoGenerationBudgetGate NewGate(
        SharedInMemoryDbFactory factory,
        decimal project = 100m,
        decimal global = 1000m,
        bool? videoGenAllowed = null,
        decimal gpuProjectMinutes = 120m,
        decimal gpuGlobalMinutes = 240m)
        => new(
            factory,
            Options.Create(new VideoGenerationOptions
            {
                DailyBudgetCredits = project,
                GlobalDailyBudgetCredits = global,
                DailyGpuMinutes = gpuProjectMinutes,
                GlobalDailyGpuMinutes = gpuGlobalMinutes,
            }),
            videoGenAllowed is null
                ? null
                : new ConfigOrganizationEntitlements(new TenancyOptions
                {
                    DefaultEntitlements = new OrganizationEntitlementSet { VideoGenAllowed = videoGenAllowed.Value }
                }));

    private static VideoGenerationReservationRequest Request(
        Guid projectId, decimal stepCap, VideoGenerationCostUnit unit, params decimal[] takes) =>
        new(projectId, Guid.NewGuid(), Guid.NewGuid(), ProviderId, stepCap,
            takes.Select((c, i) => new VideoGenerationTakeRequest($"v{i + 1}", $"hash-{Guid.NewGuid()}-{i}", c)).ToList(),
            unit);

    private static async Task SeedSpendAsync(
        SharedInMemoryDbFactory factory, Guid projectId, Guid? orgId, decimal spent, string unit = "credits")
    {
        await using var db = factory.CreateDbContext();
        if (orgId is Guid o)
        {
            db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = o });
        }

        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = orgId,
            ClipId = "c",
            RequestHash = Guid.NewGuid().ToString(),
            ProviderId = ProviderId,
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = spent,
            CostUnit = unit,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string Sentence(VideoGenerationReservation r)
    {
        r.Allowed.Should().BeFalse();
        r.DenyReason.Should().StartWith("BUDGET_EXCEEDED: ", "the code stays so callers can classify the refusal");
        return r.DenyReason!["BUDGET_EXCEEDED: ".Length..];
    }

    [Fact]
    public async Task A_project_daily_refusal_names_the_cap_its_value_the_used_amount_and_the_reset()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedSpendAsync(factory, projectId, null, 90m);

        var r = await NewGate(factory, project: 100m).ReserveAsync(
            Request(projectId, 1000m, VideoGenerationCostUnit.Credits, 20m), CancellationToken.None);

        var text = Sentence(r);
        text.Should().Contain("about 20 credits of video generation")
            .And.Contain("the project's daily video budget is 100 credits")
            .And.Contain("90 are already used today (10 credits left)")
            .And.Contain("resets at 00:00 UTC (in ")
            .And.EndWith("Generate fewer clips or wait.");
        r.Refusal.Should().NotBeNull();
        r.Refusal!.Cap.Should().Be(BudgetCapKind.ProjectDaily);
        r.Refusal.CapValue.Should().Be(100m);
        r.Refusal.SpentToday.Should().Be(90m);
        r.Refusal.Estimated.Should().Be(20m);
        r.Refusal.ResetsAtUtc.Should().Be(DateTime.UtcNow.Date.AddDays(1));
    }

    [Fact]
    public async Task A_step_limit_refusal_names_the_step_and_has_no_reset()
    {
        var factory = new SharedInMemoryDbFactory();

        var r = await NewGate(factory).ReserveAsync(
            Request(Guid.NewGuid(), 50m, VideoGenerationCostUnit.Credits, 40m, 40m), CancellationToken.None);

        var text = Sentence(r);
        text.Should().Contain("about 80 credits").And.Contain("this step's spending limit is 50 credits (50 credits left)");
        text.Should().NotContain("resets", "a step limit is not a daily cap");
        r.Refusal!.Cap.Should().Be(BudgetCapKind.StepLimit);
        r.Refusal.ResetsAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_global_refusal_names_the_platform_budget()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedSpendAsync(factory, Guid.NewGuid(), null, 95m); // someone else's spend

        var r = await NewGate(factory, project: 1000m, global: 100m).ReserveAsync(
            Request(projectId, 1000m, VideoGenerationCostUnit.Credits, 10m), CancellationToken.None);

        var text = Sentence(r);
        text.Should().StartWith("Video generation has reached the platform's daily limit; it resets at 00:00 UTC (in ");
        text.Should().NotContain("95").And.NotContain("100").And.NotContain("credits", "no global figure may reach a tenant");
        r.Refusal!.Cap.Should().Be(BudgetCapKind.GlobalDaily);
    }

    [Fact]
    public async Task A_GPU_global_refusal_names_no_global_figure()
    {
        var factory = new SharedInMemoryDbFactory();
        await SeedSpendAsync(factory, Guid.NewGuid(), null, 170m, unit: "gpuseconds");

        var r = await NewGate(factory, gpuGlobalMinutes: 3m).ReserveAsync(
            Request(Guid.NewGuid(), 6000m, VideoGenerationCostUnit.GpuSeconds, 30m), CancellationToken.None);

        Sentence(r).Should().StartWith("Video generation has reached the platform's daily limit")
            .And.NotContain("170").And.NotContain("GPU-minute");
    }

    [Fact]
    public async Task A_GPU_refusal_speaks_in_GPU_minutes()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedSpendAsync(factory, projectId, null, 90m, unit: "gpuseconds"); // 1.5 min used of 2

        var r = await NewGate(factory, gpuProjectMinutes: 2m).ReserveAsync(
            Request(projectId, 6000m, VideoGenerationCostUnit.GpuSeconds, 60m), CancellationToken.None);

        var text = Sentence(r);
        text.Should().Contain("about 1 GPU-minute of video generation")
            .And.Contain("the daily GPU-minutes budget is 2 GPU-minutes")
            .And.Contain("1.5 are already used today (0.5 GPU-minutes left)");
        text.Should().NotContain("credits");
    }

    [Fact]
    public async Task A_zero_cap_is_stated_honestly()
    {
        var factory = new SharedInMemoryDbFactory();

        var r = await NewGate(factory, project: 0m).ReserveAsync(
            Request(Guid.NewGuid(), 1000m, VideoGenerationCostUnit.Credits, 5m), CancellationToken.None);

        Sentence(r).Should().Contain("the project's daily video budget is 0 credits (0 credits left)");
    }

    [Fact]
    public async Task No_BUDGET_EXCEEDED_path_yields_an_empty_message()
    {
        var results = new List<VideoGenerationReservation>();

        var f1 = new SharedInMemoryDbFactory();
        results.Add(await NewGate(f1).ReserveAsync(Request(Guid.NewGuid(), 1m, VideoGenerationCostUnit.Credits, 5m), CancellationToken.None));
        var f2 = new SharedInMemoryDbFactory();
        results.Add(await NewGate(f2, project: 0m).ReserveAsync(Request(Guid.NewGuid(), 99m, VideoGenerationCostUnit.Credits, 5m), CancellationToken.None));
        var f3 = new SharedInMemoryDbFactory();
        results.Add(await NewGate(f3, global: 0m).ReserveAsync(Request(Guid.NewGuid(), 99m, VideoGenerationCostUnit.Credits, 5m), CancellationToken.None));
        var f4 = new SharedInMemoryDbFactory();
        results.Add(await NewGate(f4, gpuProjectMinutes: 0m).ReserveAsync(Request(Guid.NewGuid(), 99m, VideoGenerationCostUnit.GpuSeconds, 5m), CancellationToken.None));
        var f5 = new SharedInMemoryDbFactory();
        results.Add(await NewGate(f5, gpuGlobalMinutes: 0m).ReserveAsync(Request(Guid.NewGuid(), 99m, VideoGenerationCostUnit.GpuSeconds, 5m), CancellationToken.None));
        results.Should().HaveCount(5);
        foreach (var r in results)
        {
            r.Allowed.Should().BeFalse();
            r.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
            r.DenyReason!["BUDGET_EXCEEDED:".Length..].Should().NotBeNullOrWhiteSpace();
            r.Refusal.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData(5, 12, "5 h 12 min")]
    [InlineData(0, 12, "12 min")]
    [InlineData(0, 0, "1 min")]
    [InlineData(23, 59, "23 h 59 min")]
    public void The_time_to_reset_is_rounded_up_to_whole_minutes(int hours, int minutes, string expected)
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var refusal = new BudgetRefusal(
            BudgetCapKind.ProjectDaily, VideoGenerationCostUnit.Credits, 833.33m, 790m, 120m, now.AddHours(hours).AddMinutes(minutes));

        VideoBudgetMessages.Describe(refusal, now).Should().Contain($"(in {expected})");
    }

    [Fact]
    public void The_documented_example_sentence_is_produced()
    {
        var now = new DateTime(2026, 10, 17, 18, 48, 0, DateTimeKind.Utc);
        var refusal = new BudgetRefusal(
            BudgetCapKind.ProjectDaily, VideoGenerationCostUnit.Credits, 833.33m, 790m, 120m, now.Date.AddDays(1));

        VideoBudgetMessages.Describe(refusal, now).Should().Be(
            "This needs about 120 credits of video generation but the project's daily video budget is 833.33 credits "
            + "and 790 are already used today (43.33 credits left); it resets at 00:00 UTC (in 5 h 12 min). Generate fewer clips or wait.");
    }
}
