using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A tiny <see cref="IDbContextFactory{TContext}"/> over one named InMemory database, so every
/// context it hands out (the gate's, the test's) sees the same rows.
/// </summary>
internal sealed class SharedInMemoryDbFactory : IDbContextFactory<WorkflowEngineDbContext>
{
    private readonly DbContextOptions<WorkflowEngineDbContext> _options;

    public SharedInMemoryDbFactory(
        string? databaseName = null,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<WorkflowEngineDbContext> builder =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString());
        if (interceptor is not null)
            builder.AddInterceptors(interceptor).ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
        _options = builder.Options;
    }

    public WorkflowEngineDbContext CreateDbContext() => new(_options);
}

/// <summary>
/// Coverage for the video-generation spending guardrail <see cref="IVideoGenerationBudgetGate"/>:
/// <see cref="VideoGenerationBudgetGate.ReserveAsync"/> checks the step cap, the per-project
/// daily cap and the global daily cap, and inserts every new take's Reserved row, atomically.
/// </summary>
public class VideoGenerationBudgetGateTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static VideoGenerationBudgetGate NewGate(
        SharedInMemoryDbFactory factory, decimal dailyBudgetCredits = 1_000_000m, decimal globalDailyBudgetCredits = 1_000_000m)
        => new(factory, Options.Create(new VideoGenerationOptions
        {
            DailyBudgetCredits = dailyBudgetCredits,
            GlobalDailyBudgetCredits = globalDailyBudgetCredits
        }));

    private static ExternalGenerationJob MakeJob(
        Guid projectId,
        decimal costEstimate,
        ExternalGenerationJobStatus status = ExternalGenerationJobStatus.Succeeded,
        DateTime? createdAt = null,
        decimal? actualCost = null,
        Guid? executionId = null,
        string? requestHash = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId ?? Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = projectId,
            ClipId = "clip-1",
            RequestHash = requestHash ?? Guid.NewGuid().ToString(),
            ProviderId = Guid.NewGuid(),
            Status = status,
            CostEstimate = costEstimate,
            ActualCost = actualCost,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static async Task Seed(SharedInMemoryDbFactory factory, params ExternalGenerationJob[] jobs)
    {
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }

    private static int RowCount(SharedInMemoryDbFactory factory)
    {
        using var db = factory.CreateDbContext();
        return db.ExternalGenerationJobs.Count();
    }

    private static VideoGenerationReservationRequest Request(
        Guid projectId, decimal stepCap, params decimal[] takeCosts) =>
        Request(projectId, Guid.NewGuid(), stepCap, takeCosts);

    private static VideoGenerationReservationRequest Request(
        Guid projectId, Guid executionId, decimal stepCap, params decimal[] takeCosts) =>
        Request(projectId, executionId, Guid.NewGuid(), stepCap, takeCosts);

    private static VideoGenerationReservationRequest Request(
        Guid projectId, Guid executionId, Guid stepResultId, decimal stepCap, params decimal[] takeCosts) =>
        new(projectId, executionId, stepResultId, ProviderId, stepCap,
            takeCosts.Select((c, i) => new VideoGenerationTakeRequest($"clip-{i + 1}", $"hash-{i + 1}", c)).ToList());

    // ---- (1) CONFIG_INVALID ----

    [Fact]
    public async Task Denies_config_invalid_when_takes_empty()
    {
        var factory = new SharedInMemoryDbFactory();
        var result = await NewGate(factory).ReserveAsync(Request(Guid.NewGuid(), 10m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
        result.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Denies_config_invalid_when_any_take_cost_is_not_positive(decimal badCost)
    {
        var factory = new SharedInMemoryDbFactory();
        var result = await NewGate(factory).ReserveAsync(
            Request(Guid.NewGuid(), 10m, 0.40m, badCost), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
        result.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Denies_config_invalid_when_max_spend_not_positive(decimal maxSpend)
    {
        var factory = new SharedInMemoryDbFactory();
        var result = await NewGate(factory).ReserveAsync(
            Request(Guid.NewGuid(), maxSpend, 0.40m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
        RowCount(factory).Should().Be(0);
    }

    [Fact]
    public async Task Denies_config_invalid_when_step_result_id_is_empty()
    {
        var factory = new SharedInMemoryDbFactory();
        var result = await NewGate(factory).ReserveAsync(
            Request(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 10m, 0.40m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
        result.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(0);
    }

    // ---- (6) allow + insert ----

    [Fact]
    public async Task Allows_and_inserts_one_reserved_row_per_take_in_request_order()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var stepResultId = Guid.NewGuid();

        var result = await NewGate(factory).ReserveAsync(
            Request(projectId, executionId, stepResultId, 10m, 0.40m, 0.50m, 0.60m), CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.DenyReason.Should().BeNull();
        result.Estimated.Should().Be(1.50m);
        result.ProjectSpentToday.Should().Be(0m);
        result.GlobalSpentToday.Should().Be(0m);
        result.Takes.Select(t => t.ClipId).Should().Equal("clip-1", "clip-2", "clip-3");
        result.Takes.Should().OnlyContain(t => !t.Existing && t.Status == ExternalGenerationJobStatus.Reserved);

        await using var db = factory.CreateDbContext();
        var rows = await db.ExternalGenerationJobs.ToListAsync();
        rows.Should().HaveCount(3);
        foreach (var take in result.Takes)
        {
            var row = rows.Single(r => r.Id == take.JobId);
            row.ClipId.Should().Be(take.ClipId);
            row.RequestHash.Should().Be(take.RequestHash);
            row.CostEstimate.Should().Be(take.CostEstimate);
            row.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
            row.ProjectId.Should().Be(projectId);
            row.ExecutionId.Should().Be(executionId);
            row.ProviderId.Should().Be(ProviderId);
            row.StepResultId.Should().Be(stepResultId);
            row.RemoteJobId.Should().BeNull();
        }
    }

    // ---- (2) crash-resume ----

    [Theory]
    [InlineData(ExternalGenerationJobStatus.Reserved)]
    [InlineData(ExternalGenerationJobStatus.Submitted)]
    public async Task Returns_existing_row_for_same_execution_and_hash_without_reinserting(
        ExternalGenerationJobStatus existingStatus)
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var existing = MakeJob(projectId, 0.40m, existingStatus, executionId: executionId, requestHash: "hash-1");
        await Seed(factory, existing);

        var result = await NewGate(factory).ReserveAsync(
            Request(projectId, executionId, 10m, 0.40m, 0.40m), CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Estimated.Should().Be(0.40m, "only the new take is charged");
        result.Takes.Should().HaveCount(2);
        result.Takes[0].Existing.Should().BeTrue();
        result.Takes[0].JobId.Should().Be(existing.Id);
        result.Takes[0].Status.Should().Be(existingStatus);
        result.Takes[1].Existing.Should().BeFalse();
        RowCount(factory).Should().Be(2);
    }

    [Fact]
    public async Task Reused_row_keeps_the_step_result_id_of_the_attempt_that_reserved_it()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var firstAttempt = Guid.NewGuid();
        var retryAttempt = Guid.NewGuid();

        var first = await NewGate(factory).ReserveAsync(
            Request(projectId, executionId, firstAttempt, 10m, 0.40m), CancellationToken.None);
        var retry = await NewGate(factory).ReserveAsync(
            Request(projectId, executionId, retryAttempt, 10m, 0.40m, 0.50m), CancellationToken.None);

        first.Allowed.Should().BeTrue();
        retry.Allowed.Should().BeTrue();
        retry.Takes[0].Existing.Should().BeTrue();
        retry.Takes[0].JobId.Should().Be(first.Takes[0].JobId);

        await using var db = factory.CreateDbContext();
        var reused = await db.ExternalGenerationJobs.SingleAsync(j => j.Id == first.Takes[0].JobId);
        reused.StepResultId.Should().Be(firstAttempt, "a retry reusing the row must not rewrite it");
        var inserted = await db.ExternalGenerationJobs.SingleAsync(j => j.Id == retry.Takes[1].JobId);
        inserted.StepResultId.Should().Be(retryAttempt);
    }

    [Fact]
    public async Task Existing_takes_still_count_toward_step_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        await Seed(factory, MakeJob(projectId, 0.40m, ExternalGenerationJobStatus.Submitted,
            executionId: executionId, requestHash: "hash-1"));

        var result = await NewGate(factory).ReserveAsync(
            Request(projectId, executionId, 0.70m, 0.40m, 0.40m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
        RowCount(factory).Should().Be(1);
    }

    // ---- ledger ----

    [Fact]
    public async Task Ledger_never_credits_a_negative_row()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await Seed(factory,
            MakeJob(projectId, 0.90m),
            MakeJob(projectId, -100m, ExternalGenerationJobStatus.Reserved),
            MakeJob(projectId, -100m, ExternalGenerationJobStatus.Succeeded, actualCost: -100m));

        var result = await NewGate(factory, dailyBudgetCredits: 1.00m, globalDailyBudgetCredits: 1000m).ReserveAsync(
            Request(projectId, 10m, 0.20m), CancellationToken.None);

        result.Allowed.Should().BeFalse("0.90 + 0.20 > 1.00; a -100 row must not credit the ledger");
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
        result.ProjectSpentToday.Should().Be(0.90m);
        RowCount(factory).Should().Be(3);
    }

    [Fact]
    public async Task Ledger_charges_actual_cost_when_greater_than_estimate()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await Seed(factory, MakeJob(projectId, 0.10m, actualCost: 0.90m));

        var result = await NewGate(factory, dailyBudgetCredits: 1.00m).ReserveAsync(
            Request(projectId, 10m, 0.20m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.ProjectSpentToday.Should().Be(0.90m);
    }

    [Fact]
    public async Task Ignores_failed_jobs()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await Seed(factory, MakeJob(projectId, 19.98m, ExternalGenerationJobStatus.Failed));

        var result = await NewGate(factory).ReserveAsync(Request(projectId, 0.10m, 0.05m), CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.ProjectSpentToday.Should().Be(0m, "Failed jobs must not count");
    }

    // ---- (5) global daily cap ----

    [Fact]
    public async Task Denies_when_global_spend_plus_estimate_exceeds_global_daily_budget()
    {
        var factory = new SharedInMemoryDbFactory();
        await Seed(factory, MakeJob(Guid.NewGuid(), 0.90m), MakeJob(Guid.NewGuid(), 0.05m));

        var result = await NewGate(factory, dailyBudgetCredits: 20m, globalDailyBudgetCredits: 1.00m).ReserveAsync(
            Request(Guid.NewGuid(), 10m, 0.20m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:").And.Contain("the platform's daily limit");
        result.GlobalSpentToday.Should().Be(0.95m);
        result.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(2);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(20, 0)]
    [InlineData(20, -1)]
    public async Task Non_positive_caps_deny_everything(decimal daily, decimal global)
    {
        var factory = new SharedInMemoryDbFactory();

        var result = await NewGate(factory, daily, global).ReserveAsync(
            Request(Guid.NewGuid(), 10m, 0.01m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
        RowCount(factory).Should().Be(0);
    }

    // ---- atomicity ----

    private static async Task<VideoGenerationReservation[]> RunOverlapped(
        int parallelism, Func<int, Task<VideoGenerationReservation>> call)
    {
        // Start barrier: every task is scheduled and parked on the same signal before any
        // of them calls the gate, so the calls genuinely overlap.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var tasks = Enumerable.Range(0, parallelism).Select(i => Task.Run(async () =>
        {
            Interlocked.Increment(ref ready);
            await start.Task;
            return await call(i);
        })).ToArray();

        while (Volatile.Read(ref ready) < parallelism)
        {
            await Task.Delay(1);
        }

        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Concurrent_reservations_for_one_project_never_exceed_daily_cap()
    {
        const int parallelism = 12;
        for (var round = 0; round < 25; round++)
        {
            var factory = new SharedInMemoryDbFactory();
            var gate = NewGate(factory, dailyBudgetCredits: 1.00m, globalDailyBudgetCredits: 1000m);
            var projectId = Guid.NewGuid();

            var results = await RunOverlapped(parallelism, _ =>
                gate.ReserveAsync(Request(projectId, 10m, 0.15m, 0.15m), CancellationToken.None));

            await using var db = factory.CreateDbContext();
            var rows = await db.ExternalGenerationJobs.ToListAsync();
            rows.Sum(r => r.CostEstimate).Should().BeLessThanOrEqualTo(1.00m, $"round {round}");
            results.Count(r => r.Allowed).Should().Be(3, $"round {round}: 3 x 0.30 fits in 1.00, a 4th does not");
            rows.Should().HaveCount(6);
            rows.Select(r => r.Id).Should().BeEquivalentTo(
                results.Where(r => r.Allowed).SelectMany(r => r.Takes).Select(t => t.JobId));
            foreach (var loser in results.Where(r => !r.Allowed))
            {
                loser.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
                loser.Takes.Should().BeEmpty();
            }
        }
    }

    [Fact]
    public async Task Concurrent_reservations_across_projects_never_exceed_global_cap()
    {
        const int parallelism = 12;
        for (var round = 0; round < 25; round++)
        {
            var factory = new SharedInMemoryDbFactory();
            var gate = NewGate(factory, dailyBudgetCredits: 20m, globalDailyBudgetCredits: 1.00m);

            var results = await RunOverlapped(parallelism, _ =>
                gate.ReserveAsync(Request(Guid.NewGuid(), 10m, 0.30m), CancellationToken.None));

            await using var db = factory.CreateDbContext();
            var rows = await db.ExternalGenerationJobs.ToListAsync();
            rows.Sum(r => r.CostEstimate).Should().BeLessThanOrEqualTo(1.00m, $"round {round}");
            results.Count(r => r.Allowed).Should().Be(3, $"round {round}");
            rows.Should().HaveCount(3);
            foreach (var loser in results.Where(r => !r.Allowed))
            {
                loser.DenyReason.Should().StartWith("BUDGET_EXCEEDED:").And.Contain("the platform's daily limit");
                loser.Takes.Should().BeEmpty();
            }
        }
    }

    /// <summary>
    /// The step cap is a CREDIT cap, and this is the boundary that says so: a take a vendor quotes at
    /// $0.96 costs 160 credits at the rate card's own price (1 credit = $0.006 of vendor cost), so it
    /// fits a 160-credit cap exactly and is refused by a 159-credit one. A dollar cap could not tell
    /// those two apart, and the ledger the gate writes is in credits now.
    /// </summary>
    [Fact]
    public async Task The_step_cap_is_compared_in_credits_a_96_cent_take_is_160_credits()
    {
        decimal take = CreditCurrency.CreditsFromUsd(0.96m);
        take.Should().Be(160m);

        var allowedFactory = new SharedInMemoryDbFactory();
        var allowed = await NewGate(allowedFactory, dailyBudgetCredits: 1_000_000m, globalDailyBudgetCredits: 1_000_000m)
            .ReserveAsync(Request(Guid.NewGuid(), take, take), CancellationToken.None);

        allowed.Allowed.Should().BeTrue();
        allowed.Estimated.Should().Be(take);
        RowCount(allowedFactory).Should().Be(1);

        var refusedFactory = new SharedInMemoryDbFactory();
        var refused = await NewGate(refusedFactory, dailyBudgetCredits: 1_000_000m, globalDailyBudgetCredits: 1_000_000m)
            .ReserveAsync(Request(Guid.NewGuid(), take - 1m, take), CancellationToken.None);

        refused.Allowed.Should().BeFalse();
        refused.DenyReason.Should().NotBeNull().And.StartWith("BUDGET_EXCEEDED:");
        refused.DenyReason.Should().Contain("160");
        refused.Takes.Should().BeEmpty();
        RowCount(refusedFactory).Should().Be(0, "a denied reservation must never write");
    }

    [Fact]
    public async Task Denies_when_estimated_exceeds_stepCap()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid projectId = Guid.NewGuid();

        var decision = await NewGate(factory).ReserveAsync(Request(projectId, 0.10m, 0.40m, 0.40m), CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.DenyReason.Should().NotBeNull().And.StartWith("BUDGET_EXCEEDED:").And.Contain("step");
        decision.DenyReason.Should().Contain("0.8 credits");
        decision.DenyReason.Should().Contain("this step's spending limit is 0.1 credits");
        decision.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(0, "a denied reservation must never write");
    }

    [Fact]
    public async Task Allows_when_estimated_within_stepCap_and_no_prior_spend()
    {
        var factory = new SharedInMemoryDbFactory();
        Guid projectId = Guid.NewGuid();

        var decision = await NewGate(factory).ReserveAsync(Request(projectId, 0.10m, 0.05m), CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.DenyReason.Should().BeNull();
        decision.Estimated.Should().Be(0.05m);
        decision.ProjectSpentToday.Should().Be(0m);
        RowCount(factory).Should().Be(1, "an allowed reservation inserts its take's Reserved row");
    }

    [Fact]
    public async Task Denies_when_today_spend_plus_estimated_exceeds_daily_budget()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = NewGate(factory, dailyBudgetCredits: 20m, globalDailyBudgetCredits: 1_000_000m);
        Guid projectId = Guid.NewGuid();

        // Pre-seed a job from today
        await Seed(factory, MakeJob(projectId, 19.98m, ExternalGenerationJobStatus.Succeeded));

        // The step's own MaxSpendCredits would allow 0.05m, but 19.98m + 0.05m = 20.03m > 20.0m daily cap
        var decision = await gate.ReserveAsync(Request(projectId, 0.10m, 0.05m), CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.DenyReason.Should().NotBeNull().And.StartWith("BUDGET_EXCEEDED:").And.Contain("project");
        decision.ProjectSpentToday.Should().Be(19.98m);
        decision.Takes.Should().BeEmpty();
        RowCount(factory).Should().Be(1, "a denied reservation must never write");
    }

    [Fact]
    public async Task Ignores_jobs_for_different_projects()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = NewGate(factory);
        Guid projectA = Guid.NewGuid();
        Guid projectB = Guid.NewGuid();

        // Pre-seed a huge job for a different project
        await Seed(factory, MakeJob(projectB, 19.98m, ExternalGenerationJobStatus.Succeeded));

        // Project A's decision should not be affected
        var decision = await gate.ReserveAsync(Request(projectA, 0.10m, 0.05m), CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.ProjectSpentToday.Should().Be(0m);
    }

    [Fact]
    public async Task Ignores_jobs_from_yesterday()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = NewGate(factory);
        Guid projectId = Guid.NewGuid();

        // Pre-seed a job from yesterday
        var yesterday = DateTime.UtcNow.Date.AddSeconds(-1);
        await Seed(factory, MakeJob(projectId, 19.98m, ExternalGenerationJobStatus.Succeeded, yesterday));

        var decision = await gate.ReserveAsync(Request(projectId, 0.10m, 0.05m), CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.ProjectSpentToday.Should().Be(0m, "Yesterday's spend must not count toward today's budget");
    }

    [Fact]
    public async Task Ignores_cancelled_jobs()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = NewGate(factory);
        Guid projectId = Guid.NewGuid();

        // Pre-seed a cancelled job (should not count)
        await Seed(factory, MakeJob(projectId, 19.98m, ExternalGenerationJobStatus.Cancelled));

        var decision = await gate.ReserveAsync(Request(projectId, 0.10m, 0.05m), CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.ProjectSpentToday.Should().Be(0m, "Cancelled jobs must not count");
    }

    [Theory]
    [MemberData(nameof(GenerateRandomSpendPairs))]
    public async Task Allows_when_estimated_within_stepBudget_and_no_prior_spend(decimal estimated, decimal stepCap)
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = NewGate(factory);
        Guid projectId = Guid.NewGuid();

        var decision = await gate.ReserveAsync(Request(projectId, stepCap, estimated), CancellationToken.None);

        // Expected: allowed if and only if estimated <= stepCap (both caps are far above 10.01)
        bool expectedAllowed = estimated <= stepCap;
        decision.Allowed.Should().Be(expectedAllowed,
            $"estimated={estimated}, max={stepCap}, should be allowed={expectedAllowed}");
        RowCount(factory).Should().Be(expectedAllowed ? 1 : 0);
    }

    /// <summary>
    /// Generates at least 20 randomly ordered (estimated, stepCap) pairs
    /// for the parameterized test sweep. Pairs ensure variety: some allowed, some denied.
    /// </summary>
    public static IEnumerable<object[]> GenerateRandomSpendPairs()
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var pairs = new List<(decimal, decimal)>();

        for (int i = 0; i < 20; i++)
        {
            // Generate random values between 0.01 and 10.00
            decimal maxSpend = (decimal)(random.NextDouble() * 9.99 + 0.01);
            decimal estimated = (decimal)(random.NextDouble() * 10.00 + 0.01);
            pairs.Add((estimated, maxSpend));
        }

        return pairs.Select(p => new object[] { p.Item1, p.Item2 }).ToList();
    }

    // ==========================================================================================
    // Quote provenance: the ledger records the VENDOR's exact figure (the async price oracle)
    // ==========================================================================================
    //
    // THE RULE, and why it lives in the component that writes money: option B makes "the ledger
    // records the vendor's exact figure" an invariant of VideoGenerationBudgetGate rather than a
    // convention of one caller. An oracle-priced take arrives carrying the quote it was priced
    // from; if its CostEstimate is not that quote's own TotalUsd — a re-multiplied per-second
    // rate, or a drifted total — the whole reservation is refused with ZERO rows. Trusting the
    // caller would mean a future caller could persist a figure the vendor never quoted, and the
    // ledger is what every cap is charged against.
    //
    // The rule sits in the existing malformed-request block: BEFORE the ReservationLock and before
    // any insert, so a refusal serializes nothing and costs nothing. A take with a NULL quote —
    // every table-priced (MiniMax) take — is untouched by it.

    /// <summary>The vendor's own quote for one 1080P/16:9 clip, at one exact duration.</summary>
    private static VideoGenerationPriceQuote VendorQuote(decimal totalUsd, int durationSeconds = 3) =>
        new(
            model: "hf-ep-9f1c2b",
            resolution: "1080P",
            ratio: "16:9",
            durationSeconds: durationSeconds,
            pricePerSecondUsd: totalUsd / durationSeconds,
            totalUsd: totalUsd,
            credits: 12,
            source: VideoGenerationPriceSource.VendorEstimate);

    private static VideoGenerationReservationRequest QuotedRequest(
        Guid projectId, decimal stepCap, params VideoGenerationTakeRequest[] takes) =>
        new(projectId, Guid.NewGuid(), Guid.NewGuid(), ProviderId, stepCap, takes);

    /// <summary>
    /// A quoted take charged anything other than the vendor's own total — converted to credits by
    /// the ONE converter — is DENIED with a <c>CONFIG_INVALID:</c> reason and ZERO rows written,
    /// including the clean take in the same request, because the step is reserved all-or-nothing.
    /// </summary>
    /// <remarks>
    /// The two drifted figures are one decimal increment either side of the vendor's <c>5.00m</c>
    /// expressed in credits (<c>833.3333…</c>), so neither is hypothetical.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_quoted_take_charged_a_figure_that_is_not_the_vendors_own_is_denied_and_writes_nothing(
        bool driftedAboveTheVendorsFigure)
    {
        var factory = new SharedInMemoryDbFactory();
        var quote = VendorQuote(5.00m);
        decimal expected = CreditCurrency.CreditsFromUsd(quote.TotalUsd);
        decimal drift = 0.0000000000000000000000001m;
        decimal driftedFigure = driftedAboveTheVendorsFigure ? expected + drift : expected - drift;
        driftedFigure.Should().NotBe(expected);

        var clean = new VideoGenerationTakeRequest("clip-1", "hash-1", expected, quote);
        var drifted = new VideoGenerationTakeRequest("clip-2", "hash-2", driftedFigure, quote);

        var result = await NewGate(factory).ReserveAsync(
            QuotedRequest(Guid.NewGuid(), 100m, clean, drifted), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().NotBeNull().And.StartWith("CONFIG_INVALID:");
        result.DenyReason.Should().Contain("clip-2", "the refusal names the take that drifted");
        result.DenyReason.Should().Contain("5.00", "and states the vendor's own figure");
        result.DenyReason.Should().Contain(driftedFigure.ToString(), "and the figure the take was being charged at");
        result.Takes.Should().BeEmpty();

        RowCount(factory).Should().Be(
            0, "the provenance check runs before the lock and before any insert, so a refusal can never partially write");
    }

    /// <summary>
    /// The same take charged the vendor's own total in credits is ALLOWED, and the row records that
    /// exact figure — the positive half of the invariant, without which the rule above would merely
    /// be a denial.
    /// </summary>
    [Fact]
    public async Task A_quoted_take_charged_the_vendors_own_total_is_allowed_and_the_row_records_that_figure()
    {
        var factory = new SharedInMemoryDbFactory();
        var quote = VendorQuote(5.00m);
        decimal expected = CreditCurrency.CreditsFromUsd(quote.TotalUsd);

        var result = await NewGate(factory).ReserveAsync(
            QuotedRequest(Guid.NewGuid(), 1_000_000m, new VideoGenerationTakeRequest("clip-1", "hash-1", expected, quote)),
            CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.DenyReason.Should().BeNull();
        result.Estimated.Should().Be(expected);

        await using var db = factory.CreateDbContext();
        var row = await db.ExternalGenerationJobs.SingleAsync();
        row.CostEstimate.Should().Be(expected);
        row.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
    }

    /// <summary>
    /// A take with <c>Quote == null</c> — every table-priced (MiniMax) take — behaves exactly as it
    /// always did: no provenance rule applies, because there is no vendor quote to be faithful to,
    /// and the published per-second table is the price.
    /// </summary>
    [Fact]
    public async Task A_take_with_no_quote_is_unaffected_by_the_provenance_rule()
    {
        var factory = new SharedInMemoryDbFactory();

        var result = await NewGate(factory).ReserveAsync(
            Request(Guid.NewGuid(), 10m, 0.48m), CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Estimated.Should().Be(0.48m);

        await using var db = factory.CreateDbContext();
        var row = await db.ExternalGenerationJobs.SingleAsync();
        row.CostEstimate.Should().Be(0.48m);
    }

    /// <summary>
    /// A provenance fault is a CONFIG fault, not a budget one: the check runs in rule (1), before
    /// the lock and before the step cap, so a request whose estimate also exceeds
    /// <c>MaxSpendCredits</c> is refused as <c>CONFIG_INVALID:</c> rather than reported as an
    /// affordability problem it would not have had.
    /// </summary>
    [Fact]
    public async Task A_provenance_fault_reads_as_CONFIG_INVALID_even_when_the_step_cap_would_also_refuse()
    {
        var factory = new SharedInMemoryDbFactory();
        var quote = VendorQuote(5.00m);

        var result = await NewGate(factory).ReserveAsync(
            QuotedRequest(Guid.NewGuid(), 0.01m, new VideoGenerationTakeRequest("clip-1", "hash-1", 9.99m, quote)),
            CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().NotBeNull().And.StartWith("CONFIG_INVALID:");
        result.DenyReason.Should().NotContain("BUDGET_EXCEEDED");
        RowCount(factory).Should().Be(0);
    }
}
