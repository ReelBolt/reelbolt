using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// The workspace avatar library half of the avatar retention sweep (docs/design/avatar-objects.md "The workspace avatar
/// library (global avatars)"): the same two rules as a project avatar, applied to library items of PEOPLE (an object
/// has no retention period and no revoke). Deletion goes through <see cref="AvatarLibraryService.DeleteAsync"/>, the
/// library's one delete routine, which removes the item's imports first.
/// </summary>
public sealed partial class ProjectAvatarRetentionService
{
    private async Task<int> DeleteDueLibraryItemsAsync(DateTime now, CancellationToken ct)
    {
        DateTime revokedCutoff = now.AddDays(-ProjectAvatarLimits.RevokedGraceDays);
        List<(Guid Id, Guid OrganizationId)> due;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
            due = [.. (await db.AvatarLibraryItems.AsNoTracking()
                    .Where(i => i.SubjectKind == AvatarSubjectKind.Person
                                && (i.RetentionUntil <= now || (i.ConsentRevokedAt != null && i.ConsentRevokedAt <= revokedCutoff)))
                    .OrderBy(i => i.RetentionUntil)
                    .Take(MaxPerSweep)
                    .Select(i => new { i.Id, i.OrganizationId })
                    .ToListAsync(ct))
                .Select(i => (i.Id, i.OrganizationId))];
        }

        int deleted = 0;
        foreach ((Guid id, Guid organizationId) in due)
        {
            // One scope per item: a failure never leaks state into the next delete.
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
                AvatarLibraryService? library = scope.ServiceProvider.GetService<AvatarLibraryService>();
                if (library is null) return deleted; // no library in this host: nothing to sweep

                AvatarLibraryDeleteReport? report = await library.DeleteAsync(
                    organizationId, id, actorUserId: null, AvatarLibraryDeleteCause.Retention, ct);
                if (report is { ItemDeleted: true }) deleted++;
                else if (report is not null)
                    _logger.LogWarning("Retention could not remove every import of avatar library item {ItemId}; retried next sweep", id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention could not delete avatar library item {ItemId}; retried next sweep", id);
            }
        }

        return deleted;
    }

    /// <summary>
    /// The 7-day warning of a library item, to every Owner and Admin of its organization, once per attestation (a
    /// <c>LibraryRetentionWarningSent</c> event newer than the attestation suppresses a second one; confirming the item
    /// again starts a new period and a new warning). The same notification category as a project avatar's warning
    /// (<c>avatar_retention</c>), so the same per-user switch governs it.
    /// </summary>
    private async Task<int> WarnLibraryAsync(DateTime now, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IOptions<AvatarOptions>? options = scope.ServiceProvider.GetService<IOptions<AvatarOptions>>();
        if (options?.Value.Enabled != true) return 0;
        IUserActivityNotifier? notifier = scope.ServiceProvider.GetService<IUserActivityNotifier>();
        if (notifier is null) return 0;

        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        DateTime horizon = now.AddDays(WarningDays);
        List<AvatarLibraryItem> candidates = await db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.SubjectKind == AvatarSubjectKind.Person
                        && i.ConsentRevokedAt == null && i.RetentionUntil > now && i.RetentionUntil <= horizon
                        && !db.ProjectAvatarEvents.Any(e => e.AvatarId == i.Id && e.Kind == AvatarEventKind.LibraryRetentionWarningSent
                                                          && e.OccurredAt >= i.ConsentAttestedAt))
            .OrderBy(i => i.RetentionUntil)
            .Take(MaxPerSweep)
            .ToListAsync(ct);

        int warned = 0;
        foreach (AvatarLibraryItem item in candidates)
        {
            List<Guid> recipients = await db.OrganizationMemberships.AsNoTracking()
                .Where(m => m.OrganizationId == item.OrganizationId
                            && (m.Role == OrganizationRole.Owner || m.Role == OrganizationRole.Admin)
                            && !m.User.IsServiceAccount)
                .Select(m => m.UserId)
                .ToListAsync(ct);

            int published = 0;
            foreach (Guid recipient in recipients)
            {
                if (await notifier.AvatarLibraryRetentionWarningAsync(item.OrganizationId, recipient, item.Name, item.RetentionUntil, ct))
                    published++;
            }

            if (published == 0) continue; // nobody was told: try again next sweep

            ProjectAvatarEvents.AddForLibraryItem(db, item, AvatarEventKind.LibraryRetentionWarningSent, actorUserId: null,
                new Dictionary<string, object>
                {
                    ["days_left"] = Math.Max(0, (int)Math.Ceiling((item.RetentionUntil - now).TotalDays)),
                    ["recipients"] = published
                },
                occurredAt: now); // the suppression check compares this with the attestation: one clock, the sweep's
            await db.SaveChangesAsync(ct);
            warned++;
        }

        return warned;
    }
}
