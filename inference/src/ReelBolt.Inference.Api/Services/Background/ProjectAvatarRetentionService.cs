using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>What one avatar retention sweep did.</summary>
/// <param name="Deleted">Avatars deleted (past <c>retention_until</c>, or revoked longer than the grace).</param>
/// <param name="Warned">Seven-day warnings published.</param>
/// <param name="EventsPurged">Audit events older than <see cref="ProjectAvatarEvent.RetentionYears"/> years removed.</param>
/// <param name="LibraryItemsDeleted">Workspace avatar library items deleted (with their imports) for the same two reasons.</param>
/// <param name="LibraryItemsWarned">Library items whose seven-day warning was published to the organization's Owners and Admins.</param>
public sealed record AvatarRetentionSweepResult(int Deleted, int Warned, int EventsPurged, int LibraryItemsDeleted = 0, int LibraryItemsWarned = 0);

/// <summary>
/// Enforces an avatar's retention (docs/design/avatars.md "Retention sweep"), the twin of
/// <see cref="ProjectVoiceRetentionService"/> with the same constants: every <see cref="SweepInterval"/> (first run two
/// minutes after start) it
/// <list type="number">
/// <item>deletes, through the ONE delete routine (<see cref="ProjectAvatarDeletion"/>), at most <see cref="MaxPerSweep"/>
/// avatars past <c>retention_until</c> or revoked more than <see cref="ProjectAvatarLimits.RevokedGraceDays"/> days ago,
/// each writing a <c>retention_deleted</c> event with no actor;</item>
/// <item>publishes, at most <see cref="MaxPerSweep"/> per sweep, the 7-day warning (<c>avatar_retention</c>, to the
/// attesting member) for a live avatar that expires within <see cref="WarningDays"/> days, once per attestation (a
/// <c>retention_warning_sent</c> event newer than the attestation suppresses a second one; re-attesting starts a new
/// period and a new warning);</item>
/// <item>purges audit events older than three years (AV-7), at most <see cref="EventPurgeBatch"/> per sweep.</item>
/// </list>
/// <para>
/// <b>People only.</b> An avatar of an object (docs/design/avatar-objects.md) has no retention period and no revoke, so
/// it is never swept and never warned about; every query here reads <see cref="AvatarSubjectKind.Person"/> rows only.
/// </para>
/// <para>
/// <b>Switches.</b> <c>ProjectAvatars:RetentionSweepEnabled</c> (default true) is the staging switch: a copy of
/// production must never delete real people's avatars, so false stops the whole sweep. The product kill switch
/// <c>Avatars:Enabled</c> does NOT stop deletion or the event purge (deleting a likeness on time is never paused by a
/// product switch); it only stops the warning, which invites a re-attestation the closed routes could not accept.
/// Every failure is logged and skipped, never thrown: one bad avatar or a vendor outage must not stop the next.
/// </para>
/// <para>
/// <b>The workspace avatar library</b> (docs/design/avatar-objects.md) is swept by the same rules, first: an item past
/// its <c>retention_until</c> or revoked longer than the grace is deleted through the library's one delete routine,
/// which removes its imports with it, and the 7-day warning of an item goes to the organization's Owners and Admins.
/// A linked import therefore gets no warning of its own (its item's covers it), but it is still deleted on time by
/// step 1 should its item's delete fail.
/// </para>
/// </summary>
public sealed partial class ProjectAvatarRetentionService : BackgroundService
{
    public static readonly TimeSpan SweepInterval = ProjectVoiceRetentionService.SweepInterval;
    public const int MaxPerSweep = ProjectVoiceRetentionService.MaxPerSweep;
    public const int WarningDays = 7;
    public const int EventPurgeBatch = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectAvatarRetentionService> _logger;
    private readonly bool _sweepEnabled;

