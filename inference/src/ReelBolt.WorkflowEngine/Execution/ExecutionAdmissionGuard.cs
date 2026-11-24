using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>Why an execution was not admitted (F11). The names are the metric tag values and the log tokens.</summary>
public enum AdmissionRefusal
{
    /// <summary>Admitted. Nothing was refused.</summary>
    None,

    /// <summary><c>compute</c> is paused: no new execution starts anywhere on the platform.</summary>
    ComputePaused,

    /// <summary><c>inference</c> is paused AND this organisation has no inference providers of its own.</summary>
    PlatformInferencePaused,

    /// <summary>An operator disabled this organisation (<c>org:&lt;guid&gt;</c>).</summary>
    OrganizationDisabled,

    /// <summary>The organisation is already running as many executions as its plan allows on other replicas.</summary>
    OrganizationConcurrencyLimit,

    /// <summary>The settings store could not be read. Treated as paused — see <see cref="IPlatformSwitches"/>.</summary>
    SettingsUnavailable,

    /// <summary>This engine instance is already running (or admitting) this execution id. Decided by WorkflowExecutionRunner before the admission guard; not a platform deferral, never counted on executions_deferred.</summary>
    AlreadyRunningHere,
}

/// <summary>The outcome of one admission check.</summary>
public readonly record struct AdmissionDecision(bool Admitted, AdmissionRefusal Refusal)
{
    /// <summary>Admit the execution.</summary>
    public static readonly AdmissionDecision Admit = new(true, AdmissionRefusal.None);

    /// <summary>Do not dispatch; the execution stays <c>Queued</c> and is retried later.</summary>
    public static AdmissionDecision Refuse(AdmissionRefusal refusal) => new(false, refusal);

    /// <summary>The stable token logged and used as the metric tag.</summary>
    public string Reason => Refusal switch
    {
        AdmissionRefusal.ComputePaused => "compute_paused",
        AdmissionRefusal.PlatformInferencePaused => "platform_inference_paused",
        AdmissionRefusal.OrganizationDisabled => "organization_disabled",
        AdmissionRefusal.OrganizationConcurrencyLimit => "organization_concurrency",
        AdmissionRefusal.SettingsUnavailable => "settings_unavailable",
        AdmissionRefusal.AlreadyRunningHere => "already_running_here",
        _ => "admitted",
    };
}

/// <summary>
/// Decides whether an execution may start (F11), in the one place every execution passes through
/// before it is handed to the background runner.
///
/// <para>
/// <b>Refusing is not failing.</b> A refused execution is left untouched in the database as
/// <c>Queued</c> and its message is acknowledged without being consumed; <c>ExecutionRecoverySweeper</c>
/// re-publishes a Queued row that has been sitting for its stale window, so "wait" is a real state
/// rather than a lost request. That is why every check here answers by <i>not dispatching</i> instead
/// of by throwing: throwing would push the message through MassTransit's retries into the dead-letter
/// queue, where it would need a human to replay it.
/// </para>
///
/// <para>
/// <b>Order matters.</b> The platform switches are asked first, and they are the cheapest check (one
/// cached read per ten seconds for the whole process). The organisation-level checks follow, and the
/// only one that touches the database per call is the cross-replica concurrency count.
/// </para>
///
/// <para>
/// <b>What is deliberately NOT here.</b> Nothing interrupts an execution that is already running: a
/// pause stops admission, not work in flight. An operator who needs a run stopped stops it (the
/// existing stop endpoint), and the credit gate still bounds its spend — a switch that killed
/// half-finished runs would trade a bounded overspend for destroyed work.
/// </para>
/// </summary>
public sealed class ExecutionAdmissionGuard
{
    private readonly IPlatformSwitches _switches;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionLeaseService? _leases;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly ILogger<ExecutionAdmissionGuard> _logger;

    public ExecutionAdmissionGuard(
        IPlatformSwitches switches,
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutionAdmissionGuard> logger,
        ExecutionLeaseService? leases = null,
        IOrganizationEntitlements? entitlements = null)
    {
        _switches = switches;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _leases = leases;
        _entitlements = entitlements;
    }

    /// <summary>
    /// Whether <paramref name="executionId"/> (belonging to <paramref name="organizationId"/>) may be
    /// dispatched now. Never throws for a store failure: an unreadable store refuses, it does not
    /// admit by default.
    /// </summary>
    public async Task<AdmissionDecision> CheckAsync(Guid executionId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        PlatformSwitchState switches = await _switches.GetAsync(cancellationToken).ConfigureAwait(false);

        // An unreadable store IS a paused platform (IPlatformSwitches): reported with its own reason so
        // an operator can tell "someone paused this" apart from "the settings table is gone", which
        // want different responses.
        if (!switches.Known)
        {
            ReelBoltDiagnostics.SwitchStoreUnreadable.Add(1);
            return Refuse(executionId, organizationId, AdmissionRefusal.SettingsUnavailable);
        }

        if (switches.ComputePaused)
        {
            return Refuse(executionId, organizationId, AdmissionRefusal.ComputePaused);
        }

        if (organizationId != Guid.Empty)
        {
            if (switches.IsOrganizationDisabled(organizationId))
            {
                return Refuse(executionId, organizationId, AdmissionRefusal.OrganizationDisabled);
            }

            if (switches.PlatformInferencePaused && !await BringsOwnInferenceAsync(organizationId, cancellationToken).ConfigureAwait(false))
            {
                return Refuse(executionId, organizationId, AdmissionRefusal.PlatformInferencePaused);
            }

            AdmissionDecision? concurrency = await CheckOrganizationConcurrencyAsync(executionId, organizationId, cancellationToken).ConfigureAwait(false);
            if (concurrency is { } decision)
            {
                return decision;
            }
        }

        return AdmissionDecision.Admit;
    }

