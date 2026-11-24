using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Observability;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// Bound from <c>Billing:SettleSweep</c> (<c>Billing__SettleSweep__*</c>). Every value but
/// <see cref="Enabled"/> must lie within its range (see <see cref="SettleSweepServiceCollectionExtensions.AddSettleSweepOptions"/>),
/// or startup fails naming the key and its range.
/// </summary>
public sealed class SettleSweepOptions
{
    public const string SectionName = "Billing:SettleSweep";

    /// <summary>Runs the sweep (only ever while <c>Billing:Enabled</c> is also true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds from the start of one pass to the start of the next (a fixed-rate timer). Range 1..86400.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Seconds after startup before the first pass. Range 1..3600.</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>A hold younger than this many seconds is never swept (its step may still be closing it). Range 1..86400.</summary>
    public int MinAgeSeconds { get; set; } = 600;

    /// <summary>Most holds one pass closes, oldest first; the rest wait for the next pass. Range 1..10000.</summary>
    public int MaxPerSweep { get; set; } = 100;

    /// <summary>
    /// A hold whose step result still says Running (or Pending) is swept only once it is older than this
    /// many seconds AND its execution's lease has been lapsed for at least <see cref="MinAgeSeconds"/>. Must
    /// exceed the longest a hold can legitimately live: every retry attempt of a step shares one hold, 6
    /// attempts × 6 h. Range 129600..1209600.
    /// </summary>
    public int RunningStepCeilingSeconds { get; set; } = 172800;
}

public static class SettleSweepServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="SettleSweepOptions"/> and fails startup on a value outside its range, naming only
    /// the key and the range (<c>Billing:SettleSweep:&lt;Key&gt; must be between &lt;Min&gt; and &lt;Max&gt;.</c>).
    /// </summary>
    public static OptionsBuilder<SettleSweepOptions> AddSettleSweepOptions(this IServiceCollection services, IConfiguration configuration)
        => services.AddOptions<SettleSweepOptions>()
            .Bind(configuration.GetSection(SettleSweepOptions.SectionName))
            .Validate(o => InRange(o.IntervalSeconds, 1, 86400), Range(nameof(SettleSweepOptions.IntervalSeconds), 1, 86400))
            .Validate(o => InRange(o.StartupDelaySeconds, 1, 3600), Range(nameof(SettleSweepOptions.StartupDelaySeconds), 1, 3600))
            .Validate(o => InRange(o.MinAgeSeconds, 1, 86400), Range(nameof(SettleSweepOptions.MinAgeSeconds), 1, 86400))
            .Validate(o => InRange(o.MaxPerSweep, 1, 10000), Range(nameof(SettleSweepOptions.MaxPerSweep), 1, 10000))
            .Validate(o => InRange(o.RunningStepCeilingSeconds, 129600, 1209600), Range(nameof(SettleSweepOptions.RunningStepCeilingSeconds), 129600, 1209600))
            .ValidateOnStart();

    private static bool InRange(int value, int min, int max) => value >= min && value <= max;

    private static string Range(string name, int min, int max) => $"{SettleSweepOptions.SectionName}:{name} must be between {min} and {max}.";
}

/// <summary>
/// S-3: closes every credit hold a step left open once that step, or its whole run, is over — the
/// backstop for a settle whose three in-process attempts all failed (the step then recorded what it
/// metered in <see cref="WorkflowStepResult.CreditsUnsettledMillis"/>), for a successful run (which
/// has no run-level release), and for a process that died between a step's end and its settle.
/// <para>
/// <b>What is swept.</b> A Reserve with no Settle/Release child, older than
/// <see cref="SettleSweepOptions.MinAgeSeconds"/>, in one of three cases. (1) Its step result is terminal
/// (Completed, Failed, Skipped): it is closed by the step's own rule. (2) It has no step result row and its
/// execution is terminal (Passed, Failed, Cancelled). (3) Its step result still says Running or Pending: it is
/// swept only once it is older than <see cref="SettleSweepOptions.RunningStepCeilingSeconds"/>, this process
/// does not track its execution, and its execution's lease has lapsed for at least
/// <see cref="SettleSweepOptions.MinAgeSeconds"/>. A running step under a live lease is never touched, whatever
/// its execution's status says: a stop handled on another replica only writes Cancelled, while the owning
/// engine still runs the step and renews the lease. A case-3 hold is logged and counted (<c>phase=orphan</c>).
/// Oldest first, at most <see cref="SettleSweepOptions.MaxPerSweep"/> per pass.
/// </para>
/// <para>
/// <b>How.</b> By the one rule <see cref="ICreditGate.ReleaseExecutionAsync"/> also uses: a Completed
/// step is settled at its recorded amount, else at max(its metered usage, the hold), never refunded; a
/// Failed step with a class is closed by that class; anything else is released as a platform failure.
/// </para>
/// <para>
/// <b>Many replicas.</b> Every replica runs it with no coordination of its own: the gate reuses the
/// ledger's <c>gate-settle:</c>/<c>gate-release:</c> keys under the per-org advisory lock, so a hold two
/// passes race for is closed once and the second pass replays or finds it closed. One hold that cannot
/// be closed is counted (<c>reelbolt.billing.unsettled_holds</c>, <c>phase=sweep</c>) and retried next
/// pass; it never stops the pass.
/// </para>
/// </summary>
public sealed class UnsettledHoldSweeper : BackgroundService
{
    private readonly IDbContextFactory<WorkflowEngineDbContext> _factory;
    private readonly ICreditGate _gate;
    private readonly SettleSweepOptions _options;
    private readonly CreditGateOptions _billing;
    private readonly ILogger<UnsettledHoldSweeper> _logger;
    private readonly TimeProvider _time;
    private readonly ExecutionLeaseService? _leases;

