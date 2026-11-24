using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services;

/// <summary>
/// The platform's kill switches, as the WorkflowEngine sees them (F11).
///
/// <para>
/// <b>How this reads.</b> One table, <c>platform_settings</c>, owned by the Inference API and read
/// here (and by the Go runner gateway) — see <see cref="PlatformSetting"/> for the key vocabulary. The
/// value is cached for <see cref="PlatformSwitches.CacheTtl"/>, so an operator's change takes effect
/// within ten seconds on every replica without any invalidation channel between processes.
/// </para>
///
/// <para>
/// <b>What happens when the store is unreachable — the whole reason this type is its own class.</b>
/// A kill switch that fails open is worse than no kill switch, because an operator would believe
/// spending had stopped when it had not. So a failed read never produces the previous (or a default)
/// "not paused" answer: <see cref="PlatformSwitchState.Unknown"/> <i>is</i> a paused platform, every
/// decision member on it answers "stop", and a stale value is never served past its TTL. The cost of
/// being wrong in that direction is bounded and recoverable by construction — work that is not
/// admitted stays <c>Queued</c> and is re-published by <c>ExecutionRecoverySweeper</c> — whereas the
/// cost of being wrong in the other direction is unbounded spend nobody agreed to.
/// </para>
///
/// <para>
/// The cold-start answer is the same one, for the same reason: until a read has succeeded, the state
/// is <see cref="PlatformSwitchState.Unknown"/> rather than "unpaused".
/// </para>
/// </summary>
public interface IPlatformSwitches
{
    /// <summary>
    /// The current switch state, from a cache no older than
    /// <see cref="PlatformSwitches.CacheTtl"/>. Never throws for a store failure: an unreadable store
    /// is reported as <see cref="PlatformSwitchState.Unknown"/>, which is paused.
    /// </summary>
    ValueTask<PlatformSwitchState> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A resolved switch state: what is paused right now, and which organisations are disabled.
///
/// <para>
/// There is deliberately no public way to build a state that means "I do not know" other than
/// <see cref="Unknown"/>, and <see cref="Unknown"/> is paused in every direction. The only member that
/// answers false on it is <see cref="IsOrganizationDisabled"/>, and that is not a fail-open choice:
/// the platform flags on <see cref="Unknown"/> already stop everything this engine admits, so the
/// organisation predicate is never the deciding vote here. (It is the deciding vote in the runner
/// gateway's handshake, where a <c>4403</c> is terminal for the runner and is therefore only ever sent
/// on a value that was actually read.)
/// </para>
/// </summary>
public sealed class PlatformSwitchState
{
    private static readonly IReadOnlySet<Guid> NoOrganizations = new HashSet<Guid>();

    private PlatformSwitchState(bool known, bool computePaused, bool platformInferencePaused, IReadOnlySet<Guid> disabledOrganizations)
    {
        Known = known;
        ComputePaused = computePaused;
        PlatformInferencePaused = platformInferencePaused;
        DisabledOrganizations = disabledOrganizations;
    }

    /// <summary>Nothing is paused and no organisation is disabled.</summary>
    public static readonly PlatformSwitchState Running = new(true, false, false, NoOrganizations);

    /// <summary>
    /// The store could not be read (or has not been read yet). Paused in every direction.
    /// </summary>
    public static readonly PlatformSwitchState Unknown = new(false, true, true, NoOrganizations);

    /// <summary>Built from the table's rows. A platform-wide pause is a row at key <c>compute</c>/<c>inference</c> with <c>paused = true</c>.</summary>
    public static PlatformSwitchState From(IEnumerable<PlatformSetting> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        bool computePaused = false;
        bool inferencePaused = false;
        HashSet<Guid>? disabled = null;

        foreach (PlatformSetting row in rows)
        {
            if (!row.Paused)
            {
                // A recorded resume. It is kept in the table for its audit trail and pauses nothing.
                continue;
            }

            switch (row.Key)
            {
                case PlatformSetting.ComputeKey:
                    computePaused = true;
                    break;
                case PlatformSetting.InferenceKey:
                    inferencePaused = true;
                    break;
                default:
                    if (PlatformSetting.TryParseOrganizationKey(row.Key, out Guid organizationId))
                    {
                        (disabled ??= new HashSet<Guid>()).Add(organizationId);
                    }

                    // Any other key is a setting some later work package owns. This reader must not
                    // guess a meaning for it, and must not fail the read because of it.
                    break;
            }
        }

        return new PlatformSwitchState(true, computePaused, inferencePaused, disabled ?? (IReadOnlySet<Guid>)NoOrganizations);
    }

