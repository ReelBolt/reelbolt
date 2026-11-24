using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The periodic drift sweep (SPEC 13.2). Every <c>DesignSystem:Drift:SweepIntervalMinutes</c> (default
/// 360, <c>0</c> disables it) it enqueues a check of every project that has a design system with a
/// current version — the covered projects a source change may have reached without an observable sync —
/// and expires the proposals nobody answered within <c>DesignSystem:Drift:ProposalTtlDays</c> (default
/// 30). The first sweep waits <see cref="StartupDelay"/> so migrations and seeding have finished.
/// </summary>
public sealed class DesignSystemDriftSweeper : BackgroundService
{
    /// <summary>Lets migrations and seeding finish before the first sweep.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);

    public const int DefaultSweepIntervalMinutes = 360;
    public const int DefaultProposalTtlDays = 30;

    /// <summary>The most projects one sweep batch queues (<c>DesignSystem:Drift:SweepBatchSize</c>).</summary>
    public const int DefaultSweepBatchSize = 500;

    /// <summary>How long to wait before the next batch of a cycle that is not finished yet.</summary>
    public static readonly TimeSpan BatchPause = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where the next batch resumes: projects are swept in ascending id order from this cursor, and the
    /// cursor persists between sweeps so a population larger than the queue is covered in full instead
    /// of re-sweeping the same prefix. A cycle is short (a batch every <see cref="BatchPause"/>), so a
    /// restart that resets the cursor to the beginning cannot starve anyone in practice; a finished
    /// cycle also resets it.
    /// </summary>
    internal Guid Cursor { get; set; } = Guid.Empty;

    private static readonly System.Diagnostics.Metrics.Meter SweepMeter = new("ReelBolt.DesignSystem.Drift");

    /// <summary>Organizations skipped because their lane was full (a counter; no ids, no content).</summary>
    internal static readonly System.Diagnostics.Metrics.Counter<long> SkippedBusyOrganizations =
        SweepMeter.CreateCounter<long>("design_system.drift.sweep_skipped_busy_organizations");

    private List<(Guid Project, Guid Organization)> _deferred = [];
    internal IReadOnlyList<(Guid Project, Guid Organization)> Deferred => _deferred;
    internal const int MaxDeferred = 1000;
    internal const int MaxDeferredPerOrganization = 100;

    /// <summary>How many organizations the last batch skipped as busy.</summary>
    internal int LastBatchSkippedOrganizations { get; private set; }

    /// <summary>True when the last batch stopped before the end of the cycle.</summary>
    internal bool HasMore { get; private set; }

    private readonly DesignDriftQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DesignSystemDriftSweeper> _logger;

    public DesignSystemDriftSweeper(
        DesignDriftQueue queue,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DesignSystemDriftSweeper> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int minutes = SweepIntervalMinutes;
        if (minutes <= 0)
        {
            _logger.LogInformation("The design-system drift sweep is disabled (DesignSystem:Drift:SweepIntervalMinutes = {Minutes})", minutes);
            return;
        }

        TimeSpan interval = TimeSpan.FromMinutes(minutes);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The design-system drift sweep failed; retrying after the interval");
                }

                // A cycle larger than one batch continues soon; only a finished cycle waits the interval.
                await Task.Delay(HasMore ? BatchPause : interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// One sweep: expires stale proposals, queues a check of every project with a current design-system
    /// version, and returns how many projects were newly queued. Public for tests.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (SweepIntervalMinutes <= 0)
            return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        await ExpireStaleProposalsAsync(db, ct);

        int batch = Math.Max(1, _configuration.GetValue("DesignSystem:Drift:SweepBatchSize", DefaultSweepBatchSize));
        Guid cursor = Cursor;

        // The covered projects, ordered and cut in memory: a Guid comparison translated to SQL orders
        // by bytes while .NET orders by field, and the cursor must use one order throughout. Two Guids
        // per covered project is cheap next to the checks themselves.
        var covered = await (
            from s in db.DesignSystems
            where s.ProjectId != null && s.CurrentVersionId != null
            join p in db.Projects on s.ProjectId equals p.Id
            select new { p.Id, p.OrganizationId })
            .Distinct()
            .ToListAsync(ct);
        var page = covered
            .OrderBy(x => x.Id)
            .Where(x => x.Id.CompareTo(cursor) > 0)
            .Take(batch + 1)
            .ToList();

        int queued = 0;
        int examined = 0;
        int skippedProjects = 0;
        var busy = new HashSet<Guid>();

        // Projects an earlier batch had to skip because their organization's lane was full are retried
        // first, so a skip means "try this one again", never "forget it".
        List<(Guid Project, Guid Organization)> stillDeferred = [];
        foreach ((Guid project, Guid organization) in _deferred)
        {
            if (busy.Contains(organization))
            {
                stillDeferred.Add((project, organization));
                continue;
            }

            DesignDriftEnqueueResult retried = _queue.Enqueue(
                project, DesignDriftTrigger.Sweep, changedPaths: null, organization);
            if (retried is DesignDriftEnqueueResult.OrganizationBusy or DesignDriftEnqueueResult.Dropped)
            {
                busy.Add(organization);
                stillDeferred.Add((project, organization));
                skippedProjects++;
            }
            else if (retried == DesignDriftEnqueueResult.Queued)
            {
                queued++;
            }
        }

        _deferred = stillDeferred;
        var deferredIds = _deferred.Select(d => d.Project).ToHashSet();

        foreach (var project in page.Take(batch))
        {
            examined++;
            Cursor = project.Id;

            // An organization whose lane is full is skipped for the rest of this batch: the cursor never
            // pins on it, so every other tenant's projects are still swept, and the skipped project is
            // deferred to a later batch.
            if (busy.Contains(project.OrganizationId))
            {
                skippedProjects++;
                if (deferredIds.Add(project.Id))
                    _deferred.Add((project.Id, project.OrganizationId));
                continue;
            }

            DesignDriftEnqueueResult result = _queue.Enqueue(
                project.Id, DesignDriftTrigger.Sweep, changedPaths: null, project.OrganizationId);
            if (result is DesignDriftEnqueueResult.OrganizationBusy or DesignDriftEnqueueResult.Dropped)
            {
                busy.Add(project.OrganizationId);
                skippedProjects++;
                if (deferredIds.Add(project.Id))
                    _deferred.Add((project.Id, project.OrganizationId));
            }
            else if (result == DesignDriftEnqueueResult.Queued)
            {
                queued++;
            }
        }

        LastBatchSkippedOrganizations = busy.Count;
        if (busy.Count > 0)
        {
            SkippedBusyOrganizations.Add(busy.Count);
            _logger.LogInformation(
                "The design-system drift sweep skipped {Organizations} busy organization(s) ({Projects} project(s)) this batch",
                busy.Count, skippedProjects);
        }

        // The cycle is complete when every remaining project was examined; start over at the beginning.
        // A cycle is complete when every remaining project was examined, whatever is still deferred: the
        // deferred projects are carried into the next cycle (retried first in every batch), so one
        // tenant that never drains cannot keep the cursor from ever resetting for everyone else.
        HasMore = page.Count > examined;
        if (!HasMore)
            Cursor = Guid.Empty;

        int trimmed = TrimDeferred();
        if (trimmed > 0)
        {
            SkippedBusyOrganizations.Add(trimmed);
            _logger.LogInformation(
                "The design-system drift sweep dropped {Projects} deferred project(s) beyond its caps; the next cycle re-examines them",
                trimmed);
        }

        if (queued > 0)
            _logger.LogInformation("The design-system drift sweep queued {Count} project(s)", queued);
        return queued;
    }

    /// <summary>Keeps the newest entries within the per-organization and total caps; returns how many were dropped.</summary>
    private int TrimDeferred()
    {
        int before = _deferred.Count;
        var perOrganization = new Dictionary<Guid, int>();
        var kept = new List<(Guid Project, Guid Organization)>();
        for (int i = _deferred.Count - 1; i >= 0 && kept.Count < MaxDeferred; i--)
        {
            (Guid project, Guid organization) = _deferred[i];
            int held = perOrganization.GetValueOrDefault(organization);
            if (held >= MaxDeferredPerOrganization)
                continue;

            perOrganization[organization] = held + 1;
            kept.Add((project, organization));
        }

        kept.Reverse();
        _deferred = kept;
        return before - kept.Count;
    }

    private int SweepIntervalMinutes =>
        _configuration.GetValue("DesignSystem:Drift:SweepIntervalMinutes", DefaultSweepIntervalMinutes);

    private async Task ExpireStaleProposalsAsync(InferenceApiDbContext db, CancellationToken ct)
    {
        int ttlDays = _configuration.GetValue("DesignSystem:Drift:ProposalTtlDays", DefaultProposalTtlDays);
        if (ttlDays <= 0)
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-ttlDays);
        List<DesignSystemChangeProposal> stale = await db.DesignSystemChangeProposals
            .Where(p => p.Status == DesignProposalStatus.Pending && p.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0)
            return;

        foreach (DesignSystemChangeProposal proposal in stale)
            proposal.Status = DesignProposalStatus.Expired;

        // An expired proposal's Draft can no longer be approved through it: reject it rather than leave
        // an orphan Draft behind (unless it has been approved or is the live version meanwhile).
        await DesignSystemService.RejectStillDraftsAsync(db, stale.Select(p => p.ProposedVersionId).ToList(), ct);

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("The design-system drift sweep expired {Count} pending proposal(s)", stale.Count);
    }
}
