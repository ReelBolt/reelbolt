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
/// Issue #93 phase 5: a self-hosted ComfyUI bills $0, so its budget is GPU TIME. The same atomic
/// <see cref="VideoGenerationBudgetGate"/> enforces it — the unit on the request selects which caps
/// apply and which ledger rows count, and every row is written with the unit it was reserved in, so
/// USD spend and GPU time never add up into one meaningless number.
/// </summary>
public class VideoGenerationBudgetGateGpuTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static VideoGenerationBudgetGate NewGate(
        SharedInMemoryDbFactory factory, decimal dailyGpuMinutes = 120m, decimal globalDailyGpuMinutes = 240m)
        => new(factory, Options.Create(new VideoGenerationOptions
        {
            DailyBudgetCredits = 20m,
            GlobalDailyBudgetCredits = 50m,
            DailyGpuMinutes = dailyGpuMinutes,
            GlobalDailyGpuMinutes = globalDailyGpuMinutes
        }));

    private static VideoGenerationReservationRequest GpuRequest(Guid projectId, decimal maxGpuSeconds, params decimal[] takeGpuSeconds) =>
        new(projectId, Guid.NewGuid(), Guid.NewGuid(), ProviderId, maxGpuSeconds,
            takeGpuSeconds.Select((c, i) => new VideoGenerationTakeRequest($"v{i + 1}", $"hash-{i + 1}", c)).ToList(),
            VideoGenerationCostUnit.GpuSeconds);

    private static VideoGenerationReservationRequest UsdRequest(Guid projectId, decimal stepCap, params decimal[] takeCosts) =>
        new(projectId, Guid.NewGuid(), Guid.NewGuid(), ProviderId, stepCap,
            takeCosts.Select((c, i) => new VideoGenerationTakeRequest($"v{i + 1}", $"usd-hash-{i + 1}", c)).ToList());

    [Fact]
    public async Task A_GPU_reservation_writes_rows_tagged_gpuseconds()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();

        var result = await NewGate(factory).ReserveAsync(GpuRequest(projectId, 600m, 120m, 120m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Should().HaveCount(2);
        db.ExternalGenerationJobs.Should().OnlyContain(j => j.CostUnit == ExternalGenerationJob.GpuSecondsUnit);
    }

    [Fact]
    public async Task A_USD_reservation_still_writes_rows_tagged_usd_the_column_default()
    {
        var factory = new SharedInMemoryDbFactory();

        var result = await NewGate(factory).ReserveAsync(UsdRequest(Guid.NewGuid(), 10m, 0.5m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Single().CostUnit.Should().Be(ExternalGenerationJob.CreditsUnit);
    }

    [Fact]
    public async Task The_step_cap_denies_in_GPU_second_vocabulary_with_zero_rows()
    {
        var factory = new SharedInMemoryDbFactory();

        var result = await NewGate(factory).ReserveAsync(GpuRequest(Guid.NewGuid(), 100m, 60m, 60m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:").And.Contain("GPU-minutes");
        result.DenyReason.Should().NotContain("$", "a GPU budget is never expressed in dollars");
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_project_daily_GPU_cap_is_configured_in_minutes_and_enforced_in_seconds()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();

        // 2 GPU-minutes = 120 GPU-seconds a day for this project. 90 fits, a further 60 does not.
        var gate = NewGate(factory, dailyGpuMinutes: 2m, globalDailyGpuMinutes: 1000m);
        (await gate.ReserveAsync(GpuRequest(projectId, 1000m, 90m), CancellationToken.None)).Allowed.Should().BeTrue();

        var denied = await gate.ReserveAsync(GpuRequest(projectId, 1000m, 60m), CancellationToken.None);

        denied.Allowed.Should().BeFalse();
        denied.DenyReason.Should().Contain("the daily GPU-minutes budget is 2 GPU-minutes").And.Contain("1.5 are already used today");
    }

    [Fact]
    public async Task USD_spend_today_does_not_count_against_the_GPU_caps_and_vice_versa()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var gate = NewGate(factory, dailyGpuMinutes: 1m, globalDailyGpuMinutes: 1m);

        // $19.90 of USD spend today — nearly the whole $20 USD day — is irrelevant to a GPU request...
        (await gate.ReserveAsync(UsdRequest(projectId, 100m, 19.90m), CancellationToken.None)).Allowed.Should().BeTrue();
        var gpu = await gate.ReserveAsync(GpuRequest(projectId, 1000m, 59m), CancellationToken.None);
        gpu.Allowed.Should().BeTrue(gpu.DenyReason);
        gpu.ProjectSpentToday.Should().Be(0m, "the GPU ledger starts empty however much USD was spent");

        // ...and the 59 GPU-seconds just reserved are irrelevant to the next USD request.
        var usd = await gate.ReserveAsync(UsdRequest(projectId, 100m, 0.05m), CancellationToken.None);
        usd.Allowed.Should().BeTrue(usd.DenyReason);
        usd.ProjectSpentToday.Should().Be(19.90m);
    }

    [Fact]
    public async Task A_non_positive_GPU_day_denies_every_GPU_reservation_fail_closed()
    {
        var factory = new SharedInMemoryDbFactory();

        var result = await NewGate(factory, dailyGpuMinutes: 0m).ReserveAsync(GpuRequest(Guid.NewGuid(), 1000m, 1m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
    }
}
