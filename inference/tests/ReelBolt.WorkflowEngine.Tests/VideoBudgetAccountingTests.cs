using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The one video-budget accounting (<see cref="VideoBudgetAccounting"/>) the engine's budget gate and the
/// API's budget snapshot both use: what a row costs, which rows count today, the default caps and what a
/// tenant may be shown.
/// </summary>
public class VideoBudgetAccountingTests
{
    private static ExternalGenerationJob Job(
        Guid projectId, decimal estimate, decimal? actual = null,
        ExternalGenerationJobStatus status = ExternalGenerationJobStatus.Succeeded,
        string unit = "credits", DateTime? createdAt = null) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ClipId = "c",
            RequestHash = Guid.NewGuid().ToString(),
            Status = status,
            CostEstimate = estimate,
            ActualCost = actual,
            CostUnit = unit,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };

    [Theory]
    [InlineData(10.0, null, 10.0)]
    [InlineData(10.0, 25.0, 25.0)]
    [InlineData(10.0, 4.0, 10.0)]
    [InlineData(-5.0, null, 0.0)]
    public void A_row_is_charged_the_larger_of_estimate_and_actual_never_below_zero(double estimate, double? actual, double expected) =>
        VideoBudgetAccounting.Charge((decimal)estimate, (decimal?)actual).Should().Be((decimal)expected);

    [Fact]
    public void Only_todays_live_rows_of_the_unit_are_counted()
    {
        Guid p = Guid.NewGuid();
        DateTime today = DateTime.UtcNow.Date;
        var rows = new[]
        {
            Job(p, 10m, actual: 25m),
            Job(p, 7m),
            Job(p, 40m, status: ExternalGenerationJobStatus.Failed),
            Job(p, 40m, status: ExternalGenerationJobStatus.Cancelled),
            Job(p, 40m, createdAt: today.AddMinutes(-1)),
            Job(p, 40m, unit: "gpuseconds"),
        };

        var counted = VideoBudgetAccounting.TodayRows(rows.AsQueryable(), ExternalGenerationJob.CreditsUnit, today).ToList();

        counted.Sum(r => VideoBudgetAccounting.Charge(r.CostEstimate, r.ActualCost)).Should().Be(32m);
    }

    [Fact]
    public void The_shipped_default_caps_are_the_documented_figures_and_a_blank_configuration_gets_them()
    {
        // The documented defaults (docs/video-generation.md configuration table): no per-project figure, a $5000
        // operator safety ceiling per day as credits (the daily limit is the user's own setting), and 120 / 240 GPU
        // minutes. Pinned as literals so a drifted constant fails.
        Math.Round(VideoBudgetAccounting.DefaultGlobalDailyCredits, 2).Should().Be(833333.33m);
        VideoBudgetAccounting.DefaultProjectDailyGpuMinutes.Should().Be(120m);
        VideoBudgetAccounting.DefaultGlobalDailyGpuMinutes.Should().Be(240m);

        // And what the gate really runs with when nothing is configured is bound from an empty configuration.
        var bound = new VideoGenerationOptions();
        new ConfigurationBuilder().AddInMemoryCollection().Build().GetSection(VideoGenerationOptions.SectionName).Bind(bound);
        bound.DailyBudgetCredits.Should().BeNull();
        bound.GlobalDailyBudgetCredits.Should().Be(VideoBudgetAccounting.DefaultGlobalDailyCredits);
        bound.DailyGpuMinutes.Should().Be(VideoBudgetAccounting.DefaultProjectDailyGpuMinutes);
        bound.GlobalDailyGpuMinutes.Should().Be(VideoBudgetAccounting.DefaultGlobalDailyGpuMinutes);
    }

    [Fact]
    public void The_sql_charge_expression_sums_exactly_what_Charge_sums()
    {
        Guid p = Guid.NewGuid();
        DateTime today = DateTime.UtcNow.Date;
        var rows = new[]
        {
            Job(p, 10m, actual: 25m),
            Job(p, 7m),
            Job(p, 10m, actual: 4m),
            Job(p, -5m),
            Job(p, -5m, actual: -2m),
            Job(p, 40m, status: ExternalGenerationJobStatus.Failed),
            Job(p, 40m, unit: "gpuseconds"),
        };

        decimal viaSql = VideoBudgetAccounting.TodayJobs(rows.AsQueryable(), ExternalGenerationJob.CreditsUnit, today)
            .Sum(VideoBudgetAccounting.ChargeOf);

        viaSql.Should().Be(42m, "25 + 7 + 10 + 0 + 0: the same max(estimate, actual) floored at 0 as Charge");
        viaSql.Should().Be(VideoBudgetAccounting.TodayRows(rows.AsQueryable(), ExternalGenerationJob.CreditsUnit, today)
            .ToList().Sum(r => VideoBudgetAccounting.Charge(r.CostEstimate, r.ActualCost)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void When_the_platform_limit_is_reached_the_view_carries_no_figure_at_all(bool gpu)
    {
        DateTime today = DateTime.UtcNow.Date;
        IReadOnlyDictionary<string, object?> view = VideoBudgetAccounting.TenantView(
            gpu, projectCap: 100m, projectUsed: 10m, platformReached: true, todayUtc: today);

        view.Keys.Should().BeEquivalentTo(["unit", "limitedBy", "resetsAt"]);
        view["limitedBy"].Should().Be("platform");
        view["unit"].Should().Be(gpu ? "gpuMinutes" : "credits");
        view["resetsAt"].Should().Be(DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Utc));
    }

    [Fact]
    public void While_the_platform_has_room_the_tenants_own_tightest_cap_binds_and_no_plan_cap_competes()
    {
        IReadOnlyDictionary<string, object?> view = VideoBudgetAccounting.TenantView(
            gpu: false, projectCap: 100m, projectUsed: 30m, platformReached: false,
            todayUtc: DateTime.UtcNow.Date, userCap: 50m, userUsed: 45m);

        view["limitedBy"].Should().Be("user");
        view["dailyCapCredits"].Should().Be(50m);
        view["remainingTodayCredits"].Should().Be(5m);
        VideoBudgetAccounting.TenantView(gpu: false, projectCap: null, projectUsed: 0m, platformReached: false, todayUtc: DateTime.UtcNow.Date)["limitedBy"]
            .Should().BeNull("no plan caps video spend, so with no limit of their own nothing but credits bounds the day");
    }

    [Fact]
    public void A_project_cap_shows_its_own_cap_and_use()
    {
        IReadOnlyDictionary<string, object?> view = VideoBudgetAccounting.TenantView(
            gpu: false, projectCap: 100m, projectUsed: 30m, platformReached: false,
            todayUtc: DateTime.UtcNow.Date);

        view["limitedBy"].Should().Be("project");
        view["dailyCapCredits"].Should().Be(100m);
        view["usedTodayCredits"].Should().Be(30m);
        view["remainingTodayCredits"].Should().Be(70m);
    }

    [Fact]
    public void A_gpu_budget_is_reported_in_minutes()
    {
        IReadOnlyDictionary<string, object?> view = VideoBudgetAccounting.TenantView(
            gpu: true, projectCap: 120m, projectUsed: 90m, platformReached: false,
            todayUtc: DateTime.UtcNow.Date);

        view["unit"].Should().Be("gpuMinutes");
        view["usedTodayGpuMinutes"].Should().Be(1.5m);
        view["remainingTodayGpuMinutes"].Should().Be(0.5m);
    }

    [Theory]
    [InlineData(100.0, 100.0, true)]
    [InlineData(100.0, 140.0, true)]
    [InlineData(0.0, 0.0, true)]
    [InlineData(100.0, 99.99, false)]
    public void The_platform_is_reached_only_when_it_has_no_room_left(double cap, double used, bool reached) =>
        VideoBudgetAccounting.PlatformReached((decimal)cap, (decimal)used).Should().Be(reached);

    [Fact]
    public void ChargeOf_rounds_each_row_to_six_decimals_so_a_database_sum_cannot_overflow_a_decimal()
    {
        // Postgres SUM over unconstrained numeric rows like these exceeds decimal's digits (OverflowException on the
        // estimate endpoint). The true repro needs Postgres (no Testcontainers pattern here); this pins the contract.
        var row = new ExternalGenerationJob { CostEstimate = 53.333333333333333333333333333m, ActualCost = null };
        var charge = VideoBudgetAccounting.ChargeOf.Compile()(row);
        charge.Should().Be(53.333333m);
        VideoBudgetAccounting.ChargeOf.Compile()(new ExternalGenerationJob { CostEstimate = 120m, ActualCost = 126.666666666666666666666666667m })
            .Should().Be(126.666667m);
        VideoBudgetAccounting.ChargeOf.Compile()(new ExternalGenerationJob { CostEstimate = -1m, ActualCost = null }).Should().Be(0m);
    }
}
