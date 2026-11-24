using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Notifications;

/// <summary>
/// The Inference API's single door to user-activity notifications (docs/notifications.md).
/// Callers report what happened; this type resolves the workspace and publishes the event the
/// Go API consumes to mail the opted-in members.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is best-effort. A notification is a courtesy, never part of the user's
/// request: a bus failure, a missing project, or an unauthenticated caller is swallowed and
/// logged, never surfaced. The caller's own work — the upload, the layer edit — has already
/// committed by the time this is called.
/// </para>
/// </remarks>
public interface IUserActivityNotifier
{
    /// <summary>
    /// Reports that a project file was added. <paramref name="fileName"/> is the added file's
    /// name; <paramref name="actorUserId"/> is the member who added it.
    /// </summary>
    Task FileCreatedAsync(Guid projectId, Guid? actorUserId, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Reports that a thumbnail graphic in the image editor was edited. <paramref name="subject"/>
    /// is the graphic's name or a short description of the edit.
    /// </summary>
    Task GraphicsEditedAsync(Guid projectId, Guid? actorUserId, string subject, CancellationToken ct = default);

    /// <summary>
    /// Reports guest activity on a review link (Wave 4): a type <c>review_activity</c> event for the link's creator
    /// only (<paramref name="recipientUserId"/>). <paramref name="timelineName"/> becomes the event subject and
    /// <paramref name="details"/> the coalesced plain line ("3 new notes, latest from Ana (guest)"). The caller has
    /// already coalesced; the organization is passed in because the guest route has no tenant to resolve it from.
    /// Best-effort: failures are swallowed and logged without any guest text or token.
    /// </summary>
    Task GuestReviewActivityAsync(Guid organizationId, Guid projectId, Guid recipientUserId, string timelineName, string details, CancellationToken ct = default);

    /// <summary>
    /// The 7-day avatar retention warning (docs/design/avatars.md): a type <c>avatar_retention</c> event for the member
    /// who attested the avatar's consent only. Best-effort; answers whether the event was published (the sweep records
    /// a warning only when it was). Default: nothing is published (test doubles that do not care).
    /// </summary>
    Task<bool> AvatarRetentionWarningAsync(
        Guid organizationId, Guid projectId, Guid recipientUserId, string avatarName, DateTime deletedOn, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>
    /// The 7-day retention warning of a workspace avatar library item (docs/design/avatar-objects.md): the same
    /// <c>avatar_retention</c> category as <see cref="AvatarRetentionWarningAsync"/>, for one Owner or Admin of the
    /// organization, with no project (the event's project id is <see cref="Guid.Empty"/>, which the Go API reads as
    /// "the workspace library"). Best-effort; answers whether the event was published. Default: nothing is published.
    /// </summary>
    Task<bool> AvatarLibraryRetentionWarningAsync(
        Guid organizationId, Guid recipientUserId, string itemName, DateTime deletedOn, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>
    /// A social post was published or failed, or an account needs reconnecting: a type <c>social_publishing</c> event for
    /// one member. <paramref name="projectId"/> is null for a reconnect. Best-effort; answers whether it was published.
    /// Default: nothing is published (test doubles that do not care).
    /// </summary>
    Task<bool> SocialPublishingAsync(
        Guid organizationId, Guid? projectId, Guid recipientUserId, string subject, string details, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <inheritdoc/>
public sealed class UserActivityNotifier : IUserActivityNotifier
{
    private readonly IPublishEndpoint _publish;
    private readonly ReelBolt.Inference.Api.Data.InferenceApiDbContext _db;
    private readonly ILogger<UserActivityNotifier> _logger;

    public UserActivityNotifier(
        IPublishEndpoint publish,
        ReelBolt.Inference.Api.Data.InferenceApiDbContext db,
        ILogger<UserActivityNotifier> logger)
    {
        _publish = publish;
        _db = db;
        _logger = logger;
    }

    public Task FileCreatedAsync(Guid projectId, Guid? actorUserId, string fileName, CancellationToken ct = default)
        => PublishAsync(UserActivityNotificationRequested.FileCreated, projectId, actorUserId, fileName, null, ct);

    public Task GraphicsEditedAsync(Guid projectId, Guid? actorUserId, string subject, CancellationToken ct = default)
        => PublishAsync(UserActivityNotificationRequested.GraphicsEdited, projectId, actorUserId, subject, null, ct);

    public async Task GuestReviewActivityAsync(
        Guid organizationId, Guid projectId, Guid recipientUserId, string timelineName, string details, CancellationToken ct = default)
    {
        try
        {
            await _publish.Publish(new UserActivityNotificationRequested
            {
                OrganizationId = organizationId,
                ActorUserId = null,
                ProjectId = projectId,
                Type = UserActivityNotificationRequested.ReviewActivity,
                Subject = timelineName,
                Details = details,
                RecipientUserId = recipientUserId,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort. Deliberately logs no guest text, no detail line and nothing about the link.
            _logger.LogWarning(ex,
                "Review-activity notification not published (project={Project}): {Message}", projectId, ex.GetType().Name);
        }
    }

    public async Task<bool> AvatarRetentionWarningAsync(
        Guid organizationId, Guid projectId, Guid recipientUserId, string avatarName, DateTime deletedOn, CancellationToken ct = default)
    {
        try
        {
            await _publish.Publish(new UserActivityNotificationRequested
            {
                OrganizationId = organizationId,
                ActorUserId = null,
                ProjectId = projectId,
                Type = UserActivityNotificationRequested.AvatarRetention,
                Subject = avatarName,
                Details = $"Deleted on {deletedOn:yyyy-MM-dd} (UTC).",
                RecipientUserId = recipientUserId,
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Avatar retention notification not published (project={Project}): {Message}", projectId, ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> AvatarLibraryRetentionWarningAsync(
        Guid organizationId, Guid recipientUserId, string itemName, DateTime deletedOn, CancellationToken ct = default)
    {
        try
        {
            await _publish.Publish(new UserActivityNotificationRequested
            {
                OrganizationId = organizationId,
                ActorUserId = null,
                // No project: a library item belongs to the workspace.
                ProjectId = Guid.Empty,
                Type = UserActivityNotificationRequested.AvatarRetention,
                Subject = itemName,
                Details = $"Deleted on {deletedOn:yyyy-MM-dd} (UTC).",
                RecipientUserId = recipientUserId,
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Avatar library retention notification not published (org={Org}): {Message}", organizationId, ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> SocialPublishingAsync(
        Guid organizationId, Guid? projectId, Guid recipientUserId, string subject, string details, CancellationToken ct = default)
    {
        try
        {
            await _publish.Publish(new UserActivityNotificationRequested
            {
                OrganizationId = organizationId,
                ActorUserId = null,
                ProjectId = projectId ?? Guid.Empty,
                Type = UserActivityNotificationRequested.SocialPublishing,
                Subject = subject,
                Details = details,
                RecipientUserId = recipientUserId,
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Social-publishing notification not published (org={Org}): {Message}", organizationId, ex.GetType().Name);
            return false;
        }
    }

    private async Task PublishAsync(
        string type, Guid projectId, Guid? actorUserId, string subject, string? details, CancellationToken ct)
    {
        try
        {
            // A file or graphic whose project is gone (or unscoped to a workspace) has nobody
            // to mail: skip rather than guess.
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
            if (project is null)
            {
                return;
            }
            await _publish.Publish(new UserActivityNotificationRequested
            {
                OrganizationId = project.OrganizationId,
                ActorUserId = actorUserId,
                ProjectId = projectId,
                Type = type,
                Subject = subject,
                Details = details,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: log and move on. The user's upload/edit already succeeded.
            _logger.LogWarning(ex,
                "User-activity notification not published (type={Type}, project={Project}): {Message}",
                type, projectId, ex.Message);
        }
    }
}
