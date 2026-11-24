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
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// S-3: a step's settle that fails is retried with the same reservation (so the ledger key replays),
/// and one that never succeeds leaves what it metered on the step result for the sweep, instead of
/// ending free. The gate is the real <see cref="CreditGate"/> over a Sqlite ledger, wrapped by a fake
/// that fails on cue.
/// </summary>
public sealed class StepSettleRetryTests : IDisposable
{
    private const long Metered = 4_500;

    private readonly UnsettledHoldTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    /// <summary>Delegates to the real gate; settles/releases fail by script.</summary>
    private sealed class FlakyGate(ICreditGate inner, Func<int, Task<CreditSettlement>?> onSettle, Func<int, Task<CreditSettlement>?>? onRelease = null) : ICreditGate
    {
        public int SettleCalls;
        public int ReleaseCalls;
        public readonly List<Guid?> SettledIds = [];

        public Task<CreditReservation> ReserveAsync(Guid organizationId, Guid executionId, Guid stepResultId, long estimateMillis, Guid? projectId = null,
            CreditEntitlement requires = CreditEntitlement.None, int attempt = 0, CancellationToken ct = default, long? overdraftBasisMillis = null, Guid? apiKeyId = null)
            => inner.ReserveAsync(organizationId, executionId, stepResultId, estimateMillis, projectId, requires, attempt, ct, overdraftBasisMillis);

        public Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default)
        {
            int n = Interlocked.Increment(ref SettleCalls);
            SettledIds.Add(reservationId);
            return onSettle(n) ?? inner.SettleAsync(reservationId, actualMillis, ct);
        }

