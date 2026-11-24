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
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// T-014 (QA of the origin/master merge into #303): pins on the composition of #303's run hold plan
/// with #308's pixel factor that the merge's own tests leave open.
/// </summary>
public sealed class MergedResolutionPlanQaTests
{
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid DirectorId = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, AgentType> AgentTypes = new Dictionary<Guid, AgentType>
    {
        [AuthorId] = AgentType.AuthorAgent,
        [DirectorId] = AgentType.DirectorAgent
    };

    public static TheoryData<string, int?> ShapesAtEveryResolution()
    {
        TheoryData<string, int?> data = [];
        foreach (string shape in new[] { "author_compile", "timeline", "revise", "full" })
        {
            foreach (int? resolution in new int?[] { null, 720, 1080, 2160 })
            {
                data.Add(shape, resolution);
            }
        }

        return data;
    }

    /// <summary>
    /// docs/metering-and-billing.md "The pre-run estimate": on an all-cold run with no display history,
    /// <c>RequiredMillis</c> is at least the shown <c>LowMillis</c> (plus fixed-price holds). The merge scales
    /// the display and the holds by different rules (TimelineRender's display moves, its hold does not), so
    /// the claim is checked at every resolution, together with the two "no hold lower than either branch"
    /// floors on the pure estimator.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShapesAtEveryResolution))]
    public void On_a_cold_start_the_hold_covers_the_shown_low_and_both_floors_at_every_resolution(string shape, int? resolution)
    {
        WorkflowStep[] steps = Shape(shape);
        double factor = OutputResolution.PixelFactor(resolution);
        // What the API passes: the run's resolution; the shared plan decides which steps render the output
        // from the display's agent types (the Author does).
        DisplayCreditInputs display = DisplayCreditInputs.Empty with { AgentTypes = AgentTypes };

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, null, display, SeedProfilePricer.Instance, resolution);
        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, AgentTypes, SeedProfilePricer.Instance);

        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(estimate.LowMillis, $"{shape} at {resolution}: the hold covers the shown low");
        for (int i = 0; i < steps.Length; i++)
        {
            WorkflowStep step = steps[i];
            long hold = estimate.Steps.Single(s => s.StepOrder == step.StepOrder).HoldMillis;
            bool renders = ResolutionHoldScaling.RendersOutput(step.StepType, AgentTypes.GetValueOrDefault(step.AgentDefinitionId));
            long oldDefault = new StepCreditEstimator([]).Estimate(step.StepType, step.AgentDefinitionId).HoldMillis;

            hold.Should().BeGreaterThanOrEqualTo(plan.HoldsMillis[i], $"{shape} step {step.StepOrder} at {resolution}: #303's plan alone");
            long resolutionAlone = renders && factor > 1.0 ? (long)Math.Round(oldDefault * factor) : oldDefault;
            hold.Should().BeGreaterThanOrEqualTo(resolutionAlone, $"{shape} step {step.StepOrder} at {resolution}: #308's rule alone");
        }
    }

    /// <summary>
    /// Requirement 6: past-run totals are filtered to the same OutputResolution, Auto (null) included: an
    /// Auto estimate counts only Auto runs (not explicit 1080p ones), and an explicit 1080p estimate only 1080p runs.
    /// </summary>
    [Fact]
    public async Task Past_run_totals_match_the_resolution_exactly_with_auto_as_its_own_bucket()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid org = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        SeedRun(db, org, workflowId, stepId, null, 1_000);
        SeedRun(db, org, workflowId, stepId, null, 2_000);
        SeedRun(db, org, workflowId, stepId, 1080, 10_000);
        SeedRun(db, org, workflowId, stepId, 2160, 40_000);
        SeedRun(db, org, workflowId, stepId, 720, 500);
        await db.SaveChangesAsync();

        async Task<long[]> TotalsAt(int? resolution) => [.. (await DisplayCreditHistory.LoadRunTotalsAsync(
            db.UsageEvents, db.WorkflowStepResults, db.WorkflowExecutions,
            org, Past.AddDays(-1), workflowId, [], 100, resolution, CancellationToken.None)).Order()];

        (await TotalsAt(null)).Should().Equal(1_000, 2_000);
        (await TotalsAt(1080)).Should().Equal(10_000);
        (await TotalsAt(2160)).Should().Equal(40_000);
        (await TotalsAt(720)).Should().Equal(500);
    }

    private static void SeedRun(InferenceApiDbContext db, Guid org, Guid workflowId, Guid stepId, int? resolution, long credits)
    {
        Guid executionId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            ProjectId = Guid.NewGuid(),
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past,
            StartedAt = Past,
            OutputResolution = resolution
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = executionId,
            WorkflowStepId = stepId,
            Status = StepStatus.Completed,
            ExecutedAt = Past
        });
        db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            ProjectId = Guid.NewGuid(),
            ExecutionId = executionId,
            StepResultId = resultId,
            Source = UsageSource.Workflow,
            Kind = UsageKind.LlmInputTokens,
            Quantity = 1,
            ProviderKind = InferenceProviderKind.OpenAICompatible,
            ModelName = "test",
            CreditsMillis = credits,
            IdempotencyKey = $"{resultId:N}:0",
            OccurredAt = Past
        });
    }

    private static WorkflowStep[] Shape(string shape) => shape switch
    {
        "author_compile" => [Step(1, StepType.Agent, AuthorId), Step(2, StepType.VideoCompile, DirectorId)],
        "timeline" => [Step(1, StepType.Agent, DirectorId), Step(2, StepType.TimelineRender, DirectorId)],
        "revise" => [Step(1, StepType.Revise, DirectorId)],
        "full" =>
        [
            Step(1, StepType.Agent, DirectorId), Step(2, StepType.Agent, AuthorId), Step(3, StepType.VideoAnalyze, DirectorId),
            Step(4, StepType.Revise, DirectorId), Step(5, StepType.VideoCompile, DirectorId), Step(6, StepType.TimelineRender, DirectorId)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static WorkflowStep Step(int order, StepType type, Guid agent) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        AgentDefinitionId = agent
    };
}
