using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Runs workflow executions on background tasks whose lifetime is bounded only by the process,
/// decoupling how long an execution takes from how long the message transport is willing to wait
/// for its delivery to be acknowledged.
///
/// <para><b>Why this type exists.</b>
/// <c>WorkflowExecutionRequestedConsumer</c> used to <c>await</c> the entire workflow inline, so
/// the RabbitMQ delivery stayed unacknowledged for the execution's full duration — hours, for a
/// code-analysis pipeline driving a local reasoning model. RabbitMQ's <c>consumer_timeout</c>
/// force-closes the channel of any delivery not acked within its window, which MassTransit
/// surfaces by cancelling <c>ConsumeContext.CancellationToken</c>; that token was the very one
/// handed to <c>ExecuteAsync</c> as its cancellation signal, so the broker's ack deadline silently
/// doubled as a hard wall-clock cap on every workflow. Observed live and reproduced from the
/// execution table: three consecutive <c>lean-context-promo</c> runs died at
/// <b>exactly 120.0 minutes</b> — <c>consumer_timeout = 7200000</c> ms to the second — each
/// discarding 40+ minutes of completed analysis steps and each mislabelled
/// "Cancelled by user request" despite nobody touching Stop.
/// </para>
///
/// <para>
/// Raising <c>consumer_timeout</c> (which is what the previous round of this bug did, from the
/// stock 30 minutes to 2 hours) only moves the wall. Any value is eventually exceeded by a
/// legitimately long execution, and exceeding it destroys work rather than degrading. The ack
/// window and the execution's duration have to stop being the same number, which is what this
/// runner does: the consumer hands the execution over, returns, and the delivery is acked within
/// milliseconds, while the work continues here.
/// </para>
///
/// <para><b>Where the concurrency limit lives now.</b>
/// Acking immediately means MassTransit's <c>ConcurrentMessageLimit</c> no longer bounds how many
/// executions run at once — it only bounds how many are being handed over at once. The real limit
/// moved to <see cref="_admission"/> (fair-share across organizations, A12), and it is acquired <i>in the consumer</i>, before dispatch,
/// deliberately: a consumer blocked waiting for a slot is holding an unacked delivery again, but
/// it is holding one that has performed <i>no work at all</i>, so if the broker does kill it the
/// redelivery costs nothing and the execution (still <c>Queued</c> in the database) simply starts
/// later. That is the one shape of this problem that is safe to have.
/// </para>
///
/// <para><b>Crash semantics.</b>
/// Because the delivery is acked while the execution is still running, a hard process kill leaves
/// the execution row <c>Running</c> with no message to redeliver, instead of the pre-change
/// behaviour where the unacked message came back. That trade is accepted: the redelivery it
/// replaces was almost always a no-op anyway, since <c>ExecuteAsync</c> refuses to start anything
/// not in <c>Queued</c> status, so a redelivered message for a crashed mid-flight execution was
/// already discarded on arrival rather than resuming it. Graceful shutdown is handled properly —
/// see <see cref="StopAsync"/>.
/// </para>
///
/// <para><b>One run per id per process (S-49).</b>
/// <see cref="_claimed"/> is taken as the first statement of <see cref="DispatchAsync"/>. A second
/// delivery of an id this process is still running is acknowledged and refused with
/// <see cref="AdmissionRefusal.AlreadyRunningHere"/> before <c>ExecuteAsync</c> is called, so it cannot
/// re-enter the cancellation registry or the lease of the live run.
/// </para>
/// </summary>
public sealed class WorkflowExecutionRunner : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowExecutionRunner> _logger;
    private readonly FairShareAdmission _admission;

    /// <summary>Per-org concurrency cap source; null (tests, no tenancy) means no per-org cap.</summary>
    private readonly IOrganizationEntitlements? _entitlements;

    /// <summary>Slot leases of in-flight executions, released in <see cref="RunAsync"/>'s finally.</summary>
    private readonly ConcurrentDictionary<Guid, IDisposable> _slotLeases = new();

    /// <summary>
    /// F11: the platform switches and the cross-replica per-organisation limit, asked once per
    /// delivery, before any slot is taken. It is the FIRST thing <see cref="DispatchAsync"/> does, so
    /// a paused platform costs one cached read rather than a blocked concurrency waiter.
    /// </summary>
    private readonly ExecutionAdmissionGuard _admissionGuard;

    /// <summary>Renews the lease of every in-flight execution (F4); null in unit tests.</summary>
    private readonly Services.ExecutionLeaseService? _leases;

    /// <summary>
    /// D7b: the pinned runner-backed sandbox session of each in-flight execution, closed and its
    /// workspace journal sealed in <see cref="RunAsync"/>'s finally. Null in unit tests that build the
    /// runner by hand; a self-host deployment resolves it and never has an entry to close.
    /// </summary>
    private readonly Services.Compute.ISandboxSessionRegistry? _sandboxSessions;

    /// <summary>
    /// Injected directly rather than resolved per-scope: it is a singleton, so there is no
    /// captive-dependency hazard, and <see cref="StopAsync"/> would otherwise spin up one DI scope
    /// per in-flight execution just to reach the same instance.
    /// </summary>
    private readonly ExecutionCancellationRegistry _cancellationRegistry;

    /// <summary>
    /// Ties every in-flight execution to the process lifetime, and <i>only</i> to it. This is the
    /// token <see cref="WorkflowExecutorService.ExecuteAsync"/> now receives, replacing the
    /// transport's <c>ConsumeContext.CancellationToken</c> — the substitution that actually fixes
    /// the bug in this type's summary, since nothing about a broker ack deadline can reach it.
    /// </summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// The per-execution decision memo. Injected directly rather than resolved per-scope for the
    /// same reason as <see cref="_cancellationRegistry"/>: it is a singleton, so there is no
    /// captive-dependency hazard.
    /// </summary>
    /// <remarks>
    /// This type is the memo's <b>eviction point</b> (plan §1 D4). <see cref="RunAsync"/> is the one
    /// method every execution passes through exactly once and exits exactly once, whatever its
    /// outcome, so its <c>finally</c> is the only place that can guarantee "entries are evicted when
    /// the execution completes" on the completed, failed <em>and</em> cancelled paths alike. Without
    /// that call the memo is bounded only by its 64-execution cap — a claim the docs made and the
    /// code did not keep.
    /// </remarks>
    private readonly IDecisionQuestionMemoizer _decisionMemoizer;

    /// <summary>In-flight executions, so <see cref="StopAsync"/> can wait for them rather than tearing them down mid-step.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>
    /// S-49: execution ids this process has accepted and not yet finished, from the first line of
    /// <see cref="DispatchAsync"/> until <see cref="RunAsync"/>'s task has completed and left <see cref="_running"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _claimed = new();

    public WorkflowExecutionRunner(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ExecutionCancellationRegistry cancellationRegistry,
        IDecisionQuestionMemoizer decisionMemoizer,
        ExecutionAdmissionGuard admissionGuard,
        ILogger<WorkflowExecutionRunner> logger,
        Services.ExecutionLeaseService? leases = null,
        IOrganizationEntitlements? entitlements = null,
        Services.Compute.ISandboxSessionRegistry? sandboxSessions = null)
    {
        _entitlements = entitlements;
        _leases = leases;
        _sandboxSessions = sandboxSessions;
        _admissionGuard = admissionGuard;
        _scopeFactory = scopeFactory;
        _cancellationRegistry = cancellationRegistry;
        _decisionMemoizer = decisionMemoizer;
        _logger = logger;

        int maxConcurrency = Math.Max(1, configuration.GetValue("WorkflowEngine:MaxConcurrency", 4));
        _admission = new FairShareAdmission(maxConcurrency);
        _logger.LogInformation(
            "WorkflowExecutionRunner initialised with MaxConcurrency={MaxConcurrency}", maxConcurrency);
    }

    /// <summary>Number of executions currently running. Test/diagnostic hook.</summary>
    public int RunningCount => _running.Count;

    /// <summary>
    /// Admits the execution (F11 kill switches, per-organisation limits — see
    /// <see cref="ExecutionAdmissionGuard"/>), then acquires a concurrency slot (honouring
    /// <paramref name="admissionToken"/>, which is the consumer's transport token — see the class
    /// summary for why blocking here specifically is the safe place to block) and starts the
    /// execution on a background task bound to the process lifetime.
    ///
    /// <para>
    /// Returns as soon as the execution has started, so the caller can acknowledge its message. A
    /// refusal returns immediately WITHOUT starting anything and without throwing: the execution is
    /// left <c>Queued</c> in the database and the caller acknowledges the delivery, which is how
    /// "paused work stays Queued" is implemented — see <see cref="AdmissionDecision"/>.
    /// </para>
    /// </summary>
    /// <returns>Whether the execution was dispatched, and why not when it was not.</returns>
    public async Task<AdmissionDecision> DispatchAsync(
        Guid executionId, string correlationId, CancellationToken admissionToken,
        bool regenerateVideoClips = false, Guid organizationId = default, string? admittedRunRateBucket = null)
    {
        // S-49: the claim is the first statement. A second delivery of an id this process has accepted
        // and not yet finished is acknowledged and dropped here, before ExecuteAsync can be called for it.
        if (!_claimed.TryAdd(executionId, 0))
        {
            _logger.LogWarning(
                "Execution {ExecutionId} is already running in this engine instance; the duplicate delivery is acknowledged and dropped (CorrelationId={CorrelationId})",
                executionId, correlationId);
            return AdmissionDecision.Refuse(AdmissionRefusal.AlreadyRunningHere);
        }

        // Until the background task is launched, every exit (a refusal, or an OCE while waiting for a
        // slot) must free the claim; once it is launched, the task's continuation frees it.
        bool started = false;
        try
        {
            if (organizationId == Guid.Empty)
            {
                organizationId = await ResolveOrganizationAsync(executionId, admissionToken);
            }

            AdmissionDecision decision = await _admissionGuard.CheckAsync(executionId, organizationId, admissionToken);
            if (!decision.Admitted)
            {
                return decision;
            }

            // Cap read per call (never cached beyond this admission); a cap below 1 would starve the
            // org forever, so it acts as 1.
            int cap = _entitlements is null
                ? OrganizationEntitlementSet.UnlimitedInt
                : Math.Max(1, await _entitlements.MaxConcurrentExecutionsAsync(organizationId, admissionToken));

            IDisposable lease = await _admission.AcquireAsync(organizationId, cap, admissionToken);
            _slotLeases[executionId] = lease;

            // From here on the slot is owned by the background task, which releases it in its finally.
            // Anything that can throw between the Wait above and the Task.Run below must release it
            // first, hence the try/catch rather than a bare launch.
            try
            {
                Task execution = Task.Run(
                    () => RunAsync(executionId, correlationId, regenerateVideoClips, admittedRunRateBucket),
                    CancellationToken.None);

                _running[executionId] = execution;

                // Detached on purpose: the whole point is that the caller (and therefore the message
                // acknowledgement) does not wait for this. RunAsync never throws — it funnels every
                // failure into logging — so this cannot become an unobserved faulted task.
                _ = execution.ContinueWith(
                    finished =>
                    {
                        _running.TryRemove(executionId, out _);
                        _claimed.TryRemove(executionId, out _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                started = true;
            }
            catch
            {
                _slotLeases.TryRemove(executionId, out _);
                lease.Dispose();
                throw;
            }

            return AdmissionDecision.Admit;
        }
        finally
        {
            if (!started)
            {
                _claimed.TryRemove(executionId, out _);
            }
        }
    }

    /// <summary>
    /// Org of an execution whose message predates tenancy: read from the row. Any failure yields
    /// <see cref="Guid.Empty"/> (its own fairness bucket), never a failed dispatch.
    /// </summary>
    private async Task<Guid> ResolveOrganizationAsync(Guid executionId, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<WorkflowEngineDbContext>();
            if (db is null)
            {
                return Guid.Empty;
            }

            return await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => e.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the organization of execution {ExecutionId}; admitting under the default bucket", executionId);
            return Guid.Empty;
        }
    }

    private async Task RunAsync(Guid executionId, string correlationId, bool regenerateVideoClips, string? admittedRunRateBucket)
    {
        ExecutionRunFlags? runFlags = null;
        try
        {
            // A scope per execution: WorkflowExecutorService and IWorkflowEventPublisher are both
            // Scoped, and the consumer's own scope is gone the moment it acks and returns, so the
            // runner must own the scope for as long as the execution does.
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowExecutorService executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutorService>();

            runFlags = scope.ServiceProvider.GetService<ExecutionRunFlags>();
            runFlags?.SetRegenerateVideoClips(executionId, regenerateVideoClips);

            // T-015: the admitted-bucket overload, so the RUN_CLASS_CHANGED re-check always runs. The message's bucket is a
            // hint for logs; the executor reads the row's own AdmittedRunRateBucket, which survives every re-dispatch.
            await executor.ExecuteAsync(executionId, correlationId, admittedRunRateBucket, _lifetimeCts.Token);

            _logger.LogInformation(
                "Workflow execution {ExecutionId} finished (CorrelationId={CorrelationId})",
                executionId, correlationId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Workflow execution {ExecutionId} was cancelled while running in the background", executionId);
        }
        catch (Exception ex)
        {
            // ExecuteAsync has already persisted the failure and published the failure event
            // before rethrowing; there is no message left to nack (it was acked at dispatch), so
            // logging is genuinely the whole remaining obligation here.
            _logger.LogError(ex, "Workflow execution {ExecutionId} failed", executionId);
        }
        finally
        {
            // Plan §1 D4: the decision memo lives and dies with its execution. This is the one exit
            // every execution takes exactly once — completed, failed or cancelled — so evicting here
            // is what makes the documented "entries are evicted when the execution completes" true
            // rather than aspirational, and it is what bounds the per-execution answer dictionary
            // instead of leaving the 64-execution cap as the only limit.
            _decisionMemoizer.Evict(executionId);
            runFlags?.Clear(executionId);
            // Stop renewing the lease: the step loop has exited, whatever the outcome (S-3: the
            // unsettled-hold sweep trusts it, so a run still executing here must keep it).
            _leases?.Release(executionId);

            // D7b step 4: a runner-backed sandbox session is pinned for the whole execution, so this
            // is the one exit every execution takes and therefore the only place that can end it. It
            // closes the workspace on the runner, releases the execution's runner_sessions slot and
            // seals the workspace journal; it reports and never throws, because this finally decides
            // nothing and must not replace the result the execution already reached.
            if (_sandboxSessions is { } sandboxSessions)
            {
                await sandboxSessions.CloseExecutionAsync(executionId).ConfigureAwait(false);
            }

            if (_slotLeases.TryRemove(executionId, out IDisposable? slot))
            {
                slot.Dispose();
            }

            // F4 / S-50: the run has exited, so end its lease now rather than letting a quick retry wait
            // out the lease window. This is the last step, after the slot is released.
            await EndLeaseQuietlyAsync(executionId);
        }
    }

    /// <summary>
    /// Ends this instance's live lease of an execution whose run has exited. Never throws: if the update
    /// fails the lease simply lapses on its own within the lease window, so an InMemory harness (which
    /// cannot run the conditional update) logs here and carries on.
    /// </summary>
    private async Task EndLeaseQuietlyAsync(Guid executionId)
    {
        if (_leases is null)
        {
            return;
        }

        try
        {
            await _leases.EndLeaseAsync(executionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not end the lease of execution {ExecutionId}; it lapses on its own within the lease window", executionId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// On shutdown, signals in-flight executions (<see cref="ExecutionCancellationRegistry.TryCancelForShutdown"/>)
    /// and gives them the host's shutdown window to unwind. A shutdown-interrupted run is NOT written
    /// <c>Cancelled</c> or <c>Failed</c>: its executor leaves the row <c>Running</c>, <see cref="RunAsync"/>
    /// ends its lease, and <see cref="Services.ExecutionRecoverySweeper"/> (this replica after a restart, or
    /// any other) re-queues it once as an automatic resume, or fails it with a Resume on offer. A run that
    /// does not unwind before the deadline is the same case as a hard kill (the lease lapses).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] inFlight = _running.Values.ToArray();
        if (inFlight.Length == 0)
        {
            await _lifetimeCts.CancelAsync();
            return;
        }

        _logger.LogInformation(
            "WorkflowEngine is shutting down with {Count} execution(s) in flight; signalling cancellation",
            inFlight.Length);

        foreach (Guid executionId in _running.Keys)
        {
            // A shutdown is not a verdict on the run: it is left Running with its lease ended, so the
            // recovery sweeper re-queues it once instead of it ending Cancelled.
            _cancellationRegistry.TryCancelForShutdown(executionId);
        }

        await _lifetimeCts.CancelAsync();

        try
        {
            await Task.WhenAll(inFlight).WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Not all in-flight executions unwound before the shutdown deadline");
        }
    }
}
