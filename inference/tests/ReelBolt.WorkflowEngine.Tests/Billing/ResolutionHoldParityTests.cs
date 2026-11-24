#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.Shared.Briefs;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// SEC-RR-01: a run's chosen output resolution scales the credit hold of every step that renders
/// the output — VideoCompile, Revise, and an Agent granted <c>SandboxRender</c> (the Author) — by
/// <c>OutputResolution.PixelFactor</c> when above 1, never shrinking it. The API's execute preflight
/// (<see cref="WorkflowCostEstimator"/>) and the engine's per-step hold
/// (<see cref="WorkflowExecutorService.ResolveStepReserveMillisAsync"/>) must agree on the same run,
/// so both are checked against ONE expectation here.
/// <para>
/// The factor is applied to the cold-start hold re-plan run plan (<see cref="RunCreditHolds"/>: a cold step holds the
/// larger of its old default and its share of the cold steps' low edge), which is priced at the 1080p
/// reference, so no hold is below the plan's nor below the old default times the factor.
/// </para>
/// </summary>
public sealed class ResolutionHoldParityTests
{
    private static readonly Guid Org = Guid.Parse("0b0b0b0b-0000-4000-8000-00000000000b");

    private static readonly AgentDefinition Author = Agent(AgentType.AuthorAgent);
    private static readonly AgentDefinition Reviser = Agent(AgentType.CompositionReviser);
    private static readonly AgentDefinition Director = Agent(AgentType.DirectorAgent);

    private static AgentDefinition Agent(AgentType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = type.ToString(),
        Description = "d",
        SystemPrompt = "p",
        AgentType = type,
        IsBuiltIn = true,
        CreatedAt = DateTime.UtcNow
    };

