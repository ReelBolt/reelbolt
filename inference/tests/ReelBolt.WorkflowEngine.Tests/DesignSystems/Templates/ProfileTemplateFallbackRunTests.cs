using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Templates;

/// <summary>
/// Through the REAL engine: the shipped "Build brand and product profile" template
/// (<see cref="WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey"/>) run by
/// <see cref="WorkflowExecutorService.ExecuteAsync"/> with the real <see cref="ConditionalStepExecutor"/>
/// and the real <see cref="CreditGate"/> over a Sqlite ledger. The DesignSystem steps are scripted (Parse
/// answers the view under test, Compose answers an empty profile); every Agent step meters one priced
/// chat call. What is asserted is which steps ran and what the organization was held and charged:
/// the free parts hold nothing, and the LLM fallback is charged only for the part the profile misses.
/// </summary>
public sealed class ProfileTemplateFallbackRunTests : IDisposable
{
    private const long MillisPerFallbackCall = 3_250;
    private static readonly Guid PriceRowId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000098");

    private static readonly JsonSerializerOptions DesignSystemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly AgentType[] ProductAnalyzers =
    [
        AgentType.CodeStructureAnalyzer,
        AgentType.DependencyAnalyzer,
        AgentType.ComponentInventoryAnalyzer,
        AgentType.RouteAndApiAnalyzer
    ];

    private readonly SqliteConnection _ledgerConnection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly LedgerFactory _ledgerFactory;
    private readonly Guid _org = Guid.NewGuid();