    public UnsettledHoldSweeper(
        IDbContextFactory<WorkflowEngineDbContext> factory,
        ICreditGate gate,
        IOptions<SettleSweepOptions> options,
        IOptions<CreditGateOptions> billing,
        ILogger<UnsettledHoldSweeper> logger,
        TimeProvider? time = null,
        ExecutionLeaseService? leases = null)
    {
        _factory = factory;
        _gate = gate;
        _options = options.Value;
        _billing = billing.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _leases = leases;
    }

    /// <summary>One pass. Returns how many holds it closed (a hold another replica closed first counts too).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (!_billing.Enabled || !_options.Enabled)
        {
            return 0;
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime cutoff = now.AddSeconds(-_options.MinAgeSeconds);
        DateTime ceiling = now.AddSeconds(-_options.RunningStepCeilingSeconds);
        Guid?[] mine = _leases is null ? [] : _leases.TrackedIds.Select(id => (Guid?)id).ToArray();
        List<CreditGate.OpenHold> due;
        await using (WorkflowEngineDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            due = await CreditGate.OpenHolds(db)
                .Where(h => h.CreatedAt < cutoff
                    && (h.StepStatus == StepStatus.Completed || h.StepStatus == StepStatus.Failed || h.StepStatus == StepStatus.Skipped
                        || (h.StepStatus == null
                            && db.WorkflowExecutions.Any(x => x.Id == h.ExecutionId
                                && (x.Status == ExecutionStatus.Passed || x.Status == ExecutionStatus.Failed || x.Status == ExecutionStatus.Cancelled)))
                        || ((h.StepStatus == StepStatus.Running || h.StepStatus == StepStatus.Pending)
                            && h.CreatedAt < ceiling
                            && !mine.Contains(h.ExecutionId)
                            && !db.WorkflowExecutions.Any(x => x.Id == h.ExecutionId && x.LeaseExpiresAt != null && x.LeaseExpiresAt >= cutoff))))
                .OrderBy(h => h.CreatedAt)
                .Take(_options.MaxPerSweep)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        int closed = 0;
        foreach (CreditGate.OpenHold hold in due)
        {
            try
            {
                CreditSettlement s = await CreditGate.CloseOpenHoldAsync(_gate, hold, ct).ConfigureAwait(false);
                if (s.Ok)
                {
                    closed++;
                    if (hold.StepStatus is StepStatus.Running or StepStatus.Pending)
                    {
                        _logger.LogWarning(
                            "Unsettled-hold sweep released reservation {ReservationId} of execution {ExecutionId} as PlatformInternal: its step result is still {StepStatus}, the execution lease has lapsed and the hold is older than {CeilingSeconds} s",
                            hold.ReserveId, hold.ExecutionId, hold.StepStatus, _options.RunningStepCeilingSeconds);
                        ReelBoltDiagnostics.UnsettledHolds.Add(1, new KeyValuePair<string, object?>("phase", "orphan"));
                    }

                    continue;
                }

                _logger.LogWarning("Unsettled-hold sweep could not close reservation {ReservationId}: {Error}", hold.ReserveId, s.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unsettled-hold sweep could not close reservation {ReservationId}", hold.ReserveId);
            }

            ReelBoltDiagnostics.UnsettledHolds.Add(1, new KeyValuePair<string, object?>("phase", "sweep"));
        }

        if (due.Count > 0)
        {
            _logger.LogInformation("Unsettled-hold sweep closed {Closed} of {Due} open credit hold(s)", closed, due.Count);
        }

        return closed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_billing.Enabled || !_options.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), _time, stoppingToken).ConfigureAwait(false);
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(_options.IntervalSeconds), _time);
            do
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Unsettled-hold sweep pass failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