    public ProjectAvatarRetentionService(IServiceScopeFactory scopeFactory, ILogger<ProjectAvatarRetentionService> logger,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _sweepEnabled = configuration?.GetValue("ProjectAvatars:RetentionSweepEnabled", true) ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_sweepEnabled)
        {
            _logger.LogWarning("Avatar retention sweep is disabled (ProjectAvatars:RetentionSweepEnabled=false)");
            return;
        }

        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                AvatarRetentionSweepResult result = await SweepOnceAsync(DateTime.UtcNow, stoppingToken);
                if (result.Deleted + result.Warned + result.EventsPurged + result.LibraryItemsDeleted + result.LibraryItemsWarned > 0)
                {
                    _logger.LogInformation(
                        "Avatar retention sweep: deleted {Deleted} avatar(s), warned {Warned}, purged {Events} old audit event(s); library: deleted {LibraryDeleted} item(s), warned {LibraryWarned}",
                        result.Deleted, result.Warned, result.EventsPurged, result.LibraryItemsDeleted, result.LibraryItemsWarned);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avatar retention sweep failed; will retry after the interval");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One sweep at <paramref name="now"/>, exposed for tests. A disabled sweep does nothing.</summary>
    public async Task<AvatarRetentionSweepResult> SweepOnceAsync(DateTime now, CancellationToken ct)
    {
        if (!_sweepEnabled) return new AvatarRetentionSweepResult(0, 0, 0);

        // The library first: deleting an item removes its imports with it, so the next step finds them gone.
        int libraryDeleted = await DeleteDueLibraryItemsAsync(now, ct);
        int deleted = await DeleteDueAsync(now, ct);
        int warned = await WarnAsync(now, ct);
        int libraryWarned = await WarnLibraryAsync(now, ct);
        int purged = await PurgeOldEventsAsync(now, ct);
        return new AvatarRetentionSweepResult(deleted, warned, purged, libraryDeleted, libraryWarned);
    }

    private async Task<int> DeleteDueAsync(DateTime now, CancellationToken ct)
    {
        DateTime revokedCutoff = now.AddDays(-ProjectAvatarLimits.RevokedGraceDays);
        List<(Guid Id, Guid ProjectId, Guid OrganizationId)> due;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
            due = [.. (await db.ProjectAvatars.AsNoTracking()
                    .Where(a => a.SubjectKind == AvatarSubjectKind.Person
                                && (a.RetentionUntil <= now || (a.ConsentRevokedAt != null && a.ConsentRevokedAt <= revokedCutoff)))
                    .OrderBy(a => a.RetentionUntil)
                    .Take(MaxPerSweep)
                    .Join(db.Projects.IgnoreQueryFilters(), a => a.ProjectId, p => p.Id, (a, p) => new { a.Id, a.ProjectId, p.OrganizationId })
                    .ToListAsync(ct))
                .Select(a => (a.Id, a.ProjectId, a.OrganizationId))];
        }

        int deleted = 0;
        foreach ((Guid id, Guid projectId, Guid organizationId) in due)
        {
            // One scope per avatar: a failure (or a concurrency retry) never leaks state into the next delete.
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
                var deletion = scope.ServiceProvider.GetRequiredService<ProjectAvatarDeletion>();
                if (await deletion.DeleteAsync(projectId, id, organizationId, actorUserId: null, AvatarDeleteCause.Retention, ct) is not null)
                    deleted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention could not delete avatar {AvatarId} of project {ProjectId}; retried next sweep", id, projectId);
            }
        }

        return deleted;
    }

    private async Task<int> WarnAsync(DateTime now, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IOptions<AvatarOptions>? options = scope.ServiceProvider.GetService<IOptions<AvatarOptions>>();
        if (options?.Value.Enabled != true) return 0;
        IUserActivityNotifier? notifier = scope.ServiceProvider.GetService<IUserActivityNotifier>();
        if (notifier is null) return 0;

        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        DateTime horizon = now.AddDays(WarningDays);
        var candidates = await db.ProjectAvatars.AsNoTracking()
            .Where(a => a.SubjectKind == AvatarSubjectKind.Person
                        // A linked import follows its library item, whose own warning goes to the Owners and Admins.
                        && a.LibraryItemId == null
                        && a.ConsentRevokedAt == null && a.RetentionUntil > now && a.RetentionUntil <= horizon
                        && !db.ProjectAvatarEvents.Any(e => e.AvatarId == a.Id && e.Kind == AvatarEventKind.RetentionWarningSent
                                                          && e.OccurredAt >= a.ConsentAttestedAt))
            .OrderBy(a => a.RetentionUntil)
            .Take(MaxPerSweep)
            .Join(db.Projects.IgnoreQueryFilters(), a => a.ProjectId, p => p.Id, (a, p) => new { Avatar = a, p.OrganizationId })
            .ToListAsync(ct);

        int warned = 0;
        foreach (var candidate in candidates)
        {
            ProjectAvatar avatar = candidate.Avatar;
            if (avatar.ConsentAttestedByUserId is not { } recipient) continue;
            if (!await notifier.AvatarRetentionWarningAsync(candidate.OrganizationId, avatar.ProjectId, recipient, avatar.Name, avatar.RetentionUntil, ct))
                continue; // not published: try again next sweep

            ProjectAvatarEvents.Add(db, avatar, candidate.OrganizationId, AvatarEventKind.RetentionWarningSent, actorUserId: null,
                new Dictionary<string, object> { ["days_left"] = Math.Max(0, (int)Math.Ceiling((avatar.RetentionUntil - now).TotalDays)) },
                occurredAt: now); // the suppression check compares this with the attestation: one clock, the sweep's
            await db.SaveChangesAsync(ct);
            warned++;
        }

        return warned;
    }

    private async Task<int> PurgeOldEventsAsync(DateTime now, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime cutoff = now.AddYears(-ProjectAvatarEvent.RetentionYears);
        List<ProjectAvatarEvent> old = await db.ProjectAvatarEvents
            .Where(e => e.OccurredAt < cutoff)
            .OrderBy(e => e.OccurredAt)
            .Take(EventPurgeBatch)
            .ToListAsync(ct);
        if (old.Count == 0) return 0;
        db.ProjectAvatarEvents.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }
}
