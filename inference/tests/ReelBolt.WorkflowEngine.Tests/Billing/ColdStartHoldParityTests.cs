#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// for the same organization, steps, providers and rate cards, the
/// engine's per-step reservations over one pass sum to the Inference API's <c>RequiredMillis</c>, step
/// by step, on a cold start. Both sides are driven through their real code paths: the API's
/// <see cref="WorkflowCostEstimator.EstimateAsync"/> over an in-memory API context, and the engine's
/// <see cref="WorkflowExecutorService.ResolveStepReserveMillisAsync"/> over
/// <see cref="UsageHistoryCreditHoldEstimator"/> and an in-memory engine context.
/// </summary>
public sealed class ColdStartHoldParityTests(ITestOutputHelper output)
{
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Templates()
    {
        TheoryData<string> data = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            data.Add(template.Key);
        }

        return data;
    }

    [Fact]
    public void The_parity_set_includes_quick_win_promo_and_a_room_template()
    {
        WorkflowTemplateCatalog.GetAll().Should().Contain(t => t.Key == "quick-win-promo");
        StepType[] rooms = [StepType.EditRoom, StepType.GraphicsRoom, StepType.ColorGradeRoom, StepType.DailiesRoom];
        WorkflowTemplateCatalog.GetAll().Should().Contain(t => t.Steps.Any(s => rooms.Contains(s.StepType)));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task The_engine_holds_exactly_what_the_api_requires_on_a_cold_start(string templateKey)
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] steps = fixture.StepsOf(templateKey);

        WorkflowCostEstimate api = await fixture.ApiEstimateAsync(steps);
        long[] engine = await fixture.EngineHoldsAsync(steps);

        output.WriteLine($"{templateKey}: api required {api.RequiredMillis}, engine Σ {engine.Sum()}; per step api [{string.Join(", ", api.Steps.Select(s => s.HoldMillis))}] engine [{string.Join(", ", engine)}]");

        api.HasHistory.Should().BeFalse();
        engine.Should().Equal(api.Steps.Select(s => s.HoldMillis), templateKey);
        engine.Sum().Should().Be(api.RequiredMillis);
        // Every charged step reserves: the BYO DirectorAgent and the resolver-failing reviewer included.
        for (int i = 0; i < steps.Length; i++)
        {
            // These runs carry no pace, so a DynamicsPlanner is skipped and takes no hold (the test below holds it with a pace).
            if (!StepCreditDefaults.IsNotCharged(steps[i].StepType) && steps[i].AgentDefinition?.AgentType != AgentType.DynamicsPlanner)
            {
                engine[i].Should().BePositive($"{templateKey} step {steps[i].StepOrder} ({steps[i].StepType}) is charged");
            }
        }
    }

    [Fact]
    public async Task A_review_loop_with_the_visual_check_holds_what_the_api_requires_and_more_than_without_it()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] Steps(string? reviewConfig)
        {
            WorkflowStep review = fixture.Step(2, StepType.ReviewLoop, AgentType.ReviewAgent);
            review.LoopTargetStepOrder = 1;
            review.MaxIterations = 3;
            review.ReviewLoopConfigJson = reviewConfig;
            return [fixture.Step(1, StepType.Agent, AgentType.AuthorAgent), review];
        }

        WorkflowStep[] on = Steps("""{"visualCheck":{"enabled":true,"frames":8}}""");
        WorkflowCostEstimate api = await fixture.ApiEstimateAsync(on);
        long[] engine = await fixture.EngineHoldsAsync(on);
        WorkflowCostEstimate apiOff = await fixture.ApiEstimateAsync(Steps(null));

        engine.Should().Equal(api.Steps.Select(s => s.HoldMillis));
        engine.Sum().Should().Be(api.RequiredMillis);
        api.Steps[1].TypicalMillis.Should().BeGreaterThan(apiOff.Steps[1].TypicalMillis, "one vision call per iteration is priced");
    }

    [Fact]
    public async Task A_dynamics_planner_of_a_run_with_no_pace_is_neither_held_nor_estimated_and_one_with_a_pace_is_in_both()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] steps = [fixture.Step(1, StepType.Agent, AgentType.AuthorAgent), fixture.Step(2, StepType.Agent, AgentType.DynamicsPlanner)];
        string paced = RunBriefJson.Serialize(new StoredRunBrief(Pace: "fast"));

        // No pace: the API and the engine both leave the planner out. A brief pace: both hold and estimate it.
        WorkflowCostEstimate apiNone = await fixture.ApiEstimateAsync(steps);
        long[] engineNone = await fixture.EngineHoldsAsync(steps);
        WorkflowCostEstimate apiPaced = await fixture.ApiEstimateAsync(steps, paced);
        long[] enginePaced = await fixture.EngineHoldsAsync(steps, runBriefJson: paced);

        apiNone.Steps[1].HoldMillis.Should().Be(0, "a skipped planner is not held");
        apiNone.Steps[1].TypicalMillis.Should().Be(0, "nor is it in the estimate range");
        engineNone.Should().Equal(apiNone.Steps.Select(s => s.HoldMillis));
        engineNone.Sum().Should().Be(apiNone.RequiredMillis);

        apiPaced.Steps[1].HoldMillis.Should().BePositive();
        enginePaced.Should().Equal(apiPaced.Steps.Select(s => s.HoldMillis));
        enginePaced.Sum().Should().Be(apiPaced.RequiredMillis);
        apiPaced.RequiredMillis.Should().BeGreaterThan(apiNone.RequiredMillis);
        apiPaced.HighMillis.Should().BeGreaterThan(apiNone.HighMillis);
    }

    [Fact]
    public async Task A_video_analysis_that_calls_no_provider_holds_and_estimates_only_its_compute_part_in_both()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] Steps(string config)
        {
            WorkflowStep analyze = fixture.Step(1, StepType.VideoAnalyze, AgentType.VideoTransform);
            analyze.VideoAnalyzeConfigJson = config;
            return [analyze];
        }

        WorkflowStep[] free = Steps("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off"}""");
        WorkflowStep[] paid = Steps("""{"version":1,"source":{"kind":"ProjectFile"}}""");
        WorkflowStep[] unreadable = Steps("not json");

        WorkflowCostEstimate apiFree = await fixture.ApiEstimateAsync(free);
        long[] engineFree = await fixture.EngineHoldsAsync(free);
        WorkflowCostEstimate apiPaid = await fixture.ApiEstimateAsync(paid);
        long[] enginePaid = await fixture.EngineHoldsAsync(paid);
        WorkflowCostEstimate apiUnreadable = await fixture.ApiEstimateAsync(unreadable);

        engineFree.Should().Equal(apiFree.Steps.Select(s => s.HoldMillis), "the engine holds what the API requires");
        enginePaid.Should().Equal(apiPaid.Steps.Select(s => s.HoldMillis));
        apiFree.RequiredMillis.Should().BePositive("the processing time is still held");
        apiFree.RequiredMillis.Should().BeLessThan(apiPaid.RequiredMillis, "no token part is held or estimated");
        apiFree.HighMillis.Should().BeLessThan(apiPaid.HighMillis);
        apiUnreadable.RequiredMillis.Should().Be(apiPaid.RequiredMillis, "a config that cannot be read is priced as paid");
    }

    [Fact]
    public async Task Enabling_the_visual_check_on_the_same_estimator_re_plans_the_review_loop_hold()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep review = fixture.Step(2, StepType.ReviewLoop, AgentType.ReviewAgent);
        review.LoopTargetStepOrder = 1;
        review.MaxIterations = 3;
        WorkflowStep[] steps = [fixture.Step(1, StepType.Agent, AgentType.AuthorAgent), review];

        (long before, long after) = await fixture.EngineReviewHoldBeforeAndAfterAsync(
            steps, review, """{"visualCheck":{"enabled":true,"frames":8}}""");

        after.Should().BeGreaterThan(before, "the cached run plan must not outlive a change to the step's config");
    }

    [Fact]
    public async Task A_resolver_that_throws_for_one_agent_prices_it_at_the_seed_constants_and_never_holds_zero()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] steps =
        [
            fixture.Step(1, StepType.Agent, AgentType.NarrationWriter),
            fixture.Step(2, StepType.Agent, AgentType.VideoReviewAgent) // the resolver throws for this one
        ];

        WorkflowCostEstimate api = await fixture.ApiEstimateAsync(steps);
        long[] engine = await fixture.EngineHoldsAsync(steps);

        long seedTypical = StepUsageProfiles.For(StepType.Agent, AgentType.VideoReviewAgent).Quantities
            .Sum(q => StepUsageProfiles.SeedFallbackMillis(q.Kind, q.Quantity));
        api.Steps[1].TypicalMillis.Should().Be(seedTypical).And.BePositive();
        api.Steps[0].TypicalMillis.Should().NotBe(StepUsageProfiles.For(StepType.Agent, AgentType.NarrationWriter).Quantities
            .Sum(q => StepUsageProfiles.SeedFallbackMillis(q.Kind, q.Quantity)), "the other agent is priced through the rate cards");
        engine[1].Should().BeGreaterThan(1, "a seed-priced share, not the floor and not zero");
        engine.Should().Equal(api.Steps.Select(s => s.HoldMillis));
        engine.Sum().Should().Be(api.RequiredMillis).And.BeGreaterThanOrEqualTo(api.LowMillis);
    }

    [Fact]
    public async Task An_own_provider_cold_step_holds_its_previous_default_on_both_sides_and_never_one_millicredit()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] steps = [fixture.Step(1, StepType.Agent, AgentType.DirectorAgent)]; // BYO

        WorkflowCostEstimate api = await fixture.ApiEstimateAsync(steps);
        long[] engine = await fixture.EngineHoldsAsync(steps);

        long previous = StepCreditDefaults.MillisFor(StepType.Agent).Low;
        api.Steps.Single().TypicalMillis.Should().Be(0, "the precondition: a BYO step's share is 0");
        api.RequiredMillis.Should().Be(previous);
        engine.Should().Equal(previous);
    }

    [Fact]
    public async Task With_history_the_engine_keeps_the_p90_and_the_override_still_wins()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        WorkflowStep[] steps =
        [
            fixture.Step(1, StepType.Agent, AgentType.NarrationWriter),
            fixture.Step(2, StepType.VideoCompile, AgentType.NarrationWriter),
            fixture.Step(3, StepType.Agent, AgentType.AuthorAgent)
        ];
        await fixture.SeedEngineHistoryAsync(steps[0], [1_000, 2_000, 3_000, 4_000, 5_000]);

        long[] engine = await fixture.EngineHoldsAsync(steps);
        long[] pinned = await fixture.EngineHoldsAsync(steps, new StepCreditOptions { StepReserveMillisOverride = 12_345 });

        // Step 1 has five Agent samples: its own p90. Step 3 is an Agent step too, so the step-type
        // bucket prices it from history as well. Only the compile is cold, and holds the larger of its
        // old default and the low edge of its own range.
        engine[0].Should().Be(4_600);
        engine[2].Should().Be(4_600);
        long compileAlone = (await fixture.ApiEstimateAsync([steps[1]])).RequiredMillis;
        engine[1].Should().Be(compileAlone).And.BePositive();
        pinned.Should().OnlyContain(h => h == 12_345);
    }

    [Fact]
    public async Task When_the_plan_cannot_be_computed_every_cold_step_holds_B0s_default_low()
    {
        Guid org = Guid.NewGuid();
        Fixture fixture = new(org, Resolver(org));
        // Two steps at one order cannot be told apart by the plan.
        WorkflowStep[] steps =
        [
            fixture.Step(1, StepType.Agent, AgentType.NarrationWriter),
            fixture.Step(1, StepType.VideoCompile, AgentType.NarrationWriter)
        ];

        WorkflowCostEstimate api = await fixture.ApiEstimateAsync(steps);
        long[] engine = await fixture.EngineHoldsAsync(steps);

        long[] b0 = [StepCreditDefaults.MillisFor(StepType.Agent).Low, StepCreditDefaults.MillisFor(StepType.VideoCompile).Low];
        engine.Should().Equal(b0);
        api.Steps.Select(s => s.HoldMillis).Should().Equal(b0);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Chat per agent type: DirectorAgent on the organization's own provider (BYO, priced 0),
    /// VideoReviewAgent throws (a pricing failure for that agent alone), everything else Opus on the
    /// platform. Speech, transcription and video generation resolve nothing (seed constants).
    /// </summary>
    private static IInferenceProviderResolver Resolver(Guid org)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((AgentType type, Guid? _, CancellationToken _) => type switch
            {
                AgentType.DirectorAgent => Task.FromResult(new ResolvedInferenceProvider(Guid.NewGuid(), "mine", InferenceProviderKind.Anthropic,
                    "https://x", "claude-opus-5-5", "key", 60, OrganizationId: org, IsPlatformManaged: false)),
                AgentType.VideoReviewAgent => Task.FromException<ResolvedInferenceProvider>(new InvalidOperationException("resolver down for this agent")),
                _ => Task.FromResult(new ResolvedInferenceProvider(Guid.NewGuid(), "platform", InferenceProviderKind.Anthropic,
                    "https://x", "claude-opus-5-5", "key", 60))
            });
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedSpeechSynthesisProvider?)null);
        resolver.Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedTranscriptionProvider?)null);
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedVideoGenerationProvider?)null);
        return resolver.Object;
    }

    /// <summary>One organization, its agent definitions in both services' databases, and both estimators.</summary>
    private sealed class Fixture
    {
        private readonly Guid _org;
        private readonly IInferenceProviderResolver _resolver;
        private readonly IModelPriceService _rates = LegacyCardPrices.Seed();
        private readonly Dictionary<AgentType, Guid> _agents = [];
        private readonly InferenceApiDbContext _api = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        private readonly DbContextOptions<WorkflowEngineDbContext> _engine =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Fixture(Guid org, IInferenceProviderResolver resolver)
        {
            _org = org;
            _resolver = resolver;
            using WorkflowEngineDbContext engine = new(_engine);
            foreach (AgentType type in Enum.GetValues<AgentType>())
            {
                Guid id = Guid.NewGuid();
                _agents[type] = id;
                // Platform rows (no organization): both sides read them, and the steps carry no
                // navigation, so the agent types come from agent_definitions on each side.
                _api.AgentDefinitions.Add(new AgentDefinition { Id = id, Name = type.ToString(), AgentType = type });
                engine.AgentDefinitions.Add(new AgentDefinition { Id = id, Name = type.ToString(), AgentType = type });
            }

            _api.SaveChanges();
            engine.SaveChanges();
        }

        public WorkflowStep Step(int order, StepType type, AgentType agent) => new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = _agents[agent],
            // Loaded with its agent, as the engine's executor sees every step of a run.
            AgentDefinition = new AgentDefinition { Id = _agents[agent], Name = agent.ToString(), AgentType = agent }
        };

        public WorkflowStep[] StepsOf(string templateKey)
        {
            WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == templateKey);
            Guid workflowId = Guid.NewGuid();
            return [.. template.Steps.Select((s, i) => new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = _agents[s.AgentType],
                AgentDefinition = new AgentDefinition { Id = _agents[s.AgentType], Name = s.AgentType.ToString(), AgentType = s.AgentType },
                StepOrder = i + 1,
                Label = s.Label,
                StepType = s.StepType,
                ConditionExpression = s.ConditionExpression,
                LoopSourceExpression = s.LoopSourceExpression,
                LoopTargetStepOrder = s.LoopTargetStepOrder,
                MaxIterations = s.MaxIterations,
                MinScore = s.MinScore,
                ParallelAgentIdsJson = s.ParallelAgentTypes is { Count: > 0 } parallel
                    ? JsonSerializer.Serialize(parallel.Distinct().Select(t => _agents[t]))
                    : null,
                VideoGenerateConfigJson = s.VideoGenerateConfigJson
            })];
        }

        /// <summary>
        /// The API's estimate for the run the engine side below runs: <paramref name="runBriefJson"/> is that run's stored brief
        /// (null = no pace, so a DynamicsPlanner is skipped, as the engine's executor skips it).
        /// </summary>
        public async Task<WorkflowCostEstimate> ApiEstimateAsync(IReadOnlyList<WorkflowStep> steps, string? runBriefJson = null)
        {
            using IDisposable runPace = DynamicsPlannerPricing.Enter(DynamicsPlannerPricing.HasPaceStored(runBriefJson, styleConfigJson: null));
            return await new WorkflowCostEstimator(
                    new ServiceScopes(_api, _resolver, _rates),
                    Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
                    Options.Create(new StepCreditEstimateOptions()),
                    NullLogger<WorkflowCostEstimator>.Instance)
                .EstimateAsync(_org, steps, CancellationToken.None);
        }

        /// <summary>What the executor reserves before each step, in order, over ONE scoped estimator (one execution).</summary>
        public async Task<long[]> EngineHoldsAsync(IReadOnlyList<WorkflowStep> steps, StepCreditOptions? options = null, string? runBriefJson = null)
        {
            var estimator = new UsageHistoryCreditHoldEstimator(
                new Factory(_engine),
                Options.Create(new StepCreditEstimateOptions()),
                NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
                time: null,
                videoProviders: _resolver,
                videoClients: null,
                prices: _rates);
            var executor = new WorkflowExecutorService(
                Mock.Of<IServiceScopeFactory>(),
                Mock.Of<IWorkflowEventPublisher>(),
                NullLogger<WorkflowExecutorService>.Instance,
                [],
                new RabbitMqHelper(new ConfigurationBuilder().Build()),
                Options.Create(new WorkflowHardeningOptions()),
                new ExecutionCancellationRegistry(),
                creditOptions: Options.Create(options ?? new StepCreditOptions()),
                creditHoldEstimator: estimator);
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = Guid.NewGuid(), Steps = [.. steps] };
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                ProjectId = definition.ProjectId,
                OrganizationId = _org,
                RunBriefJson = runBriefJson,
                WorkflowDefinition = definition
            };

            List<long> holds = [];
            foreach (WorkflowStep step in steps)
            {
                holds.Add(await executor.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps));
            }

            return [.. holds];
        }

        /// <summary>One estimator, the same run twice: the review step's hold before and after its config changes.</summary>
        public async Task<(long Before, long After)> EngineReviewHoldBeforeAndAfterAsync(
            IReadOnlyList<WorkflowStep> steps, WorkflowStep review, string newReviewConfig)
        {
            var estimator = new UsageHistoryCreditHoldEstimator(
                new Factory(_engine),
                Options.Create(new StepCreditEstimateOptions()),
                NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
                time: null,
                videoProviders: _resolver,
                videoClients: null,
                prices: _rates);
            long before = await estimator.RunHoldMillisAsync(_org, steps, review, outputResolution: null);
            review.ReviewLoopConfigJson = newReviewConfig;
            long after = await estimator.RunHoldMillisAsync(_org, steps, review, outputResolution: null);
            return (before, after);
        }

        /// <summary>Charged history for one step's (type, agent) in the engine's database.</summary>
        public async Task SeedEngineHistoryAsync(WorkflowStep step, long[] credits)
        {
            await using WorkflowEngineDbContext db = new(_engine);
            Guid definitionId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Guid executionId = Guid.NewGuid();
            Guid stepId = Guid.NewGuid();
            DateTime past = DateTime.UtcNow.AddDays(-1);
            db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "h", ProjectId = projectId });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = definitionId,
                ProjectId = projectId,
                OrganizationId = _org,
                Status = ExecutionStatus.Passed,
                QueuedAt = past
            });
            db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = stepId,
                WorkflowDefinitionId = definitionId,
                StepOrder = 1,
                StepType = step.StepType,
                AgentDefinitionId = step.AgentDefinitionId
            });
            foreach (long value in credits)
            {
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
                    OrganizationId = _org,
                    ProjectId = projectId,
                    ExecutionId = executionId,
                    StepResultId = resultId,
                    Source = UsageSource.Workflow,
                    Kind = UsageKind.LlmInputTokens,
                    Quantity = 1,
                    ProviderKind = InferenceProviderKind.OpenAICompatible,
                    ModelName = "test",
                    CreditsMillis = value,
                    IdempotencyKey = $"{resultId:N}:0",
                    OccurredAt = past
                });
            }

            await db.SaveChangesAsync();
        }
    }

    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    /// <summary>A scope factory serving the API context plus the provider resolver and the price service.</summary>
    private sealed class ServiceScopes(InferenceApiDbContext db, IInferenceProviderResolver resolver, IModelPriceService rates)
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
