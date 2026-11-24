using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

// What the request path checked before it queued the job is checked again, read fresh from the database, before the
// worker sends anything out of the host (docs/design/avatar-objects.md "Deviations recorded at build time"): a job can
// wait in the queue for minutes, and holds, consent, membership and the switches change meanwhile. Every stage that
// sends media (the picture model, the video model, the segmentation model) asks the one helper below, so the stages
// cannot disagree.
public sealed partial class AvatarBuildProcessor
{
    /// <summary>What <see cref="RecheckBeforeSendingAsync"/> found.</summary>
    /// <param name="Refusal">Why the job must stop (nothing may be sent), or null.</param>
    /// <param name="States">One observed state per source reference given, in order (empty when none was given).</param>
    private sealed record SendRecheck(AvatarRefusal? Refusal, IReadOnlyList<AvatarReferenceState> States);

    /// <summary>
    /// The re-checks the request path made, again, failing closed: a default Vision provider is configured (the person
    /// check is mandatory), the project's content-quarantine marker and output hold are off, the media permission the job
    /// was allowed under still stands (revoked project media consent stops it), the member who asked is still a member
    /// with Edit on the project, and each of <paramref name="sources"/> still has the bytes it was attested with and is
    /// not held (a file hold of any kind: quarantine, CSAM or moderation state, the output hold).
    /// </summary>
    private async Task<SendRecheck> RecheckBeforeSendingAsync(
        AvatarBuildJob job, IReadOnlyList<ProjectAvatarReference> sources, CancellationToken ct)
    {
        // The Vision provider: no provider, nothing is sent (a job never runs with the person check skipped).
        AvatarRefusal? noVision = _checks is null ? AvatarReferenceChecks.VisionRequired() : await _checks.VisionRefusalAsync(ct);
        if (noVision is not null) return new SendRecheck(noVision, []);

        Project? project = await _db.Projects.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);
        if (project is null || project.OrganizationId != job.OrganizationId)
            return new SendRecheck(AvatarRefusal.Conflict(AvatarBuildCodes.BuildFailed, "The project is gone."), []);

        // The content-quarantine marker and the output hold (fresh: the project row is read here, never the tracked one).
        if (await ObjectAvatarCreator.QuarantineRefusalAsync(_db, project, ct) is { } quarantined) return new SendRecheck(quarantined, []);

        // The permission the job was allowed under: the project's recorded consent can be withdrawn while it waits.
        // (A permission given for this one call stands: it was the member's own, for this job.)
        if (string.Equals(job.AllowedBy, AvatarBuildJob.AllowedByProjectConsent, StringComparison.Ordinal) && project.SubjectMediaConsentAt is null)
        {
            return new SendRecheck(AvatarRefusal.Conflict(AvatarBuildCodes.ConsentRequired,
                "The permission to send this project's pictures to an outside model was withdrawn, so nothing was sent."), []);
        }

        // The requester must still be a member who may edit this project, now.
        OrganizationMembership? member = await _db.OrganizationMemberships.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == job.OrganizationId && m.UserId == job.RequestedByUserId, ct);
        if (member is null || !ProjectAccessService.Allowed(member.Role, ProjectPermission.Edit, project.OwnerId == job.RequestedByUserId))
        {
            return new SendRecheck(AvatarRefusal.Conflict("PROJECT_ACCESS_LOST",
                "The person who asked for this can no longer edit the project, so nothing was sent."), []);
        }

        if (sources.Count == 0) return new SendRecheck(null, []);
        IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
            _db, project.Id, sources, _checks!.DigestAsync, ct);
        return new SendRecheck(null, states);
    }

    /// <summary>
    /// Ends the job for a refused re-check: its words say why and that nothing was sent. What the job already metered
    /// is settled; with nothing metered the whole hold is released.
    /// </summary>
    private Task<Step> FailRecheckAsync(AvatarBuildJob job, AvatarBuildParams p, AvatarRefusal refusal) =>
        FailAsync(job, p, refusal.Code,
            p.MeteredMillis > 0 ? refusal.Message : refusal.Message + " Nothing was charged.",
            keepCharge: p.MeteredMillis > 0);
}
