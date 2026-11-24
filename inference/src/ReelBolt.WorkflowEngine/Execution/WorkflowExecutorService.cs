using ReelBolt.Shared.Observability;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Tenancy;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Skills;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.Snapshots;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Enhanced workflow executor using the step executor strategy pattern.
/// </summary>
public class WorkflowExecutorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowEventPublisher _eventPublisher;
    private readonly ILogger<WorkflowExecutorService> _logger;
    private readonly Dictionary<StepType, IStepExecutor> _executors;
    private readonly RabbitMqHelper _rabbitHelper;
    private readonly WorkflowHardeningOptions _hardeningOptions;

    // Cross-execution step-result caching (Execution/Caching). All three are nullable/optional so
    // every existing direct-construction call site (this type is resolved Scoped in production,
    // but several unit tests new it up by hand with named arguments and no knowledge of caching)
    // keeps compiling unchanged — omitting these three parameters simply means caching behaves as
    // fully disabled. IStepCachePolicy/IStepCacheKeyBuilder are pure and stateless, so they are
    // safe to accept via this OUTER constructor scope; IStepResultCache/
    // IProjectFileFingerprintProvider are themselves Scoped services that depend on
    // WorkflowEngineDbContext, and ExecuteAsync already creates its OWN inner scope (see
    // `_scopeFactory.CreateScope()` below) to resolve that DbContext — so those two are resolved
    // from that SAME inner scope inside ExecuteAsync instead, rather than injected here, to avoid
    // silently pairing them with a DIFFERENT WorkflowEngineDbContext instance than the one the rest
    // of the execution uses.
    private readonly IStepCachePolicy? _cachePolicy;
    private readonly IStepCacheKeyBuilder? _cacheKeyBuilder;
    private readonly StepCacheOptions? _cacheOptions;

    // Deterministic, PROMPT-ONLY bound on how much prior-step output is concatenated into an
    // agent's input (Execution/Context). Optional for the same reason the cache collaborators
    // above are: a null here means StepExecutionContext reproduces the previous, unbounded
    // concatenation byte-for-byte, so every hand-constructing unit test is unaffected. This value
    // is only ever handed to StepExecutionContext.BudgetOptions, which feeds BuildAgentInput and
    // nothing else — it must never reach a persistence path, since StepOutputHistory and the
    // persisted WorkflowStepResult.OutputJson always keep the raw, unbudgeted output.
    private readonly AgentInputBudgetOptions? _budgetOptions;

    // Cancellation tokens for running executions live in a separate SINGLETON
    // (ExecutionCancellationRegistry — see its doc comment for why), not as an instance field
    // here: this service is registered Scoped (it depends on the Scoped IWorkflowEventPublisher),
    // so a stop request arriving in its own DI scope/consumer instance would otherwise get a
    // brand-new WorkflowExecutorService with its own empty dictionary, sharing nothing with the
    // instance actually running the execution it's trying to cancel.
    private readonly ExecutionCancellationRegistry _cancellationRegistry;

    // T-012: the bucket the delivering message said this run was admitted under (WorkflowExecutionRequested.AdmittedRunRateBucket),
    // for the duration of one ExecuteAsync. T-015: a HINT only — the source of truth is the row's AdmittedRunRateBucket, which
    // every re-dispatch (recovery re-publish, REST retry) keeps; either one naming a non-paid bucket triggers the re-check.
    private string? _admittedRunRateBucket;

    // B6b: the per-step credit gate (D8) and the step-side reservation size. Both optional for the
    // same reason the cache collaborators above are — every hand-constructed call site keeps
    // compiling, and an omitted gate simply means no credits are ever held (which is also what
    // Billing:Enabled=false does at runtime: the gate then answers Allowed with no reservation and
    // writes nothing at all).
    private readonly ICreditGate? _creditGate;

    /// <summary>
    /// The error a claimed run ends with when its snapshot could not be taken. Platform provenance, never the user's doing
    /// or the workflow's: the run never started a step.
    /// </summary>
    public const string SnapshotFailedMessage =
        "Interrupted: the platform could not take this run's snapshot of its workflow, so the run did not start. "
        + "This was a platform failure, not a problem with the workflow or a cancellation.";

    /// <summary>This run's lease service (resolved from the run's scope in ExecuteAsync); null in minimal harnesses.</summary>
    private Services.ExecutionLeaseService? _runLeases;
    private readonly StepCreditOptions? _creditOptions;

    // B7: sizes each step's hold from the organization's recorded usage. Optional like the two above,
    // and a null here degrades to StepCreditOptions.DefaultReserveMillisFor (B0's per-step-type
    // default) rather than to no hold at all.
    private readonly ICreditHoldEstimator? _creditHoldEstimator;

    /// <summary>
    /// "Nothing is held": the reservation a step runs under when billing is off, when the gate is
    /// unresolved, or before a step has been priced. Both settle and release of it are no-ops on the
    /// gate side too (a null <see cref="CreditReservation.ReservationId"/>).
    /// </summary>
    private static readonly CreditReservation NoReservation = new(CreditReserveOutcome.Allowed, null);

    /// <summary>
    /// S-3: the waits between the three attempts to close a step's hold (settle, or release by a
    /// user-borne class). Every attempt reuses the same reservation, so the ledger key replays and a
    /// retry can never charge twice. Settable only so tests need not sleep.
    /// </summary>
    internal IReadOnlyList<TimeSpan> CreditCloseRetryDelays { get; init; } = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    public WorkflowExecutorService(
        IServiceScopeFactory scopeFactory,
        IWorkflowEventPublisher eventPublisher,
        ILogger<WorkflowExecutorService> logger,
        IEnumerable<IStepExecutor> executors,
        RabbitMqHelper rabbitHelper,
        IOptions<WorkflowHardeningOptions> hardeningOptions,
        ExecutionCancellationRegistry cancellationRegistry,
        IStepCachePolicy? cachePolicy = null,
        IStepCacheKeyBuilder? cacheKeyBuilder = null,
        IOptions<StepCacheOptions>? cacheOptions = null,
        IOptions<AgentInputBudgetOptions>? budgetOptions = null,
        ICreditGate? creditGate = null,
        IOptions<StepCreditOptions>? creditOptions = null,
        ICreditHoldEstimator? creditHoldEstimator = null)
    {
        _scopeFactory = scopeFactory;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _executors = executors.ToDictionary(e => e.StepType);
        _rabbitHelper = rabbitHelper;
        _hardeningOptions = hardeningOptions.Value;
        _cancellationRegistry = cancellationRegistry;
        _cachePolicy = cachePolicy;
        _cacheKeyBuilder = cacheKeyBuilder;
        _cacheOptions = cacheOptions?.Value;
        _budgetOptions = budgetOptions?.Value;
        _creditGate = creditGate;
        _creditOptions = creditOptions?.Value;
        _creditHoldEstimator = creditHoldEstimator;
    }

    /// <summary>
    /// Low-cardinality metric dimensions for one execution: <c>plan_tier</c> and
    /// <c>compute_target</c>. Never throws (telemetry must not fail a run). Compute target is
    /// <c>cloud</c> until execution rows record where media work ran (runner fabric, area D).
    /// </summary>
    internal static async Task<KeyValuePair<string, object?>[]> ResolveMetricDimensionsAsync(
        IServiceProvider services, Guid? organizationId)
    {
        string? tier = null;
        try
        {
            IOrganizationEntitlements? entitlements = services.GetService<IOrganizationEntitlements>();
            if (entitlements is not null && organizationId is { } org)
                tier = await entitlements.TierAsync(org);
        }
        catch
        {
            // best effort: an unreadable plan is reported as "unknown"
        }

        return
        [
            new(ReelBoltTelemetry.PlanTierTag, ReelBoltTelemetry.NormalizePlanTier(tier)),
            new(ReelBoltTelemetry.ComputeTargetTag, ReelBoltTelemetry.NormalizeComputeTarget("cloud"))
        ];
    }

    /// <summary>
    /// The run's plan-driven queue-priority class (B8c), resolved once when the execution scope opens
    /// and published on <see cref="WorkflowExecutionContext.RunPriority"/> for the compute fabric.
    /// Never throws — an unreadable plan degrades to <see cref="RunPriority.Standard"/>, which is what
    /// self-host and every minimal test scope get.
    /// </summary>
    internal static async Task<int> ResolveRunPriorityAsync(IServiceProvider services, WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.OrganizationId == Guid.Empty)
        {
            return RunPriority.Standard;
        }

        try
        {
            IOrganizationEntitlements? entitlements = services.GetService<IOrganizationEntitlements>();
            return entitlements is null
                ? RunPriority.Standard
                : await RunPriority.OfAsync(entitlements, execution.OrganizationId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // best effort: an unreadable plan queues the run at the standard class, never fails it
            return RunPriority.Standard;
        }
    }

    /// <summary>
    /// Throws when the execution's org plan no longer allows <paramref name="step"/>'s type. Reads the
    /// (TTL-cached) entitlements per step, never once per run; no org or no entitlement service means
    /// no gate (self-host / minimal test providers).
    /// </summary>
    internal static async Task EnsurePlanAllowsStepAsync(
        IServiceProvider services, WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        if (PlanGate.FeatureFor(step.StepType) is null || execution.OrganizationId == Guid.Empty)
            return;
        IOrganizationEntitlements? entitlements = services.GetService<IOrganizationEntitlements>();
        if (entitlements is null)
            return;

        PlanGateViolation? violation = PlanGate.CheckStep(step.StepType, await entitlements.GetAsync(execution.OrganizationId, ct));
        if (violation is not null)
            throw new InvalidOperationException($"Step {step.StepOrder} ({step.StepType}): {violation}");
    }

    // virtual so WorkflowExecutionLivenessTests can substitute a stub body and exercise the
    // dispatch/liveness plumbing without a database, a bus, or any agents.
    /// <summary>
    /// Runs the execution, first re-checking a run the assistant/MCP admitted under the FREE run-rate bucket (T-012):
    /// if the steps loaded now classify as paid (the workflow changed after admission), the run fails with
    /// <see cref="RunClassChangedException"/> before any step executes. T-015: the check reads the execution row's
    /// <see cref="WorkflowExecution.AdmittedRunRateBucket"/>; <paramref name="admittedRunRateBucket"/> (the message's copy)
    /// is a hint that can only ADD the check, never lift it. Null or "paid" on both is no check; any other value is
    /// checked like "free" (fail closed).
    /// </summary>
    public async Task ExecuteAsync(Guid executionId, string correlationId, string? admittedRunRateBucket, CancellationToken ct)
    {
        _admittedRunRateBucket = admittedRunRateBucket;
        try
        {
            await ExecuteAsync(executionId, correlationId, ct);
        }
        finally
        {
            _admittedRunRateBucket = null;
        }
    }

    /// <summary>True when the run was admitted as anything but paid and the steps it loaded are paid.</summary>
    internal static bool RunClassChanged(string? admittedRunRateBucket, IEnumerable<WorkflowStep> loadedSteps) =>
        AdmittedAsNonPaid(admittedRunRateBucket)
        && RunRateClassifier.Classify(loadedSteps) == RunRateBucket.Paid;

    /// <summary>
    /// T-015: the row's bucket and the message's hint, combined fail-closed: true when EITHER names a bucket other than
    /// paid. Null on both (human REST starts, revisions, rows queued before the column existed) is paid: no check.
    /// </summary>
    internal static bool RunClassChanged(string? rowBucket, string? messageHint, IEnumerable<WorkflowStep> loadedSteps) =>
        (AdmittedAsNonPaid(rowBucket) || AdmittedAsNonPaid(messageHint))
        && RunRateClassifier.Classify(loadedSteps) == RunRateBucket.Paid;

    private static bool AdmittedAsNonPaid(string? bucket) =>
        bucket is not null && !string.Equals(bucket, RunRateEvent.PaidBucket, StringComparison.OrdinalIgnoreCase);

    public virtual async Task ExecuteAsync(Guid executionId, string correlationId, CancellationToken ct)
    {
        using Activity? workflowActivity = ReelBoltDiagnostics.ActivitySource.StartActivity("ExecuteWorkflow");
        workflowActivity?.SetTag("execution.id", executionId.ToString());
        workflowActivity?.SetTag("correlation.id", correlationId);

        ReelBoltDiagnostics.ActiveWorkflows.Add(1);

        // create a linked cancellation token source so we can cancel from outside via CancelExecutionAsync
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = linkedCts.Token;
        _cancellationRegistry.Register(executionId, linkedCts);

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        // Resolved from THIS scope (not the constructor) so both share the same
        // WorkflowEngineDbContext instance `db` above uses — see the constructor's doc comment on
        // why. GetService (not GetRequiredService): unresolved simply means caching is disabled,
        // which is also what happens in every unit test that constructs this service with a
        // minimal scope/service provider that never registered them.
        IStepResultCache? stepResultCache = scope.ServiceProvider.GetService<IStepResultCache>();
        IProjectFileFingerprintProvider? projectFileFingerprintProvider =
            scope.ServiceProvider.GetService<IProjectFileFingerprintProvider>();

        // A10/R10: the step-cache key carries a fingerprint of the RESOLVED providers, so switching
        // an organization's provider invalidates a cached step. Resolved from THIS scope for the
        // same reason the two collaborators above are (the resolver itself is a singleton; the
        // organization it resolves FOR comes from the execution's tenancy scope opened just below).
        // GetService, not GetRequiredService: an unresolved resolver simply means the fingerprint is
        // null, which is what every minimally-constructed unit test gets.
        IInferenceProviderResolver? providerResolver = scope.ServiceProvider.GetService<IInferenceProviderResolver>();
        // Re-checks voice consent whenever a Voiceover result is reused instead of produced (cache hit,
        // resume, replay copy). Optional: a host without it behaves as before.
        IVoiceReuseGuard? voiceReuseGuard = scope.ServiceProvider.GetService<IVoiceReuseGuard>();
        // The avatar twin (avatars phase 2): a VideoGenerate clip of a person is reused only while that avatar is
        // still usable and unchanged. Optional for the same reason.
        Services.Avatars.IAvatarReuseGuard? avatarReuseGuard = scope.ServiceProvider.GetService<Services.Avatars.IAvatarReuseGuard>();

        WorkflowExecution? execution = await db.WorkflowExecutions
            .Include(e => e.WorkflowDefinition)
                .ThenInclude(w => w.Steps)
                    .ThenInclude(s => s.AgentDefinition)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        if (execution == null)
        {
            _logger.LogError("Workflow execution {ExecutionId} not found", executionId);
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            return;
        }

        // only queued executions are eligible to start; this prevents redelivery
        // or duplicate messages from re-running a completed/cancelled execution
        if (execution.Status != ExecutionStatus.Queued)
        {
            _logger.LogInformation(
                "Execution {ExecutionId} is in status {Status} and will not be started again",
                executionId,
                execution.Status);
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            return;
        }

        // A run pointing at a T-002 archive (a kind-5 row of a legacy run's edited steps, owned by no run) has no
        // engine-taken snapshot: it is never resumed or run from. With its Standard workflow still there it gets a
        // fresh snapshot below, as a legacy run does, and starts at step 1; with the workflow deleted there is
        // nothing safe to run.
        bool archivedSnapshot = false;
        try
        {
            if (execution.SnapshotDefinitionId is { } pointedSnapshotId
                && !await ResumeCursor.IsEngineTakenSnapshotAsync(db, execution.Id, pointedSnapshotId, ct))
            {
                archivedSnapshot = true;
                if (execution.WorkflowDefinition.Kind != WorkflowDefinitionKind.Standard)
                {
                    _logger.LogWarning(
                        "Execution {ExecutionId} points at archive {ArchiveId} and its workflow was deleted; refusing to run it",
                        executionId, pointedSnapshotId);
                    execution.Status = ExecutionStatus.Failed;
                    execution.PendingResume = false;
                    execution.ErrorMessage = "This run's workflow was deleted and the run only has an archive of its old steps, which cannot be run again.";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await _eventPublisher.PublishExecutionFailedAsync(execution, ct);
                    ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
                    _cancellationRegistry.Remove(executionId);
                    return;
                }

                _logger.LogInformation(
                    "Execution {ExecutionId} points at archive {ArchiveId}, not an engine-taken snapshot; taking a fresh snapshot and starting at step 1",
                    executionId, pointedSnapshotId);
                // The archive pointer stays on the row through the claim: it is replaced only by the save that
                // commits the new snapshot (below), so a snapshot that fails leaves the run pointing at its archive.
            }
        }
        catch
        {
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            throw;
        }

        // B8c: the run's plan priority, resolved ONCE here and carried on the scope below, so every
        // executor that creates runner work stamps the same class on it (runner_jobs.priority, which
        // the gateway's claim orders by). Reading it here rather than per job also means a plan change
        // mid-run cannot split one run across two classes.
        int runPriority = await ResolveRunPriorityAsync(scope.ServiceProvider, execution, ct);

        // The execution's tenancy, opened ONCE for the whole run. Provider resolution is org-scoped
        // (IInferenceTenantAccessor reads this), and several callers resolve providers OUTSIDE any
        // agent scope: the VideoAnalyze/VideoCompile/Voiceover/VideoGenerate executors, the decision
        // gate, the context-relevance selector and the guardrail screeners. Without this scope they
        // would silently fall to the platform-only view and ignore the org's own providers. The
        // nested per-agent scopes the executors open re-state the same organization.
        using IDisposable executionTenancyScope =
            scope.ServiceProvider.GetService<IWorkflowExecutionContextAccessor>()
                ?.BeginScope(execution.Id, execution.ProjectId, correlationId, execution.OrganizationId, runPriority)
            ?? NullDisposable.Instance;

        if (runPriority > RunPriority.Standard)
        {
            _logger.LogInformation(
                "Execution {ExecutionId} is running at plan queue priority {RunPriority}",
                executionId,
                runPriority);
        }

        // F9: the execution span starts before the tenant is known; tag it now. The metric dimensions
        // are the ONLY tenant-derived values metrics may carry, and both are bounded sets (the plan
        // key is normalized, the compute target is a three-way word), never the org id.
        if (execution.OrganizationId is { } telemetryOrgId)
            workflowActivity?.SetTag(ReelBoltTelemetry.OrgIdTag, telemetryOrgId.ToString());
        KeyValuePair<string, object?>[] metricDimensions = await ResolveMetricDimensionsAsync(scope.ServiceProvider, execution.OrganizationId);

        // Resume (plan decision 6): the intent lives on the row, so any delivery of a pending-resume row —
        // the route's own message or the sweeper's Queued re-publish — resumes it. It is consumed in the
        // same save that moves the row to Running: a dispatch lost before that save leaves the row Queued
        // with its intent intact, and one lost after it is a lost Running run (the sweeper's case).
        // A resume keeps the run's original StartedAt.
        bool resuming = execution.PendingResume && !archivedSnapshot;
        DateTime? startedAt = !resuming || execution.StartedAt is null ? DateTime.UtcNow : execution.StartedAt;
        // F4: take the execution lease in the same write that publishes Running, so there is never a
        // Running row without a lease (GetService: unresolved in unit tests, which means no lease).
        _runLeases = scope.ServiceProvider.GetService<Services.ExecutionLeaseService>();
        if (db.Database.IsRelational())
        {
            // SQFU-S4: the claim is a conditional UPDATE (WHERE status = Queued). The status check above read the
            // row; a duplicate delivery on another engine can read it as Queued too, and only one of them may start.
            Services.LeaseStamp? stamp = _runLeases?.NewStamp();
            string? leaseOwner = stamp?.Owner ?? execution.LeaseOwner;
            DateTime? heartbeatAt = stamp?.HeartbeatAt ?? execution.HeartbeatAt;
            DateTime? leaseExpiresAt = stamp?.ExpiresAt ?? execution.LeaseExpiresAt;
            int claimed = await db.WorkflowExecutions
                .Where(e => e.Id == executionId && e.Status == ExecutionStatus.Queued)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(e => e.Status, ExecutionStatus.Running)
                    .SetProperty(e => e.PendingResume, false)
                    .SetProperty(e => e.AttentionJson, (string?)null)
                    .SetProperty(e => e.StartedAt, startedAt)
                    .SetProperty(e => e.FirstStartedAt, e => e.FirstStartedAt ?? startedAt)
                    .SetProperty(e => e.LeaseOwner, leaseOwner)
                    .SetProperty(e => e.HeartbeatAt, heartbeatAt)
                    .SetProperty(e => e.LeaseExpiresAt, leaseExpiresAt), ct);
            if (claimed == 0)
            {
                _logger.LogInformation(
                    "Execution {ExecutionId} was claimed by another delivery before this one; not starting it again",
                    executionId);
                ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
                _cancellationRegistry.Remove(executionId);
                return;
            }

            if (stamp is not null)
                _runLeases!.Adopt(execution, stamp);
        }
        else
        {
            _runLeases?.StampLease(execution);
        }

        // A resume that reuses an already-exhausted review step must keep its attention; capture it before the clear.
        ExecutionAttention? priorAttention = resuming ? ExecutionAttention.TryParse(execution.AttentionJson) : null;
        execution.PendingResume = false;
        execution.AttentionJson = null; // a new attempt never inherits the last attempt's "needs attention"
        execution.Status = ExecutionStatus.Running;
        execution.StartedAt = startedAt;
        execution.FirstStartedAt ??= startedAt; // written once: a Retry or resume keeps the first start
        await db.SaveChangesAsync(ct);

        // The steps this dispatch runs: a Standard run's own snapshot, taken now if it has none yet (a
        // retry or resume of the same row reloads it), so an edit of the workflow mid-run can neither
        // change the run nor cascade-delete its results. Taken only AFTER the claim above succeeded
        // (SQFU residual b): a delivery that lost the claim never snapshots or writes snapshot_definition_id.
        // A claimed run whose snapshot fails ends Failed as a platform failure; it is never left Running.
        ExecutionRunPlan runPlan;
        try
        {
            // An archive is never run from: cleared in memory only, so PrepareAsync takes a fresh snapshot and its
            // one SaveChanges writes the new pointer together with the snapshot rows.
            if (archivedSnapshot)
                execution.SnapshotDefinitionId = null;
            runPlan = await ExecutionSnapshotter.PrepareAsync(db, execution, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while the snapshot was being taken: Cancelled with the reason the canceller recorded.
            // A shutdown leaves the claimed row Running (lease ended by the runner) for the recovery sweeper.
            if (!_cancellationRegistry.IsShutdown(executionId))
                await EndUnpreparedRunAsync(executionId, ExecutionStatus.Cancelled, FailureMessages.UserMessage(
                    FailureMessages.CancellationCode(_cancellationRegistry.GetReason(executionId))));
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            return;
        }
        catch (AppInputsStaleException stale)
        {
            // docs/design/app-inputs.md 6.3: the workflow changed between Run and the claim so a stored app-input value no
            // longer binds. Failed before the first step (platform class: nothing was held), with a plain sentence.
            _logger.LogWarning(
                "Execution {ExecutionId}: its app-input values no longer bind to the workflow ({Code}): {Problems}",
                executionId, stale.Code, string.Join(" | ", stale.Problems.Select(p => p.Message)));
            await EndUnpreparedRunAsync(executionId, ExecutionStatus.Failed, AppInputsStaleException.UserMessage);
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Taking the snapshot of execution {ExecutionId} failed after it was claimed; ending it Failed", executionId);
            await EndUnpreparedRunAsync(executionId, ExecutionStatus.Failed, SnapshotFailedMessage);
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
            throw;
        }

        await _eventPublisher.PublishExecutionRunningAsync(execution, ct);

        List<WorkflowStep> steps = runPlan.Steps;

        // The run's agents as they were when it started, read by the chat-client seam
        // (CostRoutingChatClientProvider) for every agent this execution runs. Null — live rows, as
        // before snapshots — for a run without one.
        using IDisposable agentSnapshotScope = AgentSnapshotAmbient.Enter(runPlan.Agents);

        // The agents' app-chosen Chat models (docs/design/app-inputs.md 8.5): the run's cold-start holds price their chat at
        // the chosen model, as the Inference API's estimate did. Null for every run without such a choice.
        // docs/design/app-inputs.md 28: and a Voiceover whose speech model the app chose holds at that model.
        using IDisposable appAgentModelScope = ReelBolt.Shared.Metering.AppAgentModelAmbient.Enter(
            runPlan.Agents?.AppModels(), runPlan.AppInputs?.StepModels);

        // What every step reads from the steps before it — the step cache's upstream digest (v5) and a
        // composition revision's replay both come from this one analysis.
        WorkflowStepDependencyGraph dependencyGraph = WorkflowStepDependencyGraph.Build(steps);

        // The run's editing style and output-format override (docs/editing-styles.md), carried to
        // every agent this execution runs through an async-local scope. Null — no scope content,
        // every prompt byte-identical — for a run that chose neither.
        // The run's pinned brand and product profile (docs/design-system.md), loaded here where a
        // scope and its DbContext are available and carried to every agent too. Null — no blocks —
        // when the run has no usable pin.
        DesignSystemRunState? designState = await DesignSystemRunState.LoadAsync(execution, scope.ServiceProvider, ct);

        // The run's typed brief (docs/run-brief.md): language, length, voice, source language and files.
        // The language falls back to the pinned design's voice.locale; the brief wins. Null — no block,
        // no override, every prompt and key byte-identical — for a run with neither.
        RunBriefState? briefState = RunBriefState.From(
            execution,
            designState?.Profile.Design is { } designDocument ? DesignSystemPromptView.VoiceLocale(designDocument) : null);
        using IDisposable runBriefScope = RunBriefAmbient.Enter(briefState);

        // The run's app inputs (docs/design/app-inputs.md 9.2, 10.1): input.* parameters, per-step cache components and the
        // reachable steps the holds plan over. Null for a run that carries none: nothing below changes.
        using IDisposable appInputsScope = AppInputsAmbient.Enter(runPlan.AppInputs);

        using IDisposable runStyleScope = RunStyleAmbient.Enter(RunStyleState.From(
            execution,
            scope.ServiceProvider.GetService<IStyleNudgeService>() ?? StyleNudgeService.Instance,
            briefSetsLanguage: briefState?.Language is not null));

        using IDisposable designSystemScope = DesignSystemAmbient.Enter(designState);

        // The project's own people and objects the shot planner may show (docs/design/avatar-objects.md "Avatars and
        // objects in planned shots"): read once, only for a run whose VideoGenerate step reads its plan from a planner
        // step and lets it use subjects. Null (no block, every prompt and key byte-identical) for every other run.
        using IDisposable projectSubjectsScope = Services.Avatars.ProjectSubjectsAmbient.Enter(
            await Services.Avatars.ProjectSubjectsRunState.LoadAsync(execution, steps, scope.ServiceProvider, _logger, ct));

        _logger.LogInformation(
            "Starting workflow execution {ExecutionId} for project {ProjectId} with {StepCount} steps (CorrelationId={CorrelationId})",
            execution.Id,
            execution.ProjectId,
            steps.Count,
            correlationId);

        // What the failing step said about itself, for the run-level message (see FailureMessages.RunMessage).
        FailureClass lastStepClass = FailureClass.None;
        string? lastStepCode = null;
        string? lastStepUserMessage = null;

        try
        {
            // T-012: a run admitted as free is re-classified over the steps actually loaded, before any step runs
            // (nothing is held or charged yet: holds are taken per step, inside the loop below). The failure path below
            // marks it Failed and publishes it. T-015: the row is the source of truth, so a recovery re-publish or a retry
            // whose message carries no bucket is still checked.
            if (RunClassChanged(execution.AdmittedRunRateBucket, _admittedRunRateBucket, steps))
            {
                _logger.LogWarning(
                    "Execution {ExecutionId} was admitted as {AdmittedBucket} (message hint {AdmittedHint}) but its loaded steps are paid; failing with {Code}",
                    executionId, execution.AdmittedRunRateBucket, _admittedRunRateBucket, RunClassChangedException.FailureCode);
                throw new RunClassChangedException();
            }

            string accumulatedOutput = string.Empty;
            int iterationCount = 0;
            int currentStepIndex = 0;
            ReviewExhausted? worstReviewExhausted = null;
            var stepOutputHistory = new List<StepOutputHistoryEntry>();

            // T-007: numbers from the passes of the current ReviewLoop that already ran. Kept here
            // because a loop-back prunes those passes' outputs from stepOutputHistory, so the
            // reviewer would otherwise have no memory that the edit has been shrinking. Cleared
            // when the loop passes or exhausts (the same point pendingReviewFeedback is cleared).
            var reviewPassFacts = new List<ReviewLoopStepExecutor.ReviewPassFact>();

            int stepTransitionCount = 0;
            const int MaxStepTransitions = 1000;

            // Feedback from a ReviewLoop step that just looped execution backward — seeded onto
            // every step's StepExecutionContext from the loop target through (but not including)
            // the ReviewLoop step itself, through StepExecutionContext.RecordReviewFeedback (its own
            // section of WithRetryGuidance, separate from schema-validation retries), so a retried VideoStoryEditor/
            // MotionGraphicsPlanner/AuthorAgent gets the actual review critique instead of
            // retrying blind. Cleared once the window is exited (either the ReviewLoop step is
            // reached again, or it passed/exhausted MaxIterations and moved forward) so a later,
            // unrelated ReviewLoop step in the same workflow never inherits stale feedback.
            string? pendingReviewFeedback = null;
            int pendingReviewFeedbackMinStepOrder = int.MaxValue;
            int pendingReviewFeedbackMaxStepOrderExclusive = int.MinValue;
            int pendingReviewScore = 0;
            int pendingReviewMinScore = 0;
            // The scene list of the render the reviewer judged, for the one step in the window that
            // produced it (the Author): the loop-back prunes that step's output from history, so
            // this is the only way it learns what was reviewed.
            string? pendingReviewManifestNote = null;
            int pendingReviewManifestStepOrder = int.MinValue;

            // Review-loop adjustments (docs/design/review-loop-adjustments.md): the steps an adjusted rerun reuses
            // instead of re-running (each consumed once), with the output each had before the loop-back pruned it.
            Dictionary<int, StepOutputHistoryEntry?> reviewReuse = [];
            // Change requests a review asked for INSIDE this run (docs/design/review-loop-adjustments.md "Change requests
            // inside the run"), by step order: carried out on the step's next dispatch through the same executor path a
            // customer request takes, then forgotten.
            Dictionary<int, StepChangeRequestScope> reviewChangesPending = [];
            // Steps whose latest live result carried out such a change, and (from a loop-back on) the entry to copy in a
            // later round while nothing the step reads ran again in that round; otherwise the step runs normally.
            HashSet<int> reviewChangedSteps = [];
            Dictionary<int, StepOutputHistoryEntry?> reviewChangedKeep = [];
            HashSet<int> ranLiveThisRound = [];
            // The adjusted re-run the next review records (what each step did and was charged); null when none is under way.
            List<Services.Review.ReviewAdjustmentReport.RerunStep>? adjustedRerunReport = null;
            int? adjustedRerunFrom = null;
            // A review loop with adjustments on: its best-scoring pass so far, and the review whose loop-back window is
            // running. A credit refusal inside that window stops the loop and the run goes on with the best pass.
            BestReviewPass? bestReviewPass = null;
            int? adjustedLoopReviewOrder = null;

            // Before any step can read a file: a result of a run the project's quarantine state changed under is
            // never stored (StepResultCache).
            if (stepResultCache is not null)
                await stepResultCache.CaptureQuarantineBaselineAsync(execution.ProjectId, ct);

            // A composition revision with work downstream of the film runs a per-revision snapshot of the
            // original workflow (WorkflowDefinitionKind.CompositionReplay): each step is run, copied from
            // the run being changed, or skipped, per the revision's plan. Null for every other execution.
            CompositionReplaySession? replay = await CompositionReplaySession.LoadAsync(db, execution, steps, ct);

            // A resume reuses this run's completed results, dispatch by dispatch, until the first one it cannot
            // (ResumeCursor). A Retry (no intent) runs from step 1: its superseded rows are never read. A
            // composition replay has its own reuse plan and is never resumed.
            ResumeCursor? resume = resuming && replay is null
                ? await ResumeCursor.LoadAsync(db, executionId, execution.ProjectId, ResumeCursor.StorageProbe(scope.ServiceProvider), ct)
                : null;

            while (currentStepIndex < steps.Count && !ct.IsCancellationRequested)
            {
                stepTransitionCount++;
                if (stepTransitionCount > MaxStepTransitions)
                {
                    throw new InvalidOperationException(
                        "Workflow execution exceeded the maximum allowed step transitions (possible infinite loop).");
                }

                WorkflowStep step = steps[currentStepIndex];

                using Activity? stepActivity = ReelBoltDiagnostics.ActivitySource.StartActivity("ExecuteStep");
                stepActivity?.SetTag("step.order", step.StepOrder);
                stepActivity?.SetTag("step.type", step.StepType.ToString());
                stepActivity?.SetTag("agent.type", step.AgentDefinition.AgentType.ToString());

                execution.CurrentStepId = step.Id;
                await db.SaveChangesAsync(ct);

                // Composition-revision replay: a step that does not depend on the re-rendered film (or an
                // editorial decision kept under "Keep the same cut") is copied from the run being changed,
                // and a planned skip is recorded — neither runs, holds credits or touches the cache.
                ReplayAction replayAction = replay?.Decide(step) ?? ReplayAction.Execute;
                if (replayAction.Kind == ReplayActionKind.Reuse && replayAction.Source is { } avatarSource
                    && !await AvatarReuseAllowedAsync(avatarReuseGuard, execution, step, CompositionReplaySession.OriginalOutput(avatarSource), ct))
                {
                    _logger.LogWarning(
                        "Execution {ExecutionId} step {StepOrder} (VideoGenerate): not copying the source run's clips because the avatar they show is no longer usable or changed; the step runs",
                        executionId, step.StepOrder);
                    replayAction = ReplayAction.Execute with { Reason = "the avatar these clips show is no longer usable or changed, so they are re-made" };
                    replay!.Invalidate(step.StepOrder);
                }
                if (replayAction.Kind == ReplayActionKind.Reuse && replayAction.Source is { } replaySource
                    && !await VoiceReuseAllowedAsync(voiceReuseGuard, execution, step, CompositionReplaySession.OriginalOutput(replaySource), ct))
                {
                    _logger.LogWarning(
                        "Execution {ExecutionId} step {StepOrder} (Voiceover): not copying the source run's audio because a voice it used is no longer usable; the step runs",
                        executionId, step.StepOrder);
                    replayAction = ReplayAction.Execute with { Reason = "a voice this narration used is no longer usable (consent or retention), so it is re-made" };
                    replay!.Invalidate(step.StepOrder);
                }
                if (replayAction.Kind == ReplayActionKind.Reuse && replayAction.Source is { } copiedSource
                    && !await EarlierOutputReusableAsync(db, execution, copiedSource.ExecutedAt, copiedSource.WorkflowExecutionId, ct))
                {
                    _logger.LogInformation(
                        "Execution {ExecutionId} step {StepOrder} ({StepType}): not copying the source run's result; the step runs",
                        executionId, step.StepOrder, step.StepType);
                    replayAction = ReplayAction.Execute with { Reason = "this project's files changed, so it is re-made" };
                    replay!.Invalidate(step.StepOrder);
                }
                if (replayAction.Kind != ReplayActionKind.Execute)
                {
                    accumulatedOutput = await ApplyReplayActionAsync(
                        db, execution, step, replayAction, replay!, stepOutputHistory, accumulatedOutput,
                        currentStepIndex, iterationCount, ct);
                    currentStepIndex++;
                    continue;
                }

                // Resume: this dispatch's recorded result, when it has a usable one. A reused step runs no
                // executor, holds no credits, writes no cache entry and no new row, and is not re-published; its
                // result feeds the rest of this iteration exactly as a live one would (accumulated output,
                // history, loop counters, Conditional/ReviewLoop routing). The first miss is the frontier.
                ResumedStep? resumed = null;
                if (resume is not null)
                {
                    // A Voiceover result is reused only while its voices are still usable, and no result of a project
                    // with a quarantined file is; otherwise this is the miss.
                    resumed = await resume.TryReuseAsync(step, steps, currentStepIndex, iterationCount, ct,
                        async (output, token) => await VoiceReuseAllowedAsync(voiceReuseGuard, execution, step, output, token)
                            && await AvatarReuseAllowedAsync(avatarReuseGuard, execution, step, output, token)
                            && await EarlierOutputReusableAsync(db, execution, execution.FirstStartedAt, execution.Id, token));
                    if (resumed is null)
                    {
                        int replaced = await resume.SupersedeReplacedAsync(db, ct);
                        _logger.LogInformation(
                            "Execution {ExecutionId} resumes live at step {StepOrder} ({StepType}) after reusing {ReusedCount} recorded result(s), superseding {ReplacedCount} the live run replaces: {Reason}",
                            executionId, step.StepOrder, step.StepType, resume.ReusedCount, replaced, resume.MissReason);
                        resume = null;
                    }
                }

                // An adjusted review rerun reuses this step's earlier result instead of re-running it (it does not
                // depend on any changed setting; a paid step is never re-entered by an adjustment). A resumed dispatch
                // already reused the copy the live run wrote.
                if (reviewReuse.Remove(step.StepOrder, out StepOutputHistoryEntry? reuseEntry) && resumed is null)
                {
                    accumulatedOutput = await CopyForReviewRerunAsync(
                        db, execution, step, reuseEntry, stepOutputHistory, accumulatedOutput, ct);
                    adjustedRerunReport?.Add(new(step.StepOrder, step.StepType.ToString(), "reused", 0));
                    currentStepIndex++;
                    continue;
                }

                // A change request the review asked for inside this run is carried out on this dispatch (a resumed
                // dispatch already carried it out). One the review asked for in an earlier round is kept, by copying
                // its result, while nothing the step reads ran again in this round.
                reviewChangesPending.Remove(step.StepOrder, out StepChangeRequestScope? pendingReviewChange);
                StepChangeRequestScope? reviewChange = resumed is null ? pendingReviewChange : null;
                if (reviewChange is null && reviewChangedKeep.Remove(step.StepOrder, out StepOutputHistoryEntry? keptEntry) && resumed is null)
                {
                    if (!dependencyGraph.DependsOn(step.StepOrder).Keys.Any(ranLiveThisRound.Contains))
                    {
                        accumulatedOutput = await CopyForReviewRerunAsync(
                            db, execution, step, keptEntry, stepOutputHistory, accumulatedOutput, ct);
                        adjustedRerunReport?.Add(new(step.StepOrder, step.StepType.ToString(), "reused", 0));
                        currentStepIndex++;
                        continue;
                    }

                    _logger.LogInformation(
                        "Execution {ExecutionId} step {StepOrder} ({StepType}): a step it reads ran again, so the change the review made to it is not kept; it runs normally",
                        executionId, step.StepOrder, step.StepType);
                    reviewChangedSteps.Remove(step.StepOrder);
                }

                using IDisposable? reviewChangeLease = reviewChange is null
                    ? null
                    : ReviewStepChangeRegistry.Instance.Lease(executionId, step.StepOrder, reviewChange);
                if (reviewChange is not null)
                {
                    _logger.LogInformation(
                        "Execution {ExecutionId} step {StepOrder} ({StepType}) carries out the change its review asked for (this run only)",
                        executionId, step.StepOrder, step.StepType);
                }

                // Runtime plan re-check (B8a): a downgrade or a workflow saved before the plan changed
                // must not reach a gated step type. Fails the execution with a PLAN_NOT_ENTITLED reason.
                await EnsurePlanAllowsStepAsync(scope.ServiceProvider, execution, step, ct);

                // SQFU-S1: never dispatch a step of a run this instance no longer owns.
                await EnsureLeaseHeldAsync(executionId, ct);

                if (!_executors.TryGetValue(step.StepType, out IStepExecutor? executor))
                {
                    _logger.LogWarning("No executor for step type {StepType}, using Agent executor", step.StepType);
                    executor = _executors[StepType.Agent];
                }

                _logger.LogInformation(
                    "Dispatching execution {ExecutionId} step {StepOrder} ({StepType}) to executor {ExecutorType} (Iteration={IterationCount}, CurrentStepIndex={CurrentStepIndex})",
                    executionId,
                    step.StepOrder,
                    step.StepType,
                    executor.GetType().Name,
                    iterationCount,
                    currentStepIndex);

                StepExecutionContext context = new()
                {
                    Execution = execution,
                    Step = step,
                    AllSteps = steps,
                    AccumulatedOutput = accumulatedOutput,
                    StepOutputHistory = stepOutputHistory,
                    ReviewPassHistory = ReviewLoopStepExecutor.RenderReviewPassHistory(reviewPassFacts),
                    CurrentStepIndex = currentStepIndex,
                    IterationCount = iterationCount,
                    CorrelationId = correlationId,
                    UserRequest = execution.UserRequest,
                    BudgetOptions = _budgetOptions,
                    CancellationToken = ct
                };

                if (pendingReviewFeedback is not null &&
                    IsWithinReviewFeedbackWindow(step.StepOrder, pendingReviewFeedbackMinStepOrder, pendingReviewFeedbackMaxStepOrderExclusive))
                {
                    context.RecordReviewFeedback(
                        pendingReviewScore,
                        pendingReviewMinScore,
                        pendingReviewFeedback,
                        step.StepOrder == pendingReviewManifestStepOrder ? pendingReviewManifestNote : null);
                }

                string? initialInputJson = ResolveInputJsonForPersistence(context.LastResolvedAgentInput, accumulatedOutput);
                // T-007: a ReviewLoop's earlier-pass block is part of what the reviewer reads, so a
                // cache key (only reachable via an explicit CacheMode=Always; ReviewLoop is not
                // cacheable by default) must reflect it. Null on the first pass, so a one-pass run's
                // cache-key input is byte-identical to before.
                if (step.StepType == StepType.ReviewLoop)
                    initialInputJson = AppendReviewPassHistory(initialInputJson, context.ReviewPassHistory);
                if (step.StepType == StepType.Agent)
                {
                    string _ = context.BuildAgentInput();
                    initialInputJson = ResolveInputJsonForPersistence(context.LastResolvedAgentInput, accumulatedOutput);

                    // Non-null only when the context budget actually digested or dropped something
                    // (see Execution/Context/AgentInputBudget). Deliberately logged rather than
                    // silently applied: a prompt that lost content is something an operator
                    // diagnosing a surprising agent answer needs to be able to see.
                    if (context.LastBudgetSummary is not null)
                    {
                        _logger.LogInformation(
                            "Execution {ExecutionId} step {StepOrder} ({StepType}) agent input was budgeted — {BudgetSummary}",
                            executionId,
                            step.StepOrder,
                            step.StepType,
                            context.LastBudgetSummary);
                    }
                }

                // A reused dispatch keeps its recorded (untracked) row: no second row, nothing written to it.
                WorkflowStepResult stepResult = resumed?.Row ?? new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = step.Id,
                    Output = string.Empty,
                    TokensUsed = 0,
                    DurationMs = 0,
                    ExecutedAt = DateTime.UtcNow,
                    InputJson = initialInputJson,
                    OutputJson = null,
                    Status = StepStatus.Running,
                    ErrorDetails = null,
                    IterationNumber = iterationCount,
                    CompletedAt = null,
                    OutputStorageKey = null
                };
                if (resumed is null)
                {
                    db.WorkflowStepResults.Add(stepResult);
                    await db.SaveChangesAsync(ct);
                }

                // Wire the ephemeral progress-reporting hook now that stepResult.Id exists — see
                // StepExecutionContext.ReportProgressAsync's doc comment. Capturing `stepResult`
                // by reference here is safe even though its fields mutate below: WorkflowStepProgress
                // only ever reads stepResult.Id, which is fixed at creation.
                context.StepResultId = stepResult.Id;

                // Metering scope (B3b): every chat/decision call the step makes, from agents, room seats,
                // vision captioning and the decision gate, is attributed to this org/execution/step result
                // by MeteringChatClient. Disposed at the end of the step's iteration.
                using IDisposable usageScope = UsageScope.Begin(
                    execution.OrganizationId, execution.ProjectId, executionId, stepResult.Id,
                    execution.InitiatedByUserId, UsageSource.Workflow);
                context.ProgressReporter = (stage, percent, tokensUsedSoFar, inputTokensSoFar, outputTokensSoFar, progressCt) =>
                    _eventPublisher.PublishStepProgressAsync(
                        execution, step, stepResult, stage, percent, progressCt,
                        tokensUsedSoFar, inputTokensSoFar, outputTokensSoFar);
                context.ChatTurnReporter = (turnIndex, totalTurns, speaker, speakerRole, text, idsMentioned, chatCt) =>
                    _eventPublisher.PublishStepChatTurnAsync(
                        execution, step, stepResult, turnIndex, totalTurns, speaker, speakerRole, text, idsMentioned, chatCt);

                if (resumed is null)
                {
                    await _eventPublisher.PublishStepStartedAsync(
                        execution,
                        step,
                        stepResult,
                        CreateLogPreview(initialInputJson, 800),
                        ct);
                }

                // Cross-execution step-result cache (Execution/Caching). Consulted BEFORE the
                // executor runs so a hit skips the agent call / ffmpeg invocation entirely.
                // servedFromCache/cachedTokensSaved are only ever set on the hit path below and
                // flow into stepResult in the SAME persistence block every other result already
                // goes through (see that block's own comment) — a hit is never special-cased past
                // this point, including never going through the ReviewLoop score-parsing branch
                // (automatic: ReviewLoop is never IStepCachePolicy.IsCacheable).
                bool servedFromCache = false;
                int? cachedTokensSaved = null;

                // B6b: this step's credit hold, and what it ended up charging. Declared OUTSIDE the
                // try below so the failure path can close the hold the success path would have
                // settled — the whole point of reserving before a step is that a step which throws
                // still gives the rest of its hold back.
                CreditReservation creditReservation = NoReservation;
                long creditsChargedMillis = 0;

                StepExecutionResult result;
                try
                {
                    if (replay is not null && replay.Plan.For(step.StepOrder)?.Disposition == ReplayStepDisposition.KeepIfCompatible)
                    {
                        _logger.LogInformation(
                            "Execution {ExecutionId} step {StepOrder} ({StepType}) is re-made in this revision: {Reason}",
                            executionId, step.StepOrder, step.StepType, replayAction.Reason);
                        await context.ReportProgressAsync("Re-making this step: " + replayAction.Reason);
                    }

                    string upstreamDigest = StepUpstreamDigest.Compute(dependencyGraph, step, stepOutputHistory);
                    // A step carrying out a review's change request never reads or writes the cache: its key does not
                    // know the change (a customer request runs its target step with CacheMode = Never for the same reason).
                    WorkflowStepCacheEntry? cacheEntry = resumed is not null || reviewChange is not null ? null : await TryGetCachedStepResultAsync(
                        stepResultCache, projectFileFingerprintProvider, providerResolver, execution, step, context, initialInputJson, upstreamDigest, ct,
                        scope.ServiceProvider);
                    if (cacheEntry is not null && !await VoiceReuseAllowedAsync(voiceReuseGuard, execution, step, cacheEntry.Output, ct))
                    {
                        _logger.LogWarning(
                            "Execution {ExecutionId} step {StepOrder} (Voiceover): step-result cache hit not served because a voice it used is no longer usable; the step runs",
                            executionId, step.StepOrder);
                        cacheEntry = null;
                    }
                    if (cacheEntry is not null && !await AvatarReuseAllowedAsync(avatarReuseGuard, execution, step, cacheEntry.Output, ct))
                    {
                        _logger.LogWarning(
                            "Execution {ExecutionId} step {StepOrder} (VideoGenerate): step-result cache hit not served because the avatar it shows is no longer usable or changed; the step runs",
                            executionId, step.StepOrder);
                        cacheEntry = null;
                    }

                    if (resumed is not null)
                    {
                        result = resumed.Result;
                        _logger.LogInformation(
                            "Execution {ExecutionId} step {StepOrder} ({StepType}) reuses its recorded result {StepResultId} (resume)",
                            executionId, step.StepOrder, step.StepType, resumed.Row.Id);
                    }
                    else if (cacheEntry is not null)
                    {
                        result = new StepExecutionResult
                        {
                            // A replayed narration was not synthesized now, so the provider-health notices recorded
                            // when it was made would be stale (a re-test that passed must clear them).
                            Output = step.StepType == StepType.Voiceover
                                ? VoiceoverStepExecutor.WithoutProviderHealthWarnings(cacheEntry.Output)
                                : cacheEntry.Output,
                            NextStepIndex = currentStepIndex + 1,
                            NewIterationCount = context.IterationCount,
                            TokensUsed = 0,
                            InputTokens = 0,
                            OutputTokens = 0,
                            DurationMs = 0,
                            Status = StepStatus.Completed,
                            OutputStorageKey = cacheEntry.OutputStorageKey,
                            ArtifactStorageKey = cacheEntry.ArtifactStorageKey,
                            TimelineStorageKey = cacheEntry.TimelineStorageKey,
                            ChatTranscriptJson = cacheEntry.ChatTranscriptJson
                            // D12: no provenance to carry over. A runner result is never STORED
                            // (see IsAdmissible), so it can never be a hit; and this run put
                            // nothing on a runner to replay — the default Local target is exactly
                            // what happened for this step. FromRunner therefore stays false on a
                            // hit by construction, not by accident.
                        };
                        servedFromCache = true;
                        cachedTokensSaved = cacheEntry.TokensUsed;

                        await context.ReportProgressAsync("Reusing cached result");

                        _logger.LogInformation(
                            "Step {StepOrder} ({StepType}) for execution {ExecutionId} served from the step-result cache (key {CacheKeyPrefix}..., saved {TokensSaved} tokens vs. the original run)",
                            step.StepOrder,
                            step.StepType,
                            executionId,
                            cacheEntry.CacheKey[..Math.Min(12, cacheEntry.CacheKey.Length)],
                            cacheEntry.TokensUsed);
                    }
                    else
                    {
                        // B6b/D8: hold credits BEFORE the step runs. A cache hit never reaches this
                        // branch — a cached step costs zero and reserves nothing
                        // (docs/design/saas-metering.md) — and the reservation is idempotent per
                        // (stepResultId, attempt = 0), so a crash-resumed step is handed back the
                        // hold it already has instead of a second one. Retries live INSIDE
                        // ExecuteStepWithRetryAsync under this single hold: a step that fails
                        // attempt 1 and succeeds on attempt 2 settles once, for what both attempts
                        // together consumed, and is never billed twice.
                        // SQFU-S1: nor reserve credits for it, nor settle them: a step that ran on after the run was
                        // handed over gives its hold back (platform class), and the new owner charges it once.
                        await EnsureLeaseHeldAsync(executionId, ct);
                        creditReservation = await ReserveStepCreditsAsync(execution, steps, step, stepResult, ct);
                        context.CreditHoldMillis = creditReservation.ReservationId is null ? null : creditReservation.ReservedMillis;

                        WebSearchCallCounter? searchCounter = scope.ServiceProvider.GetService<IWorkflowExecutionContextAccessor>()?.Current?.RunWebSearchCalls;
                        int searchesBefore = searchCounter is null ? 0 : Volatile.Read(ref searchCounter.Count);
                        result = await ExecuteStepWithRetryAsync(executor, context, step, ct);
                        if (searchCounter is not null)
                            result.WebSearchCalls = Math.Max(0, Volatile.Read(ref searchCounter.Count) - searchesBefore);

                        await EnsureLeaseHeldAsync(executionId, ct);

                        if (result.Status == StepStatus.Completed && reviewChange is null)
                        {
                            await TryStoreCachedStepResultAsync(
                                stepResultCache, projectFileFingerprintProvider, providerResolver, execution, step, context, initialInputJson, upstreamDigest, result, ct,
                                scope.ServiceProvider);
                        }

                        // Charge what the step ACTUALLY consumed — the usage scope's priced total
                        // for this step, every retry attempt included — and release the remainder.
                        creditsChargedMillis = await SettleStepCreditsAsync(creditReservation, stepResult);
                    }
                }
                catch (Exception ex)
                {
                    result = BuildFailureStepResult(step, context, ex);

                    // B6b/D8: close the hold by failure class — platform-caused classes and
                    // anything we cannot attribute refund the step, user classes keep what it
                    // actually consumed. The class was recorded by whoever produced the failure
                    // (the executor's own result, or the retry wrapper's last attempt); the
                    // cancellation registry's recorded reason decides a cancellation, so a
                    // transport kill or a restart is never billed as the user's stop.
                    FailureClass failureClass = FailureClassifier.Classify(
                        context.LastFailureClass,
                        context.LastFailureCode,
                        ex,
                        _cancellationRegistry.GetReason(executionId));
                    creditsChargedMillis = await ReleaseStepCreditsAsync(creditReservation, failureClass, stepResult);
                    stepResult.FailureClass = failureClass;
                    stepResult.FailureCode = FailureMessages.EnsureCode(
                        context.LastFailureCode, ex, _cancellationRegistry.GetReason(executionId));
                    lastStepClass = failureClass;
                    lastStepCode = stepResult.FailureCode;
                    lastStepUserMessage = context.LastFailureUserMessage;
                    stepResult.CreditsChargedMillis = creditsChargedMillis;

                    stepResult.Output = result.Output;
                    stepResult.TokensUsed = result.TokensUsed;
                    ApplyTokenSplit(stepResult, result);
                    stepResult.DurationMs = result.DurationMs;
                    stepResult.InputJson = ResolveInputJsonForPersistence(context.LastResolvedAgentInput, accumulatedOutput);
                    stepResult.OutputJson = EnsureJsonForJsonbColumn(result.Output);
                    stepResult.Status = StepStatus.Failed;
                    stepResult.ErrorDetails = result.ErrorDetails;
                    stepResult.IterationNumber = result.IterationNumber;
                    stepResult.CompletedAt = DateTime.UtcNow;
                    stepResult.OutputStorageKey = result.OutputStorageKey;
                    stepResult.ArtifactStorageKey = result.ArtifactStorageKey;
                    stepResult.TimelineStorageKey = result.TimelineStorageKey;
                    stepResult.ToolCallsJson = result.ToolCalls.Count > 0 ? JsonSerializer.Serialize(result.ToolCalls) : null;
                    stepResult.ReasoningJson = result.Reasoning.Count > 0 ? JsonSerializer.Serialize(result.Reasoning) : null;
                    stepResult.ChatTranscriptJson = result.ChatTranscriptJson;
                    // D12: the failure path applies provenance too, so both paths write the same
                    // four columns and neither can drift from the other. What it carries today is
                    // whatever the failing attempt reported — and a step that fails before any
                    // executor produced a result reports Local, because the target is known to the
                    // code that ran the work (D6's selector, copied by the executor that used it).
                    // The runner job of a failed step is still reachable the long way round:
                    // runner_jobs is keyed by (execution_id, step_id).
                    ApplyRunnerProvenance(stepResult, result);

                    await db.SaveChangesAsync(ct);
                    await _eventPublisher.PublishStepCompletedAsync(execution, step, stepResult, result, ct);
                    await _eventPublisher.PublishStepDiagnosticsAsync(execution, step, stepResult, result, ct);

                    // docs/design/review-loop-adjustments.md: inside the window of a review loop that may change steps,
                    // the credit gate refusing a hold stops the loop, never the run: the best pass so far is kept and
                    // the run goes on after the review (marked as needing attention, like an exhausted loop).
                    if (Services.Review.ReviewAdjustmentReport.IsRefusedPaidRerun(ex, stepResult.FailureCode)
                        && adjustedLoopReviewOrder is { } stoppedLoop
                        && bestReviewPass is { } best && best.ReviewStepOrder == stoppedLoop && resumed is null)
                    {
                        _logger.LogWarning(
                            "Execution {ExecutionId} step {StepOrder} ({StepType}): the credit gate or a spend cap refused its paid re-run during review step {ReviewStep}'s re-run; stopping the loop and keeping its best pass (score {Score}/10)",
                            executionId, step.StepOrder, step.StepType, best.ReviewStepOrder, best.Score);
                        adjustedRerunReport?.Add(new(step.StepOrder, step.StepType.ToString(), "stopped", creditsChargedMillis));

                        // The worse passes stay on record but no longer count: a replay or a change request reads the best pass.
                        DateTime supersededAt = DateTime.UtcNow;
                        foreach (WorkflowStepResult later in await db.WorkflowStepResults
                            .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null
                                && r.Id != best.ReviewResultId && r.ExecutedAt > best.PassAt)
                            .ToListAsync(ct))
                        {
                            later.SupersededAt = supersededAt;
                        }

                        // Why the loop stopped is written onto the kept review (adjustmentRerun.refused + a warning), so the
                        // finished film's run says which step was refused and by which cap.
                        string refusalCode = stepResult.FailureCode ?? (ex as CreditRefusalException)?.Code ?? "BUDGET_EXCEEDED";
                        string refusalMessage = string.IsNullOrWhiteSpace(lastStepUserMessage)
                            ? (result.ErrorDetails ?? ex.Message)
                            : lastStepUserMessage;
                        AgentType? reviewAgentType = steps.FirstOrDefault(s => s.StepOrder == best.ReviewStepOrder)?.AgentDefinition?.AgentType;
                        WorkflowStepResult? keptReview = await db.WorkflowStepResults.FirstOrDefaultAsync(r => r.Id == best.ReviewResultId, ct);
                        if (keptReview?.OutputJson is { Length: > 0 } keptJson)
                        {
                            keptReview.OutputJson = EnsureJsonForJsonbColumn(Services.Review.ReviewAdjustmentReport.AttachRerunRefusal(
                                keptJson, reviewAgentType, step.StepOrder, step.StepType.ToString(), refusalCode, refusalMessage));
                        }

                        await db.SaveChangesAsync(ct);
                        stepOutputHistory.Clear();
                        stepOutputHistory.AddRange(best.History);
                        accumulatedOutput = Services.Review.ReviewAdjustmentReport.AttachRerunRefusal(
                            best.Output, reviewAgentType, step.StepOrder, step.StepType.ToString(), refusalCode, refusalMessage);
                        ReviewExhausted stopped = new(best.ReviewStepOrder, best.Score, best.MinScore, iterationCount);
                        if (worstReviewExhausted is null || stopped.Score < worstReviewExhausted.Score)
                            worstReviewExhausted = stopped;
                        reviewReuse.Clear();
                        reviewChangesPending.Clear();
                        reviewChangedKeep.Clear();
                        adjustedRerunReport = null;
                        adjustedLoopReviewOrder = null;
                        pendingReviewFeedback = null;
                        pendingReviewFeedbackMinStepOrder = int.MaxValue;
                        pendingReviewFeedbackMaxStepOrderExclusive = int.MinValue;
                        pendingReviewManifestNote = null;
                        pendingReviewManifestStepOrder = int.MinValue;
                        reviewPassFacts.Clear();
                        lastStepClass = FailureClass.None;
                        lastStepCode = null;
                        lastStepUserMessage = null;
                        currentStepIndex = best.ReviewStepIndex + 1;
                        execution.IterationCount = iterationCount;
                        continue;
                    }

                    throw;
                }

                // An adjusted re-run's steps are recorded (with what each was charged) for the review that follows it.
                if (resumed is null)
                {
                    if (step.StepType != StepType.ReviewLoop)
                    {
                        ranLiveThisRound.Add(step.StepOrder);
                        adjustedRerunReport?.Add(new(step.StepOrder, step.StepType.ToString(), reviewChange is null ? "rerun" : "changed", creditsChargedMillis));
                    }
                    else if (adjustedRerunReport is { Count: > 0 } rerunSteps)
                    {
                        result = WithOutput(result, Services.Review.ReviewAdjustmentReport.AttachRerunCharges(
                            result.Output, adjustedRerunFrom, rerunSteps, step.AgentDefinition?.AgentType));
                    }

                    if (reviewChange is not null && result.Status == StepStatus.Completed)
                        reviewChangedSteps.Add(step.StepOrder);
                    else if (step.StepType != StepType.ReviewLoop)
                        reviewChangedSteps.Remove(step.StepOrder);
                }

                if (step.StepType == StepType.ReviewLoop)
                    adjustedRerunReport = null;

                // A replay's review loop loops back only into what the revision re-made (see
                // CompositionReplaySession.AdjustLoopBack); otherwise the review is recorded and the run
                // moves on.
                if (replay is not null && step.StepType == StepType.ReviewLoop && result.NextStepIndex <= currentStepIndex)
                {
                    (int adjustedNext, string? loopNote) = replay.AdjustLoopBack(steps, currentStepIndex, result.NextStepIndex);
                    if (adjustedNext != result.NextStepIndex)
                    {
                        _logger.LogInformation(
                            "Execution {ExecutionId} review step {StepOrder} would loop back to index {Proposed}; continuing at {Adjusted}: {Note}",
                            executionId, step.StepOrder, result.NextStepIndex, adjustedNext, loopNote);
                        result = WithNextStepIndex(result, adjustedNext, loopNote);
                    }
                }

                _logger.LogInformation(
                    "Step {StepOrder} ({StepType}) completed for execution {ExecutionId} with status {Status}; duration {DurationMs}ms; tokens {TokensUsed}; nextStepIndex {NextStepIndex}",
                    step.StepOrder,
                    step.StepType,
                    executionId,
                    result.Status,
                    result.DurationMs,
                    result.TokensUsed,
                    result.NextStepIndex);

                string? inputJsonForPersistence = ResolveInputJsonForPersistence(context.LastResolvedAgentInput, accumulatedOutput);
                string outputJsonForPersistence = Shared.Workflows.Review.ReviewEngineRecord.Without(result.Output);

                _logger.LogDebug(
                    "Persisting step result for execution {ExecutionId}, step {StepOrder} ({StepType}). InputJsonValid={InputJsonValid}, OutputJsonValid={OutputJsonValid}, InputPreview={InputPreview}, OutputPreview={OutputPreview}",
                    executionId,
                    step.StepOrder,
                    step.StepType,
                    IsValidJson(inputJsonForPersistence),
                    IsValidJson(outputJsonForPersistence),
                    CreateLogPreview(inputJsonForPersistence, 250),
                    CreateLogPreview(outputJsonForPersistence, 250));

                // Persist final state to the previously inserted running step row (a reused dispatch
                // has none: its recorded row is left exactly as the dispatch that ran it wrote it)
                if (resumed is null)
                {
                    stepResult.Output = result.Output;
                    stepResult.TokensUsed = result.TokensUsed;
                    ApplyTokenSplit(stepResult, result);
                    stepResult.DurationMs = result.DurationMs;
                    stepResult.InputJson = inputJsonForPersistence;
                    stepResult.OutputJson = EnsureJsonForJsonbColumn(outputJsonForPersistence);
                    stepResult.Status = result.Status;
                    stepResult.ErrorDetails = result.ErrorDetails;
                    stepResult.IterationNumber = result.IterationNumber;
                    stepResult.CompletedAt = DateTime.UtcNow;
                    stepResult.OutputStorageKey = result.OutputStorageKey;
                    stepResult.ArtifactStorageKey = result.ArtifactStorageKey;
                    stepResult.TimelineStorageKey = result.TimelineStorageKey;
                    stepResult.ToolCallsJson = result.ToolCalls.Count > 0 ? JsonSerializer.Serialize(result.ToolCalls) : null;
                    stepResult.ReasoningJson = result.Reasoning.Count > 0 ? JsonSerializer.Serialize(result.Reasoning) : null;
                    stepResult.ChatTranscriptJson = result.ChatTranscriptJson;
                    ApplyRunnerProvenance(stepResult, result);
                    stepResult.FromCache = servedFromCache;
                    stepResult.CachedTokensSaved = cachedTokensSaved;
                    // B6b: a step that succeeded carries no failure class, and its charge is whatever
                    // the settle above took (0 for a cache hit, and 0 whenever billing is off).
                    stepResult.FailureClass = result.FailureClass;
                    stepResult.FailureCode = result.Status == StepStatus.Failed
                        ? FailureMessages.EnsureResultCode(result.FailureCode, result.Output, result.ErrorDetails)
                        : result.FailureCode;
                    stepResult.CreditsChargedMillis = creditsChargedMillis;
                }

                // Handle review scores for ReviewLoop steps
                if (step.StepType == StepType.ReviewLoop && result.IterationNumber.HasValue)
                {
                    int score = ReviewLoopStepExecutor.ParseReviewScore(result.Output, step.AgentDefinition?.AgentType);
                    db.ReviewScores.Add(new ReviewScore
                    {
                        Id = Guid.NewGuid(),
                        WorkflowExecutionId = executionId,
                        IterationNumber = result.IterationNumber.Value,
                        Score = score,
                        Comments = result.Output,
                        CreatedAt = DateTime.UtcNow,
                        // The dispatch that produced it, so a Retry or resume superseding this row hides
                        // exactly this score (ReviewScoreLineage), with no clock involved.
                        StepResultId = stepResult.Id
                    });
                }

                // Capture/clear pending loop-back review feedback (see the declaration above the
                // while loop). Must run for every ReviewLoop step regardless of outcome so a pass
                // (or exhausted MaxIterations) clears any window left over from an earlier loop.
                if (step.StepType == StepType.ReviewLoop)
                {
                    if (result.NextStepIndex <= currentStepIndex)
                    {
                        int minScore = step.MinScore ?? 9;
                        // The score the loop gated on: the reviewer's, after the deterministic caps the loop applied
                        // to the same prior output. The revision brief states this number, never the raw one.
                        ReviewLoopStepExecutor.CappedReviewScore scored =
                            ReviewLoopStepExecutor.ScoreReview(result.Output, context.AccumulatedOutput, step.AgentDefinition?.AgentType,
                                visualCheckEnabled: ReviewLoopStepExecutor.VisualCheckEnabled(step),
                                renderQualityEnabled: ReviewLoopStepExecutor.RenderQualityEnabled(step));
                        pendingReviewFeedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(
                            result.Output, minScore, scored, step.AgentDefinition?.AgentType);
                        pendingReviewScore = scored.Score;
                        pendingReviewMinScore = minScore;
                        pendingReviewFeedbackMinStepOrder = steps[result.NextStepIndex].StepOrder;
                        pendingReviewFeedbackMaxStepOrderExclusive = step.StepOrder;

                        // T-007: keep this pass's numbers for the next pass. They are read from
                        // context.AccumulatedOutput (the plan/cut the reviewer judged), never from
                        // an encoded file, so T-004's plan-only passes work. Null for a prior output
                        // that is not a compile summary, which leaves the next prompt unchanged.
                        if (ReviewLoopStepExecutor.TryReadReviewPassFact(
                                context.AccumulatedOutput,
                                result.IterationNumber ?? iterationCount + 1,
                                scored.ReviewerScore) is { } passFact)
                        {
                            reviewPassFacts.Add(passFact);
                        }

                        // Read before the loop-back prune below drops these entries: the latest step
                        // in the window whose output is a render manifest.
                        pendingReviewManifestNote = null;
                        pendingReviewManifestStepOrder = int.MinValue;
                        for (int h = stepOutputHistory.Count - 1; h >= 0; h--)
                        {
                            StepOutputHistoryEntry entry = stepOutputHistory[h];
                            if (!IsWithinReviewFeedbackWindow(entry.StepOrder, pendingReviewFeedbackMinStepOrder, pendingReviewFeedbackMaxStepOrderExclusive))
                                continue;

                            string? note = ReviewLoopStepExecutor.BuildPreviousManifestNote(entry.Output);
                            if (note is null)
                                continue;

                            pendingReviewManifestNote = note;
                            pendingReviewManifestStepOrder = entry.StepOrder;
                            break;
                        }
                    }
                    else
                    {
                        pendingReviewFeedback = null;
                        pendingReviewFeedbackMinStepOrder = int.MaxValue;
                        pendingReviewFeedbackMaxStepOrderExclusive = int.MinValue;
                        pendingReviewManifestNote = null;
                        pendingReviewManifestStepOrder = int.MinValue;
                        // The loop ended (passed or exhausted): its pass history must not leak into
                        // a later, unrelated ReviewLoop step of the same run.
                        reviewPassFacts.Clear();
                    }
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed persisting step result for execution {ExecutionId}, step {StepOrder} ({StepType}). InputJsonValid={InputJsonValid}, OutputJsonValid={OutputJsonValid}, InputPreview={InputPreview}, OutputPreview={OutputPreview}",
                        executionId,
                        step.StepOrder,
                        step.StepType,
                        IsValidJson(inputJsonForPersistence),
                        IsValidJson(outputJsonForPersistence),
                        CreateLogPreview(inputJsonForPersistence, 250),
                        CreateLogPreview(outputJsonForPersistence, 250));
                    throw;
                }

                // A reused result was published (and measured), and its output registered, by the dispatch that
                // produced it.
                if (resumed is null)
                {
                    // Step option registerOutputAsProjectFile: best effort, never fails the step.
                    await Services.Compositions.StepOutputRegistration.TryRegisterAsync(_scopeFactory, execution, step, stepResult, _logger, ct);
                    await _eventPublisher.PublishStepCompletedAsync(execution, step, stepResult, result, ct);
                    await _eventPublisher.PublishStepDiagnosticsAsync(execution, step, stepResult, result, ct);

                    ReelBoltDiagnostics.StepDuration.Record(result.DurationMs,
                        new KeyValuePair<string, object?>("step.type", step.StepType.ToString()),
                        new KeyValuePair<string, object?>("agent.type", step.AgentDefinition.AgentType.ToString()),
                        metricDimensions[0], metricDimensions[1]);
                }

                replay?.RecordExecuted(step.StepOrder, result.Output);

                // A review loop that stopped below its minimum: keep the lowest score; a later pass never clears it.
                ReviewExhausted? reusedExhaustion = resumed is not null
                    && step.StepType == StepType.ReviewLoop
                    && priorAttention is { } prior
                    && prior.StepOrder == step.StepOrder
                    && result.NewIterationCount == prior.Iterations
                        ? new ReviewExhausted(prior.StepOrder, prior.Score, prior.MinScore, prior.Iterations)
                        : null;
                if ((result.ReviewExhausted ?? reusedExhaustion) is { } exhausted
                    && (worstReviewExhausted is null || exhausted.Score < worstReviewExhausted.Score))
                    worstReviewExhausted = exhausted;

                accumulatedOutput = result.Output;
                // A skipped step produced nothing: its result carries the previous step's output forward (the chain), but
                // the history must not record that under this step's order, or a later step that resolves this step's
                // output (the compile's DynamicsPlan) would find the previous step's.
                if (RecordsInOutputHistory(result, step.AgentDefinition?.AgentType))
                {
                    string stepLabel = string.IsNullOrWhiteSpace(step.Label)
                        ? step.AgentDefinition.Name
                        : step.Label;
                    stepOutputHistory.Add(new StepOutputHistoryEntry(
                        step.StepOrder, stepLabel, result.Output, result.OutputStorageKey, result.ArtifactStorageKey));
                }
                // Prune stale history on loop-back (belt-and-braces alongside the
                // LastOrDefault/greatest-StepOrder fixes at each StepOutputHistory consumer): a
                // ReviewLoop step rewinding execution re-executes every step from the loop
                // target through (but not including) itself, which is about to append a second
                // entry for each of those StepOrders. Drop the now-stale entries up front so
                // history keeps its "each StepOrder appears at most once, always the freshest"
                // invariant true for any future consumer, not just the ones already hardened to
                // pick the latest match themselves.
                // A review loop that may change steps keeps its best-scoring pass, restored if the credit gate stops a
                // later re-run (see the catch above).
                bool adjustmentsAllowed = step.StepType == StepType.ReviewLoop && replay is null && ReviewLoopStepExecutor.AdjustmentsAllowed(step);
                if (adjustmentsAllowed)
                {
                    int passScore = ReviewLoopStepExecutor.ParseReviewScore(result.Output, step.AgentDefinition?.AgentType);
                    if (bestReviewPass is null || bestReviewPass.ReviewStepOrder != step.StepOrder || passScore > bestReviewPass.Score)
                        bestReviewPass = new BestReviewPass(step.StepOrder, currentStepIndex, passScore, step.MinScore ?? 9, [.. stepOutputHistory], result.Output, stepResult.Id, stepResult.ExecutedAt);
                    adjustedLoopReviewOrder = result.NextStepIndex <= currentStepIndex ? step.StepOrder : null;
                }

                if (result.NextStepIndex <= currentStepIndex)
                {
                    int loopTargetStepOrder = steps[result.NextStepIndex].StepOrder;
                    if (step.StepType == StepType.ReviewLoop && replay is null)
                    {
                        reviewReuse.Clear();
                        reviewChangesPending.Clear();
                        reviewChangedKeep.Clear();
                        ranLiveThisRound.Clear();
                        IReadOnlyList<int> reuse = [];
                        IReadOnlyList<Shared.Workflows.Review.ReviewStepChange> stepChanges = [];
                        bool rerunPlanned = false;
                        if (resumed is null && result.ReviewAdjustments is { HasChanges: true } plan)
                        {
                            await ApplyReviewAdjustmentsAsync(db, execution, steps, plan, ct);
                            reuse = plan.ReusedStepOrders;
                            stepChanges = plan.StepChanges;
                            rerunPlanned = true;
                        }
                        else if (resumed is not null && adjustmentsAllowed)
                        {
                            reuse = Services.Review.ReviewAdjustmentReport.ReadReusedStepOrders(result.Output, step.AgentDefinition?.AgentType);
                            // A recorded round is only as trusted as a live one: its changes pass the same checks again.
                            stepChanges = Services.Review.ReviewAdjustmentReport.ValidRecordedChanges(
                                Services.Review.ReviewAdjustmentReport.ReadStepChanges(result.Output, step.AgentDefinition?.AgentType),
                                steps, loopTargetStepOrder, step.StepOrder,
                                o => stepOutputHistory.LastOrDefault(h => h.StepOrder == o)?.Output);
                            foreach (Shared.Workflows.Review.ReviewStepChange recorded in stepChanges)
                                reviewChangedSteps.Add(recorded.StepOrder);
                        }

                        int loopTargetOrder = step.LoopTargetStepOrder ?? int.MinValue;
                        foreach (int order in reuse.Where(o => o >= loopTargetStepOrder && o < loopTargetOrder && !stepChanges.Any(c => c.StepOrder == o)))
                            reviewReuse[order] = stepOutputHistory.LastOrDefault(h => h.StepOrder == order);

                        // Each change request starts from the step's latest result in this run (read before the prune).
                        foreach (Shared.Workflows.Review.ReviewStepChange change in stepChanges)
                        {
                            if (execution.SnapshotDefinitionId is null)
                                continue;
                            StepOutputHistoryEntry? latest = stepOutputHistory.LastOrDefault(h => h.StepOrder == change.StepOrder);
                            reviewChangesPending[change.StepOrder] = new StepChangeRequestScope(
                                ReviewChangeRequestId(stepResult.Id, change.StepOrder),
                                change.Payload,
                                string.IsNullOrWhiteSpace(latest?.Output) ? null : latest.Output,
                                OwnArtifactKey(execution.ProjectId, latest?.ArtifactStorageKey));
                        }

                        foreach (int order in reviewChangedSteps.Where(o => o >= loopTargetStepOrder && o < step.StepOrder && !reviewChangesPending.ContainsKey(o)))
                            reviewChangedKeep[order] = stepOutputHistory.LastOrDefault(h => h.StepOrder == order);

                        adjustedRerunReport = rerunPlanned ? [] : null;
                        adjustedRerunFrom = rerunPlanned ? loopTargetStepOrder : null;
                    }

                    stepOutputHistory.RemoveAll(h => h.StepOrder >= loopTargetStepOrder);
                }

                // Only a ReviewLoop step counts iterations; every other step passes the count through.
                // Taking it from whatever step ran made the loop's MaxIterations cap depend on every
                // executor in the loop remembering to copy it: the Voiceover step did not (default 0),
                // so a narrated workflow's review loop reset its count on every pass and re-ran its
                // paid narration, synthesis and encode ~35 times before it was stopped by hand.
                if (step.StepType == StepType.ReviewLoop)
                    iterationCount = result.NewIterationCount;
                currentStepIndex = result.NextStepIndex;

                execution.IterationCount = iterationCount;
            }

            await EnsureAuthorArtifactProducedAsync(execution.Id, steps, db, ct);

            // Defense in depth, on top of the ExecutionCancellationRegistry fix (see that class's
            // doc comment for the actual root cause this whole mechanism guards against): even
            // with the token now genuinely reaching a concurrent CancelExecutionAsync call, a stop
            // request racing the exact moment the last step naturally finishes could still slip
            // through without ever causing an awaited call to throw. Check the flag explicitly
            // here, at the one place a "successful" completion is about to be written, so a
            // request to stop always wins regardless of whether anything deeper in the call chain
            // happened to observe and throw on it in time.
            if (ct.IsCancellationRequested)
            {
                await MarkCancelledAsync(execution, db, executionId);
                return;
            }

            if (!await StillOwnsRunAsync(executionId))
            {
                _logger.LogWarning(
                    "Execution {ExecutionId} finished here after this instance lost its lease; leaving the run to its new owner",
                    executionId);
                return;
            }

            execution.Status = ExecutionStatus.Passed;
            // A Passed run is not necessarily a clean pass. Narration the compile node reports as
            // partly unspoken is the one the run card must name in its own words — the generic
            // review-exhausted sentence ("did not reach the quality bar") hides it — so narration
            // loss wins the attention slot when both apply; a review exhaustion over complete
            // narration still sets its own attention.
            IncompleteNarration? incompleteNarration = FindIncompleteNarration(stepOutputHistory, steps);
            execution.AttentionJson = incompleteNarration is { } narration
                ? ExecutionAttention.ToNarrationJson(
                    narration.StepOrder,
                    worstReviewExhausted?.Score ?? 0,
                    worstReviewExhausted?.MinScore ?? 0,
                    worstReviewExhausted?.Iterations ?? iterationCount,
                    narration.AppliedLineCount,
                    narration.LineCount,
                    narration.ReanchoredLineCount)
                : worstReviewExhausted is null ? null : ReviewExhausted.ToAttentionJson(worstReviewExhausted);
            execution.ResultJson = EnsureJsonForJsonbColumn(accumulatedOutput);
            execution.CompletedAt = DateTime.UtcNow;
            execution.CurrentStepId = null;
            _logger.LogDebug(
                "Persisting execution completion for execution {ExecutionId}. ResultJsonValid={ResultJsonValid}, ResultPreview={ResultPreview}",
                executionId,
                IsValidJson(execution.ResultJson),
                CreateLogPreview(execution.ResultJson, 250));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(
                    ex,
                    "Failed persisting execution completion for execution {ExecutionId}. ResultJsonValid={ResultJsonValid}, ResultPreview={ResultPreview}",
                    executionId,
                    IsValidJson(execution.ResultJson),
                    CreateLogPreview(execution.ResultJson, 250));
                throw;
            }
            await _eventPublisher.PublishExecutionCompletedAsync(execution, ct);

            ReelBoltDiagnostics.CompletedWorkflows.Add(1,
                new KeyValuePair<string, object?>("status", "passed"), metricDimensions[0], metricDimensions[1]);

            _logger.LogInformation("Workflow execution {ExecutionId} completed successfully", executionId);
            _logger.LogInformation(
                "Execution {ExecutionId} summary: FinalStatus={Status}, Iterations={IterationCount}, StepOutputsRecorded={StepOutputCount}",
                executionId,
                execution.Status,
                execution.IterationCount,
                stepOutputHistory.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(execution, db, executionId);
            // do not rethrow; cancellation is expected
        }
        catch (Exception ex) when (ct.IsCancellationRequested && _cancellationRegistry.IsShutdown(executionId))
        {
            // The process is shutting down and a step reported the interruption as a failed result rather than
            // by throwing OperationCanceledException (the "never throws" executors do). That is still the
            // shutdown, not a verdict on the run: hand it over exactly like a cancelled one.
            _logger.LogInformation(ex,
                "Execution {ExecutionId} stopped with a failure while the engine was shutting down; treating it as interrupted by the shutdown",
                executionId);
            await MarkCancelledAsync(execution, db, executionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow execution {ExecutionId} failed", executionId);
            if (!await StillOwnsRunAsync(executionId))
            {
                // SQFU-S1: not this instance's run any more. No terminal write, no event, and no execution-wide
                // hold release: the open holds of this execution id are the new owner's.
                _logger.LogWarning(
                    "Execution {ExecutionId} failed here after this instance lost its lease; leaving the run to its new owner",
                    executionId);
                throw;
            }

            execution.Status = ExecutionStatus.Failed;
            execution.ErrorMessage = FailureMessages.RunMessage(ex, lastStepClass, lastStepCode, lastStepUserMessage);
            execution.CompletedAt = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                // db may still be tracking an entity whose SaveChangesAsync already failed once
                // in this request (e.g. a step result — EF does not roll back the change tracker
                // on a failed save), so retrying on the same context can reproduce the exact same
                // failure forever, and the execution's Status update is lost with it — permanently
                // "Running" even though it has, in fact, failed. Fall back to a fresh scope/
                // DbContext touching only the execution row, decoupled from whatever the shared
                // context is still holding onto (found by e2e QA).
                _logger.LogError(saveEx,
                    "Failed to persist failure state for execution {ExecutionId} via the primary " +
                    "DbContext; retrying with a fresh scope", executionId);
                using IServiceScope failureScope = _scopeFactory.CreateScope();
                WorkflowEngineDbContext failureDb = failureScope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                WorkflowExecution? freshExecution = await failureDb.WorkflowExecutions
                    .FirstOrDefaultAsync(e => e.Id == executionId, CancellationToken.None);
                if (freshExecution != null)
                {
                    freshExecution.Status = ExecutionStatus.Failed;
                    freshExecution.ErrorMessage = FailureMessages.RunMessage(ex, lastStepClass, lastStepCode, lastStepUserMessage);
                    freshExecution.CompletedAt = DateTime.UtcNow;
                    await failureDb.SaveChangesAsync(CancellationToken.None);
                }
            }
            await _eventPublisher.PublishExecutionFailedAsync(execution, ct);

            // B6b: the whole-run path. Every step that failed already closed its own hold, so this
            // is the sweep for a hold left open by a failure that never reached a step's catch (a
            // fault between the reservation and the executor, e.g. the author-artifact check) —
            // released as a platform failure, so an interrupted run is never charged for work it
            // never got. Idempotent: it finds nothing once every hold is closed.
            await ReleaseExecutionCreditsAsync(executionId);

            ReelBoltDiagnostics.CompletedWorkflows.Add(1,
                new KeyValuePair<string, object?>("status", "failed"), metricDimensions[0], metricDimensions[1]);

            throw;
        }
        finally
        {
            ReelBoltDiagnostics.ActiveWorkflows.Add(-1);
            _cancellationRegistry.Remove(executionId);
        }
    }

    /// <summary>
    /// Persists a Cancelled result and publishes it as a failure (existing consumers treat the two
    /// the same way). Shared by the two places <see cref="ExecuteAsync"/> can discover a stop
    /// request was honored: an <see cref="OperationCanceledException"/> actually observed and
    /// thrown from within the step-execution loop, and the defensive check right before writing a
    /// "successful" completion — for exactly why that second path exists and is not redundant, see
    /// the comment at its call site.
    /// </summary>
    private async Task MarkCancelledAsync(WorkflowExecution execution, WorkflowEngineDbContext db, Guid executionId)
    {
        if (!await StillOwnsRunAsync(executionId))
        {
            // SQFU-S1: the cancellation is this instance letting go of a run it lost (ExecutionLeaseService cancelled
            // it with LeaseLostReason); the row is the new owner's to write.
            _logger.LogWarning(
                "Execution {ExecutionId} stopped here: {Reason}. Leaving the run to its new owner",
                executionId, Services.ExecutionLeaseService.LeaseLostReason);
            return;
        }

        // A shutdown of THIS process is not a verdict on the run (a deploy used to lose a run this way: the
        // run was written Cancelled and so never reached the sweeper, which only recovers Running rows). Write nothing:
        // the row stays Running, RunAsync ends its lease on the way out, and ExecutionRecoverySweeper re-queues it once
        // (auto_resume_count 0, engine snapshot, no paid VideoGenerate in flight) or fails it as a platform
        // interruption with Resume on offer. No Failed event goes out: the run is not over. The step that was running
        // was already closed (Failed, platform class, its hold refunded) by the step loop's catch.
        if (_cancellationRegistry.IsShutdown(executionId))
        {
            _logger.LogInformation(
                "Workflow execution {ExecutionId} interrupted by engine shutdown; leaving it Running for the recovery sweeper to resume",
                executionId);
            ReelBoltDiagnostics.CompletedWorkflows.Add(1,
                new KeyValuePair<string, object?>("status", "interrupted"));
            return;
        }

        // Report the cause the canceller actually recorded, never an assumed one. This used to
        // hardcode "Cancelled by user request" for every cancellation, which made a broker-side
        // kill (RabbitMQ closing the channel once consumer_timeout elapsed) indistinguishable from
        // a real Stop click — and, worse, actively blamed the user for it. Three consecutive
        // multi-hour executions were written off that way before the real cause was found, each
        // dying at exactly the 120-minute consumer_timeout with nobody having touched Stop.
        // Note that the genuine user path (CancelExecutionAsync) has always written its own
        // distinct "Stopped by user {id}" message, so the old string was only ever reachable for
        // cancellations that were NOT user requests — it was wrong 100% of the time it appeared.
        string reason = _cancellationRegistry.GetReason(executionId);
        _logger.LogInformation("Workflow execution {ExecutionId} was cancelled: {Reason}", executionId, reason);
        execution.Status = ExecutionStatus.Cancelled;
        execution.ErrorMessage = FailureMessages.UserMessage(
            FailureMessages.CancellationCode(reason), reason);
        execution.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        // publish as failed so existing consumers treat it similarly
        await _eventPublisher.PublishExecutionFailedAsync(execution, CancellationToken.None);

        ReelBoltDiagnostics.CompletedWorkflows.Add(1,
            new KeyValuePair<string, object?>("status", "cancelled"));
    }

    /// <summary>
    /// Cancels a running or queued execution. If the execution is currently
    /// being processed the associated cancellation token will be triggered.
    /// The database record is also updated to <c>Cancelled</c> when applicable.
    /// <paramref name="reason"/> is the plain-words cause recorded by whoever asked (a user pressing Stop,
    /// or a revoked API key). Null keeps the historical "Stopped by user {id}" wording.
    /// </summary>
    public async Task CancelExecutionAsync(Guid executionId, Guid requestedByUserId, string? reason = null)
    {
        string cancellationReason = string.IsNullOrWhiteSpace(reason)
            ? $"Stopped by user {requestedByUserId}"
            : reason;

        _logger.LogInformation("Cancellation requested for execution {ExecutionId} by user {UserId}: {Reason}",
            executionId, requestedByUserId, cancellationReason);

        // cancel any running token, recording who asked so MarkCancelledAsync can report the real
        // cause rather than inferring one (see ExecutionCancellationRegistry._reasons).
        if (_cancellationRegistry.TryCancel(executionId, cancellationReason))
        {
            _logger.LogInformation("Triggered cancellation token for running execution {ExecutionId}", executionId);
        }

        // if we have no scope factory (e.g. running in unit tests) just return
        if (_scopeFactory == null)
            return;

        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        var execution = await db.WorkflowExecutions.FindAsync(executionId);
        if (execution == null)
        {
            _logger.LogWarning("Cancellation requested for unknown execution {ExecutionId}", executionId);
            return;
        }

        if (execution.Status == ExecutionStatus.Queued)
        {
            // attempt to remove the pending message from RabbitMQ so it won't be
            // delivered later. this is a best-effort operation; if the message has
            // already been delivered to the engine it won't be found.
            bool removed = await _rabbitHelper.RemoveExecutionMessageAsync(executionId);
            if (removed)
            {
                _logger.LogInformation("Removed queued message for cancelled execution {ExecutionId}", executionId);
            }
            else
            {
                _logger.LogDebug("No queued message found for execution {ExecutionId} during cancellation", executionId);
            }
        }

        if (execution.Status == ExecutionStatus.Queued || execution.Status == ExecutionStatus.Running)
        {
            ExecutionStatus previousStatus = execution.Status;
            execution.Status = ExecutionStatus.Cancelled;
            execution.ErrorMessage = FailureMessages.UserMessage(FailureMessages.CancellationCode(cancellationReason), cancellationReason);
            execution.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Execution {ExecutionId} marked as cancelled (previous status: {PreviousStatus})",
                executionId,
                previousStatus);
        }
        else
        {
            _logger.LogInformation(
                "Cancellation request ignored for execution {ExecutionId} because current status is {Status}",
                executionId,
                execution.Status);
        }
    }

    internal async Task<StepExecutionResult> ExecuteStepWithRetryAsync(
        IStepExecutor executor,
        StepExecutionContext context,
        WorkflowStep step,
        CancellationToken ct)
    {
        int maxRetries = ResolveMaxRetries(step);
        int attemptNumber = 0;
        Exception? lastException = null;

        while (attemptNumber < maxRetries)
        {
            attemptNumber++;
            _logger.LogInformation(
                "Executing step {StepOrder} ({StepType}) for execution {ExecutionId}, attempt {Attempt}/{MaxAttempts}",
                step.StepOrder,
                step.StepType,
                context.Execution.Id,
                attemptNumber,
                maxRetries);
            try
            {
                StepExecutionResult result = await executor.ExecuteAsync(context);

                // Check if step failed
                if (result.Status == StepStatus.Failed)
                {
                    // B6b: carry the class the executor assigned (and the code its failure envelope
                    // names) out through this wrapper — it flattens every failed attempt into a bare
                    // InvalidOperationException below, which is the boundary the code and the class
                    // used to be discarded at (docs/design/saas-metering.md, "classification
                    // point"). The LAST attempt's class is the one that is charged by.
                    context.RecordFailure(
                        result.FailureClass,
                        result.FailureCode ?? FailureClassifier.CodeFromOutput(result.Output),
                        result.UserMessage);

                    if (!result.Retryable)
                    {
                        _logger.LogWarning(
                            "Step {StepOrder} ({StepType}) failed deterministically on attempt {Attempt}; not retrying. Error: {Error}",
                            step.StepOrder, step.StepType, attemptNumber, result.ErrorDetails);
                        throw new InvalidOperationException(
                            $"Step {step.StepOrder} ({step.StepType}) failed and was not retried because the failure is deterministic. Last error: {result.ErrorDetails}");
                    }

                    if (attemptNumber < maxRetries)
                    {
                        if (_hardeningOptions.EnableStructuredRetryDiagnostics)
                            context.RecordRetryFeedback(attemptNumber, BuildRetryDiagnosticMessage(result.ErrorDetails));
                        else
                            context.RecordRetryFeedback(attemptNumber, result.ErrorDetails ?? "Step returned failed status without details.");

                        double delaySeconds = Math.Pow(_hardeningOptions.RetryBaseDelaySeconds, attemptNumber);
                        _logger.LogWarning(
                            "Step {StepOrder} ({StepType}) failed on attempt {Attempt}/{Max}. Error: {Error}. Retrying in {Delay}s...",
                            step.StepOrder, step.StepType, attemptNumber, maxRetries,
                            result.ErrorDetails, delaySeconds);

                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                        continue;
                    }
                    else
                    {
                        // Max retries exceeded - throw to trigger workflow failure
                        throw new InvalidOperationException(
                            $"Step {step.StepOrder} ({step.StepType}) failed after {maxRetries} attempts. Last error: {result.ErrorDetails}");
                    }
                }

                // Success - return result
                if (attemptNumber > 1)
                {
                    _logger.LogInformation(
                        "Step {StepOrder} ({StepType}) succeeded on attempt {Attempt}",
                        step.StepOrder, step.StepType, attemptNumber);
                }
                return WithAttemptMetadata(result, attemptNumber);
            }
            // do not retry on InvalidOperationException or our workflow‑abort exception
            catch (AgentWorkflowException awf)
            {
                // agent explicitly requested workflow abort; log then rethrow immediately
                _logger.LogInformation(
                    "Step {StepOrder} invoked FailWorkflow: {Reason}",
                    step.StepOrder, awf.Reason);
                // B6b: the agent determined the input is unworkable, so the user keeps the charge for
                // what this step consumed (docs/design/saas-metering.md, "Mapping: exceptions").
                RecordFailureClassification(context, awf);
                throw;
            }
            // A timed-out agent run is not retried. Retrying costs the FULL per-attempt budget
            // again for a near-certain repeat: no partial progress carries over between attempts
            // (see AgentStepExecutor), so attempt 2 restarts the identical workload with the
            // identical budget. With a raised per-agent budget the waste is severe — a 3-hour
            // RemotionComponentTranslator timeout retried 3x is 9 hours of the same wall — and it
            // is what turned one translator timeout into the multi-hour window that the old
            // 120-minute transport kill then landed inside. Fail fast with the real reason
            // instead, so the operator can raise Agents:<name>:RunTimeoutSeconds or reduce the
            // step's workload rather than waiting out N identical attempts.
            catch (TimeoutException tex)
            {
                _logger.LogWarning(tex,
                    "Step {StepOrder} ({StepType}) timed out on attempt {Attempt}/{Max}; not retrying, " +
                    "because a from-scratch retry would repeat the same workload under the same budget",
                    step.StepOrder, step.StepType, attemptNumber, maxRetries);
                RecordFailureClassification(context, tex);
                throw;
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not AgentWorkflowException && attemptNumber < maxRetries)
            {
                lastException = ex;
                RecordFailureClassification(context, ex);
                context.RecordRetryFeedback(attemptNumber, BuildRetryDiagnosticMessage(ex.Message));

                double delaySeconds = Math.Pow(_hardeningOptions.RetryBaseDelaySeconds, attemptNumber);
                _logger.LogWarning(ex,
                    "Step {StepOrder} ({StepType}) threw exception on attempt {Attempt}/{Max}. Retrying in {Delay}s...",
                    step.StepOrder, step.StepType, attemptNumber, maxRetries, delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
            // Deliberately no catch-all here: an exception with no attempt left to retry propagates
            // to ExecuteAsync's step catch, which classifies it from the exception itself. Recording
            // one here would overwrite the class the failed RESULT of this attempt already recorded
            // with the wrapper's own opaque "failed after N attempts" InvalidOperationException.
        }

        // If we get here, all retries are exhausted
        throw new InvalidOperationException(
            $"Step {step.StepOrder} ({step.StepType}) failed after {maxRetries} attempts",
            lastException);
    }

    /// <summary>
    /// B6b: records how <paramref name="exception"/> should be charged. A cancellation is decided by
    /// the cancellation registry's recorded provenance, so a transport kill or a restart is never
    /// re-charged to the user as their own stop (see
    /// <see cref="ExecutionCancellationRegistry.GetReason"/>).
    /// </summary>
    private void RecordFailureClassification(StepExecutionContext context, Exception exception)
        => context.RecordFailure(
            FailureClassifier.ForException(exception, _cancellationRegistry.GetReason(context.Execution.Id)),
            FailureClassifier.CodeFromException(exception));

    private int ResolveMaxRetries(WorkflowStep step)
    {
        if (step.StepType == StepType.Extract
            || step.StepType == StepType.VideoAnalyze
            || step.StepType == StepType.VideoCompile
            || step.StepType == StepType.EditRoom
            || step.StepType == StepType.GraphicsRoom
            || step.StepType == StepType.ThumbnailGenerate
            || step.StepType == StepType.Revise
            || step.StepType == StepType.DesignGraphic
            || step.StepType == StepType.DesignSystem
            || step.StepType == StepType.ExtractFrame
            || step.StepType == StepType.PinInsert
            || step.StepType == StepType.AudioCleanup
            // VideoEnhance: a hosted retry would pay the vendor twice for one run; a failure is refunded by class and the person asks again.
            || step.StepType == StepType.VideoEnhance)
        {
            // AudioCleanup: a hosted retry would pay the vendor twice for one run; a failure is refunded by class and the person asks again.
            // DesignGraphic: like Revise, a retry would design a second, different graphic; a failed
            // design is refunded by its class and the person asks again.
            // Revise: a whole revision (restore, agent run, render) is not something to repeat
            // blindly — a failed revision is refunded by its class and the owner asks again.
            // Deterministic, non-LLM steps: retrying the whole step reproduces the same failure
            // (Extract) or re-burns minutes of ffmpeg decode/encode to reproduce a deterministic
            // failure (VideoAnalyze/VideoCompile). ASR's own network call inside
            // VideoAnalyzeStepExecutor has its own small bounded retry around just that call —
            // it does not go through this outer step-level retry mechanism. The room steps
            // (EditRoom/GraphicsRoom) are NOT deterministic (they make many LLM calls), but an
            // outer retry re-running a whole room from scratch is expensive — a room step's own
            // internal synthesis retry (IRoomStepConfig.MaxSynthesisAttempts) is where retry
            // value actually is.
            return 1;
        }

        int configuredDefault = Math.Clamp(_hardeningOptions.MaxStepRetries, 1, 6);
        AgentType? agentType = step.AgentDefinition?.AgentType;

        return agentType switch
        {
            AgentType.AuthorAgent => Math.Clamp(_hardeningOptions.MaxAuthorStepRetries, 1, 6),
            AgentType.RemotionComponentTranslator => Math.Clamp(_hardeningOptions.MaxTranslatorStepRetries, 1, 6),
            _ => configuredDefault
        };
    }

    private static StepExecutionResult BuildFailureStepResult(WorkflowStep step, StepExecutionContext context, Exception ex)
    {
        string diagnostic = BuildRetryDiagnosticMessage(ex.Message);
        return new StepExecutionResult
        {
            // BuildRetryDiagnosticMessage returns PLAIN TEXT, not JSON — and this Output value
            // is written verbatim into WorkflowStepResult.OutputJson, a jsonb column. Writing
            // plain text there fails the whole SaveChangesAsync with Postgres error 22P02
            // ("invalid input syntax for type json"), which then gets retried at the message-bus
            // level while the execution is already marked Running — leaving it stuck forever.
            // This is the single most common way to reach this method: any step whose
            // ResolveMaxRetries is 1 (Extract, VideoAnalyze, VideoCompile) throws here on its
            // very first failure, discarding its own already-valid-JSON failure envelope. Wrap
            // the diagnostic in a minimal JSON envelope so persistence never crashes.
            Output = JsonSerializer.Serialize(new { status = "failed", error = new { message = diagnostic } }),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = 0,
            TokensUsed = 0,
            Status = StepStatus.Failed,
            ErrorDetails = ex.Message,
            AttemptCount = 1,
            RetryCount = 0
        };
    }

    /// <summary>
    /// Copies the input/output token split and the primary provider attribution onto the persisted row.
    /// Shared by the success and exception persistence paths so neither can drift from the other.
    /// </summary>
    private static void ApplyTokenSplit(WorkflowStepResult stepResult, StepExecutionResult result)
    {
        stepResult.InputTokens = result.InputTokens;
        stepResult.OutputTokens = result.OutputTokens;
        stepResult.PrimaryProviderId = result.PrimaryProviderId;
        stepResult.PrimaryModelName = result.PrimaryModelName;
    }

    /// <summary>
    /// D12: copies WHERE the step's work ran onto the persisted row — the target, the runner device
    /// and the runner job that produced it, plus the <see cref="WorkflowStepResult.FromRunner"/> tag
    /// that follows from the target. Shared by the success and exception persistence paths, exactly
    /// like <see cref="ApplyTokenSplit"/>, so both always write the same four columns and neither can
    /// drift from the other.
    /// <para>
    /// The tag is copied from <see cref="StepExecutionResult.FromRunner"/>, which is DERIVED from
    /// <see cref="StepExecutionResult.ComputeTarget"/> rather than set independently — so a stored row
    /// can never claim a provenance its target contradicts.
    /// </para>
    /// </summary>
    private static void ApplyRunnerProvenance(WorkflowStepResult stepResult, StepExecutionResult result)
    {
        stepResult.ComputeTarget = result.ComputeTarget;
        stepResult.FromRunner = result.FromRunner;
        stepResult.RunnerId = result.RunnerId;
        stepResult.RunnerJobId = result.RunnerJobId;
    }

    /// <summary>
    /// The best-scoring pass of a review loop that may change steps: its score, where the review sits, and what the run
    /// held after it (the history and the review's output), restored when the credit gate stops a later re-run.
    /// </summary>
    private sealed record BestReviewPass(
        int ReviewStepOrder, int ReviewStepIndex, int Score, int MinScore, IReadOnlyList<StepOutputHistoryEntry> History, string Output,
        Guid ReviewResultId = default, DateTime PassAt = default);

    /// <summary>A stable id for a change request a review made inside a run (the review's step result and the target step).</summary>
    internal static Guid ReviewChangeRequestId(Guid reviewStepResultId, int stepOrder)
    {
        byte[] bytes = reviewStepResultId.ToByteArray();
        byte[] order = BitConverter.GetBytes(stepOrder);
        for (int i = 0; i < order.Length; i++)
            bytes[12 + i] ^= order[i];
        return new Guid(bytes);
    }

    /// <summary>An artifact key handed to a change-request executor only when it lies under this project's own prefix.</summary>
    internal static string? OwnArtifactKey(Guid projectId, string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.StartsWith($"projects/{projectId}/agentFiles/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
            ? key
            : null;

    /// <summary>
    /// Writes a review's applied adjustments (docs/design/review-loop-adjustments.md) to the steps this run executes and to
    /// the run's OWN snapshot rows: only a step of the engine-taken snapshot is ever written, so the user's saved workflow
    /// (and any hidden system workflow) is never changed. A resume reloads the snapshot and so keeps the change.
    /// </summary>
    private async Task ApplyReviewAdjustmentsAsync(
        WorkflowEngineDbContext db, WorkflowExecution execution, List<WorkflowStep> steps, Shared.Workflows.Review.ReviewAdjustmentPlan plan, CancellationToken ct)
    {
        if (execution.SnapshotDefinitionId is not { } snapshotId)
            return;

        foreach (Shared.Workflows.Review.ReviewAdjustmentChange change in plan.Changes)
        {
            WorkflowStep? step = steps.FirstOrDefault(s => s.StepOrder == change.StepOrder);
            if (step is null || step.WorkflowDefinitionId != snapshotId)
            {
                _logger.LogWarning(
                    "Execution {ExecutionId}: review adjustment of step {StepOrder} not written: the step is not part of this run's own snapshot",
                    execution.Id, change.StepOrder);
                continue;
            }

            Shared.Workflows.Review.ReviewAdjustableSettings.SetConfigJson(step, change.ConfigJson);
            WorkflowStep? row = await db.WorkflowSteps.FirstOrDefaultAsync(s => s.Id == step.Id && s.WorkflowDefinitionId == snapshotId, ct);
            if (row is not null && !ReferenceEquals(row, step))
                Shared.Workflows.Review.ReviewAdjustableSettings.SetConfigJson(row, change.ConfigJson);
            _logger.LogInformation(
                "Execution {ExecutionId}: review adjusted step {StepOrder} ({StepType}) of this run's snapshot",
                execution.Id, step.StepOrder, step.StepType);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The row of a step an adjusted review rerun reuses: a copy of this run's latest completed result of that step
    /// (<see cref="WorkflowStepResult.ReusedFromStepResultId"/>), never charged, cached or re-run, fed into the run like a
    /// live result. Falls back to the output the step had before the loop-back when no row is found.
    /// </summary>
    private async Task<string> CopyForReviewRerunAsync(
        WorkflowEngineDbContext db, WorkflowExecution execution, WorkflowStep step, StepOutputHistoryEntry? previous,
        List<StepOutputHistoryEntry> stepOutputHistory, string accumulatedOutput, CancellationToken ct)
    {
        WorkflowStepResult? source = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == execution.Id && r.WorkflowStepId == step.Id
                && r.SupersededAt == null && r.Status == StepStatus.Completed)
            .OrderByDescending(r => r.ExecutedAt)
            .FirstOrDefaultAsync(ct);
        if (source is null && previous is null)
        {
            _logger.LogWarning(
                "Execution {ExecutionId} step {StepOrder}: nothing to reuse for the review's rerun; keeping the run's current input",
                execution.Id, step.StepOrder);
            return accumulatedOutput;
        }

        string output = source is not null ? CompositionReplaySession.OriginalOutput(source) : previous!.Output;
        WorkflowStepResult row = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            Output = output,
            OutputJson = source?.OutputJson ?? EnsureJsonForJsonbColumn(output),
            Status = StepStatus.Completed,
            IterationNumber = source?.IterationNumber,
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            OutputStorageKey = source?.OutputStorageKey ?? previous?.OutputStorageKey,
            ArtifactStorageKey = source?.ArtifactStorageKey ?? previous?.ArtifactStorageKey,
            TimelineStorageKey = source?.TimelineStorageKey,
            ChatTranscriptJson = source?.ChatTranscriptJson,
            ReusedFromStepResultId = source?.Id
        };
        db.WorkflowStepResults.Add(row);
        await db.SaveChangesAsync(ct);

        StepExecutionResult published = new()
        {
            Output = row.Output,
            NextStepIndex = 0,
            Status = row.Status,
            OutputStorageKey = row.OutputStorageKey,
            ArtifactStorageKey = row.ArtifactStorageKey,
            TimelineStorageKey = row.TimelineStorageKey,
            ChatTranscriptJson = row.ChatTranscriptJson
        };
        await _eventPublisher.PublishStepStartedAsync(execution, step, row, null, ct);
        await _eventPublisher.PublishStepCompletedAsync(execution, step, row, published, ct);
        _logger.LogInformation(
            "Execution {ExecutionId} step {StepOrder} ({StepType}) reused for the review's adjusted rerun (no setting it reads changed)",
            execution.Id, step.StepOrder, step.StepType);

        if (!string.IsNullOrEmpty(row.Output))
        {
            string stepLabel = string.IsNullOrWhiteSpace(step.Label) ? step.AgentDefinition.Name : step.Label;
            stepOutputHistory.Add(new StepOutputHistoryEntry(step.StepOrder, stepLabel, row.Output, row.OutputStorageKey, row.ArtifactStorageKey));
        }

        return row.Output;
    }

    private static StepExecutionResult WithAttemptMetadata(StepExecutionResult result, int attemptCount)
    {
        return new StepExecutionResult
        {
            Output = result.Output,
            NextStepIndex = result.NextStepIndex,
            NewIterationCount = result.NewIterationCount,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            DurationMs = result.DurationMs,
            AttemptCount = attemptCount,
            RetryCount = Math.Max(0, attemptCount - 1),
            Status = result.Status,
            ErrorDetails = result.ErrorDetails,
            IterationNumber = result.IterationNumber,
            OutputStorageKey = result.OutputStorageKey,
            // Easy to miss (plan R3): this method hand-copies every field of the attempt's
            // result — anything added to StepExecutionResult but not copied here is silently
            // dropped on any step that goes through a retry attempt.
            ArtifactStorageKey = result.ArtifactStorageKey,
            TimelineStorageKey = result.TimelineStorageKey,
            // D12: where the attempt ran. Same reason as the storage keys below — a retry that ran
            // on a runner must not come back looking like a local result.
            ComputeTarget = result.ComputeTarget,
            RunnerId = result.RunnerId,
            RunnerJobId = result.RunnerJobId,
            // B6b: the failure classification of the attempt whose result is being returned.
            FailureClass = result.FailureClass,
            FailureCode = result.FailureCode,
            ToolCalls = result.ToolCalls,
            Reasoning = result.Reasoning,
            ChatTranscriptJson = result.ChatTranscriptJson,
            ReviewExhausted = result.ReviewExhausted,
            ReviewAdjustments = result.ReviewAdjustments
        };
    }

    /// <summary>
    /// <paramref name="result"/> with a different <see cref="StepExecutionResult.NextStepIndex"/> — a
    /// composition replay's review loop that is not allowed to loop back (see
    /// <see cref="CompositionReplaySession.AdjustLoopBack"/>). The note is added to the review's JSON
    /// output as <c>replay.loopBack</c> so the run page says why the review did not loop; a non-object
    /// output is left as it is.
    /// </summary>
    /// <summary><paramref name="result"/> with another <see cref="StepExecutionResult.Output"/> (every other field kept).</summary>
    internal static StepExecutionResult WithOutput(StepExecutionResult result, string output) =>
        Rebuild(result, output, result.NextStepIndex);

    internal static StepExecutionResult WithNextStepIndex(StepExecutionResult result, int nextStepIndex, string? note)
    {
        string output = result.Output;
        if (!string.IsNullOrWhiteSpace(note))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(output) is System.Text.Json.Nodes.JsonObject obj)
                {
                    obj["replay"] = new System.Text.Json.Nodes.JsonObject { ["loopBack"] = note };
                    output = obj.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // Not a JSON object: keep the output verbatim.
            }
        }

        return Rebuild(result, output, nextStepIndex);
    }

    private static StepExecutionResult Rebuild(StepExecutionResult result, string output, int nextStepIndex)
    {
        return new StepExecutionResult
        {
            Output = output,
            NextStepIndex = nextStepIndex,
            NewIterationCount = result.NewIterationCount,
            TokensUsed = result.TokensUsed,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            PrimaryProviderId = result.PrimaryProviderId,
            PrimaryModelName = result.PrimaryModelName,
            DurationMs = result.DurationMs,
            AttemptCount = result.AttemptCount,
            RetryCount = result.RetryCount,
            Status = result.Status,
            ErrorDetails = result.ErrorDetails,
            IterationNumber = result.IterationNumber,
            OutputStorageKey = result.OutputStorageKey,
            ArtifactStorageKey = result.ArtifactStorageKey,
            TimelineStorageKey = result.TimelineStorageKey,
            ComputeTarget = result.ComputeTarget,
            RunnerId = result.RunnerId,
            RunnerJobId = result.RunnerJobId,
            FailureClass = result.FailureClass,
            FailureCode = result.FailureCode,
            ToolCalls = result.ToolCalls,
            Reasoning = result.Reasoning,
            ChatTranscriptJson = result.ChatTranscriptJson,
            ReviewExhausted = result.ReviewExhausted,
            ReviewAdjustments = result.ReviewAdjustments
        };
    }

    /// <summary>
    /// Writes the row of a step a composition replay copies or skips, publishes it like any finished step
    /// (so the run page and the SSE stream show it), and — for a copy — feeds the copied output into the
    /// run exactly as if the step had produced it: the history entry later steps resolve by step order,
    /// and the accumulated output the next step reads. Returns the new accumulated output.
    /// </summary>
    private async Task<string> ApplyReplayActionAsync(
        WorkflowEngineDbContext db,
        WorkflowExecution execution,
        WorkflowStep step,
        ReplayAction action,
        CompositionReplaySession replay,
        List<StepOutputHistoryEntry> stepOutputHistory,
        string accumulatedOutput,
        int currentStepIndex,
        int iterationCount,
        CancellationToken ct)
    {
        WorkflowStepResult row;
        if (action.Kind == ReplayActionKind.Reuse && action.Source is { } source)
        {
            string output = CompositionReplaySession.OriginalOutput(source);
            row = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                Output = output,
                OutputJson = StripReplayRegistrationOverQuota(source.OutputJson) ?? EnsureJsonForJsonbColumn(output),
                Status = source.Status,
                IterationNumber = source.IterationNumber,
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                OutputStorageKey = source.OutputStorageKey,
                ArtifactStorageKey = source.ArtifactStorageKey,
                TimelineStorageKey = source.TimelineStorageKey,
                ChatTranscriptJson = source.ChatTranscriptJson,
                ReusedFromStepResultId = source.Id
            };
        }
        else
        {
            string output = JsonSerializer.Serialize(new { skipped = true, reason = action.Reason });
            row = new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                Output = output,
                OutputJson = output,
                Status = StepStatus.Skipped,
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
        }

        db.WorkflowStepResults.Add(row);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Execution {ExecutionId} step {StepOrder} ({StepType}) {Action} in the revision's replay: {Reason}",
            execution.Id, step.StepOrder, step.StepType,
            action.Kind == ReplayActionKind.Reuse ? (action.Kept ? "kept" : "reused") : "skipped",
            action.Reason);

        StepExecutionResult published = new()
        {
            Output = row.Output,
            NextStepIndex = currentStepIndex + 1,
            NewIterationCount = iterationCount,
            Status = row.Status,
            OutputStorageKey = row.OutputStorageKey,
            ArtifactStorageKey = row.ArtifactStorageKey,
            TimelineStorageKey = row.TimelineStorageKey,
            ChatTranscriptJson = row.ChatTranscriptJson
        };
        await _eventPublisher.PublishStepStartedAsync(execution, step, row, null, ct);
        await _eventPublisher.PublishStepCompletedAsync(execution, step, row, published, ct);

        if (action.Kind != ReplayActionKind.Reuse)
        {
            replay.RecordSkipped(step.StepOrder);
            return accumulatedOutput;
        }

        replay.RecordReused(step.StepOrder, action.Kept);
        if (!string.IsNullOrEmpty(row.Output))
        {
            string stepLabel = string.IsNullOrWhiteSpace(step.Label) ? step.AgentDefinition.Name : step.Label;
            stepOutputHistory.Add(new StepOutputHistoryEntry(
                step.StepOrder, stepLabel, row.Output, row.OutputStorageKey, row.ArtifactStorageKey));
        }

        return row.Output;
    }

    private static string BuildRetryDiagnosticMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Step execution failed without diagnostics.";

        string[] lines = raw
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Cannot", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("TS", StringComparison.Ordinal))
            .Take(8)
            .ToArray();

        return lines.Length == 0
            ? raw.Trim()
            : string.Join("\n", lines);
    }

    internal static string? ResolveInputJsonForPersistence(string? resolvedAgentInput, string accumulatedOutput)
    {
        if (!string.IsNullOrWhiteSpace(resolvedAgentInput))
            return resolvedAgentInput;

        return string.IsNullOrWhiteSpace(accumulatedOutput) ? null : accumulatedOutput;
    }

    /// <summary>
    /// Appends the T-007 earlier-pass block to a ReviewLoop's resolved input for the cache key.
    /// Null or empty <paramref name="reviewPassHistory"/> (every first pass, and every run whose
    /// loop does not repeat) returns <paramref name="resolvedInput"/> untouched, so a one-pass
    /// run's cache-key input is byte-identical to before the feature existed.
    /// </summary>
    internal static string? AppendReviewPassHistory(string? resolvedInput, string? reviewPassHistory)
    {
        if (string.IsNullOrEmpty(reviewPassHistory))
            return resolvedInput;

        return resolvedInput is null ? reviewPassHistory : resolvedInput + "\n\n" + reviewPassHistory;
    }

    /// <summary>
    /// True when <paramref name="stepOrder"/> falls in the half-open window
    /// <c>[minStepOrderInclusive, maxStepOrderExclusive)</c> a ReviewLoop step's loop-back opened —
    /// i.e. the loop target through (but not including) the ReviewLoop step itself. Extracted as a
    /// pure, directly-testable helper from the main execution loop's inline condition (see
    /// <see cref="ExecuteAsync"/>'s <c>pendingReviewFeedback</c> bookkeeping).
    /// </summary>
    internal static bool IsWithinReviewFeedbackWindow(int stepOrder, int minStepOrderInclusive, int maxStepOrderExclusive) =>
        stepOrder >= minStepOrderInclusive && stepOrder < maxStepOrderExclusive;

    // -----------------------------------------------------------------
    // B6b: the per-step credit gate (D8). A step reserves before it runs, settles after it
    // succeeds, and releases by failure class when it fails. Every call here is best-effort —
    // billing must never be able to fail a step, nor mask the failure already propagating (the
    // same contract the step-result cache has below).
    // -----------------------------------------------------------------

    /// <summary>
    /// Millicredits to hold for <paramref name="step"/> (B7). Precedence: an explicit
    /// <see cref="StepCreditOptions.StepReserveMillisOverride"/>, then the estimate off the
    /// organization's usage history, then B0's per-step-type default
    /// (<see cref="StepCreditDefaults"/>). Never negative, and never a silent zero for a step type
    /// that is charged: a zero estimate short-circuits the gate into taking no reservation, and a
    /// step with no reservation is never settled, so it would be free rather than merely unbilled up
    /// front.
    ///
    /// <para>
    /// the estimate is the run-level plan (<see cref="ICreditHoldEstimator.RunHoldMillisAsync"/>
    /// over <paramref name="runSteps"/>, the same <c>RunCreditHolds</c> the Inference API sums into
    /// <c>RequiredMillis</c>): a history step its p90, a cold step the larger of its previous default and
    /// its share of the low edge of the cold steps' usual range. <paramref name="runSteps"/> defaults to
    /// the execution's loaded workflow steps. The plan is made at the run's output resolution and its holds
    /// are final: a step that renders the output (VideoCompile, TimelineRender, Revise, an Agent granted
    /// <c>SandboxRender</c>) holds its Auto/1080p plan figure times the run's <c>OutputResolution.PixelFactor</c>
    /// above 1, never less, raised to the p90 of its own history at that resolution when that is higher
    /// (<c>ResolutionHoldScaling.HoldAt</c>), exactly what the API's preflight sums.
    /// </para>
    /// </summary>
    internal async ValueTask<long> ResolveStepReserveMillisAsync(
        WorkflowExecution execution, WorkflowStep step, CancellationToken ct, IReadOnlyList<WorkflowStep>? runSteps = null)
        => (await ResolveStepReserveAsync(execution, step, ct, runSteps).ConfigureAwait(false)).Reserve;

    /// <summary>
    /// The hold and the full-step figure it was scaled from. The credit gate derives its overdraft headroom from the
    /// full figure so a scaled-down change-request hold keeps the whole step's tolerance. An unpriced step
    /// (<c>full &lt;= 0</c>) returns it unscaled, exactly as before.
    /// </summary>
    internal async ValueTask<(long Reserve, long Full)> ResolveStepReserveAsync(
        WorkflowExecution execution, WorkflowStep step, CancellationToken ct, IReadOnlyList<WorkflowStep>? runSteps = null)
    {
        long full = await ResolveFullStepReserveMillisAsync(execution, step, ct, runSteps).ConfigureAwait(false);
        if (full <= 0)
        {
            return (full, full);
        }

        // A step change request re-makes only some of its target step's items: hold their share, never more than the
        // whole step (docs/change-requests.md "What it costs"). Every other step of the replay holds as always.
        double? fraction = (await ResolveChangeRowAsync(execution, step, ct).ConfigureAwait(false))?.ScopeFraction;
        return (StepChangeScope.ApplyToHold(full, fraction), full);
    }

    private sealed record ChangeRowProbe(double? ScopeFraction, string? OverridesJson);

    /// <summary>
    /// The <c>ScopeFraction</c> of the step change request this execution carries when <paramref name="step"/> is its
    /// target; null for every other run and step, and when the row cannot be read (the whole step is then held).
    /// </summary>
    private async ValueTask<(double? ScopeFraction, string? OverridesJson)?> ResolveChangeRowAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        if (execution.WorkflowDefinition is { Kind: not WorkflowDefinitionKind.CompositionReplay })
        {
            return null;
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            if (execution.WorkflowDefinition is null
                && await db.WorkflowDefinitions.AsNoTracking()
                    .Where(d => d.Id == execution.WorkflowDefinitionId && d.Kind == WorkflowDefinitionKind.CompositionReplay)
                    .AnyAsync(ct).ConfigureAwait(false) is false)
            {
                return null;
            }

            return await db.StepChangeRequests.AsNoTracking()
                .Where(r => r.ExecutionId == execution.Id && r.ProjectId == execution.ProjectId && r.TargetStepOrder == step.StepOrder)
                .Select(r => new ChangeRowProbe(r.ScopeFraction, r.OverridesJson))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } row
                ? (row.ScopeFraction, row.OverridesJson)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the change scope of execution {ExecutionId}; holding the whole step.", execution.Id);
            return null;
        }
    }

    /// <summary>
    /// Whether the run's project holds any avatar (a person or an object), read only for a step whose hold depends on
    /// it (<see cref="ReelBolt.Shared.Metering.ProjectSubjectsPricing.Matters"/>). Null = not read or not readable,
    /// which the hold treats as "it may": a hold only ever grows.
    /// </summary>
    private async Task<bool?> ProjectHasAvatarsAsync(Guid projectId, string? videoGenerateConfigJson, CancellationToken ct)
    {
        if (_scopeFactory is null || !ReelBolt.Shared.Metering.ProjectSubjectsPricing.Matters(videoGenerateConfigJson))
            return null;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            return await db.ProjectAvatars.AsNoTracking().AnyAsync(a => a.ProjectId == projectId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read whether project {ProjectId} holds an avatar; holding as if it may.", projectId);
            return null;
        }
    }

    private async ValueTask<long> ResolveFullStepReserveMillisAsync(
        WorkflowExecution execution, WorkflowStep step, CancellationToken ct, IReadOnlyList<WorkflowStep>? runSteps)
    {
        // Encode-once: a Plan-stage compile records no usage, so it reserves nothing (the Final-stage compile
        // after the review reserves the one encode). Ahead of the pinned override, which would give it a hold.
        if (CompileEncodeStages.IsPlanStage(step))
        {
            return 0;
        }

        // A DynamicsPlanner of a run with no pace is skipped by its executor: it takes no hold at all (the same rule).
        if (step.AgentDefinition is { } plannerAgent
            && ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(plannerAgent.AgentType, RunStyleOverrides.EffectiveDynamics(execution) is not null))
        {
            return 0;
        }

        if (_creditOptions?.StepReserveMillisOverride is { } pinned)
        {
            return Math.Max(0, pinned);
        }

        // A local AudioCleanup target is free whatever the estimator or the options would say: nothing to hold.
        if (step.StepType == StepType.AudioCleanup && StepCreditDefaults.IsLocalAudioCleanup(step.AudioCleanupConfigJson))
        {
            return 0;
        }

        // A local VideoEnhance is free whatever the estimator or the options would say: nothing to hold.
        if (step.StepType == StepType.VideoEnhance && StepCreditDefaults.IsLocalVideoEnhance(step.VideoEnhanceConfigJson))
        {
            return 0;
        }

        if (_creditHoldEstimator is not null && execution.OrganizationId != Guid.Empty)
        {
            // B6c: a VideoGenerate step is priced from its OWN settings first — how many clips it
            // will order and how many seconds each, at the resolved model's per-second rate-card
            // price. That is the same arithmetic the step's VideoGenSeconds settlement uses, so the
            // hold matches the charge by construction; the history/defaults path below is only the
            // fallback for a step whose provider or price cannot be resolved. Before B6c the step
            // held nothing at all, and a zero hold short-circuits the gate into taking no
            // reservation — which is why its recorded credit rows were never charged.
            if (step.StepType == StepType.VideoGenerate)
            {
                using IDisposable briefAvatar = ReelBolt.Shared.Metering.BriefAvatarPricing.Enter(
                    Services.Briefs.RunBriefAmbient.Current?.Brief.Avatar?.AvatarId);
                // A plan-sourced step holds for the subjects its planner may use only when the project holds any
                // avatar: the same fact the API's estimate enters, so the two holds stay equal.
                using IDisposable projectSubjects = ReelBolt.Shared.Metering.ProjectSubjectsPricing.Enter(
                    await ProjectHasAvatarsAsync(execution.ProjectId, step.VideoGenerateConfigJson, ct).ConfigureAwait(false));
                long? declared = await _creditHoldEstimator
                    .TryVideoGenerateHoldMillisAsync(step.VideoGenerateConfigJson, ct)
                    .ConfigureAwait(false);

                if (declared is { } millis)
                {
                    return Math.Max(0, millis);
                }
            }

            // T-108: an AudioGenerate step likewise, from its takes x the chosen model's billed units at the
            // price row its MusicGenMinutes/SoundEffectGenSeconds settlement uses; the fallback is the same.
            if (step.StepType == StepType.AudioGenerate)
            {
                // A change request that re-makes a take LONGER than configured is held at that length: the target's
                // pricing config carries the longest override (the run still executes each take's own override).
                string? pricedConfig = step.AudioGenerateConfigJson;
                if ((await ResolveChangeRowAsync(execution, step, ct).ConfigureAwait(false)) is { OverridesJson: { } overridesJson })
                {
                    pricedConfig = StepChangePricing.AudioConfigWithOverrides(pricedConfig, overridesJson);
                }

                long? declared = await _creditHoldEstimator
                    .TryAudioGenerateHoldMillisAsync(pricedConfig, ct)
                    .ConfigureAwait(false);

                if (declared is { } millis)
                {
                    return Math.Max(0, millis);
                }
            }

            // ImageGenerate: pictures x the chosen model's price-book row (ImageGenerateCreditPricing); the fallback is the
            // cold-start row, never a zero hold.
            if (step.StepType == StepType.ImageGenerate)
            {
                long? declared = await _creditHoldEstimator
                    .TryImageGenerateHoldMillisAsync(step.ImageGenerateConfigJson, ct)
                    .ConfigureAwait(false);

                if (declared is { } millis)
                {
                    return Math.Max(0, millis);
                }
            }

            // AudioCleanup: a local target is free (no hold at all); a hosted one is held at the price-book figure of the
            // reference length. When it cannot be priced the step takes the cold-start row below (never a zero hold) and
            // the executor then refuses it, so nothing unpriced runs.
            if (step.StepType == StepType.AudioCleanup)
            {
                long? declared = await _creditHoldEstimator
                    .TryAudioCleanupHoldMillisAsync(step.AudioCleanupConfigJson, ct)
                    .ConfigureAwait(false);

                if (declared is { } millis)
                {
                    return Math.Max(0, millis);
                }
            }

            // VideoEnhance: a local operation is free (no hold at all); a hosted one is held at the price-book figure of
            // the config's own length bound. When it cannot be priced the step takes the cold-start row below (never a
            // zero hold) and the executor then refuses it, so nothing unpriced runs.
            if (step.StepType == StepType.VideoEnhance)
            {
                long? declared = await _creditHoldEstimator
                    .TryVideoEnhanceHoldMillisAsync(step.VideoEnhanceConfigJson, ct)
                    .ConfigureAwait(false);

                if (declared is { } millis)
                {
                    return Math.Max(0, millis);
                }
            }

            IReadOnlyList<WorkflowStep> run = runSteps
                ?? execution.WorkflowDefinition?.Steps.OrderBy(s => s.StepOrder).ToList()
                ?? [step];
            // The run's pace, so the plan leaves a skipped planner out exactly as the API's estimate does.
            using IDisposable runPace = ReelBolt.Shared.Metering.DynamicsPlannerPricing.Enter(RunStyleOverrides.EffectiveDynamics(execution) is not null);
            long estimated = await _creditHoldEstimator
                .RunHoldMillisAsync(execution.OrganizationId, run, step, RunStyleOverrides.OutputResolutionOf(execution), ct)
                .ConfigureAwait(false);

            // Only a genuinely uncosted step type may be 0 (Conditional/Extract); the estimator
            // answers 0 for exactly those, so a 0 here is trusted.
            return Math.Max(0, estimated);
        }

        return Math.Max(0, _creditOptions?.DefaultReserveMillisFor(step.StepType)
            ?? StepCreditDefaults.MillisFor(step.StepType).Low);
    }

    /// <summary>
    /// What the org's plan must allow before the hold is taken, derived from the SAME
    /// <see cref="PlanGate"/> mapping the runtime plan re-check above uses, so the two cannot drift
    /// apart. <see cref="StepType.VideoGenerate"/> maps to
    /// <see cref="CreditEntitlement.VideoGeneration"/> since B6c, which moved that step onto credits:
    /// the entitlement additionally requires a non-zero daily video budget, and
    /// <c>VideoGenerationBudgetGate</c> already denies a zero-budget organization with the same
    /// <c>PLAN_NOT_ENTITLED</c> reading, so the two gates agree rather than duplicate.
    /// </summary>
    internal static CreditEntitlement ResolveCreditEntitlement(WorkflowStep step)
        => step.StepType == StepType.VideoEnhance
            // Config-driven like segmentation: only a hosted operation needs the plan feature; a local one is free on every plan.
            ? (StepCreditDefaults.IsLocalVideoEnhance(step.VideoEnhanceConfigJson) ? CreditEntitlement.None : CreditEntitlement.VideoEnhance)
            : ResolveCreditEntitlement(step.StepType);

    internal static CreditEntitlement ResolveCreditEntitlement(StepType stepType)
        => PlanGate.FeatureFor(stepType) switch
        {
            PlanGate.RoomsFeature => CreditEntitlement.Rooms,
            PlanGate.VideoGenerationFeature => CreditEntitlement.VideoGeneration,
            PlanGate.AudioGenerationFeature => CreditEntitlement.AudioGeneration,
            _ => CreditEntitlement.None
        };

    /// <summary>
    /// Ends a claimed run whose snapshot could not be taken: Failed with <see cref="SnapshotFailedMessage"/> (or Cancelled
    /// with the recorded reason, for a stop that arrived meanwhile) and the failure event. Written through a fresh context (the run's own still tracks the snapshot rows whose save failed,
    /// and would try them again) and only while the row is still this claim's Running row. Best-effort: it runs while the
    /// snapshot's exception propagates, so it logs rather than replace that exception.
    /// </summary>
    private async Task EndUnpreparedRunAsync(Guid executionId, ExecutionStatus status, string message)
    {
        try
        {
            if (!await StillOwnsRunAsync(executionId))
                return;

            using IServiceScope failureScope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext failureDb = failureScope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            WorkflowExecution? row = await failureDb.WorkflowExecutions
                .FirstOrDefaultAsync(e => e.Id == executionId && e.Status == ExecutionStatus.Running, CancellationToken.None);
            if (row is null)
                return;

            row.Status = status;
            row.ErrorMessage = message;
            row.CompletedAt = DateTime.UtcNow;
            await failureDb.SaveChangesAsync(CancellationToken.None);
            await _eventPublisher.PublishExecutionFailedAsync(row, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the snapshot failure of execution {ExecutionId}", executionId);
        }
    }

    /// <summary>SQFU-S1: throws (lease-lost cancellation) unless this instance still owns the run's lease. No lease service, no check.</summary>
    private Task EnsureLeaseHeldAsync(Guid executionId, CancellationToken ct) =>
        _runLeases is null ? Task.CompletedTask : _runLeases.ThrowIfLeaseLostAsync(executionId, ct);

    /// <summary>
    /// SQFU-S1: whether this instance may still write the run's terminal status. A failed check (the database is
    /// away) keeps the old behaviour and lets the write be tried: only a confirmed loss of the lease skips it.
    /// </summary>
    private async Task<bool> StillOwnsRunAsync(Guid executionId)
    {
        if (_runLeases is null)
            return true;
        try
        {
            return await _runLeases.TryHoldAsync(executionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the lease of execution {ExecutionId}; writing its outcome anyway", executionId);
            return true;
        }
    }

    /// <summary>
    /// Whether a step's result is recorded in the run's step-output history. A skipped DynamicsPlanner (only that step; every other
    /// skipped result, e.g. a Conditional branch, keeps its history behaviour) produced nothing: its result
    /// only carries the previous step's output forward, so recording it under this step's order would make a later step
    /// that resolves this step's output (the compile's DynamicsPlan) read the previous step's.
    /// </summary>
    internal static bool RecordsInOutputHistory(StepExecutionResult result, AgentType? agentType) =>
        !string.IsNullOrEmpty(result.Output)
        && !(result.Status == StepStatus.Skipped && agentType == AgentType.DynamicsPlanner);

    /// <summary>
    /// Holds credits for one step, or throws <see cref="CreditRefusalException"/> so the step fails
    /// with a clear <c>INSUFFICIENT_CREDITS</c> / <c>PLAN_NOT_ENTITLED</c> reason instead of running
    /// uncharged. Never silently allows a step the gate refused; never throws when billing is off,
    /// when no gate is wired, or for an execution with no organization (the same "no org, no gate"
    /// reading the runtime plan re-check takes).
    /// </summary>
    private async Task<CreditReservation> ReserveStepCreditsAsync(
        WorkflowExecution execution, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, WorkflowStepResult stepResult, CancellationToken ct)
    {
        if (_creditGate is null)
            return NoReservation;

        if (execution.OrganizationId == Guid.Empty)
        {
            _logger.LogWarning(
                "Execution {ExecutionId} has no organization; skipping the credit reservation for step {StepOrder}",
                execution.Id, step.StepOrder);
            return NoReservation;
        }

        (long estimate, long fullEstimate) = await ResolveStepReserveAsync(execution, step, ct, runSteps).ConfigureAwait(false);
        try
        {
            CreditReservation reservation = await _creditGate.ReserveAsync(
                execution.OrganizationId,
                execution.Id,
                stepResult.Id,
                estimate,
                execution.ProjectId,
                ResolveCreditEntitlement(step),
                attempt: 0,
                ct,
                overdraftBasisMillis: fullEstimate > estimate ? fullEstimate : null,
                apiKeyId: execution.ApiKeyId).ConfigureAwait(false);

            if (!reservation.Allowed)
                throw CreditRefusalException.For(reservation, step);

            if (reservation.ReservationId is not null)
            {
                _logger.LogInformation(
                    "Held {ReservedMillis} millicredits for execution {ExecutionId} step {StepOrder} ({StepType}); {AvailableMillis} available",
                    reservation.ReservedMillis, execution.Id, step.StepOrder, step.StepType, reservation.AvailableMillis);
            }

            return reservation;
        }
        catch (CreditRefusalException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ReserveAsync fails closed on its own (it answers GATE_UNAVAILABLE rather than
            // throwing); this is the belt-and-braces for anything between here and it. A gate we
            // cannot evaluate must not hand out free compute.
            _logger.LogError(ex,
                "Credit reservation for execution {ExecutionId} step {StepOrder} threw; refusing the step",
                execution.Id, step.StepOrder);
            throw CreditRefusalException.For(
                new CreditReservation(CreditReserveOutcome.InsufficientCredits, null, FailureClassifier.GateUnavailableCode, estimate),
                step);
        }
    }

    /// <summary>
    /// Charges what the step actually consumed (the ambient usage scope's priced millicredit total
    /// for this step, retries included) and releases the rest of the hold. Returns what was charged.
    /// <para>
    /// <see cref="CancellationToken.None"/> on purpose, exactly like <c>MarkCancelledAsync</c>'s
    /// writes: this runs on the way OUT of a step, and a settle skipped because the run was winding
    /// down would leave the org's reservation held forever.
    /// </para>
    /// <para>
    /// S-3: a settle that throws or is refused is tried three times (<see cref="CreditCloseRetryDelays"/>)
    /// against the SAME reservation, so the ledger's <c>gate-settle:</c> key replays and an attempt whose
    /// answer was lost after it committed is never charged twice. When all three fail the step still
    /// completes, but what it metered is recorded on <paramref name="stepResult"/>
    /// (<see cref="WorkflowStepResult.CreditsUnsettledMillis"/>, saved with the step) and the hold stays
    /// open for <see cref="UnsettledHoldSweeper"/>, which settles it at that amount: never free.
    /// </para>
    /// </summary>
    private async Task<long> SettleStepCreditsAsync(CreditReservation reservation, WorkflowStepResult stepResult)
    {
        if (_creditGate is null || reservation.ReservationId is null)
            return 0;

        long metered = UsageScope.RunningCreditsMillis;
        CreditSettlement? settlement = await CloseStepHoldWithRetryAsync(
            () => _creditGate.SettleAsync(reservation.ReservationId, metered, CancellationToken.None),
            reservation.ReservationId.Value, "settle", FailureClass.None).ConfigureAwait(false);

        if (settlement is null)
        {
            RecordUnsettledHold(stepResult, reservation.ReservationId.Value, metered, "settle", FailureClass.None);
            return 0;
        }

        return settlement.Ok ? Math.Max(0, settlement.ChargedMillis) : 0;
    }

    /// <summary>
    /// Closes the step's hold after a failure, charging nothing for a platform-caused class (and
    /// refunding it when the step had already settled) and the consumed amount for a user class.
    /// Returns what stayed charged. See <see cref="FailureClassifier"/> for the mapping and
    /// <see cref="SettleStepCreditsAsync"/> for why the token is not the execution's.
    /// <para>
    /// S-3: a user-borne class owes what it consumed, so its close is retried and, when every attempt
    /// fails, recorded exactly like a failed settle. A refundable class owes nothing: one attempt, and
    /// the run-level release (or the sweep) closes a hold it could not.
    /// </para>
    /// </summary>
    private async Task<long> ReleaseStepCreditsAsync(CreditReservation reservation, FailureClass failureClass, WorkflowStepResult stepResult)
    {
        if (_creditGate is null || reservation.ReservationId is null)
            return 0;

        long metered = UsageScope.RunningCreditsMillis;
        bool owed = !CreditGate.IsRefundable(failureClass);
        CreditSettlement? settlement = await CloseStepHoldWithRetryAsync(
            () => _creditGate.ReleaseAsync(reservation.ReservationId, failureClass, metered, CancellationToken.None),
            reservation.ReservationId.Value, "release", failureClass, attempts: owed ? 3 : 1).ConfigureAwait(false);

        if (settlement is null)
        {
            if (owed)
            {
                RecordUnsettledHold(stepResult, reservation.ReservationId.Value, metered, "release", failureClass);
            }
            else
            {
                _logger.LogError(
                    "Credit release of reservation {ReservationId} as {FailureClass} failed; the run-level release or the unsettled-hold sweep refunds it",
                    reservation.ReservationId, failureClass);
            }

            return 0;
        }

        if (settlement.Ok && settlement.RefundedMillis > 0)
        {
            _logger.LogInformation(
                "Refunded {RefundedMillis} millicredits for {FailureClass} failure of reservation {ReservationId}",
                settlement.RefundedMillis, failureClass, reservation.ReservationId);
        }

        return settlement.Ok ? Math.Max(0, settlement.ChargedMillis) : 0;
    }

    /// <summary>
    /// Runs <paramref name="close"/> up to <paramref name="attempts"/> times, waiting
    /// <see cref="CreditCloseRetryDelays"/> between attempts. Returns the first Ok answer, or a terminal
    /// refusal (<see cref="CreditLedgerErrors.ReservationClosed"/>/<see cref="CreditLedgerErrors.ReservationNotFound"/>:
    /// there is no open hold left to close); null when every attempt threw or was refused.
    /// </summary>
    private async Task<CreditSettlement?> CloseStepHoldWithRetryAsync(
        Func<Task<CreditSettlement>> close, Guid reservationId, string operation, FailureClass failureClass, int attempts = 3)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                CreditSettlement settlement = await close().ConfigureAwait(false);
                if (settlement.Ok)
                    return settlement;

                if (settlement.Error is CreditLedgerErrors.ReservationClosed or CreditLedgerErrors.ReservationNotFound)
                {
                    _logger.LogError(
                        "Credit {Operation} of reservation {ReservationId} ({FailureClass}) refused: {Error}; nothing left to close",
                        operation, reservationId, failureClass, settlement.Error);
                    return settlement;
                }

                _logger.LogWarning(
                    "Credit {Operation} of reservation {ReservationId} ({FailureClass}) failed on attempt {Attempt}/{Attempts}: {Error}",
                    operation, reservationId, failureClass, attempt, attempts, settlement.Error);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Credit {Operation} of reservation {ReservationId} ({FailureClass}) threw on attempt {Attempt}/{Attempts}",
                    operation, reservationId, failureClass, attempt, attempts);
            }

            if (attempt < attempts && attempt - 1 < CreditCloseRetryDelays.Count)
            {
                await Task.Delay(CreditCloseRetryDelays[attempt - 1], CancellationToken.None).ConfigureAwait(false);
            }
        }

        return null;
    }

    private void RecordUnsettledHold(WorkflowStepResult stepResult, Guid reservationId, long metered, string operation, FailureClass failureClass)
    {
        stepResult.CreditsUnsettledMillis = Math.Max(0, metered);
        ReelBoltDiagnostics.UnsettledHolds.Add(1, new KeyValuePair<string, object?>("phase", "step"));
        _logger.LogError(
            "Credit {Operation} of reservation {ReservationId} ({FailureClass}) gave up after every attempt; recorded {MeteredMillis} millicredits on step result {StepResultId} for the unsettled-hold sweep",
            operation, reservationId, failureClass, metered, stepResult.Id);
    }

    /// <summary>
    /// Closes every still-open hold of a failed execution (B6b's whole-run path): a completed step's
    /// hold is settled, a failed step's is closed by its class, the rest are released as a platform
    /// failure (S-3, see <see cref="ICreditGate.ReleaseExecutionAsync"/>). Best-effort: it runs while an exception is already propagating, so it swallows and
    /// logs rather than replacing that exception.
    /// </summary>
    private async Task ReleaseExecutionCreditsAsync(Guid executionId)
    {
        if (_creditGate is null)
            return;

        try
        {
            int released = await _creditGate.ReleaseExecutionAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (released > 0)
            {
                _logger.LogInformation(
                    "Released {Count} open credit hold(s) of failed execution {ExecutionId}", released, executionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Releasing the credit holds of failed execution {ExecutionId} threw", executionId);
        }
    }

    // -----------------------------------------------------------------
    // Cross-execution step-result cache (Execution/Caching). Both methods below are best-effort
    // wrappers around IStepResultCache — that interface's own contract already guarantees it never
    // throws, but building a StepCacheKeyInputs here also calls IProjectFileFingerprintProvider
    // (this project's own code, no such guarantee) and AgentInputContextResolver, so the whole
    // lookup/store is wrapped in its own try/catch as a second line of defense: the cache must
    // never be able to fail a workflow step it is trying to speed up.
    // -----------------------------------------------------------------

    /// <summary>
    /// Voiceover only: true when every voice a REUSED result used is still usable (see <see cref="IVoiceReuseGuard"/>).
    /// Any other step type, or a host without the guard, is allowed with no query. A false answer is treated by the
    /// caller as a miss, so the step runs live and the executor's own consent guard decides.
    /// </summary>
    private static async Task<bool> VoiceReuseAllowedAsync(
        IVoiceReuseGuard? guard, WorkflowExecution execution, WorkflowStep step, string? output, CancellationToken ct)
    {
        if (guard is null || step.StepType != StepType.Voiceover)
            return true;
        return await guard.IsReuseAllowedAsync(execution.ProjectId, step.VoiceoverConfigJson, output, ct);
    }

    /// <summary>
    /// VideoGenerate only: true when the avatar a REUSED result shows (if any) is still usable and unchanged (see
    /// <see cref="Services.Avatars.IAvatarReuseGuard"/>). Any other step type, or a host without the guard, is allowed
    /// with no query. A false answer is a miss: the step runs live and the executor's own avatar checks decide.
    /// </summary>
    private static async Task<bool> AvatarReuseAllowedAsync(
        Services.Avatars.IAvatarReuseGuard? guard, WorkflowExecution execution, WorkflowStep step, string? output, CancellationToken ct)
    {
        if (guard is null || step.StepType != StepType.VideoGenerate)
            return true;

        // Judged on the config the executor would run: the run brief's avatar fills a step that names none.
        string? configJson = step.VideoGenerateConfigJson;
        if (Services.Briefs.RunBriefAmbient.Current?.Brief.Avatar?.AvatarId is not null && !string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                VideoGenerateStepConfig? parsed = JsonSerializer.Deserialize<VideoGenerateStepConfig>(configJson, VideoGenerateStepConfig.JsonOptions);
                if (parsed is not null)
                    configJson = JsonSerializer.Serialize(
                        Services.Briefs.RunBriefOverrides.ApplyToVideoGenerate(parsed, Services.Briefs.RunBriefAmbient.Current),
                        VideoGenerateStepConfig.JsonOptions);
            }
            catch (JsonException)
            {
                // The guard reads the raw config and fails closed on it.
            }
        }

        return await guard.IsReuseAllowedAsync(execution.ProjectId, configJson, output, ct);
    }

    /// <summary>
    /// False when an earlier result may predate the project's quarantine marker, or that cannot be read: it may depend
    /// on the file and would then feed the steps after it, so the step runs live instead. Output made after the marker
    /// is still reused, so a run started after it costs what it was quoted.
    /// </summary>
    private async Task<bool> EarlierOutputReusableAsync(
        WorkflowEngineDbContext db, WorkflowExecution execution, DateTime? resultExecutedAt, Guid sourceExecutionId, CancellationToken ct)
    {
        try
        {
            DateTime? runFirstStartedAt = await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.Id == sourceExecutionId)
                .Select(e => e.FirstStartedAt)
                .FirstOrDefaultAsync(ct);
            return !await ProjectQuarantineState.ResultPredatesAsync(db, execution.ProjectId, resultExecutedAt, runFirstStartedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the file state of project {ProjectId}; earlier results are not reused", execution.ProjectId);
            return false;
        }
    }

    /// <summary>
    /// Looks up a cached result for <paramref name="step"/>, when caching is fully wired
    /// (<paramref name="cache"/>/<paramref name="fingerprintProvider"/> resolved, <see cref="_cacheKeyBuilder"/>/
    /// <see cref="_cachePolicy"/> constructed) and the step is policy-eligible
    /// (<see cref="IStepCachePolicy.IsCacheable"/>). Returns null — meaning "run the step
    /// normally" — for every reason that isn't an actual cache MISS too: caching unresolved,
    /// disabled, or the step ineligible in the first place. Never throws.
    /// </summary>
    private async Task<WorkflowStepCacheEntry?> TryGetCachedStepResultAsync(
        IStepResultCache? cache,
        IProjectFileFingerprintProvider? fingerprintProvider,
        IInferenceProviderResolver? providerResolver,
        WorkflowExecution execution,
        WorkflowStep step,
        StepExecutionContext context,
        string? resolvedInput,
        string? upstreamDigest,
        CancellationToken ct,
        IServiceProvider? services = null)
    {
        if (cache is null || fingerprintProvider is null || _cacheKeyBuilder is null || _cachePolicy is null)
            return null;
        if (_cacheOptions is { Enabled: false })
            return null;

        try
        {
            if (!_cachePolicy.IsCacheable(step))
                return null;

            StepCacheKeyInputs inputs = await BuildCacheKeyInputsAsync(fingerprintProvider, providerResolver, execution, step, context, resolvedInput, upstreamDigest, ct, services);
            inputs = inputs with { ProjectPronunciation = await ProjectPronunciationDigestAsync(execution, step, ct) };
            // Validate-on-hit: a compile cached under a higher output cap (before a plan downgrade) must
            // not be served; the cache compares the cached render's short side with this run's cap.
            if (step.StepType == StepType.VideoCompile && services is not null)
                inputs = inputs with { MaxOutputShortSide = await ResolveOutputCapAsync(services, execution, ct) };
            StepCacheLookupResult lookup = await cache.TryGetAsync(inputs, ct);
            return lookup.Hit ? lookup.Entry : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Step-result cache lookup failed for execution {ExecutionId} step {StepOrder} ({StepType}); running the step normally",
                execution.Id, step.StepOrder, step.StepType);
            return null;
        }
    }

    /// <summary>
    /// The tallest short side this run may produce (<see cref="RunStyleOverrides.OutputCap"/>), or null
    /// when the run has no plan to read (no organization, no entitlement service) — the same cases in
    /// which <c>VideoCompileStepExecutor</c> applies no output policy, so nothing is checked.
    /// </summary>
    private static async Task<int?> ResolveOutputCapAsync(IServiceProvider services, WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.OrganizationId == Guid.Empty)
            return null;
        IOrganizationEntitlements? entitlements = services.GetService<IOrganizationEntitlements>();
        if (entitlements is null)
            return null;

        return RunStyleOverrides.OutputCap(await entitlements.GetAsync(execution.OrganizationId, ct), execution);
    }

    /// <summary>
    /// Persists <paramref name="result"/> to the step-result cache under the same conditions
    /// <see cref="TryGetCachedStepResultAsync"/> checks, PLUS one that only applies here:
    /// <see cref="IStepCachePolicy.IsAdmissible"/>. Never throws — a store failure must not undo the
    /// step's own already-successful completion.
    /// <para>
    /// D12: the two policy questions are asked at different moments and both are required.
    /// <see cref="IStepCachePolicy.IsCacheable"/> is about the STEP and is asked before the step runs
    /// (the lookup path asks it too); admissibility is about the RESULT — what the run actually did —
    /// so it can only be asked here, after the step has produced it. A result from the organization's
    /// own runner is skipped, while the step keeps everything else it already earned; the run
    /// proceeds exactly as if caching were off for it.
    /// </para>
    /// </summary>
    private async Task TryStoreCachedStepResultAsync(
        IStepResultCache? cache,
        IProjectFileFingerprintProvider? fingerprintProvider,
        IInferenceProviderResolver? providerResolver,
        WorkflowExecution execution,
        WorkflowStep step,
        StepExecutionContext context,
        string? resolvedInput,
        string? upstreamDigest,
        StepExecutionResult result,
        CancellationToken ct,
        IServiceProvider? services = null)
    {
        if (cache is null || fingerprintProvider is null || _cacheKeyBuilder is null || _cachePolicy is null)
            return;
        if (_cacheOptions is { Enabled: false })
            return;

        try
        {
            if (!_cachePolicy.IsCacheable(step))
                return;

            // D12: an OrgRunner result is inadmissible — see IsAdmissible's doc comment for why, and
            // for why a CloudPool result is NOT. Skipped silently-but-logged: this is a normal,
            // permanent property of the result, not a failure of the cache.
            if (!_cachePolicy.IsAdmissible(result))
            {
                if (result.WebSearchCalls > 0)
                {
                    _logger.LogInformation(
                        "Step {StepOrder} ({StepType}) for execution {ExecutionId} is not written to the step-result cache because it searched the web",
                        step.StepOrder, step.StepType, execution.Id);
                    return;
                }

                _logger.LogInformation(
                    "Step {StepOrder} ({StepType}) for execution {ExecutionId} produced a runner result (runner job {RunnerJobId}); it is not written to the step-result cache",
                    step.StepOrder, step.StepType, execution.Id, result.RunnerJobId);
                return;
            }

            StepCacheKeyInputs inputs = await BuildCacheKeyInputsAsync(fingerprintProvider, providerResolver, execution, step, context, resolvedInput, upstreamDigest, ct, services);
            // Filed under the glossary the step actually used (the digest it reports), not one re-read now: a glossary
            // edited while the step ran must not get this run's audio under its new digest.
            inputs = inputs with { ProjectPronunciation = GlossaryDigestUsedBy(step, result) };
            await cache.StoreAsync(inputs, result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Step-result cache store failed for execution {ExecutionId} step {StepOrder} ({StepType}); the step's own result is unaffected",
                execution.Id, step.StepOrder, step.StepType);
        }
    }

    /// <summary>
    /// The digest of the glossary entries a Voiceover step used, as the executor reported it in
    /// <c>meta.pronunciation.glossaryDigest</c>; null for other steps and when no glossary was used. Internal for tests.
    /// </summary>
    internal static string? GlossaryDigestUsedBy(WorkflowStep step, StepExecutionResult result)
    {
        if (step.StepType != StepType.Voiceover || string.IsNullOrEmpty(result.Output))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.Output);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("meta", out var meta)
                && meta.ValueKind == System.Text.Json.JsonValueKind.Object
                && meta.TryGetProperty("pronunciation", out var pronunciation)
                && pronunciation.ValueKind == System.Text.Json.JsonValueKind.Object
                && pronunciation.TryGetProperty("glossaryDigest", out var digest)
                && digest.ValueKind == System.Text.Json.JsonValueKind.String)
                return digest.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// The glossary digest of a Voiceover step's project, read for the cache LOOKUP before the step runs (null for other
    /// steps, an empty glossary or no resolver). A read failure throws into the caller's catch, which treats the cache as
    /// unavailable for this step.
    /// </summary>
    private async Task<string?> ProjectPronunciationDigestAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        if (step.StepType != StepType.Voiceover || _scopeFactory is null)
            return null;
        using IServiceScope scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IProjectPronunciationResolver>();
        if (resolver is null)
            return null;
        return PronunciationRespeller.Digest(await resolver.GetEntriesAsync(execution.ProjectId, ct));
    }

    /// <summary>
    /// Gathers everything <see cref="IStepCacheKeyBuilder.Build"/> hashes for <paramref name="step"/>
    /// out of the step definition, the resolved agent (when it is an Agent step), the execution's
    /// user request, and the project's current file fingerprint. <paramref name="resolvedInput"/>
    /// is the SAME value <see cref="ExecuteAsync"/> already computed for persistence
    /// (<c>context.LastResolvedAgentInput</c> for an Agent step — already resolved via
    /// <c>context.BuildAgentInput()</c> just above its call site — or the accumulated output for
    /// every other step type) — passed in rather than recomputed, per R: "do NOT change how
    /// inputs are built".
    /// </summary>
    private static async Task<StepCacheKeyInputs> BuildCacheKeyInputsAsync(
        IProjectFileFingerprintProvider fingerprintProvider,
        IInferenceProviderResolver? providerResolver,
        WorkflowExecution execution,
        WorkflowStep step,
        StepExecutionContext context,
        string? resolvedInput,
        string? upstreamDigest,
        CancellationToken ct,
        IServiceProvider? services = null)
    {
        string fingerprint = await fingerprintProvider.GetFingerprintAsync(execution.ProjectId, ct);

        return new StepCacheKeyInputs
        {
            ProjectId = execution.ProjectId,
            StepType = step.StepType,
            AgentType = step.AgentDefinition?.AgentType ?? AgentType.Custom,
            AgentDefinitionId = step.AgentDefinitionId,
            AgentSystemPrompt = step.AgentDefinition?.SystemPrompt,
            AgentOutputSchemaName = step.AgentDefinition?.OutputSchemaName,
            AgentInferenceProviderId = step.AgentDefinition?.InferenceProviderId,
            AgentAssignedSkillsJson = ResolvedAgentSkillsJson(step.AgentDefinition),
            AgentInputContextMode = AgentInputContextResolver.ResolveEffectiveMode(step),
            SelectedPriorStepOrdersJson = step.SelectedPriorStepOrdersJson,
            InputMappingJson = step.InputMappingJson,
            ExtractConfigJson = step.ExtractConfigJson,
            VideoAnalyzeConfigJson = step.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = step.VideoCompileConfigJson,
            VideoGenerateConfigJson = step.VideoGenerateConfigJson,
            VoiceoverConfigJson = step.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson = step.ThumbnailGenerateConfigJson,
            ExtractFrameConfigJson = step.ExtractFrameConfigJson,
            AudioGenerateConfigJson = step.AudioGenerateConfigJson,
            PinInsertConfigJson = step.PinInsertConfigJson,
            AudioCleanupConfigJson = step.AudioCleanupConfigJson,
            VideoEnhanceConfigJson = step.VideoEnhanceConfigJson,
            ImageGenerateConfigJson = step.ImageGenerateConfigJson,
            TimelineRenderConfigJson = step.TimelineRenderConfigJson,
            EditRoomConfigJson = step.EditRoomConfigJson,
            GraphicsRoomConfigJson = step.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson = step.ColorGradeRoomConfigJson,
            DailiesRoomConfigJson = step.DailiesRoomConfigJson,
            ConditionExpression = step.ConditionExpression,
            MaxIterations = step.MaxIterations,
            MinScore = step.MinScore,
            ResolvedInput = resolvedInput,
            InputProfileRecipes = step.StepType switch
            {
                StepType.VideoCompile => ReelBolt.WorkflowEngine.Services.Video.SourceCameraProfile.CacheKeyComponentFromJson(step.VideoCompileConfigJson),
                StepType.VideoAnalyze => ReelBolt.WorkflowEngine.Services.Video.SourceCameraProfile.CacheKeyComponentFromJson(step.VideoAnalyzeConfigJson),
                _ => null
            },
            UserRequest = context.UserRequest,
            RunStyle = RunStyleOverrides.CacheKeyComponent(execution, context.AllSteps),
            // QA-DYN-61: the run's dynamics key a compile only (a pace-only change must not re-run analysis, voice or paid generation).
            RunDynamics = step.StepType == StepType.VideoCompile ? RunStyleOverrides.DynamicsCacheKeyComponent(execution) : null,
            // Only the brief fields this step's role or type reads, null (not hashed) when none are set.
            RunBrief = RunBriefAmbient.Current?.CacheKeyComponent(
                step.StepType, step.AgentDefinition?.AgentType, step.VideoAnalyzeConfigJson, step.ImageGenerateConfigJson),
            // The rendered design/product blocks this role consumes, from the ambient the execution
            // entered above; null when there is no usable pin or the role sees no view. For
            // VideoCompile with brand overrides on, the same call also carries the derived-values
            // c= component (SPEC 14.2), which is derived from the step's own config.
            DesignSystem = DesignSystemAmbient.Current?.CacheKeyComponent(
                step.StepType, step.AgentDefinition?.AgentType, step),
            ResolvedProviderFingerprint = await ResolvedProviderFingerprintAsync(providerResolver, step, ct),
            VoiceoverDefaultVoice = await VoiceoverDefaultVoiceAsync(services, execution, step, ct),
            SfxLibraryDigest = await SfxLibraryDigestAsync(services, step, context.AllSteps, ct),
            AvatarConsent = await AvatarConsentComponentAsync(services, execution, step, ct),
            // The app-input values this step consumed (docs/design/app-inputs.md 10.1); null (not hashed) when none.
            AppInputs = AppInputsAmbient.Current?.ComponentFor(step.StepOrder),
            // The [PROJECT SUBJECTS] block a shot-planner step reads (docs/design/avatar-objects.md); null (not hashed) otherwise.
            ProjectSubjects = Services.Avatars.ProjectSubjectsAmbient.Current?.CacheKeyComponent(
                step.StepType, step.AgentDefinition?.AgentType),
            ProjectFileFingerprint = fingerprint,
            UpstreamDigest = upstreamDigest,
            WorkflowDefinitionId = execution.WorkflowDefinitionId
        };
    }

    /// <summary>
    /// The resolved-provider fingerprint of one step (A10/R10), or null when no resolver is wired
    /// (a minimally-constructed executor, which is what several unit tests build). The step's own
    /// explicit provider references come from <see cref="StepProviderReferences.Extract"/>, the one
    /// place that knows which config field of which step type names a provider.
    /// </summary>
    /// <summary>
    /// The skill set the agent actually runs with, as the step-cache key's skills component. A
    /// custom agent's is its own <c>AssignedSkillsJson</c>; a built-in's comes from
    /// <see cref="SkillCatalog.DefaultsFor"/> in code and its column is always null, so hashing the
    /// column alone made a change to a built-in's default skills invisible to the cache and let a
    /// re-run replay output produced without them. A built-in with no default skills stays null,
    /// so its key is unchanged.
    /// </summary>
    internal static string? ResolvedAgentSkillsJson(AgentDefinition? agent)
    {
        if (agent is null)
            return null;

        if (!agent.IsBuiltIn)
            return agent.AssignedSkillsJson;

        IReadOnlyList<string> defaults = SkillCatalog.DefaultsFor(agent.AgentType);
        return defaults.Count == 0 ? null : JsonSerializer.Serialize(defaults);
    }

    /// <summary>
    /// The built-in sound library's content digest for a VideoAnalyze or VideoCompile step of a workflow where some analysis
    /// offers the library (<c>offerSfxClips</c> with <c>includeSfxLibrary</c>); null for everything else, so no other key changes.
    /// </summary>
    private static async Task<string?> SfxLibraryDigestAsync(
        IServiceProvider? services, WorkflowStep step, IEnumerable<WorkflowStep>? allSteps, CancellationToken ct)
    {
        if (services is null || step.StepType is not (StepType.VideoAnalyze or StepType.VideoCompile))
            return null;

        static bool OffersLibrary(WorkflowStep s)
        {
            if (s.StepType != StepType.VideoAnalyze || string.IsNullOrWhiteSpace(s.VideoAnalyzeConfigJson))
                return false;
            try
            {
                VideoAnalyzeStepConfig? c = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(
                    s.VideoAnalyzeConfigJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
                return c is { OfferSfxClips: true, IncludeSfxLibrary: true };
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (!(allSteps ?? [step]).Any(OffersLibrary))
            return null;

        Services.Video.ISfxLibraryLookup? lookup = services.GetService<Services.Video.ISfxLibraryLookup>();
        return lookup is null ? null : Services.Video.SfxLibraryDigest.Of(await lookup.ListEnabledAsync(ct));
    }

    /// <summary>
    /// The project-default-voice component of a Voiceover step's key (<see cref="ProjectVoiceDefault.CacheKeyComponent"/>),
    /// computed exactly as the executor decides it (config with the run brief applied, same resolver), so adding,
    /// revoking or deleting a cloned voice stops a cached narration from being replayed. Null for any other step type,
    /// a step that names a voice, a project without a clone, or when no resolver is wired.
    /// </summary>
    private static async Task<string?> VoiceoverDefaultVoiceAsync(
        IServiceProvider? services, WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        if (step.StepType != StepType.Voiceover || services is null || string.IsNullOrWhiteSpace(step.VoiceoverConfigJson))
            return null;

        IProjectVoiceResolver? resolver = services.GetService<IProjectVoiceResolver>();
        if (resolver is null)
            return null;

        try
        {
            VoiceoverStepConfig? config = VoiceoverStepConfig.Parse(step.VoiceoverConfigJson);
            if (config is null)
                return null;

            config = RunBriefOverrides.ApplyToVoiceover(config, RunBriefAmbient.Current);
            return (await ProjectVoiceDefault.ResolveAsync(resolver, execution.ProjectId, config, ct)).CacheKeyComponent();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The avatar component of a VideoGenerate step's key (<see cref="StepCacheKeyInputs.AvatarConsent"/>): the avatar's
    /// fingerprint as it is now, or <c>unusable:{reason}</c>, so revoking, expiring or re-attesting an avatar stops a
    /// cached clip of that person from being replayed (the reuse guard re-checks a hit as well). Null for any other step,
    /// a step without an avatar, or when no resolver is wired.
    /// </summary>
    private static async Task<string?> AvatarConsentComponentAsync(
        IServiceProvider? services, WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        // Avatars phase 3: a Voiceover step that speaks in the avatar's voice keys on the voice linked NOW, so relinking
        // the avatar to another voice never replays narration in the old one. Null when the step does not opt in.
        if (step.StepType == StepType.Voiceover)
            return await AvatarVoiceComponentAsync(services, execution, step, ct);

        if (step.StepType != StepType.VideoGenerate || services is null || string.IsNullOrWhiteSpace(step.VideoGenerateConfigJson))
            return null;

        Services.Avatars.IAvatarResolver? resolver = services.GetService<Services.Avatars.IAvatarResolver>();
        if (resolver is null)
            return null;

        VideoGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.VideoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (config is null)
            return null;

        // The step reads the brief's avatar exactly as the executor does (an inline step that names none).
        config = Services.Briefs.RunBriefOverrides.ApplyToVideoGenerate(config, Services.Briefs.RunBriefAmbient.Current);

        // One legacy avatar: its fingerprint, as always. Several subjects: every one of them, in order.
        return await Services.Avatars.SubjectsCacheComponent.ComputeAsync(resolver, execution.ProjectId, config, ct);
    }

    /// <summary>The avatar-voice component of a Voiceover step with <c>VoiceFromAvatar</c>; null for every other one.</summary>
    private static async Task<string?> AvatarVoiceComponentAsync(
        IServiceProvider? services, WorkflowExecution execution, WorkflowStep step, CancellationToken ct)
    {
        if (services is null || string.IsNullOrWhiteSpace(step.VoiceoverConfigJson))
            return null;

        VoiceoverStepConfig? config;
        try
        {
            config = VoiceoverStepConfig.Parse(step.VoiceoverConfigJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (config is not { VoiceFromAvatar: true })
            return null;

        Services.Briefs.RunBriefState? brief = Services.Briefs.RunBriefAmbient.Current;
        if (Services.Briefs.RunBriefOverrides.VoiceIdOf(brief?.Voice) is not null)
            return null; // the brief's own voice wins, and the brief is already part of the key
        if (brief?.Brief.Avatar?.AvatarId is not Guid avatarId)
            return "voice:no-avatar";

        Services.Avatars.IAvatarResolver? resolver = services.GetService<Services.Avatars.IAvatarResolver>();
        if (resolver is null)
            return "voice:no-resolver";

        Services.Avatars.ResolvedAvatar avatar = await resolver.ResolveAsync(execution.ProjectId, avatarId, ct);
        return avatar.Usable
            ? "voice:" + (avatar.ProjectVoiceId?.ToString("N") ?? "none")
            : "voice:unusable:" + avatar.Reason;
    }

    private static async Task<string?> ResolvedProviderFingerprintAsync(
        IInferenceProviderResolver? providerResolver, WorkflowStep step, CancellationToken ct)
    {
        if (providerResolver is null)
            return null;

        return await ResolvedProviderFingerprint.ComputeAsync(
            providerResolver,
            step.AgentDefinition?.AgentType ?? AgentType.Custom,
            step.AgentDefinitionId,
            AgentSnapshotAmbient.Current?.Find(step.AgentDefinitionId)?.AppModelProviderId,
            StepProviderReferences.Extract(
                step.StepType,
                step.ConditionalConfigJson,
                step.ReviewLoopConfigJson,
                step.VideoAnalyzeConfigJson,
                step.VideoGenerateConfigJson,
                step.VoiceoverConfigJson,
                step.EditRoomConfigJson,
                step.GraphicsRoomConfigJson,
                step.ColorGradeRoomConfigJson,
                step.AudioGenerateConfigJson,
                step.AudioCleanupConfigJson,
                step.VideoEnhanceConfigJson,
                step.ImageGenerateConfigJson),
            ct);
    }

    private static async Task EnsureAuthorArtifactProducedAsync(
        Guid executionId,
        IReadOnlyCollection<WorkflowStep> workflowSteps,
        WorkflowEngineDbContext db,
        CancellationToken ct)
    {
        if (!workflowSteps.Any(step => step.AgentDefinition.AgentType == AgentType.AuthorAgent))
            return;

        HashSet<Guid> authorStepIds = workflowSteps
            .Where(step => step.AgentDefinition.AgentType == AgentType.AuthorAgent)
            .Select(step => step.Id)
            .ToHashSet();

        List<string?> outputStorageKeys = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(result => result.WorkflowExecutionId == executionId
                && result.Status == StepStatus.Completed
                && result.SupersededAt == null
                && authorStepIds.Contains(result.WorkflowStepId))
            .Select(result => result.OutputStorageKey)
            .ToListAsync(ct);

        if (outputStorageKeys.Any(key => !string.IsNullOrWhiteSpace(key)))
            return;

        throw new InvalidOperationException(
            "NO_RENDER_PRODUCED: Workflow contains an Author step but no rendered media artifact was produced. Ensure the Author step completes rendering and sets outputStorageKey.");
    }

    /// <summary>
    /// Ensures a value is safe to write to a jsonb column: null/empty or already-valid JSON pass
    /// through unchanged; anything else (e.g. a chat completion that didn't conform to its
    /// requested output schema — structured-output enforcement is a request, not a guarantee, and
    /// some OpenAI-compatible endpoints ignore it entirely) is wrapped as a JSON string. Without
    /// this, writing arbitrary text into WorkflowStepResult.OutputJson/WorkflowExecution.ResultJson
    /// (both jsonb) fails the whole SaveChangesAsync with Postgres error 22P02 ("invalid input
    /// syntax for type json") — and since the step/execution's Status change is batched in the
    /// same SaveChangesAsync call, that failure discards the Status update too, leaving the
    /// execution stuck "Running" forever (found by e2e QA).
    /// </summary>
    private static string? EnsureJsonForJsonbColumn(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            using JsonDocument _ = JsonDocument.Parse(value);
            return value;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(value);
        }
    }

    /// <summary>
    /// QA-P0-29: a composition replay copies the source step's OutputJson verbatim, which carries the
    /// SOURCE run's <c>registration.overQuota</c> — a storage-limit note the revision run never earned,
    /// and which the read model would warn about. Strip that one member; every other member (including
    /// <c>registration.registered</c>) is copied as it stands. Returns <paramref name="outputJson"/>
    /// unchanged when it is not a JSON object or carries no <c>registration.overQuota</c>.
    /// </summary>
    private static string? StripReplayRegistrationOverQuota(string? outputJson)
    {
        if (string.IsNullOrEmpty(outputJson))
            return outputJson;

        try
        {
            if (JsonNode.Parse(outputJson) is JsonObject root
                && root["registration"] is JsonObject registration
                && registration.ContainsKey("overQuota"))
            {
                registration.Remove("overQuota");
                return root.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not a JSON object: there is no registration node to strip.
        }

        return outputJson;
    }

    /// <summary>
    /// A compile step's narration counts, read from the voiceover node of its output. <see cref="LineCount"/>
    /// is the DECLARED count; <see cref="AppliedLineCount"/> the lines the render actually carries.
    /// </summary>
    private readonly record struct IncompleteNarration(
        int StepOrder, int LineCount, int AppliedLineCount, int ReanchoredLineCount);

    /// <summary>
    /// Reads narration counts from the NEWEST VideoCompile step output in this run — the render
    /// that actually shipped — and returns null when that compile was complete (or carried no
    /// readable voiceover node). History entries are appended in completion order and pruned on a
    /// review-loop rewind, so the last VideoCompile entry is the freshest; an OLDER incomplete
    /// compile (a previous attempt, or another VideoCompile step earlier in the workflow) must
    /// never override it. Returns non-null only when the newest compile itself reports fewer
    /// applied lines than declared.
    /// </summary>
    private static IncompleteNarration? FindIncompleteNarration(
        IReadOnlyList<StepOutputHistoryEntry> history, IReadOnlyList<WorkflowStep> steps)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            StepOutputHistoryEntry entry = history[i];
            if (steps.FirstOrDefault(s => s.StepOrder == entry.StepOrder)?.StepType != StepType.VideoCompile)
                continue;

            // This IS the newest compile: whatever it says is the answer. Do not fall back to an
            // older entry when it is complete, unreadable or output-less.
            if (string.IsNullOrEmpty(entry.Output))
                return null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(entry.Output);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("voiceover", out JsonElement voiceover)
                    || voiceover.ValueKind != JsonValueKind.Object
                    || !TryInt(voiceover, "lineCount", out int lineCount)
                    || !TryInt(voiceover, "appliedLineCount", out int appliedLineCount)
                    || lineCount <= appliedLineCount)
                    return null;

                int reanchoredLineCount = TryInt(voiceover, "reanchoredLineCount", out int reanchored) ? reanchored : 0;
                return new IncompleteNarration(entry.StepOrder, lineCount, appliedLineCount, reanchoredLineCount);
            }
            catch (JsonException)
            {
                // A malformed newest compile is not evidence of narration loss, and an older entry
                // is not the film that shipped.
                return null;
            }
        }

        return null;

        static bool TryInt(JsonElement element, string name, out int value)
        {
            value = 0;
            return element.TryGetProperty(name, out JsonElement e)
                && e.ValueKind == JsonValueKind.Number
                && e.TryGetInt32(out value);
        }
    }

    private static bool IsValidJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        try
        {
            using JsonDocument _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateLogPreview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength];
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// T-012: the run was admitted under the free run-rate bucket, and the steps the engine loaded are paid (the workflow
/// changed between admission and the run). Thrown before the first step, so nothing ran and nothing was charged.
/// </summary>
public sealed class RunClassChangedException : InvalidOperationException
{
    public const string FailureCode = "RUN_CLASS_CHANGED";

    public const string UserMessage = "This workflow changed after the run started; start it again.";

    public RunClassChangedException()
        : base(UserMessage)
    {
    }

    public string Code => FailureCode;
}