        public Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default)
        {
            int n = Interlocked.Increment(ref ReleaseCalls);
            return onRelease?.Invoke(n) ?? inner.ReleaseAsync(reservationId, failureClass, actualMillis, ct);
        }

        public Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default) => inner.ReleaseExecutionAsync(executionId, ct);

        public Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default)
            => inner.SettleOpenAsync(reserveId, recordedMillis, ct);

        public Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default)
            => inner.ReleaseOpenAsync(reserveId, failureClass, recordedMillis, ct);
    }

    [Fact]
    public async Task Settle_that_throws_twice_then_succeeds_charges_once_with_the_same_key()
    {
        await _kit.GrantAsync(100_000);
        CreditGate real = _kit.Gate();
        FlakyGate gate = null!;
        gate = new FlakyGate(real, n => n switch
        {
            // Attempt 1 commits, then the answer is lost (a dropped connection after COMMIT).
            1 => CommitThenThrowAsync(real, gate.SettledIds[0]),
            // Attempt 2 never reaches the ledger.
            2 => Task.FromException<CreditSettlement>(new InvalidOperationException("pool exhausted")),
            _ => null
        });

        (WorkflowExecution execution, WorkflowStepResult step) = await RunAsync(gate, Metered, StepStatus.Completed);

        execution.Status.Should().Be(ExecutionStatus.Passed);
        gate.SettleCalls.Should().Be(3);
        gate.SettledIds.Distinct().Should().ContainSingle("every attempt reuses the one reservation");

        IReadOnlyList<CreditLedgerEntry> ledger = await _kit.EntriesAsync();
        CreditLedgerEntry reserve = ledger.Single(e => e.Kind == CreditLedgerEntryKind.Reserve);
        CreditLedgerEntry settle = ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle);
        settle.IdempotencyKey.Should().Be($"gate-settle:{reserve.Id:N}");
        settle.BalanceDeltaMillis.Should().Be(-Metered);
        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release);

        step.CreditsChargedMillis.Should().Be(Metered);
        step.CreditsUnsettledMillis.Should().BeNull();
        CreditLedgerResult balance = await _kit.BalanceAsync();
        balance.BalanceMillis.Should().Be(100_000 - Metered, "charged exactly once");
        balance.ReservedMillis.Should().Be(0);

        static async Task<CreditSettlement> CommitThenThrowAsync(ICreditGate real, Guid? id)
        {
            (await real.SettleAsync(id, Metered)).Ok.Should().BeTrue();
            throw new TimeoutException("answer lost after commit");
        }
    }

    [Fact]
    public async Task Settle_that_never_succeeds_records_the_metered_amount_on_the_step_result()
    {
        await _kit.GrantAsync(100_000);
        var gate = new FlakyGate(_kit.Gate(), n => n == 2
            ? Task.FromException<CreditSettlement>(new InvalidOperationException("pool exhausted"))
            : Task.FromResult(new CreditSettlement(false, "GATE_UNAVAILABLE")));

        (WorkflowExecution execution, WorkflowStepResult step) = await RunAsync(gate, Metered, StepStatus.Completed);

        gate.SettleCalls.Should().Be(3);
        execution.Status.Should().Be(ExecutionStatus.Passed);
        step.Status.Should().Be(StepStatus.Completed, "a billing failure never fails the step");
        step.CreditsUnsettledMillis.Should().Be(Metered);
        step.CreditsChargedMillis.Should().Be(0);
        (await _kit.BalanceAsync()).ReservedMillis.Should().Be(UnsettledHoldTestKit.HoldMillis, "the hold stays open for the sweep");
    }

    [Fact]
    public async Task Settle_refused_as_reservation_closed_is_terminal_and_not_retried()
    {
        await _kit.GrantAsync(100_000);
        var gate = new FlakyGate(_kit.Gate(), _ => Task.FromResult(new CreditSettlement(false, CreditLedgerErrors.ReservationClosed)));

        (_, WorkflowStepResult step) = await RunAsync(gate, Metered, StepStatus.Completed);

        gate.SettleCalls.Should().Be(1);
        step.CreditsUnsettledMillis.Should().BeNull("a closed hold has nothing left for the sweep");
    }

    [Fact]
    public async Task Release_for_a_user_class_that_never_succeeds_records_the_metered_amount()
    {
        await _kit.GrantAsync(100_000);
        var gate = new FlakyGate(_kit.Gate(), _ => null,
            _ => Task.FromException<CreditSettlement>(new InvalidOperationException("pool exhausted")));

        (_, WorkflowStepResult step) = await RunAsync(gate, 3_000, StepStatus.Failed, failCode: "CONFIG_INVALID");

        gate.ReleaseCalls.Should().Be(3);
        step.Status.Should().Be(StepStatus.Failed);
        step.FailureClass.Should().Be(FailureClass.UserInput);
        step.CreditsUnsettledMillis.Should().Be(3_000);
    }

    [Fact]
    public async Task Release_for_a_refundable_class_is_not_retried()
    {
        await _kit.GrantAsync(100_000);
        var gate = new FlakyGate(_kit.Gate(), _ => null,
            _ => Task.FromException<CreditSettlement>(new InvalidOperationException("pool exhausted")));

        (_, WorkflowStepResult step) = await RunAsync(gate, 3_000, StepStatus.Failed, failCode: "ENCODE_FAILED");

        step.FailureClass.Should().Be(FailureClass.PlatformInternal);
        gate.ReleaseCalls.Should().Be(1, "nothing is owed, so the run-level release (or the sweep) closes it");
        step.CreditsUnsettledMillis.Should().BeNull();
    }

    [Fact]
    public async Task Release_execution_settles_a_completed_steps_open_hold_instead_of_refunding_it()
    {
        await _kit.GrantAsync(100_000);
        (Guid execution, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Running, unsettledMillis: Metered);

        (await _kit.Gate().ReleaseExecutionAsync(execution)).Should().Be(1);

        IReadOnlyList<CreditLedgerEntry> ledger = await _kit.EntriesAsync();
        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release);
        CreditLedgerEntry settle = ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle);
        settle.RefEntryId.Should().Be(reserve);
        settle.IdempotencyKey.Should().Be($"gate-settle:{reserve:N}");
        settle.BalanceDeltaMillis.Should().Be(-Metered);
        CreditLedgerResult balance = await _kit.BalanceAsync();
        balance.BalanceMillis.Should().Be(100_000 - Metered);
        balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Release_execution_closes_a_failed_steps_hold_by_its_recorded_class()
    {
        await _kit.GrantAsync(100_000);
        (Guid execution, _, _) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Running, unsettledMillis: 3_000, failureClass: FailureClass.UserInput);

        (await _kit.Gate().ReleaseExecutionAsync(execution)).Should().Be(1);

        CreditLedgerEntry settle = (await _kit.EntriesAsync()).Single(e => e.Kind == CreditLedgerEntryKind.Settle);
        settle.BalanceDeltaMillis.Should().Be(-3_000);
        settle.Note.Should().Be(nameof(FailureClass.UserInput));
    }

    [Fact]
    public async Task Release_execution_never_refunds_a_hold_settled_before_it_runs()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Running, unsettledMillis: Metered, failureClass: FailureClass.PlatformInternal);
        CreditGate.OpenHold snapshot;
        await using (WorkflowEngineDbContext db = _kit.Factory.CreateDbContext())
        {
            // The hold as the release saw it: still open.
            snapshot = await CreditGate.OpenHolds(db).SingleAsync(h => h.ReserveId == reserve);
        }

        // The step's own settle lands after that read and before the release closes the hold.
        (await _kit.Gate().SettleAsync(reserve, Metered)).Ok.Should().BeTrue();
        CreditSettlement closed = await CreditGate.CloseOpenHoldAsync(_kit.Gate(), snapshot, CancellationToken.None);

        closed.Ok.Should().BeTrue();
        closed.RefundedMillis.Should().Be(0);
        IReadOnlyList<CreditLedgerEntry> ledger = await _kit.EntriesAsync();
        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
        ledger.Single(e => e.Kind == CreditLedgerEntryKind.Settle).RefEntryId.Should().Be(reserve);
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(100_000 - Metered);
    }

    [Fact]
    public async Task Running_step_under_a_cancelled_execution_keeps_its_hold_through_a_sweep_and_settles_normally_when_it_finishes()
    {
        await _kit.GrantAsync(100_000);
        int? swept = null;
        Func<StepExecutionContext, WorkflowEngineDbContext, Task> duringStep = async (context, _) =>
        {
            // Another replica has stopped this run: its row is Cancelled with a live lease, and this step is still Running.
            await using (WorkflowEngineDbContext engine = _kit.Factory.CreateDbContext())
            {
                engine.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = context.Execution.Id,
                    WorkflowDefinitionId = context.Execution.WorkflowDefinitionId,
                    OrganizationId = _kit.Org,
                    Status = ExecutionStatus.Cancelled,
                    LeaseOwner = "replica-b",
                    LeaseExpiresAt = DateTime.UtcNow.AddDays(3)
                });
                engine.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = context.StepResultId,
                    WorkflowExecutionId = context.Execution.Id,
                    WorkflowStepId = context.Step.Id,
                    Status = StepStatus.Running
                });
                await engine.SaveChangesAsync();
            }

            swept = await new UnsettledHoldSweeper(
                _kit.Factory, _kit.Gate(), Options.Create(new SettleSweepOptions { MinAgeSeconds = 600 }),
                Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<UnsettledHoldSweeper>.Instance,
                new FixedTime(DateTimeOffset.UtcNow.AddHours(49))).SweepOnceAsync(CancellationToken.None);
        };

        (_, WorkflowStepResult step) = await RunAsync(_kit.Gate(), Metered, StepStatus.Completed, duringStep: duringStep);

        swept.Should().Be(0, "a Running step under a live lease is never swept, whatever its execution says");
        IReadOnlyList<CreditLedgerEntry> ledger = await _kit.EntriesAsync();
        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
        CreditLedgerEntry settle = ledger.Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle).Subject;
        settle.BalanceDeltaMillis.Should().Be(-Metered);
        step.CreditsUnsettledMillis.Should().BeNull();
    }

    [Fact]
    public async Task A_step_cancelled_by_propagation_is_classified_UserCancelled()
    {
        await _kit.GrantAsync(100_000);
        ExecutionCancellationRegistry registry = new();
        int propagated = -1;
        string? reason = null;
        Func<StepExecutionContext, WorkflowEngineDbContext, Task> duringStep = async (context, engine) =>
        {
            // Another replica consumed the Stop: it wrote Cancelled with the reason CancelExecutionAsync records.
            WorkflowExecution row = await engine.WorkflowExecutions.SingleAsync(e => e.Id == context.Execution.Id);
            row.Status = ExecutionStatus.Cancelled;
            row.ErrorMessage = "Stopped by user 0e1f";
            row.LeaseOwner = "replica-a";
            row.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1);
            await engine.SaveChangesAsync();

            ExecutionLeaseService leases = new(new ScopeFactory(engine), ConfigOf("replica-a"), NullLogger<ExecutionLeaseService>.Instance, cancellations: registry);
            leases.Track(context.Execution.Id);
            propagated = await leases.PropagateStopsAsync(CancellationToken.None);
            reason = registry.GetReason(context.Execution.Id);

            // The step observes the stop the way a local one reaches it: its token is cancelled.
            throw new OperationCanceledException(context.CancellationToken);
        };

        (_, WorkflowStepResult step) = await RunAsync(_kit.Gate(), Metered, StepStatus.Failed, duringStep: duringStep, registry: registry);

        propagated.Should().Be(1);
        reason.Should().Be("Stopped by user 0e1f");
        step.FailureClass.Should().Be(FailureClass.UserCancelled);
    }

    [Fact]
    public async Task A_duplicate_delivery_while_a_stopped_step_is_running_is_refused_and_the_hold_settles_once()
    {
        // S-49/S-50: a redelivery of a run whose stopped step is still running is refused by the
        // runner, so the second run never re-enters the registry or releases the first run's hold.
        await _kit.GrantAsync(100_000);
        using InMemoryContext db = new();
        ExecutionCancellationRegistry registry = new();
        ExecutionLeaseService leases = new(new ScopeFactory(db), ConfigOf("replica-a"), NullLogger<ExecutionLeaseService>.Instance, cancellations: registry);

        TaskCompletionSource inStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executorCalls = 0;
        var executor = new ScriptedExecutor(StepType.Agent, async context =>
        {
            if (Interlocked.Increment(ref executorCalls) == 1)
            {
                // Another replica consumed the Stop: the row is Cancelled, and the lease is untouched.
                WorkflowExecution row = await db.WorkflowExecutions.SingleAsync(e => e.Id == context.Execution.Id);
                row.Status = ExecutionStatus.Cancelled;
                row.ErrorMessage = "Stopped by user 0e1f";
                await db.SaveChangesAsync();
                inStep.TrySetResult();
                await proceed.Task;
            }

            await new UsageRecorder(new FixedPriceService(Metered), new NullUsageSink(), NullLogger<UsageRecorder>.Instance)
                .RecordAsync(new UsageRecordRequest(UsageKind.LlmOutputTokens, 1_000m, null, InferenceProviderKind.OpenAICompatible, "test-model", UsageBillingMode.Platform));
            return new StepExecutionResult { Output = "{\"ok\":true}", NextStepIndex = context.CurrentStepIndex + 1, NewIterationCount = context.IterationCount, Status = StepStatus.Completed };
        });

        WorkflowExecutorService service = NewService(db, _kit.Gate(), executor, registry, new ScopeFactory(db, leases));
        WorkflowExecutionRunner runner = new(
            new ScopeFactory(db, service, leases),
            new ConfigurationBuilder().Build(),
            registry,
            new DecisionQuestionMemoizer(),
            new ExecutionAdmissionGuard(new TestPlatformSwitches(), new ScopeFactory(db, service, leases), NullLogger<ExecutionAdmissionGuard>.Instance),
            NullLogger<WorkflowExecutionRunner>.Instance,
            leases);

        Guid projectId = Guid.NewGuid();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id, AgentDefinition = agent };
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = projectId, Steps = [step] };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            OrganizationId = _kit.Org,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        (await runner.DispatchAsync(execution.Id, "corr-d1", CancellationToken.None, false, _kit.Org)).Admitted.Should().BeTrue();
        await inStep.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AdmissionDecision second = await runner.DispatchAsync(execution.Id, "corr-d2", CancellationToken.None, false, _kit.Org);
        second.Admitted.Should().BeFalse();
        second.Refusal.Should().Be(AdmissionRefusal.AlreadyRunningHere);
        leases.TrackedIds.Should().Contain(execution.Id);
        registry.Contains(execution.Id).Should().BeTrue();

        proceed.TrySetResult();
        await WaitForAsync(() => runner.RunningCount == 0);

        executorCalls.Should().Be(1);
        IReadOnlyList<CreditLedgerEntry> ledger = await _kit.EntriesAsync();
        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
        ledger.Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle).Subject.BalanceDeltaMillis.Should().Be(-Metered);
        leases.TrackedIds.Should().NotContain(execution.Id);
    }

    [Fact]
    public async Task A_stop_recorded_before_the_run_does_not_bill_a_platform_cancel_as_UserCancelled()
    {
        // S-53: a Stop recorded before the run registers belongs to no run. The step's own cancellation
        // is therefore a platform interruption (refunded), not a user cancel (billed).
        await _kit.GrantAsync(100_000);
        ExecutionCancellationRegistry registry = new();

        (_, WorkflowStepResult step) = await RunAsync(
            _kit.Gate(), Metered, StepStatus.Failed,
            registry: registry,
            beforeExecute: id => registry.TryCancel(id, "Stopped by user 0e1f"),
            cancelAfterMetering: true);

        step.FailureClass.Should().Be(FailureClass.PlatformInternal);
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(100_000);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 10 s.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void Close_retry_waits_are_200ms_then_1s()
    {
        using var db = new InMemoryContext();
        var service = new WorkflowExecutorService(
            new ScopeFactory(db), new NoOpEventPublisher(), NullLogger<WorkflowExecutorService>.Instance, [],
            new RabbitMqHelper(new ConfigurationBuilder().Build()), Options.Create(new WorkflowHardeningOptions()),
            new ExecutionCancellationRegistry());

        service.CreditCloseRetryDelays.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));
    }

    // -----------------------------------------------------------------
    // Harness: the real ExecuteAsync over one scripted step.
    // -----------------------------------------------------------------

    private async Task<(WorkflowExecution Execution, WorkflowStepResult Step)> RunAsync(
        ICreditGate gate, long metered, StepStatus outcome, string? failCode = null,
        Func<StepExecutionContext, WorkflowEngineDbContext, Task>? duringStep = null, ExecutionCancellationRegistry? registry = null,
        Action<Guid>? beforeExecute = null, bool cancelAfterMetering = false)
    {
        using var db = new InMemoryContext();
        Guid projectId = Guid.NewGuid();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        StepType type = outcome == StepStatus.Completed ? StepType.Agent : StepType.VideoCompile;
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = type, AgentDefinitionId = agent.Id, AgentDefinition = agent };
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "t", ProjectId = projectId, Steps = [step] };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            OrganizationId = _kit.Org,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var executor = new ScriptedExecutor(type, async context =>
        {
            if (duringStep is not null)
            {
                await duringStep(context, db);
            }

            await new UsageRecorder(new FixedPriceService(metered), new NullUsageSink(), NullLogger<UsageRecorder>.Instance)
                .RecordAsync(new UsageRecordRequest(UsageKind.LlmOutputTokens, 1_000m, null, InferenceProviderKind.OpenAICompatible, "test-model", UsageBillingMode.Platform));
            if (cancelAfterMetering)
            {
                throw new OperationCanceledException();
            }

            return outcome == StepStatus.Completed
                ? new StepExecutionResult { Output = "{\"ok\":true}", NextStepIndex = context.CurrentStepIndex + 1, NewIterationCount = context.IterationCount, Status = StepStatus.Completed }
                : new StepExecutionResult
                {
                    Output = $"{{\"status\":\"failed\",\"error\":{{\"code\":\"{failCode}\",\"message\":\"boom\"}}}}",
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    Status = StepStatus.Failed,
                    ErrorDetails = "boom"
                };
        });

        WorkflowExecutorService service = NewService(db, gate, executor, registry);
        beforeExecute?.Invoke(execution.Id);
        try
        {
            await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);
        }
        catch (Exception) when (outcome == StepStatus.Failed)
        {
            // a failed step fails the run; the assertions are about the money
        }

        WorkflowStepResult stepResult = await db.WorkflowStepResults.AsNoTracking().SingleAsync(r => r.WorkflowExecutionId == execution.Id);
        WorkflowExecution after = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == execution.Id);
        return (after, stepResult);
    }

    private static IConfiguration ConfigOf(string instance)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = instance }).Build();

    private static WorkflowExecutorService NewService(WorkflowEngineDbContext db, ICreditGate? gate, IStepExecutor? executor = null, ExecutionCancellationRegistry? registry = null, IServiceScopeFactory? scopeFactory = null)
        => new(
            scopeFactory: scopeFactory ?? new ScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executor is null ? [] : [executor],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: registry ?? new ExecutionCancellationRegistry(),
            creditGate: gate,
            creditOptions: Options.Create(new StepCreditOptions { StepReserveMillisOverride = UnsettledHoldTestKit.HoldMillis }))
        {
            CreditCloseRetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
        };

    private sealed class InMemoryContext() : WorkflowEngineDbContext(
        new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class ScriptedExecutor(StepType stepType, Func<StepExecutionContext, Task<StepExecutionResult>> body) : IStepExecutor
    {
        public StepType StepType { get; } = stepType;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => body(context);
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

    /// <summary>
    /// Every scope resolves <paramref name="db"/>, then the first of <paramref name="extras"/> assignable to
    /// the requested type, then null: the same lookup the DI container would do for these singletons.
    /// </summary>
    private sealed class ScopeFactory(WorkflowEngineDbContext db, params object[] extras) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(db, extras);

        private sealed class Scope(WorkflowEngineDbContext db, object[] extras) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(WorkflowEngineDbContext))
                {
                    return db;
                }

                return extras.FirstOrDefault(serviceType.IsInstanceOfType);
            }

            public void Dispose() { }
        }
    }

    private sealed class FixedPriceService(long creditsMillis) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(creditsMillis, 0.001m, Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(creditsMillis, costUsd, null));

        public void Invalidate() { }
    }

    private sealed class NullUsageSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }
}
