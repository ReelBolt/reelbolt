using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// The output-moderation lane: ONE consumer judges one output at a time, so moderation never
/// competes with requests, and a sweep offers work only while the lane is idle.
/// </summary>
/// <remarks>
/// The step-completed consumer and the views offer outputs; the sweep is both the one paced backfill
/// of outputs that never had a verdict and the recovery of offers lost with a restart. A decided
/// verdict is never offered again by age: only changed bytes, or a recorded outage whose retry time
/// has come, are judged again.
/// </remarks>
public sealed class OutputModerationWorker : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The hard upper bound on the wait between two sweeps when nothing is found. The effective ceiling is
    /// <c>ContentModeration:MaxIdleSweepSeconds</c> (default 60 s, see <see cref="OutputModerationTiming.IdleSweepCeiling"/>):
    /// ten minutes left a cap-refused burst unseen for minutes, and the sweep is a cheap existence query. The long
    /// bound is kept only for an operator who deliberately configures it. Recent rows still keep <see cref="SweepInterval"/>.
    /// </summary>
    internal static readonly TimeSpan MaxIdleSweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>The most candidates one organization contributes to a sweep, so a big backlog cannot fill the window.</summary>
    internal const int PerOrganizationWindow = 3;

    private bool _lastSweepFoundNothing;
    private bool _lastSweepSawRecentRow;

    /// <summary>True when the last sweep ran on an idle lane and found no candidate (the loop then waits longer).</summary>
    internal bool LastSweepFoundNothing => _lastSweepFoundNothing;

    /// <summary>True when the last sweep saw an output produced within <see cref="RecentRowThreshold"/>.</summary>
    internal bool LastSweepSawRecentRow => _lastSweepSawRecentRow;

    /// <summary>The age under which a produced output keeps the sweep on its short interval (config, default 5 min).</summary>
    internal TimeSpan RecentRowThreshold => _recentRowThreshold;

    /// <summary>
    /// The wait before the next sweep. A recent output means the lane must come back at <see cref="SweepInterval"/>
    /// even if this sweep offered nothing (every row was already judged or cooling): a fresh output can be waiting
    /// behind a refused offer. Only a lane whose newest work is older than the recent-row threshold may back off, and
    /// never past <paramref name="ceiling"/> (the configurable idle ceiling, 60 s by default; the hard maximum when omitted).
    /// </summary>
    internal static TimeSpan NextSweepInterval(bool foundNothing, bool sawRecentRow, TimeSpan current, TimeSpan? ceiling = null)
    {
        if (!foundNothing || sawRecentRow) return SweepInterval;
        TimeSpan max = ceiling ?? MaxIdleSweepInterval;
        return TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks));
    }

    private TimeSpan _idleInterval = SweepInterval;
    private readonly TimeSpan _recentRowThreshold;
    private readonly TimeSpan _idleCeiling;

    /// <summary>The effective ceiling for the idle sleep: the configured value, clamped to 30 s..<see cref="MaxIdleSweepInterval"/>.</summary>
    internal TimeSpan IdleCeiling => _idleCeiling;

    /// <summary>How many outputs one sweep offers.</summary>
    internal const int SweepBatch = 2;

    /// <summary>
    /// An output the sweep offered is not offered again for this long when the run wrote nothing (a missing object, or a
    /// project that is gone, leaves no row to stop it). An offer that ended in a stored row (a verdict, or an outage with
    /// its own retry time) or in a CSAM hold is forgotten at once: the row, or the hold lifting, decides when it is due.
    /// </summary>
    internal static readonly TimeSpan OfferCooldown = TimeSpan.FromMinutes(10);

    private readonly IBackgroundTaskQueue<OutputModerationTask> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ContentModerationOptions> _options;
    private readonly ILogger<OutputModerationWorker> _logger;
    private readonly bool _csamEnabled;
    private readonly IOutputModerationEnqueuer? _enqueuer;
    private readonly ICsamScanRequests? _csamScans;
    private readonly IOutputModerationWake? _wake;

    // Written by the sweep and by the consumer (which forgets an offer once its row or hold governs it).
    private readonly ConcurrentDictionary<OutputModerationTask, DateTime> _recentlyOffered = new();

    public OutputModerationWorker(
        IBackgroundTaskQueue<OutputModerationTask> queue,
        IServiceScopeFactory scopeFactory,
        IOptions<ContentModerationOptions> options,
        ILogger<OutputModerationWorker> logger,
        IOptions<CsamOptions>? csamOptions = null,
        IOutputModerationEnqueuer? enqueuer = null,
        IConfiguration? configuration = null,
        ICsamScanRequests? csamScans = null,
        IOutputModerationWake? wake = null)
    {
        _recentRowThreshold = OutputModerationTiming.RecentRowThreshold(configuration);
        _idleCeiling = OutputModerationTiming.IdleSweepCeiling(configuration);
        _enqueuer = enqueuer;
        _csamScans = csamScans;
        _wake = wake;
        OutputModerationDiagnostics.DepthProbe = () => queue.TrackedCount;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _csamEnabled = csamOptions?.Value.Arachnid.Enabled ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Output moderation worker started with one consumer");
        await Task.WhenAll(SweepLoopAsync(stoppingToken), ConsumerLoopAsync(stoppingToken));
    }

    /// <summary>The single consumer loop. Exposed for tests: handles exactly one queued task.</summary>
    internal async Task<bool> RunOneAsync(OutputModerationTask task, CancellationToken ct)
    {
        bool reoffer = false;
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            OutputModerationProcessor processor =
                scope.ServiceProvider.GetRequiredService<OutputModerationProcessor>();
            OutputModerationRunOutcome outcome = await processor.ProcessForOutcomeAsync(task, ct);
            reoffer = outcome == OutputModerationRunOutcome.Reoffer;

            switch (outcome)
            {
                case OutputModerationRunOutcome.Held:
                    // Nothing may go to the vendor until the project's pending CSAM scans have verdicts. Ask for them now
                    // rather than at the CSAM sweep's next interval (15 minutes by default, the wait every new render had);
                    // the scan wakes the sweep when it is done. A quarantine or the output marker stays held: the scan
                    // finds no pending row there and changes nothing. The held offer is forgotten, so the sweep (which
                    // skips held projects) offers it again as soon as the hold lifts.
                    _csamScans?.Request(task.ProjectId);
                    _recentlyOffered.TryRemove(task, out _);
                    break;
                case OutputModerationRunOutcome.Stored:
                    // The row now decides: a verdict is never offered again by age, an outage at its own retry time.
                    _recentlyOffered.TryRemove(task, out _);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A container, scope or storage failure must not kill the lane; the sweep finds the output again.
            _logger.LogError(ex, "Output moderation failed for one output");
        }
        finally
        {
            _queue.Complete(task);
            _enqueuer?.Released(task);
        }

        // The offer is released first: the same task is tracked until now, so it could not queue itself.
        if (reoffer) _queue.TryQueue(task);
        return reoffer;
    }

    private async Task ConsumerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            OutputModerationTask task;
            try
            {
                task = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RunOneAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int offered = await SweepOnceAsync(stoppingToken);
                if (offered > 0)
                    _logger.LogInformation("Output moderation sweep offered {Count} output(s)", offered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Output moderation sweep failed; will retry after the interval");
            }

            // Work found, or an output produced a moment ago: back to the short interval. Only a lane whose newest
            // work is older than the recent-row threshold waits longer each time.
            _idleInterval = NextSweepInterval(_lastSweepFoundNothing, _lastSweepSawRecentRow, _idleInterval, _idleCeiling);

            // A wake (a CSAM scan finished, so held outputs can be judged) ends the sleep at once.
            try
            {
                if (_wake is null) await Task.Delay(_idleInterval, stoppingToken);
                else await _wake.WaitAsync(_idleInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The sweep stands back only when the lane is crowded. A single busy tenant can never crowd it by itself (its views
    /// are capped per organization), so one tenant's activity cannot stop the sweep for everyone.
    /// </summary>
    internal const int SweepQueueCeiling = 4 * OutputModerationEnqueuer.MaxQueuedPerOrganization;

    /// <summary>
    /// The sweep looks for never-judged outputs only among those completed this recently (lost offers, a restart). An
    /// older output that never got a verdict is still judged the moment anything reads it. The bound is what keeps the
    /// idle query cheap: at 200,000 step results the unbounded form read about 360,000 buffers (about 300 ms on a
    /// throwaway Postgres), this one about 40,000 (about 100 ms).
    /// </summary>
    internal static readonly TimeSpan FreshWindow = TimeSpan.FromDays(30);

    /// <summary>How long a served organization is remembered for the rotation.</summary>
    private static readonly TimeSpan ServedMemory = TimeSpan.FromHours(6);

    private readonly Dictionary<Guid, DateTime> _lastServed = new();

    /// <summary>
    /// One sweep, exposed for tests. Offers nothing while moderation is off or the lane is crowded.
    /// Otherwise offers up to <see cref="SweepBatch"/> outputs: organizations take turns, the one served
    /// longest ago (or never) first, and within an organization a recorded outage whose retry time has
    /// come goes before completed step results with an output that has no verdict row (newest first). A row
    /// at any version, once decided, is never offered by age. Returns how many were offered.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        _lastSweepFoundNothing = false;
        _lastSweepSawRecentRow = false;
        if (!_options.Value.Enabled) return 0;
        if (_queue.TrackedCount >= SweepQueueCeiling) return 0;

        DateTime now = DateTime.UtcNow;
        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

        foreach (OutputModerationTask stale in _recentlyOffered
                     .Where(kv => now - kv.Value > OfferCooldown).Select(kv => kv.Key).ToList())
        {
            _recentlyOffered.TryRemove(stale, out _);
        }

        foreach (Guid forgotten in _lastServed.Where(kv => now - kv.Value > ServedMemory).Select(kv => kv.Key).ToList())
            _lastServed.Remove(forgotten);

        // Keys offered a moment ago stay out of the SQL window, so unjudgeable rows cannot fill it.
        string[] cooling = _recentlyOffered.Keys.Select(k => k.StorageKey).ToArray();
        var held = OutputModerationHold.FilePredicate(_csamEnabled);
        var marked = OutputModerationHold.ProjectMarkerPredicate();

        int offered = 0;
        bool anyCandidate = false;
        using (tenant.System())
        {
            // Is there work fresh enough that a refused offer could still be waiting? Judged or not: while a recent
            // output exists the sweep keeps its short interval, so a row the consumer could not queue is found in the
            // next round rather than after the idle backoff.
            DateTime recentSince = now - _recentRowThreshold;
            _lastSweepSawRecentRow = await db.WorkflowStepResults
                .AsNoTracking()
                .AnyAsync(r => r.Status == StepStatus.Completed
                               && r.OutputStorageKey != null
                               && (r.CompletedAt ?? r.ExecutedAt) >= recentSince
                               && (r.OutputStorageKey.StartsWith("projects/" + r.WorkflowExecution.ProjectId.ToString() + "/outputFiles/")
                                   || r.OutputStorageKey.StartsWith("projects/" + r.WorkflowExecution.ProjectId.ToString() + "/agentFiles/generated/")),
                    ct);

            // A recorded outage whose retry time has come (a certain refusal has a "never" time, and the sweep
            // stops once next_attempt_at is null).
            IQueryable<OutputModerationVerdict> dueQuery = db.OutputModerationVerdicts
                .AsNoTracking()
                .Where(v => v.State == nameof(ModerationAction.Unavailable)
                            && v.NextAttemptAt != null && v.NextAttemptAt <= now
                            && !cooling.Contains(v.StorageKey)
                            && !db.ProjectFiles.IgnoreQueryFilters()
                                .Where(f => f.ProjectId == v.ProjectId).Any(held)
                            && !db.Projects.IgnoreQueryFilters().Where(p => p.Id == v.ProjectId).Any(marked));

            // Completed outputs that never had a verdict, newest first (backfill and lost offers).
            DateTime since = now - FreshWindow;
            var freshQuery = db.WorkflowStepResults
                .AsNoTracking()
                .Where(r => r.Status == StepStatus.Completed
                            && r.OutputStorageKey != null
                            && (r.CompletedAt ?? r.ExecutedAt) >= since
                            && (r.OutputStorageKey.StartsWith("projects/" + r.WorkflowExecution.ProjectId.ToString() + "/outputFiles/")
                                || r.OutputStorageKey.StartsWith("projects/" + r.WorkflowExecution.ProjectId.ToString() + "/agentFiles/generated/"))
                            && !cooling.Contains(r.OutputStorageKey)
                            && !db.OutputModerationVerdicts.Any(v =>
                                v.ProjectId == r.WorkflowExecution.ProjectId && v.StorageKey == r.OutputStorageKey)
                            && !db.ProjectFiles.IgnoreQueryFilters()
                                .Where(f => f.ProjectId == r.WorkflowExecution.ProjectId).Any(held)
                            && !db.Projects.IgnoreQueryFilters().Where(p => p.Id == r.WorkflowExecution.ProjectId).Any(marked))
                .Select(r => new
                {
                    r.WorkflowExecution.ProjectId,
                    Key = r.OutputStorageKey!,
                    Org = r.WorkflowExecution.OrganizationId,
                    At = r.CompletedAt ?? r.ExecutedAt
                });

            // EVERY organization with something waiting, not the newest few: a window by recency lets two busy
            // tenants keep a third out for as long as they have work. The order is the rotation.
            Dictionary<Guid, DateTime> waiting = [];
            foreach (var d in await dueQuery.GroupBy(v => v.OrganizationId)
                         .Select(g => new { Org = g.Key, At = g.Min(v => v.NextAttemptAt!.Value) }).ToListAsync(ct))
                waiting[d.Org] = d.At;
            foreach (var f in await freshQuery.GroupBy(x => x.Org)
                         .Select(g => new { Org = g.Key, At = g.Max(x => x.At) }).ToListAsync(ct))
                waiting.TryAdd(f.Org, f.At);

            anyCandidate = waiting.Count > 0;
            List<Guid> rotation = waiting
                .OrderBy(kv => _lastServed.TryGetValue(kv.Key, out DateTime served) ? served : DateTime.MinValue)
                .ThenByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            // Round by round, one output per organization in turn (the rotation order), until the batch is full. An
            // organization's candidates are read only when its turn comes, so a long rotation costs nothing extra.
            Dictionary<Guid, Queue<OutputModerationTask>> pending = [];
            bool progressed = true;
            while (offered < SweepBatch && progressed)
            {
                progressed = false;
                foreach (Guid organization in rotation)
                {
                    if (offered >= SweepBatch) break;

                    if (!pending.TryGetValue(organization, out Queue<OutputModerationTask>? mine))
                    {
                        // This organization's own few: its due retries first, then its newest fresh outputs.
                        List<OutputModerationTask> own = [];
                        own.AddRange((await dueQuery.Where(v => v.OrganizationId == organization)
                                .OrderBy(v => v.NextAttemptAt)
                                .Select(v => new { v.ProjectId, v.StorageKey })
                                .Take(PerOrganizationWindow)
                                .ToListAsync(ct))
                            .Select(d => new OutputModerationTask(d.ProjectId, d.StorageKey)));
                        own.AddRange((await freshQuery.Where(x => x.Org == organization)
                                .OrderByDescending(x => x.At)
                                .Take(PerOrganizationWindow)
                                .ToListAsync(ct))
                            .Where(x => ProjectOutputKeys.IsAddressableOutput(x.ProjectId, x.Key))
                            .Select(x => new OutputModerationTask(x.ProjectId, x.Key)));
                        pending[organization] = mine = new Queue<OutputModerationTask>(own.Distinct());
                    }

                    while (mine.Count > 0)
                    {
                        OutputModerationTask candidate = mine.Dequeue();
                        // Marked before it is queued: the consumer may finish it at once and forget the mark (a stored
                        // row or a hold), and a mark written after that would cool down an output that is due.
                        if (!_recentlyOffered.TryAdd(candidate, now)) continue;
                        if (!_queue.TryQueue(candidate))
                        {
                            _recentlyOffered.TryRemove(candidate, out _);
                            continue;
                        }

                        _lastServed[organization] = now;
                        offered++;
                        progressed = true;
                        break;
                    }
                }
            }
        }

        _lastSweepFoundNothing = !anyCandidate;
        return offered;
    }
}