    public ProfileTemplateFallbackRunTests()
    {
        _ledgerConnection.Open();
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_ledgerConnection).Options))
        {
            owner.Database.EnsureCreated();
        }

        _ledgerFactory = new LedgerFactory(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_ledgerConnection).Options);
    }

    public void Dispose() => _ledgerConnection.Dispose();

    // -----------------------------------------------------------------
    // The four paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_profile_the_files_fully_cover_runs_no_agent_step_and_holds_nothing()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: false, fallbackDesign: false);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AgentTypesRun.Should().BeEmpty();
        run.StepOrdersRun.Should().NotContain(StepOrdersOf(StepTypeEnum.Agent));
        run.Ledger.Should().OnlyContain(e => e.Kind == CreditLedgerEntryKind.PlanRefill,
            "Parse, the Conditionals and Compose are free: no hold, no charge");
        run.Balance.BalanceMillis.Should().Be(Grant);
    }

    [Fact]
    public async Task A_missing_design_runs_only_the_style_extractor()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: false, fallbackDesign: true);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AgentTypesRun.Should().Equal(AgentType.StyleAndThemeExtractor);
        AssertChargedPerAgentStep(run, agentSteps: 1);
    }

    [Fact]
    public async Task A_missing_product_map_runs_the_product_analyzers_and_not_the_style_extractor()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: true, fallbackDesign: false);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AgentTypesRun.Should().Equal(ProductAnalyzers);
        run.AgentTypesRun.Should().NotContain(AgentType.StyleAndThemeExtractor,
            "the design was already covered by the files, so its fallback must not be charged");
        AssertChargedPerAgentStep(run, agentSteps: 4);
    }

    [Fact]
    public async Task Both_parts_missing_run_every_fallback_step_once()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: true, fallbackDesign: true);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AgentTypesRun.Should().Equal([.. ProductAnalyzers, AgentType.StyleAndThemeExtractor]);
        AssertChargedPerAgentStep(run, agentSteps: 5);
    }

    [Fact]
    public async Task Every_conditional_reads_the_parse_steps_view_not_the_previous_steps_output()
    {
        // Both flags true: the Conditional after the product analyzers runs after RouteAndApiAnalyzer,
        // whose output carries no view at all. It must still see the Parse step's fallbackDesign.
        Outcome run = await RunTemplateAsync(fallbackProduct: true, fallbackDesign: true);

        run.ConditionalInputs.Should().NotBeEmpty();
        run.ConditionalInputs.Should().OnlyContain(o => o.Contains("\"fallbackDesign\":true"),
            "each Conditional evaluates $.view.* against the Parse step's output");
    }

    [Fact]
    public async Task A_fallback_agent_step_takes_a_normal_agent_hold_and_settles_against_the_price_row()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: false, fallbackDesign: true);

        CreditLedgerEntry reserve = run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve);
        reserve.ReservedDeltaMillis.Should().Be(StepCreditDefaults.MillisFor(StepTypeEnum.Agent).Low,
            "the fallback is an ordinary Agent step and takes the Agent step's hold");
        run.Ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).BalanceDeltaMillis
            .Should().Be(-MillisPerFallbackCall, "it settles at what the metered call was priced");

        PriceRequest priced = run.PriceRequests.Should().ContainSingle().Subject;
        priced.Capability.Should().Be(InferenceProviderCapability.Chat);
        priced.ModelCode.Should().Be("fallback-chat-model");
        priced.BillingMode.Should().Be(UsageBillingMode.Platform);
        run.QuotedPriceIds.Should().Equal(PriceRowId);

        UsageScopeState scope = run.AgentScopes.Should().ContainSingle().Subject;
        scope.Source.Should().Be(UsageSource.Workflow);
        scope.OrganizationId.Should().Be(_org);
        scope.ProjectId.Should().Be(run.ProjectId);
    }

    [Fact]
    public async Task The_design_system_steps_run_inside_an_attributed_workflow_scope()
    {
        Outcome run = await RunTemplateAsync(fallbackProduct: false, fallbackDesign: false);

        run.DesignSystemScopes.Should().HaveCount(2, "Parse and Compose both ran");
        run.DesignSystemScopes.Should().OnlyContain(s =>
            s.Source == UsageSource.Workflow && s.OrganizationId == _org && s.ProjectId == run.ProjectId,
            "any usage a design-system step ever records is the run's, never Unattributed");
    }

    private void AssertChargedPerAgentStep(Outcome run, int agentSteps)
    {
        run.Ledger.Count(e => e.Kind == CreditLedgerEntryKind.Reserve).Should().Be(agentSteps,
            "only the Agent steps that ran take a hold; the free steps reserve nothing");
        run.Ledger.Where(e => e.Kind == CreditLedgerEntryKind.Reserve)
            .Should().OnlyContain(e => e.ReservedDeltaMillis == StepCreditDefaults.MillisFor(StepTypeEnum.Agent).Low);
        run.Ledger.Where(e => e.Kind == CreditLedgerEntryKind.Settle)
            .Should().HaveCount(agentSteps).And.OnlyContain(e => e.BalanceDeltaMillis == -MillisPerFallbackCall);
        run.Balance.BalanceMillis.Should().Be(Grant - (agentSteps * MillisPerFallbackCall));
        run.Balance.ReservedMillis.Should().Be(0);
    }

    private static int[] StepOrdersOf(StepTypeEnum type) =>
        [.. Template().Steps.Select((s, i) => (s, Order: i + 1)).Where(x => x.s.StepType == type).Select(x => x.Order)];

    private static WorkflowTemplateDefinition Template() =>
        WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private const long Grant = 1_000_000;

    private sealed record Outcome(
        Guid ProjectId,
        ExecutionStatus Status,
        IReadOnlyList<int> StepOrdersRun,
        IReadOnlyList<AgentType> AgentTypesRun,
        IReadOnlyList<string> ConditionalInputs,
        IReadOnlyList<UsageScopeState> AgentScopes,
        IReadOnlyList<UsageScopeState> DesignSystemScopes,
        IReadOnlyList<PriceRequest> PriceRequests,
        IReadOnlyList<Guid?> QuotedPriceIds,
        IReadOnlyList<CreditLedgerEntry> Ledger,
        CreditLedgerResult Balance);

    private async Task<Outcome> RunTemplateAsync(bool fallbackProduct, bool fallbackDesign)
    {
        await using (WorkflowEngineDbContext ledgerDb = _ledgerFactory.CreateDbContext())
        {
            (await new CreditLedger(ledgerDb).GrantAsync(_org, CreditGrantSource.Plan, Grant, null, Guid.NewGuid().ToString("N")))
                .Ok.Should().BeTrue();
        }

        using var db = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        Guid projectId = Guid.NewGuid();
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "profile", ProjectId = projectId, Steps = [] };
        Dictionary<AgentType, AgentDefinition> agents = [];
        int order = 0;
        foreach (WorkflowTemplateStepDefinition t in Template().Steps)
        {
            order++;
            if (!agents.TryGetValue(t.AgentType, out AgentDefinition? agentRow))
            {
                agentRow = new AgentDefinition { Id = Guid.NewGuid(), Name = t.AgentType.ToString(), AgentType = t.AgentType, SystemPrompt = "" };
                agents[t.AgentType] = agentRow;
                db.AgentDefinitions.Add(agentRow);
            }

            definition.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = definition.Id,
                StepOrder = order,
                Label = t.Label,
                StepType = t.StepType,
                AgentDefinitionId = agentRow.Id,
                AgentDefinition = agentRow,
                ConditionExpression = t.ConditionExpression,
                TrueBranchStepOrder = t.TrueBranchStepOrder,
                FalseBranchStepOrder = t.FalseBranchStepOrder,
                ConditionalConfigJson = t.ConditionalConfigJson,
                AgentInputContextMode = t.AgentInputContextMode ?? AgentInputContextMode.PreviousStepOnly,
                DesignSystemConfigJson = t.DesignSystemConfigJson,
                MaxIterations = t.MaxIterations
            });
        }

        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            OrganizationId = _org,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        string parseOutput = JsonSerializer.Serialize(new
        {
            view = new { fallbackProduct, fallbackDesign, designTokenCount = 3, routeCount = 1, componentCount = 2 },
            meta = new { artifactStorageKey = "design-system/parse.json" }
        });

        var stepOrdersRun = new ConcurrentQueue<int>();
        var agentTypesRun = new ConcurrentQueue<AgentType>();
        var conditionalInputs = new ConcurrentQueue<string>();
        var agentScopes = new ConcurrentQueue<UsageScopeState>();
        var designSystemScopes = new ConcurrentQueue<UsageScopeState>();
        var prices = new RecordingPriceService();
        var recorder = new UsageRecorder(prices, new NullUsageSink(), NullLogger<UsageRecorder>.Instance);

        var designSystem = new ScriptedExecutor(StepTypeEnum.DesignSystem, context =>
        {
            stepOrdersRun.Enqueue(context.Step.StepOrder);
            designSystemScopes.Enqueue(UsageScope.Current!);
            DesignSystemStepConfig config = JsonSerializer.Deserialize<DesignSystemStepConfig>(
                context.Step.DesignSystemConfigJson!, DesignSystemJsonOptions)!;
            return Task.FromResult(Completed(context, config.Op == DesignSystemOp.Parse
                ? parseOutput
                : """{"view":{"designTokenCount":3},"meta":{}}"""));
        });

        var agent = new ScriptedExecutor(StepTypeEnum.Agent, async context =>
        {
            stepOrdersRun.Enqueue(context.Step.StepOrder);
            agentTypesRun.Enqueue(context.Step.AgentDefinition.AgentType);
            agentScopes.Enqueue(UsageScope.Current!);
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmOutputTokens, 1_000m, null, InferenceProviderKind.OpenAICompatible,
                "fallback-chat-model", UsageBillingMode.Platform, Capability: InferenceProviderCapability.Chat));
            return Completed(context, $$"""{"agent":"{{context.Step.AgentDefinition.AgentType}}"}""");
        });

        var conditional = new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, Mock.Of<IDecisionGate>());
        var observedConditional = new ObservingExecutor(conditional, context =>
        {
            stepOrdersRun.Enqueue(context.Step.StepOrder);
            // Read as raw JSON so this observer does not depend on the config record's shape.
            int? sourceStepOrder = string.IsNullOrWhiteSpace(context.Step.ConditionalConfigJson)
                ? null
                : System.Text.Json.Nodes.JsonNode.Parse(context.Step.ConditionalConfigJson)?["sourceStepOrder"]?.GetValue<int>();
            string evaluated = sourceStepOrder is { } source
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source)?.Output ?? string.Empty
                : context.AccumulatedOutput;
            conditionalInputs.Enqueue(evaluated);
        });

        var service = new WorkflowExecutorService(
            scopeFactory: new ScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [designSystem, agent, observedConditional],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            creditGate: new CreditGate(_ledgerFactory, Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance),
            creditOptions: Options.Create(new StepCreditOptions()));

        await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);

        WorkflowExecution finished = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == execution.Id);

        await using WorkflowEngineDbContext ledger = _ledgerFactory.CreateDbContext();
        List<CreditLedgerEntry> entries = await ledger.CreditLedgerEntries.AsNoTracking()
            .Where(e => e.OrganizationId == _org).ToListAsync();
        CreditLedgerResult balance = await new CreditLedger(ledger).GetBalanceAsync(_org);

        return new Outcome(
            projectId,
            finished.Status,
            [.. stepOrdersRun],
            [.. agentTypesRun],
            [.. conditionalInputs],
            [.. agentScopes],
            [.. designSystemScopes],
            [.. prices.Requests],
            [.. prices.QuotedIds],
            entries,
            balance);
    }

    private static StepExecutionResult Completed(StepExecutionContext context, string output) => new()
    {
        Output = output,
        NextStepIndex = context.CurrentStepIndex + 1,
        NewIterationCount = context.IterationCount,
        Status = StepStatus.Completed
    };

    private sealed class LedgerFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private sealed class ScriptedExecutor(StepTypeEnum stepType, Func<StepExecutionContext, Task<StepExecutionResult>> body) : IStepExecutor
    {
        public StepTypeEnum StepType { get; } = stepType;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => body(context);
    }

    /// <summary>The real executor, with a hook that sees the context it is handed first.</summary>
    private sealed class ObservingExecutor(IStepExecutor inner, Action<StepExecutionContext> observe) : IStepExecutor
    {
        public StepTypeEnum StepType => inner.StepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            observe(context);
            return inner.ExecuteAsync(context);
        }
    }

    /// <summary>Prices only the fallback's chat model, at one fixed price row, and records what it was asked.</summary>
    private sealed class RecordingPriceService : IModelPriceService
    {
        public ConcurrentQueue<PriceRequest> Requests { get; } = new();
        public ConcurrentQueue<Guid?> QuotedIds { get; } = new();

        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (request.ModelCode != "fallback-chat-model" || request.Capability != InferenceProviderCapability.Chat)
                throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);

            QuotedIds.Enqueue(PriceRowId);
            return ValueTask.FromResult(new PriceQuote(MillisPerFallbackCall, 0.00325m, PriceRowId));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Invalidate() { }
    }

    private sealed class NullUsageSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpEventPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ScopeFactory(WorkflowEngineDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db);

        private sealed class Scope(WorkflowEngineDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(db);
            public void Dispose() { }
        }

        private sealed class Provider(WorkflowEngineDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db : null;
        }
    }
}