    /// <summary>
    /// Whether the organisation pays for its own inference, which is what keeps it running while
    /// <c>inference</c> is paused.
    ///
    /// <para>
    /// The test is "this organisation has at least one <c>inference_providers</c> row of its own". It
    /// is deliberately the permissive direction and it is a proxy, not a proof: a run of an
    /// organisation that owns one provider but resolves some other capability to a platform-managed
    /// row still spends the platform's budget. Resolving that exactly means resolving every
    /// capability every agent of the workflow can reach, at admission, before the workflow's steps
    /// are even loaded — see docs/platform-kill-switches.md "What the switches do not do". The
    /// alternative proxy (assume every run is platform-funded) would stop organisations that pay their
    /// own provider bill, which is the one thing this switch exists not to do.
    /// </para>
    /// </summary>
    private async Task<bool> BringsOwnInferenceAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext? db = scope.ServiceProvider.GetService<WorkflowEngineDbContext>();
            if (db is null)
            {
                return false;
            }

            return await db.InferenceProviders.AsNoTracking()
                .AnyAsync(p => p.OrganizationId == organizationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreadable means "we cannot show that this organisation pays its own bill", so it is
            // refused. Failing open here would let a store outage quietly spend the platform's money.
            _logger.LogError(ex, "Could not read the inference providers of organization {OrganizationId}; treating it as platform-funded", organizationId);
            return false;
        }
    }

    /// <summary>
    /// The cross-replica half of the per-organisation concurrency limit. The process-local cap is
    /// <see cref="FairShareAdmission"/> and it is what makes a same-replica run wait promptly; this
    /// counts the org's <c>Running</c> rows held by <i>other</i> replicas, which no in-process
    /// semaphore can see. Null means "do not refuse for this reason" (no cap, or no database to
    /// count against).
    /// </summary>
    private async Task<AdmissionDecision?> CheckOrganizationConcurrencyAsync(Guid executionId, Guid organizationId, CancellationToken cancellationToken)
    {
        int cap = _entitlements is null
            ? OrganizationEntitlementSet.UnlimitedInt
            : await _entitlements.MaxConcurrentExecutionsAsync(organizationId, cancellationToken).ConfigureAwait(false);

        if (cap >= OrganizationEntitlementSet.UnlimitedInt)
        {
            return null;
        }

        int runningElsewhere;
        try
        {
            Guid[] mine = _leases?.TrackedIds.ToArray() ?? [];

            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext? db = scope.ServiceProvider.GetService<WorkflowEngineDbContext>();
            if (db is null)
            {
                return null;
            }

            runningElsewhere = await db.WorkflowExecutions.AsNoTracking()
                .CountAsync(
                    e => e.OrganizationId == organizationId
                         && e.Status == ExecutionStatus.Running
                         && !mine.Contains(e.Id),
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The cap could not be checked, so it is not known to be exceeded... which would be the
            // wrong direction to guess. Refuse: the execution stays Queued, which is exactly what an
            // organisation at its limit would do anyway, and the sweeper retries it.
            _logger.LogError(ex, "Could not count the running executions of organization {OrganizationId}; refusing admission", organizationId);
            return Refuse(executionId, organizationId, AdmissionRefusal.SettingsUnavailable);
        }

        // The local cap is a floor of 1 (a plan may not starve its own work) and is enforced by
        // FairShareAdmission for this replica's own runs; only the rest is counted here.
        return runningElsewhere >= Math.Max(1, cap)
            ? Refuse(executionId, organizationId, AdmissionRefusal.OrganizationConcurrencyLimit)
            : null;
    }

    private AdmissionDecision Refuse(Guid executionId, Guid organizationId, AdmissionRefusal refusal)
    {
        AdmissionDecision decision = AdmissionDecision.Refuse(refusal);
        ReelBoltDiagnostics.ExecutionsDeferred.Add(1, new KeyValuePair<string, object?>("reason", decision.Reason));
        _logger.LogInformation(
            "Not admitting execution {ExecutionId} of organization {OrganizationId}: {Reason}. It stays Queued and will be retried.",
            executionId, organizationId, decision.Reason);
        return decision;
    }
}
