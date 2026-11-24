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
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// QA pins for saas-estimate-range (T-004): the display range's history is scoped to the caller's
/// organization and to the workflow, the hold and the 402 decision do not move when every display
/// dependency fails, and malformed structure never inverts the range at the estimator level.
/// </summary>
public sealed class EstimateRangeQaTests
{
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    // -----------------------------------------------------------------
    // Tenant and workflow scoping of the display history
    // -----------------------------------------------------------------

    [Fact]
    public async Task Another_organizations_runs_of_the_same_workflow_are_never_past_runs()
    {
        using var db = NewApiContext();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000, 60_000 })
        {
            SeedRun(db, theirs, workflow, (step, [total]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await Estimator(db).EstimateAsync(mine, [step], CancellationToken.None);

        estimate.PastRunCount.Should().Be(0);
        estimate.RangeBasis.Should().Be(CreditRangeBasis.FirstRun);
        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.Default));
    }

    [Fact]
    public async Task Another_organizations_samples_never_fill_the_exact_or_agent_type_bucket()
    {
        using var db = NewApiContext();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, AgentType.NarrationWriter);
        // Same agent definition id AND a sibling of the same agent type, both charged, both theirs.
        AgentDefinition sibling = new() { Id = Guid.NewGuid(), Name = "sibling", AgentType = AgentType.NarrationWriter };
        db.AgentDefinitions.Add(sibling);
        WorkflowStep siblingStep = AddStep(db, workflow, 2, StepType.Agent, sibling.Id);
        for (int i = 0; i < 5; i++)
        {
            SeedRun(db, theirs, workflow, (step, [900_000]), (siblingStep, [900_000]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await Estimator(db).EstimateAsync(mine, [step], CancellationToken.None);

        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.Default));
        estimate.RangeBasis.Should().Be(CreditRangeBasis.FirstRun);
        estimate.HasHistory.Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_review_passes_never_set_the_loop_model()
    {
        using var db = NewApiContext();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, AgentType.DirectorAgent);
        AgentDefinition reviewer = new() { Id = Guid.NewGuid(), Name = "r", AgentType = AgentType.VideoReviewAgent };
        db.AgentDefinitions.Add(reviewer);
        WorkflowStep loop = AddStep(db, workflow, 2, StepType.ReviewLoop, reviewer.Id, loopTarget: 1, maxIterations: 3);
        for (int i = 0; i < 4; i++)
        {
            // Their runs always pass review first time: one loop result each.
            SeedRun(db, theirs, workflow, (body, [1_000]), (loop, [500]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await Estimator(db).EstimateAsync(mine, [body, loop], CancellationToken.None);

        // Mine keeps the cold-start 1–2 passes (mean 1.5), not their 1.0.
        estimate.Steps.Should().OnlyContain(s => s.ExpectedRuns == RunCreditRange.ColdStartMeanPasses);
    }

    [Fact]
    public async Task Runs_of_a_different_workflow_in_the_same_organization_are_not_past_runs()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition other, WorkflowStep otherStep) = SeedWorkflow(db, AgentType.DirectorAgent);
        (WorkflowDefinition _, WorkflowStep step) = SeedWorkflow(db, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, other, (otherStep, [total]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await Estimator(db).EstimateAsync(org, [step], CancellationToken.None);

        estimate.PastRunCount.Should().Be(0);
        estimate.RangeBasis.Should().NotBe(CreditRangeBasis.PastRuns);
    }

    // -----------------------------------------------------------------
    // The hold and the 402 do not move with the display path
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_hold_and_the_402_are_unchanged_when_every_display_dependency_fails()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, AgentType.DirectorAgent);
        AgentDefinition reviewer = new() { Id = Guid.NewGuid(), Name = "r", AgentType = AgentType.VideoReviewAgent };
        db.AgentDefinitions.Add(reviewer);
        WorkflowStep loop = AddStep(db, workflow, 2, StepType.ReviewLoop, reviewer.Id, loopTarget: 1, maxIterations: 3);
        WorkflowStep compile = AddStep(db, workflow, 3, StepType.VideoCompile, Guid.NewGuid());
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, workflow, (body, [total]), (loop, [500, 700]), (compile, [0]));
        }
        await db.SaveChangesAsync();

        var resolver = new Mock<IInferenceProviderResolver>(MockBehavior.Loose);
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver down"));
        var rates = new Mock<IModelPriceService>();
        rates.Setup(r => r.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate cards down"));

        WorkflowStep[] steps = [body, loop, compile];
        WorkflowCostEstimator estimator = Estimator(db, resolver.Object, rates.Object);

        // What master computed: the hold query, then Σ StepCreditEstimator holds.
        IReadOnlyList<StepCreditSample> samples = await StepCreditHistoryQuery.LoadAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
            org, DateTime.UtcNow.AddDays(-new StepCreditEstimateOptions().WindowDays),
            new StepCreditEstimateOptions().MaxSamples, CancellationToken.None);
        var hold = new StepCreditEstimator(samples, StepCreditEstimator.DefaultMinSamples);
        long expected = steps.Sum(s => hold.Estimate(s.StepType, s.AgentDefinitionId, null).HoldMillis);

        WorkflowPreflightResult preflight = await estimator.PreflightAsync(org, steps, CancellationToken.None);

        expected.Should().BePositive();
        preflight.Estimate.RequiredMillis.Should().Be(expected);
        // No credit account: 0 available, so the advisory preflight must still refuse (not fail open).
        preflight.Refused.Should().BeTrue();
        preflight.Estimate.RangeBasis.Should().Be(CreditRangeBasis.PastRuns);
        preflight.Estimate.LowMillis.Should().BeLessThanOrEqualTo(preflight.Estimate.TypicalMillis);
        preflight.Estimate.TypicalMillis.Should().BeLessThanOrEqualTo(preflight.Estimate.HighMillis);
    }

    [Fact]
    public async Task A_balance_between_the_shown_low_and_the_cold_start_requirement_is_refused_with_a_402()
    {
        // on a cold start each cold step holds the larger of its old default and its share of
        // the low edge, so the requirement can sit above the shown low. A balance that covers the low but
        // not the requirement must be refused at preflight, not let through to overdraw step by step.
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep agent) = SeedWorkflow(db, AgentType.NarrationWriter);
        WorkflowStep analyze = AddStep(db, workflow, 2, StepType.VideoAnalyze, Guid.NewGuid());
        WorkflowStep compile = AddStep(db, workflow, 3, StepType.VideoCompile, Guid.NewGuid());
        await db.SaveChangesAsync();
        WorkflowStep[] steps = [agent, analyze, compile];
        WorkflowCostEstimator estimator = Estimator(db);

        WorkflowCostEstimate estimate = await estimator.EstimateAsync(org, steps, CancellationToken.None);
        long before = steps.Sum(s => new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples)
            .Estimate(s.StepType, s.AgentDefinitionId, null).HoldMillis);
        estimate.HasHistory.Should().BeFalse();
        estimate.RequiredMillis.Should().BeGreaterThan(estimate.LowMillis, "the precondition: the old defaults lift the requirement above the low");
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(before);

        CreditAccount account = new() { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = (estimate.LowMillis + estimate.RequiredMillis) / 2 };
        db.CreditAccounts.Add(account);
        await db.SaveChangesAsync();

        WorkflowPreflightResult refused = await estimator.PreflightAsync(org, steps, CancellationToken.None);

        refused.BalanceMillis.Should().BeGreaterThanOrEqualTo(estimate.LowMillis).And.BeLessThan(estimate.RequiredMillis);
        refused.Refused.Should().BeTrue();
        refused.Estimate.RequiredMillis.Should().Be(estimate.RequiredMillis);

        account.BalanceMillis = estimate.RequiredMillis;
        await db.SaveChangesAsync();
        (await estimator.PreflightAsync(org, steps, CancellationToken.None)).Refused.Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Malformed structure, through the whole pure estimator
    // -----------------------------------------------------------------

    public static TheoryData<string> MalformedShapes() =>
    [
        "foreach_max_zero", "foreach_max_negative", "foreach_literal_empty", "foreach_literal_broken",
        "loop_target_past_loop", "loop_target_zero", "loop_target_null", "loop_max_one", "loop_max_negative",
        "overlapping_loops", "parallel_broken_json", "parallel_bad_guid", "video_in_loop", "duplicate_orders"
    ];

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public void Malformed_structure_never_throws_and_never_inverts_the_range(string shape)
    {
        WorkflowStep[] steps = Shape(shape);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        estimate.LowMillis.Should().BeGreaterThanOrEqualTo(0, shape);
        estimate.LowMillis.Should().BeLessThanOrEqualTo(estimate.TypicalMillis, shape);
        estimate.TypicalMillis.Should().BeLessThanOrEqualTo(estimate.HighMillis, shape);
        estimate.FixedMaxMillis.Should().BeGreaterThanOrEqualTo(0, shape);
        estimate.Steps.Should().OnlyContain(s => s.LowMillis >= 0 && s.LowMillis <= s.TypicalMillis && s.TypicalMillis <= s.HighMillis, shape);
        estimate.Steps.Should().OnlyContain(s => double.IsFinite(s.ExpectedRuns) && s.ExpectedRuns >= 0, shape);
        // the cold-start hold re-plan: the hold is the sum of the per-step holds, every charged step holds at least 1
        // millicredit and a not-charged one nothing, whatever the shape.
        estimate.RequiredMillis.Should().Be(estimate.Steps.Sum(s => s.HoldMillis), shape);
        estimate.Steps.Where(s => !StepCreditDefaults.IsNotCharged(Enum.Parse<StepType>(s.StepType)))
            .Should().OnlyContain(s => s.HoldMillis >= 1, shape);
        estimate.Steps.Where(s => StepCreditDefaults.IsNotCharged(Enum.Parse<StepType>(s.StepType)))
            .Should().OnlyContain(s => s.HoldMillis == 0, shape);

        long b0 = steps.Sum(s => new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples)
            .Estimate(s.StepType, s.AgentDefinitionId, null).HoldMillis);
        if (steps.Select(s => s.StepOrder).Distinct().Count() != steps.Length)
        {
            // Two steps at one order cannot be told apart by the plan: every step falls back to B0's low.
            estimate.RequiredMillis.Should().Be(b0, shape);
            return;
        }

        // On a cold start each cold step holds the larger of its old default and its share of the low
        // edge of the variable part (the same loops and profiles the display combines): never below
        // either the low edge or the old figure.
        long video = estimate.Steps.Where(s => s.StepType == nameof(StepType.VideoGenerate)).Sum(s => s.HoldMillis);
        (estimate.RequiredMillis - video).Should().BeGreaterThanOrEqualTo(estimate.LowMillis, shape);
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(b0, shape);
    }

    [Fact]
    public void A_ForEach_with_MaxIterations_int_MaxValue_does_not_overflow()
    {
        WorkflowStep[] steps = [PureStep(1, StepType.Agent), PureStep(2, StepType.ForEach, maxIterations: int.MaxValue)];

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        WorkflowStepCostEstimate forEach = estimate.Steps.Single(s => s.StepOrder == 2);
        double.IsFinite(forEach.ExpectedRuns).Should().BeTrue();
        // Uniform on 1..int.MaxValue: a ForEach always runs at least once, and here about a billion times.
        forEach.ExpectedRuns.Should().BeGreaterThanOrEqualTo(1);
        estimate.LowMillis.Should().BeGreaterThanOrEqualTo(0);
        estimate.LowMillis.Should().BeLessThanOrEqualTo(estimate.TypicalMillis);
        estimate.TypicalMillis.Should().BeLessThanOrEqualTo(estimate.HighMillis);
    }

    // -----------------------------------------------------------------
    // Agent definitions read for Parallel branches are tenant-scoped
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_parallel_branch_reads_only_built_in_or_own_organization_agent_definitions()
    {
        using var db = NewApiContext();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        AgentDefinition foreign = new() { Id = Guid.NewGuid(), Name = "theirs", AgentType = AgentType.AuthorAgent, OrganizationId = theirs };
        AgentDefinition own = new() { Id = Guid.NewGuid(), Name = "mine", AgentType = AgentType.AuthorAgent, OrganizationId = mine };
        AgentDefinition builtIn = new() { Id = Guid.NewGuid(), Name = "platform", AgentType = AgentType.AuthorAgent, IsBuiltIn = true };
        db.AgentDefinitions.AddRange(foreign, own, builtIn);
        await db.SaveChangesAsync();
        WorkflowCostEstimator estimator = Estimator(db);

        async Task<long> TypicalWithBranch(Guid branch)
        {
            WorkflowStep parallel = PureStep(1, StepType.Parallel, parallel: $"[\"{branch}\"]");
            WorkflowCostEstimate estimate = await estimator.EstimateAsync(mine, [parallel], CancellationToken.None);
            return estimate.TypicalMillis;
        }

        long unknown = await TypicalWithBranch(Guid.NewGuid());

        // Another organization's agent is priced as an unknown agent: its type is never read.
        (await TypicalWithBranch(foreign.Id)).Should().Be(unknown);
        // Control: the same agent type is read when it is the caller's own or a platform agent.
        (await TypicalWithBranch(own.Id)).Should().NotBe(unknown);
        (await TypicalWithBranch(builtIn.Id)).Should().NotBe(unknown);
    }

    private static WorkflowStep[] Shape(string shape)
    {
        WorkflowStep agent = PureStep(1, StepType.Agent);
        WorkflowStep second = PureStep(2, StepType.Agent);
        switch (shape)
        {
            case "foreach_max_zero":
                return [agent, PureStep(2, StepType.ForEach, maxIterations: 0)];
            case "foreach_max_negative":
                return [agent, PureStep(2, StepType.ForEach, maxIterations: -5)];
            case "foreach_literal_empty":
                return [agent, PureStep(2, StepType.ForEach, source: "[]")];
            case "foreach_literal_broken":
                return [agent, PureStep(2, StepType.ForEach, source: "[1, 2")];
            case "loop_target_past_loop":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: 7)];
            case "loop_target_zero":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: 0)];
            case "loop_target_null":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: null)];
            case "loop_max_one":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: 1, maxIterations: 1)];
            case "loop_max_negative":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: 1, maxIterations: -3)];
            case "overlapping_loops":
                return [agent, second, PureStep(3, StepType.ReviewLoop, loopTarget: 2), PureStep(4, StepType.ReviewLoop, loopTarget: 1)];
            case "parallel_broken_json":
                return [agent, PureStep(2, StepType.Parallel, parallel: "[not json")];
            case "parallel_bad_guid":
                return [agent, PureStep(2, StepType.Parallel, parallel: "[\"not-a-guid\"]")];
            case "video_in_loop":
                return [agent, PureStep(2, StepType.VideoGenerate), PureStep(3, StepType.ReviewLoop, loopTarget: 1, maxIterations: 4)];
            case "duplicate_orders":
                return [agent, PureStep(1, StepType.ReviewLoop, loopTarget: 1), PureStep(1, StepType.Agent)];
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static WorkflowStep PureStep(
        int order, StepType type, int? loopTarget = null, int maxIterations = 3, string? source = null, string? parallel = null) => new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = Guid.NewGuid(),
            LoopTargetStepOrder = loopTarget,
            MaxIterations = maxIterations,
            LoopSourceExpression = source,
            ParallelAgentIdsJson = parallel
        };

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static InferenceApiDbContext NewApiContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WorkflowCostEstimator Estimator(
        InferenceApiDbContext db, IInferenceProviderResolver? resolver = null, IModelPriceService? rates = null)
        => new(new Scopes(db, resolver, rates), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

    private static (WorkflowDefinition Workflow, WorkflowStep Step) SeedWorkflow(InferenceApiDbContext db, AgentType agentType)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "w",
            ProjectId = Guid.NewGuid(),
            UpdatedAt = Past.AddDays(-1)
        };
        db.WorkflowDefinitions.Add(workflow);
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = agentType.ToString(), AgentType = agentType };
        db.AgentDefinitions.Add(agent);
        return (workflow, AddStep(db, workflow, 1, StepType.Agent, agent.Id));
    }

    private static WorkflowStep AddStep(
        InferenceApiDbContext db, WorkflowDefinition workflow, int order, StepType type, Guid agentId,
        int? loopTarget = null, int maxIterations = 3)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = agentId,
            LoopTargetStepOrder = loopTarget,
            MaxIterations = maxIterations
        };
        db.WorkflowSteps.Add(step);
        return step;
    }

    /// <summary>One Passed execution of <paramref name="workflow"/> owned by <paramref name="org"/>: a charged result per figure.</summary>
    private static void SeedRun(InferenceApiDbContext db, Guid org, WorkflowDefinition workflow, params (WorkflowStep Step, long[] Credits)[] results)
    {
        Guid executionId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflow.Id,
            ProjectId = workflow.ProjectId,
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past,
            StartedAt = Past
        });

        foreach ((WorkflowStep step, long[] credits) in results)
        {
            foreach (long value in credits)
            {
                Guid resultId = Guid.NewGuid();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = resultId,
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = step.Id,
                    Status = StepStatus.Completed,
                    ExecutedAt = Past
                });
                db.UsageEvents.Add(new UsageEvent
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org,
                    ProjectId = workflow.ProjectId,
                    ExecutionId = executionId,
                    StepResultId = resultId,
                    Source = UsageSource.Workflow,
                    Kind = UsageKind.LlmInputTokens,
                    Quantity = 1,
                    ProviderKind = InferenceProviderKind.OpenAICompatible,
                    ModelName = "test",
                    CreditsMillis = value,
                    IdempotencyKey = $"{resultId:N}:0",
                    OccurredAt = Past
                });
            }
        }
    }

    /// <summary>A scope factory serving the seeded context, and optionally a resolver and a rate-card service.</summary>
    private sealed class Scopes(InferenceApiDbContext db, IInferenceProviderResolver? resolver, IModelPriceService? rates)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(InferenceApiDbContext) ? db
            : serviceType == typeof(IInferenceProviderResolver) ? resolver
            : serviceType == typeof(IModelPriceService) ? rates
            : null;

        public void Dispose()
        {
        }
    }
}
