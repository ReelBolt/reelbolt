using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>Bound from <c>Billing</c> (<c>Billing__*</c> env vars).</summary>
public sealed class CreditGateOptions
{
    public const string SectionName = "Billing";

    /// <summary>False (self-host default): the gate always allows and writes nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>A reservation may be up to this share of its estimate short of the available balance (policy: 20%).</summary>
    public int OverdraftTolerancePercent { get; set; } = 20;

    /// <summary>The overdraft never reaches below minus this many credits (policy: 100).</summary>
    public long OverdraftFloorCredits { get; set; } = 100;
}

/// <summary>
/// <see cref="ICreditGate"/> over <see cref="CreditLedger"/>. The ledger already serialises per org in
/// one transaction under <c>pg_advisory_xact_lock(hashtext(org_id))</c>; this gate adds a per-org
/// in-process semaphore (the same belt-and-braces as <c>VideoGenerationBudgetGate</c>, and the only
/// guarantee on providers without advisory locks), crash-resume via the ledger's idempotency keys,
/// and fail-closed behaviour. Singleton-safe: one short-lived context per call.
/// </summary>
public sealed class CreditGate : ICreditGate
{
    private const long Millis = 1000;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> OrgLocks = new();

    private readonly IDbContextFactory<WorkflowEngineDbContext> _factory;
    private readonly CreditGateOptions _options;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly TimeProvider _time;
    private readonly ILogger<CreditGate> _logger;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _orgLocks;

    public CreditGate(
        IDbContextFactory<WorkflowEngineDbContext> factory,
        IOptions<CreditGateOptions> options,
        ILogger<CreditGate> logger,
        IOrganizationEntitlements? entitlements = null,
        TimeProvider? time = null)
        : this(factory, options, logger, OrgLocks, entitlements, time)
    {
    }