    private static WorkflowStep[] Steps() =>
    [
        new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Author.Id, AgentDefinition = Author },
        new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Revise, AgentDefinitionId = Reviser.Id, AgentDefinition = Reviser },
        new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, AgentDefinitionId = Director.Id, AgentDefinition = Director },
        new() { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director }
    ];

    /// <summary>The old per-step cold-start default of a step (B0's low), before the cold-start hold re-plan and any factor.</summary>
    private static long OldDefault(WorkflowStep step) =>
        new StepCreditEstimator([]).Estimate(step.StepType, step.AgentDefinitionId).HoldMillis;

    /// <summary>The cold-start run plan (no history, seed prices — what both sides see here), before any resolution factor.</summary>
    private static RunHoldPlan Plan(IReadOnlyList<WorkflowStep> steps) =>
        RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, ColdStartStepPricing.KnownAgentTypes(steps), SeedProfilePricer.Instance);

    private static int Multiplier(WorkflowStep step, int? resolution) =>
        step.StepOrder is 1 or 2 or 3 && resolution == 2160 ? 4 : 1;

    /// <summary>The one expectation both sides are held to: render steps' plan hold × factor above 1, others the plan hold.</summary>
    private static long Expected(IReadOnlyList<WorkflowStep> steps, WorkflowStep step, int? resolution) =>
        Plan(steps).HoldsMillis[steps.ToList().IndexOf(step)] * Multiplier(step, resolution);

    [Theory]
    [InlineData(2160)]
    [InlineData(1080)]
    [InlineData(720)]
    [InlineData(null)]
    public async Task The_engine_hold_matches_the_shared_expectation(int? resolution)
    {
        WorkflowExecutorService service = EngineService();
        WorkflowStep[] steps = Steps();
        WorkflowExecution execution = Execution(steps, resolution);

        foreach (WorkflowStep step in steps)
        {
            OldDefault(step).Should().BePositive("a zero base would make the multiplier unobservable");
            long hold = await service.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None);
            hold.Should().Be(Expected(steps, step, resolution), $"step {step.StepOrder} ({step.StepType}/{step.AgentDefinition!.AgentType}) at {resolution}");
        }
    }

    [Theory]
    [InlineData(2160)]
    [InlineData(1080)]
    [InlineData(720)]
    [InlineData(null)]
    public async Task The_api_preflight_hold_matches_the_shared_expectation(int? resolution)
    {
        using InferenceApiDbContext db = NewDb();
        WorkflowStep[] steps = Steps();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(Org, steps, resolution, CancellationToken.None);

        foreach (WorkflowStep step in steps)
        {
            estimate.Steps.Single(s => s.StepOrder == step.StepOrder).HoldMillis
                .Should().Be(Expected(steps, step, resolution), $"step {step.StepOrder} ({step.StepType}) at {resolution}");
        }
        estimate.RequiredMillis.Should().Be(steps.Sum(s => Expected(steps, s, resolution)));
    }

    [Theory]
    [InlineData(2160)]
    [InlineData(1080)]
    [InlineData(720)]
    [InlineData(null)]
    public void No_hold_is_below_what_the_plan_or_the_resolution_rule_alone_would_give(int? resolution)
    {
        WorkflowStep[] steps = Steps();
        foreach (WorkflowStep step in steps)
        {
            long merged = Expected(steps, step, resolution);
            long planAlone = Plan(steps).HoldsMillis[steps.ToList().IndexOf(step)];
            long resolutionAlone = OldDefault(step) * Multiplier(step, resolution);

            merged.Should().BeGreaterThanOrEqualTo(planAlone, $"step {step.StepOrder} at {resolution}: the plan alone");
            merged.Should().BeGreaterThanOrEqualTo(resolutionAlone, $"step {step.StepOrder} at {resolution}: the resolution factor alone");
        }
    }

    [Fact]
    public async Task A_2160p_runs_required_amount_is_the_cold_start_plan_times_the_4k_factor()
    {
        // Every step renders the output, so the whole cold-start plan is multiplied by PixelFactor(2160) = 4.
        WorkflowStep[] steps =
        [
            new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Author.Id, AgentDefinition = Author },
            new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = Director.Id, AgentDefinition = Director }
        ];
        RunHoldPlan plan = Plan(steps);
        plan.UsedFallback.Should().BeFalse();
        plan.RequiredMillis.Should().BeGreaterThanOrEqualTo(steps.Sum(OldDefault), "the cold-start hold re-plan never holds less than the old defaults");
        OutputResolution.PixelFactor(2160).Should().Be(4.0);
        long expected = plan.RequiredMillis * 4;

        using InferenceApiDbContext db = NewDb();
        WorkflowCostEstimator api = ApiEstimator(db);
        WorkflowCostEstimate at4k = await api.EstimateAsync(Org, steps, 2160, CancellationToken.None);
        WorkflowCostEstimate atAuto = await api.EstimateAsync(Org, steps, null, CancellationToken.None);

        atAuto.RequiredMillis.Should().Be(plan.RequiredMillis, "Auto is the plan exactly");
        at4k.RequiredMillis.Should().Be(expected);
        at4k.Steps.Select(s => s.HoldMillis).Should().Equal(plan.HoldsMillis.Select(h => h * 4));

        // The engine reserves the same, step by step.
        WorkflowExecutorService engine = EngineService();
        WorkflowExecution execution = Execution(steps, 2160);
        long reserved = 0;
        foreach (WorkflowStep step in steps)
        {
            reserved += await engine.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None);
        }
        reserved.Should().Be(expected);

        // A non-rendering step in the same 4K run keeps its plan hold: only the render is dearer.
        WorkflowStep[] mixed = [.. steps, new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director }];
        RunHoldPlan mixedPlan = Plan(mixed);
        WorkflowCostEstimate mixed4k = await api.EstimateAsync(Org, mixed, 2160, CancellationToken.None);
        mixed4k.RequiredMillis.Should().Be((mixedPlan.HoldsMillis[0] + mixedPlan.HoldsMillis[1]) * 4 + mixedPlan.HoldsMillis[2]);
    }

    [Fact]
    public async Task A_4k_run_with_an_author_step_holds_four_times_its_plan_hold_on_both_sides()
    {
        WorkflowStep author = Steps()[0];
        long expected = Plan([author]).HoldsMillis[0] * 4;

        long engine = await EngineService().ResolveStepReserveMillisAsync(Execution([author], 2160), author, CancellationToken.None);

        using InferenceApiDbContext db = NewDb();
        WorkflowCostEstimate api = await ApiEstimator(db).EstimateAsync(Org, [author], 2160, CancellationToken.None);

        expected.Should().BeGreaterThanOrEqualTo(OldDefault(author) * 4);
        engine.Should().Be(expected);
        api.RequiredMillis.Should().Be(expected);
    }

    [Fact]
    public async Task A_4k_compile_shows_its_whole_range_four_times_higher_and_a_720p_one_lower_but_holds_no_less()
    {
        WorkflowStep[] steps = [new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = Director.Id, AgentDefinition = Director }];
        using InferenceApiDbContext db = NewDb();
        WorkflowCostEstimator api = ApiEstimator(db);

        WorkflowCostEstimate auto = await api.EstimateAsync(Org, steps, null, CancellationToken.None);
        WorkflowCostEstimate uhd = await api.EstimateAsync(Org, steps, 2160, CancellationToken.None);
        WorkflowCostEstimate hd = await api.EstimateAsync(Org, steps, 720, CancellationToken.None);

        auto.TypicalMillis.Should().BePositive();
        uhd.TypicalMillis.Should().BeCloseTo(auto.TypicalMillis * 4, 4);
        uhd.LowMillis.Should().BeGreaterThan(auto.LowMillis);
        uhd.HighMillis.Should().BeGreaterThan(auto.HighMillis);
        hd.TypicalMillis.Should().BeLessThan(auto.TypicalMillis, "a 720p encode is cheaper to show");
        hd.RequiredMillis.Should().Be(auto.RequiredMillis, "a smaller resolution never shrinks a hold");
        uhd.RequiredMillis.Should().Be(auto.RequiredMillis * 4);
    }

    // ── QA-14: a TimelineRender step renders the run's output too ───────────────

    private static WorkflowStep TimelineRenderStep() =>
        new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.TimelineRender };

    [Fact]
    public void TimelineRender_RendersOutput() =>
        ResolutionHoldScaling.RendersOutput(StepType.TimelineRender, null).Should().BeTrue();

    [Theory]
    [InlineData(2160, 4)]
    [InlineData(720, 1)]
    [InlineData(null, 1)]
    public async Task TimelineRender_HoldScalesAt4K_OnBothSides(int? resolution, int factor)
    {
        WorkflowStep[] steps = [TimelineRenderStep()];
        long expected = Plan(steps).HoldsMillis[0] * factor;
        expected.Should().BePositive("a zero base would make the multiplier unobservable");

        using InferenceApiDbContext db = NewDb();
        WorkflowCostEstimate api = await ApiEstimator(db).EstimateAsync(Org, steps, resolution, CancellationToken.None);
        long engine = await EngineService().ResolveStepReserveMillisAsync(Execution(steps, resolution), steps[0], CancellationToken.None);

        api.RequiredMillis.Should().Be(expected);
        engine.Should().Be(expected);
    }

    [Fact]
    public void TimelineRender_HoldAtLeastDisplayedHigh_At4K()
    {
        // With history the hold is the p90 and the display's high the p75 moved by the factor: a flat
        // history makes them equal at 1080p, so the 4K hold must grow with the display to stay above it.
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.TimelineRender, AgentDefinitionId = Director.Id };
        StepCreditSample[] samples = [.. Enumerable.Repeat(new StepCreditSample(StepType.TimelineRender, Director.Id, 1_000), 5)];

        WorkflowCostEstimate api = WorkflowCostEstimator.EstimateFrom(samples, [step], null, outputResolution: 2160);

        WorkflowStepCostEstimate shown = api.Steps.Single();
        shown.HighMillis.Should().Be(4_000, "the display moves a TimelineRender's whole range with the factor");
        shown.HoldMillis.Should().BeGreaterThanOrEqualTo(shown.HighMillis);
    }

    // ── QA-15: history bucketed by output resolution, the same on both sides ───

    /// <summary>Per (step type, agent): what the history's Auto runs and its 4K runs charged.</summary>
    private static readonly (StepType Type, Guid Agent, long Auto, long Uhd)[] MixedHistory =
    [
        (StepType.Agent, Author.Id, 50_000, 300_000),
        (StepType.Revise, Reviser.Id, 30_000, 50_000),
        (StepType.VideoCompile, Director.Id, 10_000, 60_000),
        (StepType.Agent, Director.Id, 8_000, 20_000),
        (StepType.TimelineRender, Guid.Empty, 10_000, 30_000)
    ];

    /// <summary>
    /// The same usage rows for <see cref="MixedHistory"/>, four step results per resolution, as fresh
    /// entities each call (one set per database). The ids are fixed so both databases hold the same rows.
    /// </summary>
    private static IEnumerable<object> MixedHistoryRows(Guid seed)
    {
        static Guid Id(Guid seed, int n) => new(BitConverter.GetBytes(n).Concat(seed.ToByteArray().Skip(4)).ToArray());

        int n = 0;
        Guid projectId = Id(seed, ++n);
        Guid definitionId = Id(seed, ++n);
        DateTime past = DateTime.UtcNow.AddDays(-1);
        yield return new WorkflowDefinition { Id = definitionId, Name = "h", ProjectId = projectId };
        foreach (int? resolution in new int?[] { null, 2160 })
        {
            Guid executionId = Id(seed, ++n);
            yield return new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = definitionId,
                ProjectId = projectId,
                OrganizationId = Org,
                Status = ExecutionStatus.Passed,
                OutputResolution = resolution,
                QueuedAt = past
            };

            foreach ((StepType type, Guid agent, long auto, long uhd) in MixedHistory)
            {
                Guid stepId = Id(seed, ++n);
                yield return new WorkflowStep { Id = stepId, WorkflowDefinitionId = definitionId, StepOrder = n, StepType = type, AgentDefinitionId = agent };
                for (int k = 0; k < 4; k++)
                {
                    Guid resultId = Id(seed, ++n);
                    yield return new WorkflowStepResult
                    {
                        Id = resultId,
                        WorkflowExecutionId = executionId,
                        WorkflowStepId = stepId,
                        Status = StepStatus.Completed,
                        ExecutedAt = past
                    };
                    yield return new UsageEvent
                    {
                        Id = Id(seed, ++n),
                        OrganizationId = Org,
                        ProjectId = projectId,
                        ExecutionId = executionId,
                        StepResultId = resultId,
                        Source = UsageSource.Workflow,
                        Kind = UsageKind.EncodeOutputSeconds,
                        Quantity = 1,
                        ProviderKind = InferenceProviderKind.OpenAICompatible,
                        ModelName = "test",
                        CreditsMillis = resolution is null ? auto : uhd,
                        IdempotencyKey = $"{resultId:N}:0",
                        OccurredAt = past
                    };
                }
            }
        }
    }

    [Fact]
    public async Task MixedResolutionHistory_ApiAndEngineAgree()
    {
        Guid seed = Guid.NewGuid();
        using InferenceApiDbContext api = NewDb();
        api.AddRange(MixedHistoryRows(seed));
        await api.SaveChangesAsync();
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (WorkflowEngineDbContext engineDb = new(engineOptions))
        {
            engineDb.AddRange(MixedHistoryRows(seed));
            await engineDb.SaveChangesAsync();
        }

        WorkflowStep[] steps = [.. Steps(), new() { Id = Guid.NewGuid(), StepOrder = 5, StepType = StepType.TimelineRender }];
        foreach (int? resolution in new int?[] { null, 720, 1080, 2160 })
        {
            WorkflowCostEstimate estimate = await ApiEstimator(api).EstimateAsync(Org, steps, resolution, CancellationToken.None);

            WorkflowExecutorService engine = EngineService(new EngineFactory(engineOptions));
            WorkflowExecution execution = Execution(steps, resolution);
            long reserved = 0;
            foreach (WorkflowStep step in steps)
            {
                long hold = await engine.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps);
                estimate.Steps.Single(s => s.StepOrder == step.StepOrder).HoldMillis
                    .Should().Be(hold, $"step {step.StepOrder} ({step.StepType}) at {resolution}");
                reserved += hold;
            }

            estimate.RequiredMillis.Should().Be(reserved, $"at {resolution}");
            estimate.Steps.Single(s => s.StepOrder == 3).HoldMillis.Should().Be(
                resolution == 2160 ? Math.Max(10_000 * 4, 60_000) : 10_000,
                $"the compile step at {resolution}: reference p90, scaled, raised by the 4K p90");
        }
    }

    /// <summary>
    /// Usage rows for arbitrary samples: one execution per resolution, one step per (type, agent), one
    /// charged step result per sample. Deterministic ids from <paramref name="seed"/>, so two databases
    /// seeded with the same seed hold the same rows (fresh entities on every enumeration).
    /// </summary>
    private static IEnumerable<object> HistoryRows(Guid seed, IReadOnlyList<(StepType Type, Guid Agent, int? Resolution, long Credits)> samples)
    {
        static Guid Id(Guid seed, int n) => new(BitConverter.GetBytes(n).Concat(seed.ToByteArray().Skip(4)).ToArray());

        int n = 0;
        Guid projectId = Id(seed, ++n);
        Guid definitionId = Id(seed, ++n);
        DateTime past = DateTime.UtcNow.AddDays(-1);
        yield return new WorkflowDefinition { Id = definitionId, Name = "h", ProjectId = projectId };

        Dictionary<int, Guid> executions = [];
        foreach (int? resolution in samples.Select(s => s.Resolution).Distinct())
        {
            Guid executionId = Id(seed, ++n);
            executions[resolution ?? 0] = executionId;
            yield return new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = definitionId,
                ProjectId = projectId,
                OrganizationId = Org,
                Status = ExecutionStatus.Passed,
                OutputResolution = resolution,
                QueuedAt = past
            };
        }

        Dictionary<(StepType, Guid), Guid> stepIds = [];
        foreach ((StepType type, Guid agent, int? resolution, long credits) in samples)
        {
            if (!stepIds.TryGetValue((type, agent), out Guid stepId))
            {
                stepId = Id(seed, ++n);
                stepIds[(type, agent)] = stepId;
                yield return new WorkflowStep { Id = stepId, WorkflowDefinitionId = definitionId, StepOrder = n, StepType = type, AgentDefinitionId = agent };
            }

            Guid resultId = Id(seed, ++n);
            yield return new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executions[resolution ?? 0],
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                ExecutedAt = past
            };
            yield return new UsageEvent
            {
                Id = Id(seed, ++n),
                OrganizationId = Org,
                ProjectId = projectId,
                ExecutionId = executions[resolution ?? 0],
                StepResultId = resultId,
                Source = UsageSource.Workflow,
                Kind = UsageKind.EncodeOutputSeconds,
                Quantity = 1,
                ProviderKind = InferenceProviderKind.OpenAICompatible,
                ModelName = "test",
                CreditsMillis = credits,
                IdempotencyKey = $"{resultId:N}:0",
                OccurredAt = past
            };
        }
    }

    /// <summary>Seeds the same rows (fresh entities per database) into a new API database and a new engine database.</summary>
    private static async Task<(InferenceApiDbContext Api, DbContextOptions<WorkflowEngineDbContext> Engine)> SeedBothAsync(Func<IEnumerable<object>> rows)
    {
        InferenceApiDbContext api = NewDb();
        api.AddRange(rows());
        await api.SaveChangesAsync();
        DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (WorkflowEngineDbContext engineDb = new(engineOptions))
        {
            engineDb.AddRange(rows());
            await engineDb.SaveChangesAsync();
        }

        return (api, engineOptions);
    }

    /// <summary>
    /// QA-T004 (Review Focus 1, run level): a render step whose ONLY history is 4K is cold at the reference,
    /// so on an Auto run it joins the cold plan exactly as if it had no history at all (its 4K samples are
    /// invisible; the cold low edge and the other cold steps' shares follow), while a 4K run holds
    /// max(its cold share × 4, its 4K p90). The API and the engine agree step by step at every resolution,
    /// over the same rows.
    /// </summary>
    [Fact]
    public async Task Only4KRenderHistory_JoinsTheColdPlanOnAuto_AndApiAndEngineAgree()
    {
        long[] uhd = [300_000, 310_000, 320_000, 330_000, 340_000];
        (StepType Type, Guid Agent, int? Resolution, long Credits)[] directorAuto =
            [.. Enumerable.Repeat((StepType.Agent, Director.Id, (int?)null, 8_000L), 5)];
        (StepType Type, Guid Agent, int? Resolution, long Credits)[] compile4K =
            [.. uhd.Select(c => (StepType.VideoCompile, Director.Id, (int?)2160, c))];
        (StepType Type, Guid Agent, int? Resolution, long Credits)[] samples = [.. directorAuto, .. compile4K];

        Guid seed = Guid.NewGuid();
        (InferenceApiDbContext api, DbContextOptions<WorkflowEngineDbContext> engineOptions) = await SeedBothAsync(() => HistoryRows(seed, samples));
        using InferenceApiDbContext apiDb = api;

        WorkflowStep[] steps =
        [
            new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Author.Id, AgentDefinition = Author },
            new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = Director.Id, AgentDefinition = Director },
            new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.Agent, AgentDefinitionId = Director.Id, AgentDefinition = Director },
            new() { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.TimelineRender }
        ];
        IReadOnlyDictionary<Guid, AgentType> types = ColdStartStepPricing.KnownAgentTypes(steps);
        StepCreditSample[] directorOnly = [.. directorAuto.Select(s => new StepCreditSample(s.Type, s.Agent, s.Credits, s.Resolution))];
        StepCreditSample[] all = [.. samples.Select(s => new StepCreditSample(s.Type, s.Agent, s.Credits, s.Resolution))];

        // The Auto plan with the 4K samples is the plan with no compile history at all.
        RunHoldPlan noCompileHistory = RunCreditHolds.Plan(steps, new StepCreditEstimator(directorOnly), null, types, SeedProfilePricer.Instance, null);
        RunHoldPlan autoPlan = RunCreditHolds.Plan(steps, new StepCreditEstimator(all), null, types, SeedProfilePricer.Instance, null);
        autoPlan.HoldsMillis.Should().Equal(noCompileHistory.HoldsMillis, "4K-only history leaves the compile step cold on an Auto run");
        autoPlan.ColdLowMillis.Should().Be(noCompileHistory.ColdLowMillis);
        autoPlan.ColdLowMillis.Should().BePositive("the compile step is in the cold set with the Author and the TimelineRender");

        foreach (int? resolution in new int?[] { null, 720, 1080, 2160 })
        {
            WorkflowCostEstimate estimate = await ApiEstimator(apiDb).EstimateAsync(Org, steps, resolution, CancellationToken.None);
            WorkflowExecutorService engine = EngineService(new EngineFactory(engineOptions));
            WorkflowExecution execution = Execution(steps, resolution);
            RunHoldPlan expected = RunCreditHolds.Plan(steps, new StepCreditEstimator(all), null, types, SeedProfilePricer.Instance, resolution);

            long reserved = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                long hold = await engine.ResolveStepReserveMillisAsync(execution, steps[i], CancellationToken.None, steps);
                hold.Should().Be(expected.HoldsMillis[i], $"engine, step {steps[i].StepOrder} ({steps[i].StepType}) at {resolution}");
                estimate.Steps.Single(s => s.StepOrder == steps[i].StepOrder).HoldMillis
                    .Should().Be(hold, $"API vs engine, step {steps[i].StepOrder} ({steps[i].StepType}) at {resolution}");
                reserved += hold;
            }

            estimate.RequiredMillis.Should().Be(reserved, $"at {resolution}");
            long compile = expected.HoldsMillis[1];
            if (resolution == 2160)
            {
                compile.Should().Be(Math.Max(noCompileHistory.HoldsMillis[1] * 4, CreditPercentiles.Continued(uhd, 0.9)));
            }
            else
            {
                compile.Should().Be(noCompileHistory.HoldsMillis[1], $"at {resolution} the compile step holds its cold share, never less");
            }
        }
    }

    /// <summary>
    /// QA-T004: the API now takes a step's "renders the output" flag from the display's agent types instead
    /// of an unfiltered lookup. An Author step whose navigation is NOT loaded must resolve the same way on
    /// both sides (platform and own-organization rows only, like the engine's <c>LoadAgentTypesAsync</c>):
    /// a platform Author scales at 4K on both, another tenant's Author is unknown (not scaled) on both.
    /// </summary>
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public async Task AuthorStepWithoutNavigation_ResolvesItsTypeOnBothSidesByTheSameTenantRule(bool foreign, int factor)
    {
        Guid authorId = Guid.Parse("a7a7a7a7-0000-4000-8000-0000000000a7");
        IEnumerable<object> Rows()
        {
            yield return new AgentDefinition
            {
                Id = authorId,
                Name = "author",
                Description = "d",
                SystemPrompt = "p",
                AgentType = AgentType.AuthorAgent,
                IsBuiltIn = !foreign,
                OrganizationId = foreign ? Guid.Parse("f0f0f0f0-0000-4000-8000-0000000000f0") : null,
                CreatedAt = DateTime.UtcNow
            };
        }

        (InferenceApiDbContext api, DbContextOptions<WorkflowEngineDbContext> engineOptions) = await SeedBothAsync(Rows);
        using InferenceApiDbContext apiDb = api;

        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = authorId };
        WorkflowStep[] steps = [step];
        Dictionary<Guid, AgentType> known = foreign ? [] : new() { [authorId] = AgentType.AuthorAgent };
        long planHold = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, known, SeedProfilePricer.Instance).HoldsMillis[0];

        WorkflowCostEstimate estimate = await ApiEstimator(apiDb).EstimateAsync(Org, steps, 2160, CancellationToken.None);
        long engine = await EngineService(new EngineFactory(engineOptions))
            .ResolveStepReserveMillisAsync(Execution(steps, 2160), step, CancellationToken.None, steps);

        engine.Should().Be(planHold * factor, foreign ? "another tenant's agent is never read: not a render step" : "a platform Author renders the film");
        estimate.RequiredMillis.Should().Be(engine);
    }

    /// <summary>
    /// The engine prices a VideoGenerate step's hold with the run brief's avatar in scope (the same value the API's run gate
    /// and estimates enter), and with none for a run whose brief names no avatar.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_engine_hold_prices_a_video_step_with_the_run_briefs_avatar(bool briefAvatar)
    {
        Guid avatar = Guid.Parse("00000000-0000-4000-8000-0000000000aa");
        Guid? seen = Guid.Empty;
        var estimator = new Mock<ICreditHoldEstimator>();
        estimator.Setup(e => e.TryVideoGenerateHoldMillisAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                seen = BriefAvatarPricing.Current;
                return ValueTask.FromResult<long?>(1_000);
            });
        WorkflowExecutorService engine = new(
            scopeFactory: Mock.Of<IServiceScopeFactory>(),
            eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: estimator.Object);
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5}"""
        };
        WorkflowExecution execution = Execution([step], null);
        execution.RunBriefJson = briefAvatar
            ? RunBriefJson.Serialize(new StoredRunBrief(Avatar: new RunBriefAvatar(avatar)))
            : null;

        using IDisposable scope = RunBriefAmbient.Enter(RunBriefState.From(execution));
        long hold = await engine.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, [step]);

        hold.Should().Be(1_000);
        seen.Should().Be(briefAvatar ? avatar : null);
    }

    private sealed class EngineFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private static WorkflowExecution Execution(IReadOnlyList<WorkflowStep> steps, int? resolution) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = Org,
        OutputResolution = resolution,
        // The engine plans the execution's own steps, as the API plans the run it prices.
        WorkflowDefinition = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Run", Steps = [.. steps] }
    };

    private static InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WorkflowCostEstimator ApiEstimator(InferenceApiDbContext db) => new(
        new SingleContextScopes(db),
        Options.Create(new BillingOptions { Enabled = true }),
        Options.Create(new StepCreditEstimateOptions()),
        NullLogger<WorkflowCostEstimator>.Instance);

    // ── fixture ──────────────────────────────────────────────────────────────

    private static WorkflowExecutorService EngineService() => EngineService(new ThrowingFactory());

    private static WorkflowExecutorService EngineService(IDbContextFactory<WorkflowEngineDbContext> history) => new(
        scopeFactory: Mock.Of<IServiceScopeFactory>(),
        eventPublisher: Mock.Of<IWorkflowEventPublisher>(),
        logger: NullLogger<WorkflowExecutorService>.Instance,
        executors: [],
        rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
        hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
        cancellationRegistry: new ExecutionCancellationRegistry(),
        creditOptions: Options.Create(new StepCreditOptions()),
        creditHoldEstimator: new UsageHistoryCreditHoldEstimator(
            history,
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<UsageHistoryCreditHoldEstimator>.Instance));

    /// <summary>No history: every hold is the cold-start default, on both sides.</summary>
    private sealed class ThrowingFactory : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => throw new InvalidOperationException("no history");
    }

    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);

            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