    /// <summary>False only for <see cref="Unknown"/>. Diagnostic and logging only — never a decision input.</summary>
    public bool Known { get; }

    /// <summary>New executions are not admitted and the gateway assigns no job while this is true.</summary>
    public bool ComputePaused { get; }

    /// <summary>The platform's own inference spend is frozen; organisations paying their own provider bill still run.</summary>
    public bool PlatformInferencePaused { get; }

    /// <summary>Organisations an operator has disabled. Empty on <see cref="Unknown"/> — see the class summary.</summary>
    public IReadOnlySet<Guid> DisabledOrganizations { get; }

    /// <summary>Whether <paramref name="organizationId"/> is disabled by an operator.</summary>
    public bool IsOrganizationDisabled(Guid organizationId)
        => DisabledOrganizations.Contains(organizationId);
}

/// <summary>
/// <see cref="IPlatformSwitches"/> over the <c>platform_settings</c> table (see the interface for the
/// semantics, including what an unreadable store means).
/// </summary>
public sealed class PlatformSwitches : IPlatformSwitches
{
    /// <summary>
    /// How long a successful read is served. F11 names ten seconds; it is the entire propagation delay
    /// of an operator's change, which is why there is no cross-process invalidation anywhere.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<WorkflowEngineDbContext> _db;
    private readonly ILogger<PlatformSwitches> _logger;
    private readonly TimeProvider _clock;

    /// <summary>Serializes refreshes so a burst of admissions makes one query, not one each.</summary>
    private readonly SemaphoreSlim _refresh = new(1, 1);

    /// <summary>
    /// The last answer handed out. Starts as <see cref="PlatformSwitchState.Unknown"/>: before the
    /// first successful read this process does not know, and not knowing is paused.
    /// </summary>
    private PlatformSwitchState _state = PlatformSwitchState.Unknown;

    /// <summary>When <see cref="_state"/> stops being servable. <see cref="DateTimeOffset.MinValue"/> forces the first read.</summary>
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    /// <summary>Set while consecutive reads are failing, so the failure is logged on the transition rather than per admission.</summary>
    private bool _failing;

    public PlatformSwitches(
        IDbContextFactory<WorkflowEngineDbContext> db,
        ILogger<PlatformSwitches> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<PlatformSwitchState> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_expires > _clock.GetUtcNow())
        {
            return _state;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited for the lock.
            DateTimeOffset now = _clock.GetUtcNow();
            if (_expires > now)
            {
                return _state;
            }

            try
            {
                await using WorkflowEngineDbContext db = await _db.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                List<PlatformSetting> rows = await db.PlatformSettings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

                _state = PlatformSwitchState.From(rows);
                _expires = now + CacheTtl;

                if (_failing)
                {
                    _failing = false;
                    _logger.LogInformation(
                        "The platform settings store is readable again; compute paused: {ComputePaused}, platform inference paused: {InferencePaused}, disabled organisations: {DisabledCount}",
                        _state.ComputePaused, _state.PlatformInferencePaused, _state.DisabledOrganizations.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The answer is "paused", never the previous value and never a default: see the
                // interface's summary. The TTL still applies to the FAILURE, so a store that is down
                // is asked once every ten seconds rather than once per execution.
                _state = PlatformSwitchState.Unknown;
                _expires = now + CacheTtl;

                if (!_failing)
                {
                    _failing = true;
                    _logger.LogError(
                        ex,
                        "The platform settings store could not be read. Until a read succeeds, compute and platform inference are treated as PAUSED: no new execution is admitted, so nothing starts and nothing is lost.");
                }
            }

            return _state;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
