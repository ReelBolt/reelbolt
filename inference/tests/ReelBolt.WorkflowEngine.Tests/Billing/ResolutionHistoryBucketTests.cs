#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// QA-15: a render step's credit history is bucketed by the output resolution its run chose. The hold
/// reads the reference samples (Auto and 1080p, pixel factor 1) and scales them, and a resolution's own
/// samples may only RAISE the hold above that scaled reference, never lower it. Steps that do not render
/// the output keep pooling every resolution. The display range uses a resolution's own samples unscaled
/// once there are enough of them, else the reference scaled.
/// </summary>
public sealed class ResolutionHistoryBucketTests
{
    private static readonly Guid Compiler = Guid.Parse("c0c0c0c0-0000-4000-8000-0000000000c1");

    private static readonly AgentDefinition Director = new()
    {
        Id = Guid.Parse("d1d1d1d1-0000-4000-8000-0000000000d1"),
        Name = "Director",
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.DirectorAgent,
        IsBuiltIn = true,
        CreatedAt = DateTime.UtcNow
    };

    private static WorkflowStep Compile() =>
        new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = Compiler };

    private static IEnumerable<StepCreditSample> Samples(StepType type, Guid agent, int? resolution, params long[] credits)
        => credits.Select(c => new StepCreditSample(type, agent, c, resolution));

    private static IEnumerable<StepCreditSample> Repeat(StepType type, Guid agent, int? resolution, long credits, int count)
        => Enumerable.Repeat(new StepCreditSample(type, agent, credits, resolution), count);

    private static long HoldAt(IReadOnlyList<WorkflowStep> steps, IEnumerable<StepCreditSample> samples, int? resolution) =>
        RunCreditHolds.Plan(
            steps,
            new StepCreditEstimator(samples),
            null,
            ColdStartStepPricing.KnownAgentTypes(steps),
            SeedProfilePricer.Instance,
            resolution).HoldsMillis[0];

    private static long ColdPlanHold(IReadOnlyList<WorkflowStep> steps) =>
        RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, ColdStartStepPricing.KnownAgentTypes(steps), SeedProfilePricer.Instance).HoldsMillis[0];

    private static StepCreditSample[] Mixed() =>
    [
        .. Repeat(StepType.VideoCompile, Compiler, null, 10_000, 5),
        .. Repeat(StepType.VideoCompile, Compiler, 2160, 40_000, 5)
    ];

    [Fact]
    public void MixedHistory_4KSamplesDoNotInflateTheReferenceHold()
    {
        HoldAt([Compile()], Mixed(), null).Should().Be(10_000, "the reference hold is the p90 of the Auto/1080p samples only");
    }

    [Fact]
    public void MixedHistory_4KHold_IsMaxOfScaledReferenceAndNativeP90()
    {
        HoldAt([Compile()], Mixed(), 2160).Should().Be(Math.Max(10_000 * 4, 40_000));
    }

    [Fact]
    public void Only4KHistory_ReferenceIsCold_4KHoldAtLeastColdTimesFour()
    {
        WorkflowStep[] steps = [Compile()];
        StepCreditSample[] samples = [.. Samples(StepType.VideoCompile, Compiler, 2160, 300_000, 310_000, 320_000, 330_000, 340_000)];
        long cold = ColdPlanHold(steps);
        cold.Should().BePositive();

        HoldAt(steps, samples, null).Should().Be(cold, "with no Auto/1080p samples the reference is the cold plan");
        HoldAt(steps, samples, 1080).Should().Be(cold);
        long native = CreditPercentiles.Continued([300_000, 310_000, 320_000, 330_000, 340_000], 0.9);
        HoldAt(steps, samples, 2160).Should().Be(Math.Max(cold * 4, native));
    }

    [Fact]
    public void NativeBucket_NeverLowersTheScaledReference()
    {
        StepCreditSample[] samples =
        [
            .. Repeat(StepType.VideoCompile, Compiler, null, 10_000, 5),
            .. Repeat(StepType.VideoCompile, Compiler, 2160, 20_000, 5)
        ];

        HoldAt([Compile()], samples, 2160).Should().Be(40_000, "cheaper 4K history never lowers the scaled reference");
    }

    [Fact]
    public void SmallerResolution_NeverShrinks()
    {
        StepCreditSample[] samples =
        [
            .. Repeat(StepType.VideoCompile, Compiler, 1080, 10_000, 5),
            .. Repeat(StepType.VideoCompile, Compiler, 720, 3_000, 5)
        ];

        HoldAt([Compile()], samples, 720).Should().Be(10_000, "a 720p run holds no less than the reference hold");
    }

    [Fact]
    public void NonRenderStep_PoolsAllResolutions()
    {
        WorkflowStep director = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director };
        long[] auto = [10_000, 11_000, 12_000, 13_000, 14_000];
        long[] uhd = [40_000, 41_000, 42_000, 43_000, 44_000];
        StepCreditSample[] samples = [.. Samples(StepType.Agent, Director.Id, null, auto), .. Samples(StepType.Agent, Director.Id, 2160, uhd)];
        long pooled = CreditPercentiles.Continued([.. auto.Concat(uhd).Order()], 0.9);

        HoldAt([director], samples, null).Should().Be(pooled);
        HoldAt([director], samples, 2160).Should().Be(pooled);
    }

    [Fact]
    public void TooFewNativeSamples_FallsBackToScaledReference()
    {
        StepCreditSample[] samples =
        [
            .. Repeat(StepType.VideoCompile, Compiler, null, 10_000, 5),
            .. Repeat(StepType.VideoCompile, Compiler, 2160, 90_000, StepCreditEstimator.DefaultMinSamples - 1)
        ];

        HoldAt([Compile()], samples, 2160).Should().Be(40_000);
    }

    [Fact]
    public void Plan_FlagsTheStepsThatRenderOutput()
    {
        WorkflowStep director = new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director };
        WorkflowStep[] steps = [Compile(), director];

        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, ColdStartStepPricing.KnownAgentTypes(steps), SeedProfilePricer.Instance, 2160);

        plan.RendersOutput.Should().Equal(true, false);
    }

    /// <summary>
    /// QA-T004 (Global Constraint "Auto unchanged"): for an org whose history has no explicit-resolution
    /// runs, an Auto run's holds are master's — every warm step's pooled p90
    /// (<see cref="StepCreditEstimator.Estimate(StepType, Guid?, long?, decimal?, bool)"/> with referenceOnly false, the default), render
    /// steps included — and its display is the pooled p25/p75. Explicit 1080p history is the same reference.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1080)]
    public void Auto_WithOnlyReferenceHistory_HoldsAndDisplayAreThePooledHistory(int? historyResolution)
    {
        AgentDefinition author = new()
        {
            Id = Guid.Parse("a1a1a1a1-0000-4000-8000-0000000000a1"),
            Name = "Author",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        WorkflowStep[] steps =
        [
            Compile(),
            new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = author.Id, AgentDefinition = author },
            new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director },
            new() { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.TimelineRender, AgentDefinitionId = Compiler }
        ];
        long[] compile = [9_000, 10_000, 12_000, 15_000, 21_000];
        long[] authoring = [40_000, 45_000, 47_000, 60_000, 90_000];
        long[] directing = [5_000, 6_000, 7_500, 8_000, 13_000];
        long[] rendering = [2_000, 2_500, 3_000, 3_200, 7_000];
        StepCreditSample[] samples =
        [
            .. Samples(StepType.VideoCompile, Compiler, historyResolution, compile),
            .. Samples(StepType.Agent, author.Id, historyResolution, authoring),
            .. Samples(StepType.Agent, Director.Id, historyResolution, directing),
            .. Samples(StepType.TimelineRender, Compiler, historyResolution, rendering)
        ];
        var pooled = new StepCreditEstimator(samples);
        IReadOnlyDictionary<Guid, AgentType> types = ColdStartStepPricing.KnownAgentTypes(steps);

        RunHoldPlan plan = RunCreditHolds.Plan(steps, pooled, null, types, SeedProfilePricer.Instance, null);

        plan.RendersOutput.Should().Equal(true, true, false, true);
        plan.HoldsMillis.Should().Equal(steps.Select(s => pooled.Estimate(s.StepType, s.AgentDefinitionId).HoldMillis));
        plan.HoldsMillis.Should().Equal(RunCreditHolds.Plan(steps, pooled, null, types, SeedProfilePricer.Instance).HoldsMillis);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(samples, steps, null, outputResolution: null);
        estimate.RequiredMillis.Should().Be(plan.RequiredMillis);
        foreach ((int order, long[] credits) in new[] { (1, compile), (2, authoring), (3, directing), (4, rendering) })
        {
            WorkflowStepCostEstimate shown = estimate.Steps.Single(s => s.StepOrder == order);
            shown.LowMillis.Should().Be(CreditPercentiles.Continued(credits, 0.25), $"step {order}");
            shown.HighMillis.Should().Be(CreditPercentiles.Continued(credits, 0.75), $"step {order}");
        }
    }

    // ── display ────────────────────────────────────────────────────────────

    private static readonly long[] ReferenceCredits = [10_000, 20_000, 30_000, 40_000, 50_000];
    private static readonly long[] NativeCredits = [100_000, 200_000, 300_000, 400_000, 500_000];

    private static WorkflowCostEstimate DisplayAt4K(long[] native)
    {
        IEnumerable<DisplayStepSample> display =
        [
            .. ReferenceCredits.Select(c => new DisplayStepSample(StepType.VideoCompile, null, Compiler, c, null)),
            .. native.Select(c => new DisplayStepSample(StepType.VideoCompile, null, Compiler, c, 2160))
        ];

        return WorkflowCostEstimator.EstimateFrom(
            [], [Compile()], null,
            DisplayCreditInputs.Empty with { StepSamples = [.. display] },
            SeedProfilePricer.Instance,
            outputResolution: 2160);
    }

    [Fact]
    public void Display_RenderStepAt4K_UsesNativeBucketUnscaled_WhenEnough()
    {
        WorkflowStepCostEstimate step = DisplayAt4K(NativeCredits).Steps.Single();

        step.LowMillis.Should().Be(CreditPercentiles.Continued(NativeCredits, 0.25));
        step.HighMillis.Should().Be(CreditPercentiles.Continued(NativeCredits, 0.75));
    }

    [Fact]
    public void Display_RenderStepAt4K_FallsBackToScaledReference()
    {
        WorkflowStepCostEstimate step = DisplayAt4K([.. NativeCredits.Take(StepCreditEstimator.DefaultMinSamples - 1)]).Steps.Single();

        step.LowMillis.Should().Be(CreditPercentiles.Continued(ReferenceCredits, 0.25) * 4);
        step.HighMillis.Should().Be(CreditPercentiles.Continued(ReferenceCredits, 0.75) * 4);
    }

    // ── the queries ────────────────────────────────────────────────────────

    private static readonly Guid Org = Guid.Parse("0e0e0e0e-0000-4000-8000-00000000000e");

    /// <summary>One step with three charged results: under a 4K execution, an Auto one, and one whose execution row is gone.</summary>
    private static async Task<InferenceApiDbContext> SeedAsync()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid definitionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        DateTime past = DateTime.UtcNow.AddDays(-1);
        db.AgentDefinitions.Add(new AgentDefinition { Id = Compiler, Name = "c", AgentType = AgentType.DirectorAgent });
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "h", ProjectId = projectId });
        db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definitionId, StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = Compiler });

        (int? Resolution, bool HasRow)[] runs = [(2160, true), (null, true), (2160, false)];
        int n = 0;
        foreach ((int? resolution, bool hasRow) in runs)
        {
            Guid executionId = Guid.NewGuid();
            if (hasRow)
            {
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId,
                    WorkflowDefinitionId = definitionId,
                    ProjectId = projectId,
                    OrganizationId = Org,
                    Status = ExecutionStatus.Passed,
                    OutputResolution = resolution,
                    QueuedAt = past
                });
            }

            Guid resultId = Guid.NewGuid();
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                ExecutedAt = past
            });
            db.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = Org,
                ProjectId = projectId,
                ExecutionId = executionId,
                StepResultId = resultId,
                Source = UsageSource.Workflow,
                Kind = UsageKind.EncodeOutputSeconds,
                Quantity = 1,
                ProviderKind = InferenceProviderKind.OpenAICompatible,
                ModelName = "test",
                CreditsMillis = 1_000 * ++n,
                IdempotencyKey = $"{resultId:N}:0",
                OccurredAt = past
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task HistoryQuery_TagsSamplesWithExecutionResolution_AndKeepsOrphans()
    {
        await using InferenceApiDbContext db = await SeedAsync();
        DateTime since = DateTime.UtcNow.AddDays(-30);

        IReadOnlyList<StepCreditSample> hold = await StepCreditHistoryQuery.LoadAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
            Org, since, 100, CancellationToken.None);
        IReadOnlyList<DisplayStepSample> display = await DisplayCreditHistory.LoadStepSamplesAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions, db.AgentDefinitions,
            Org, since, 100, CancellationToken.None);

        hold.OrderBy(s => s.CreditsMillis).Select(s => (s.CreditsMillis, s.OutputResolution))
            .Should().Equal((1_000L, (int?)2160), (2_000L, (int?)null), (3_000L, (int?)null));
        display.OrderBy(s => s.CreditsMillis).Select(s => (s.CreditsMillis, s.OutputResolution))
            .Should().Equal((1_000L, (int?)2160), (2_000L, (int?)null), (3_000L, (int?)null));
    }

    // SEC-EF-6: above the cap, both queries (the API's and the engine's) keep the SAME rows: the newest.
    [Fact]
    public async Task HistoryQueries_AboveTheCap_KeepTheNewestSamples()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using (db)
        {
            Guid definitionId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Guid stepId = Guid.NewGuid();
            Guid executionId = Guid.NewGuid();
            db.AgentDefinitions.Add(new AgentDefinition { Id = Compiler, Name = "c", AgentType = AgentType.DirectorAgent });
            db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "h", ProjectId = projectId });
            db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definitionId, StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = Compiler });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = definitionId,
                ProjectId = projectId,
                OrganizationId = Org,
                Status = ExecutionStatus.Passed,
                QueuedAt = DateTime.UtcNow.AddDays(-6)
            });

            // Oldest first, so an unordered Take keeps the OLDEST rows (the store's insertion order).
            for (int daysAgo = 5; daysAgo >= 1; daysAgo--)
            {
                DateTime at = DateTime.UtcNow.AddDays(-daysAgo);
                Guid resultId = Guid.NewGuid();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = resultId,
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Completed,
                    ExecutedAt = at
                });
                db.UsageEvents.Add(new UsageEvent
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = Org,
                    ProjectId = projectId,
                    ExecutionId = executionId,
                    StepResultId = resultId,
                    Source = UsageSource.Workflow,
                    Kind = UsageKind.EncodeOutputSeconds,
                    Quantity = 1,
                    ProviderKind = InferenceProviderKind.OpenAICompatible,
                    ModelName = "test",
                    CreditsMillis = 1_000 * daysAgo,
                    IdempotencyKey = $"{resultId:N}:0",
                    OccurredAt = at
                });
            }
            await db.SaveChangesAsync();
            DateTime since = DateTime.UtcNow.AddDays(-30);

            IReadOnlyList<StepCreditSample> hold = await StepCreditHistoryQuery.LoadAsync(
                db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
                Org, since, 2, CancellationToken.None);
            IReadOnlyList<DisplayStepSample> display = await DisplayCreditHistory.LoadStepSamplesAsync(
                db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions, db.AgentDefinitions,
                Org, since, 2, CancellationToken.None);

            hold.Select(s => s.CreditsMillis).Should().BeEquivalentTo([1_000L, 2_000L], "the two newest results");
            display.Select(s => s.CreditsMillis).Should().BeEquivalentTo([1_000L, 2_000L], "the two newest results");
        }
    }
}
