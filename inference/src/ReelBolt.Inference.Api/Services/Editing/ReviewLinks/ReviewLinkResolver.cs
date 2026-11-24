using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>A link that passed every liveness check, with the rows the public routes need.</summary>
public sealed record LiveReviewLink(EditReviewLink Link, EditTimelineEntity Timeline, ReviewRender Render);

/// <summary>
/// Turns a presented token into a live link, or null for ANY reason at all (the caller answers one uniform
/// empty 404 without saying which): malformed, unknown, revoked, expired, timeline deleted, render gone,
/// project or organization gone, or the creator no longer able to edit the project.
/// </summary>
/// <remarks>
/// <para>
/// The shape is checked before the database is touched. The lookup is an equality on the SHA-256 column's
/// unique index; the application compares nothing itself. The route is anonymous, so there is no request
/// tenant: only the named <c>Tenant</c> filter is ignored (soft-delete filters still apply) and the link's own
/// project id scopes every further query.
/// </para>
/// <para>
/// A link stops working the moment its creator stops being able to edit the project (removed from the
/// workspace, demoted below Member, account deleted): a share is delegated authority, not a standalone
/// secret that outlives who granted it.
/// </para>
/// </remarks>
public sealed class ReviewLinkResolver(InferenceApiDbContext db, ReviewRenderResolver renders, TimeProvider? clock = null)
{
    private static readonly string[] Tenant = [InferenceApiDbContext.TenantFilterName];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<LiveReviewLink?> ResolveAsync(string? token, CancellationToken ct)
    {
        if (!ReviewLinkTokens.IsWellFormed(token)) return null;

        byte[] hash = ReviewLinkTokens.Hash(token!);
        EditReviewLink? link = await db.EditReviewLinks.IgnoreQueryFilters(Tenant)
            .FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
        if (link is null) return null;

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (link.RevokedAt is not null || link.ExpiresAt <= now) return null;

        EditTimelineEntity? timeline = await db.EditTimelines.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == link.TimelineId && t.ProjectId == link.ProjectId, ct);
        if (timeline is null) return null;

        Project? project = await db.Projects.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == link.ProjectId && p.OrganizationId == link.OrganizationId, ct);
        if (project is null) return null;

        bool organizationLive = await db.Organizations.AsNoTracking()
            .AnyAsync(o => o.Id == link.OrganizationId && o.DeletedAt == null, ct);
        if (!organizationLive) return null;

        ApplicationUser? creator = await db.ApplicationUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == link.CreatedByUserId, ct);
        if (creator is null || creator.DeletionRequestedAt is not null) return null;

        OrganizationMembership? membership = await db.OrganizationMemberships.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == link.OrganizationId && m.UserId == creator.Id, ct);
        if (membership is null || !ProjectAccessService.Allowed(membership.Role, ProjectPermission.Edit, project.OwnerId == creator.Id))
            return null;

        ReviewRender? render = await renders.ForExecutionAsync(link.ProjectId, link.TimelineId, link.RenderExecutionId, ct);
        if (render is null) return null;

        return new LiveReviewLink(link, timeline, render);
    }
}