    /// <summary>
    /// A gate with its own in-process lock table. The public constructor shares one static table, so
    /// two gates in one test process serialise on it and never reach the ledger's advisory lock. Tests
    /// that stand for two replicas give each gate its own table, so the ledger's lock and idempotency
    /// keys are what keep the outcome exactly-once.
    /// </summary>
    internal CreditGate(
        IDbContextFactory<WorkflowEngineDbContext> factory,
        IOptions<CreditGateOptions> options,
        ILogger<CreditGate> logger,
        ConcurrentDictionary<Guid, SemaphoreSlim> orgLocks,
        IOrganizationEntitlements? entitlements = null,
        TimeProvider? time = null)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
        _orgLocks = orgLocks;
        _entitlements = entitlements;
        _time = time ?? TimeProvider.System;
    }

    private static string ReserveKey(Guid stepResultId, int attempt) => $"gate-reserve:{stepResultId:N}:{attempt}";

    public async Task<CreditReservation> ReserveAsync(
        Guid organizationId, Guid executionId, Guid stepResultId, long estimateMillis, Guid? projectId = null,
        CreditEntitlement requires = CreditEntitlement.None, int attempt = 0, CancellationToken ct = default,
        long? overdraftBasisMillis = null, Guid? apiKeyId = null)
    {
        if (!_options.Enabled)
        {
            return new CreditReservation(CreditReserveOutcome.Allowed, null, "BILLING_DISABLED");
        }

        if (organizationId == Guid.Empty || stepResultId == Guid.Empty || estimateMillis < 0)
        {
            return Deny("CONFIG_INVALID");
        }

        try
        {
            if (requires != CreditEntitlement.None && _entitlements is not null)
            {
                OrganizationEntitlementSet set = await _entitlements.GetAsync(organizationId, ct).ConfigureAwait(false);
                bool entitled = requires switch
                {
                    CreditEntitlement.Rooms => set.RoomsAllowed,
                    CreditEntitlement.VideoGeneration => set.VideoGenAllowed,
                    CreditEntitlement.AudioGeneration => set.AudioGenAllowed,
                    CreditEntitlement.VideoEnhance => set.VideoEnhanceAllowed,
                    _ => true
                };
                if (!entitled)
                {
                    return new CreditReservation(CreditReserveOutcome.EntitlementDenied, null, $"PLAN_NOT_ENTITLED:{requires}");
                }
            }

            if (estimateMillis == 0)
            {
                return new CreditReservation(CreditReserveOutcome.Allowed, null, "NO_PLATFORM_COST");
            }

            long headroomBasis = Math.Max(estimateMillis, overdraftBasisMillis ?? estimateMillis);
            long headroom = Math.Min(headroomBasis * _options.OverdraftTolerancePercent / 100, _options.OverdraftFloorCredits * Millis);
            using IDisposable orgLock = await LockAsync(organizationId, ct).ConfigureAwait(false);
            await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            CreditLedgerResult r = await new CreditLedger(db, _time).ReserveAsync(
                organizationId, estimateMillis, ReserveKey(stepResultId, attempt),
                new CreditLedgerContext(projectId, executionId, stepResultId, ApiKeyId: apiKeyId),
                minAvailableAfterMillis: -Math.Max(0, headroom), ct: ct).ConfigureAwait(false);

            if (r.Ok)
            {
                return new CreditReservation(CreditReserveOutcome.Allowed, r.Entry!.Id, r.Replayed ? "RESUMED" : null, estimateMillis, r.AvailableMillis);
            }

            return new CreditReservation(CreditReserveOutcome.InsufficientCredits, null, r.Error, estimateMillis, r.AvailableMillis, r.ApiKeyBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: a gate we cannot evaluate must not hand out free compute.
            _logger.LogError(ex, "Credit reservation for step {StepResultId} failed; denying", stepResultId);
            return Deny("GATE_UNAVAILABLE");
        }
    }

    public async Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default)
    {
        if (reservationId is null || !_options.Enabled)
        {
            return new CreditSettlement(true);
        }

        CreditLedgerEntry? reserve = await FindReserveAsync(reservationId.Value, ct).ConfigureAwait(false);
        if (reserve is null)
        {
            return new CreditSettlement(false, CreditLedgerErrors.ReservationNotFound);
        }

        using IDisposable orgLock = await LockAsync(reserve.OrganizationId, ct).ConfigureAwait(false);
        await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        long actual = await AtLeastTheHoldWhenUnpricedAsync(db, reserve, Math.Max(0, actualMillis), ct).ConfigureAwait(false);
        CreditLedgerResult r = await new CreditLedger(db, _time).SettleAsync(
            reserve.OrganizationId, reserve.Id, actual, $"gate-settle:{reserve.Id:N}",
            new CreditLedgerContext(Note: actual > reserve.ReservedDeltaMillis ? "overage" : null), ct).ConfigureAwait(false);
        return r.Ok ? new CreditSettlement(true, ChargedMillis: -(r.Entry?.BalanceDeltaMillis ?? 0)) : new CreditSettlement(false, r.Error);
    }

    public async Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default)
    {
        if (reservationId is null || !_options.Enabled)
        {
            return new CreditSettlement(true);
        }

        CreditLedgerEntry? reserve = await FindReserveAsync(reservationId.Value, ct).ConfigureAwait(false);
        if (reserve is null)
        {
            return new CreditSettlement(false, CreditLedgerErrors.ReservationNotFound);
        }

        using IDisposable orgLock = await LockAsync(reserve.OrganizationId, ct).ConfigureAwait(false);
        await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        CreditLedgerEntry? closing = await db.CreditLedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.RefEntryId == reserve.Id && (e.Kind == CreditLedgerEntryKind.Settle || e.Kind == CreditLedgerEntryKind.Release), ct)
            .ConfigureAwait(false);

        bool refund = IsRefundable(failureClass);
        if (!refund && closing is null)
        {
            actualMillis = await AtLeastTheHoldWhenUnpricedAsync(db, reserve, actualMillis, ct).ConfigureAwait(false);
        }

        CreditLedger ledger = new(db, _time);
        CreditLedgerContext note = new(Note: failureClass.ToString());

        if (closing is null)
        {
            CreditLedgerResult r = refund || actualMillis <= 0
                ? await ledger.ReleaseAsync(reserve.OrganizationId, reserve.Id, $"gate-release:{reserve.Id:N}", note, ct).ConfigureAwait(false)
                : await ledger.SettleAsync(reserve.OrganizationId, reserve.Id, actualMillis, $"gate-settle:{reserve.Id:N}", note, ct).ConfigureAwait(false);
            return r.Ok
                ? new CreditSettlement(true, ChargedMillis: -(r.Entry?.BalanceDeltaMillis ?? 0))
                : new CreditSettlement(false, r.Error);
        }

        if (refund && closing.Kind == CreditLedgerEntryKind.Settle && closing.BalanceDeltaMillis < 0)
        {
            // Already charged, then the step turned out to be our fault: give it back (once, by key).
            CreditLedgerResult r = await ledger.RefundAsync(
                reserve.OrganizationId, closing.Id, -closing.BalanceDeltaMillis, $"gate-refund:{reserve.Id:N}", note, ct).ConfigureAwait(false);
            return r.Ok
                ? new CreditSettlement(true, RefundedMillis: r.Replayed ? 0 : r.Entry?.BalanceDeltaMillis ?? 0)
                : new CreditSettlement(false, r.Error);
        }

        return new CreditSettlement(true);
    }

    public async Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        List<OpenHold> open;
        await using (WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            open = await OpenHolds(db)
                .Where(h => h.ExecutionId == executionId)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        int closed = 0;
        foreach (OpenHold hold in open)
        {
            CreditSettlement s = await CloseOpenHoldAsync(this, hold, ct).ConfigureAwait(false);
            if (s.Ok)
            {
                closed++;
            }
        }

        return closed;
    }

    public async Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new CreditSettlement(true);
        }

        CreditLedgerEntry? reserve = await FindReserveAsync(reserveId, ct).ConfigureAwait(false);
        if (reserve is null)
        {
            return new CreditSettlement(false, CreditLedgerErrors.ReservationNotFound);
        }

        using IDisposable orgLock = await LockAsync(reserve.OrganizationId, ct).ConfigureAwait(false);
        await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        bool closed = await db.CreditLedgerEntries.AsNoTracking()
            .AnyAsync(e => e.RefEntryId == reserve.Id && (e.Kind == CreditLedgerEntryKind.Settle || e.Kind == CreditLedgerEntryKind.Release), ct)
            .ConfigureAwait(false);
        if (closed)
        {
            return new CreditSettlement(true);
        }

        long actual;
        if (recordedMillis is { } recorded)
        {
            actual = await AtLeastTheHoldWhenUnpricedAsync(db, reserve, Math.Max(0, recorded), ct).ConfigureAwait(false);
        }
        else
        {
            // Nothing recorded: what the flushed usage rows say, and never less than the hold (#343 §5.6).
            long metered = await MeteredMillisAsync(db, reserve, ct).ConfigureAwait(false);
            actual = Math.Max(metered, reserve.ReservedDeltaMillis);
        }

        CreditLedgerResult r = await new CreditLedger(db, _time).SettleAsync(
            reserve.OrganizationId, reserve.Id, actual, $"gate-settle:{reserve.Id:N}",
            new CreditLedgerContext(Note: actual > reserve.ReservedDeltaMillis ? "overage" : null), ct).ConfigureAwait(false);
        if (r.Ok)
        {
            return new CreditSettlement(true, ChargedMillis: r.Replayed ? 0 : -(r.Entry?.BalanceDeltaMillis ?? 0));
        }

        // Closed by another replica between our check and the ledger's lock: nothing left to do.
        return r.Error == CreditLedgerErrors.ReservationClosed ? new CreditSettlement(true) : new CreditSettlement(false, r.Error);
    }

    public async Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new CreditSettlement(true);
        }

        CreditLedgerEntry? reserve = await FindReserveAsync(reserveId, ct).ConfigureAwait(false);
        if (reserve is null)
        {
            return new CreditSettlement(false, CreditLedgerErrors.ReservationNotFound);
        }

        using IDisposable orgLock = await LockAsync(reserve.OrganizationId, ct).ConfigureAwait(false);
        await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        bool closed = await db.CreditLedgerEntries.AsNoTracking()
            .AnyAsync(e => e.RefEntryId == reserve.Id && (e.Kind == CreditLedgerEntryKind.Settle || e.Kind == CreditLedgerEntryKind.Release), ct)
            .ConfigureAwait(false);
        if (closed)
        {
            // Already settled or released (by a step, a retry or another replica): left as it is, never refunded.
            return new CreditSettlement(true);
        }

        bool refund = IsRefundable(failureClass);
        long actual = 0;
        if (!refund)
        {
            // What the step recorded, else what its usage rows say; never less than the hold when that usage was unpriced.
            long basis = recordedMillis ?? await MeteredMillisAsync(db, reserve, ct).ConfigureAwait(false);
            actual = await AtLeastTheHoldWhenUnpricedAsync(db, reserve, Math.Max(0, basis), ct).ConfigureAwait(false);
        }

        CreditLedger ledger = new(db, _time);
        CreditLedgerContext note = new(Note: failureClass.ToString());
        CreditLedgerResult r = refund || actual <= 0
            ? await ledger.ReleaseAsync(reserve.OrganizationId, reserve.Id, $"gate-release:{reserve.Id:N}", note, ct).ConfigureAwait(false)
            : await ledger.SettleAsync(reserve.OrganizationId, reserve.Id, actual, $"gate-settle:{reserve.Id:N}", note, ct).ConfigureAwait(false);
        if (r.Ok)
        {
            return new CreditSettlement(true, ChargedMillis: r.Replayed ? 0 : -(r.Entry?.BalanceDeltaMillis ?? 0));
        }

        // Closed by another replica between our check and the ledger's lock: nothing left to do.
        return r.Error == CreditLedgerErrors.ReservationClosed ? new CreditSettlement(true) : new CreditSettlement(false, r.Error);
    }

    /// <summary>A still-open hold and what its step result says about it (all null when there is no step result row).</summary>
    /// <remarks>Init properties, not a positional record, so EF can still filter and order after the projection.</remarks>
    internal sealed record OpenHold
    {
        public Guid ReserveId { get; init; }
        public Guid? ExecutionId { get; init; }
        public DateTime CreatedAt { get; init; }
        public StepStatus? StepStatus { get; init; }
        public FailureClass? FailureClass { get; init; }
        public long? UnsettledMillis { get; init; }
    }

    /// <summary>Reserves with no Settle/Release child, joined to their step result.</summary>
    internal static IQueryable<OpenHold> OpenHolds(WorkflowEngineDbContext db)
        => from e in db.CreditLedgerEntries.AsNoTracking()
           where e.Kind == CreditLedgerEntryKind.Reserve
               && !db.CreditLedgerEntries.Any(c => c.RefEntryId == e.Id
                   && (c.Kind == CreditLedgerEntryKind.Settle || c.Kind == CreditLedgerEntryKind.Release))
           join s in db.WorkflowStepResults.AsNoTracking() on e.StepResultId equals (Guid?)s.Id into steps
           from s in steps.DefaultIfEmpty()
           select new OpenHold
           {
               ReserveId = e.Id,
               ExecutionId = e.ExecutionId,
               CreatedAt = e.CreatedAt,
               StepStatus = s == null ? null : (StepStatus?)s.Status,
               FailureClass = s == null ? null : (FailureClass?)s.FailureClass,
               UnsettledMillis = s == null ? null : s.CreditsUnsettledMillis
           };

    /// <summary>
    /// S-3's one rule for closing a hold its step left open (shared by <see cref="ReleaseExecutionAsync"/>
    /// and <see cref="UnsettledHoldSweeper"/>): a Completed step is SETTLED, never refunded; a Failed
    /// step with a recorded class is closed by that class at what it metered
    /// (<see cref="ICreditGate.ReleaseOpenAsync"/>); anything else (no step row, still Running or Pending,
    /// Skipped, or a failure nobody classified) is released as a platform failure. Only an OPEN hold is
    /// closed: one that already has its settle or release is left as it is.
    /// </summary>
    internal static async Task<CreditSettlement> CloseOpenHoldAsync(ICreditGate gate, OpenHold hold, CancellationToken ct)
    {
        if (hold.StepStatus == StepStatus.Completed)
        {
            return await gate.SettleOpenAsync(hold.ReserveId, hold.UnsettledMillis, ct).ConfigureAwait(false);
        }

        if (hold.StepStatus == StepStatus.Failed && hold.FailureClass is { } cls && cls != FailureClass.None)
        {
            return await gate.ReleaseOpenAsync(hold.ReserveId, cls, hold.UnsettledMillis, ct).ConfigureAwait(false);
        }

        return await gate.ReleaseOpenAsync(hold.ReserveId, FailureClass.PlatformInternal, null, ct).ConfigureAwait(false);
    }

    private static Task<long> MeteredMillisAsync(WorkflowEngineDbContext db, CreditLedgerEntry reserve, CancellationToken ct)
        => reserve.StepResultId is { } step
            ? db.UsageEvents.AsNoTracking().Where(u => u.StepResultId == step).SumAsync(u => u.CreditsMillis, ct)
            : Task.FromResult(0L);

    /// <summary>
    /// a step that metered any PLATFORM unit it could not price (recorded at 0 credits, flagged unpriced) is
    /// charged at least its hold — max(metered, hold) — because its metered sum understates what it really cost. Known
    /// in-process from the step's own usage scope (<see cref="UsageScopeState.SawUnpricedPlatformUsage"/>; the rows themselves are
    /// flushed asynchronously) or, once flushed, from <c>usage_events.unpriced</c> on the step result.
    /// </summary>
    private async Task<long> AtLeastTheHoldWhenUnpricedAsync(
        WorkflowEngineDbContext db, CreditLedgerEntry reserve, long actualMillis, CancellationToken ct)
    {
        long hold = reserve.ReservedDeltaMillis;
        if (actualMillis >= hold) return actualMillis;

        UsageScopeState? scope = UsageScope.Current;
        bool unpriced = (scope is { SawUnpricedPlatformUsage: true } && (scope.StepResultId is null || scope.StepResultId == reserve.StepResultId))
            || (reserve.StepResultId is { } step && await db.UsageEvents.AsNoTracking()
                .AnyAsync(e => e.StepResultId == step && e.Unpriced, ct).ConfigureAwait(false));
        if (!unpriced) return actualMillis;

        _logger.LogWarning(
            "Step result {StepResultId} metered unpriced platform usage; charging its hold of {HoldMillis} millicredits instead of the metered {MeteredMillis}",
            reserve.StepResultId, hold, actualMillis);
        return hold;
    }

    /// <summary>Platform-caused classes and anything we cannot attribute (D9) are on us.</summary>
    internal static bool IsRefundable(FailureClass c) => c switch
    {
        FailureClass.UserInput or FailureClass.UserCancelled or FailureClass.ByoProviderError
            or FailureClass.InsufficientCredits or FailureClass.EntitlementDenied => false,
        // D12: a runner that produced nothing, over-sized, unreadable, wrong-shaped or blank output
        // burned the customer's own machine, not ours — and refunding it would pay a modified runner
        // for failing verification. Listed explicitly rather than by falling through to the default:
        // the default is "refund", which is the right answer for an unattributable failure and the
        // wrong one for this.
        FailureClass.Runner => false,
        _ => true
    };

    private async Task<CreditLedgerEntry?> FindReserveAsync(Guid id, CancellationToken ct)
    {
        await using WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.CreditLedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.Kind == CreditLedgerEntryKind.Reserve, ct).ConfigureAwait(false);
    }

    private static CreditReservation Deny(string reason) => new(CreditReserveOutcome.InsufficientCredits, null, reason);

    private async Task<IDisposable> LockAsync(Guid orgId, CancellationToken ct)
    {
        SemaphoreSlim gate = _orgLocks.GetOrAdd(orgId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>
/// F4's hook: an orphaned execution ended by the platform releases every hold it still has, so the
/// org is never charged for work the platform lost.
/// </summary>
public sealed class CreditPlatformFailureHook(ICreditGate gate, ILogger<CreditPlatformFailureHook> logger) : IPlatformFailureHook
{
    public async Task OnPlatformFailureAsync(WorkflowExecution execution, string reason, CancellationToken ct)
    {
        int n = await gate.ReleaseExecutionAsync(execution.Id, ct).ConfigureAwait(false);
        if (n > 0)
        {
            logger.LogInformation("Released {Count} credit hold(s) of platform-failed execution {ExecutionId}", n, execution.Id);
        }
    }
}
