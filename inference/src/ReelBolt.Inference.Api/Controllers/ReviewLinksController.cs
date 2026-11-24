using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Guest review links, owner side (Wave 4, <c>docs/design/guest-review-links.md</c>): create, list, inspect and
/// revoke the capability URLs of one timeline. Every action resolves the project through
/// <see cref="IProjectAccessService"/> exactly once (Edit to create/revoke, View to read), then looks the timeline
/// and link up WITHIN that project; the tenant filter on <c>edit_review_links</c> is the backstop. A foreign or
/// unknown id is a 404.
/// </summary>
/// <remarks>
/// The URL (and so the token) is returned by <see cref="Create"/> and nowhere else: the list and detail never
/// carry it, and the database holds only its hash. Nothing here logs the token.
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/edit-timelines/{id:guid}/review-links")]
[Authorize]
public sealed class ReviewLinksController : ControllerBase
{
    private const int MaxListed = 100;
    private const int MaxDecisionHistory = 50;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ReviewRenderResolver _renders;
    private readonly PublishModeration? _publishModeration;
    private readonly ReviewLinkOptions _options;
    private readonly string _origin;
    private readonly TimeProvider _clock;

    public ReviewLinksController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        ReviewRenderResolver renders,
        IConfiguration configuration,
        IOptions<ReviewLinkOptions>? options = null,
        PublishModeration? publishModeration = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _renders = renders;
        _publishModeration = publishModeration;
        _options = options?.Value ?? new ReviewLinkOptions();
        _clock = clock ?? TimeProvider.System;
        _origin = AbsoluteOrigin(configuration["OutputLinks:PublicBaseUrl"])
                  ?? AbsoluteOrigin(configuration["CloudStorage:PublicBaseUrl"])
                  ?? string.Empty;
    }

    /// <summary>
    /// Creates a link to one passed render of the timeline (default: the newest). <c>201</c> with the URL, shown
    /// ONCE. <c>409 NO_RENDER_TO_SHARE</c> without a shareable render, <c>409 REVIEW_LINK_LIMIT</c> at the live-link cap,
    /// <c>400</c> for a bad label or expiry; the moderation publish gate fails closed when enabled.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, Guid id, [FromBody] CreateReviewLinkRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        request ??= new CreateReviewLinkRequest(null, null, null, null);

        string? label = null;
        if (!string.IsNullOrWhiteSpace(request.Label))
        {
            label = ReviewGuestText.NormalizeName(request.Label);
            if (label is null || label.Length > 80)
                return BadRequest(new ReviewLinkMessage("The label must be 1 to 80 characters."));
        }

        int days = request.ExpiresInDays ?? _options.EffectiveDefaultExpiryDays;
        if (days < 1 || days > _options.EffectiveMaxExpiryDays)
            return BadRequest(new ReviewLinkMessage($"Choose an expiry between 1 and {_options.EffectiveMaxExpiryDays} days."));

        ReviewRender? render = request.RenderExecutionId is { } chosen
            ? await _renders.ForExecutionAsync(projectId, id, chosen, ct)
            : await _renders.NewestAsync(projectId, id, ct);
        if (render is null || !IsMp4(render.FileName))
        {
            return Conflict(new ReviewLinkMessage(
                "Render this timeline first, then share the finished video.", "NO_RENDER_TO_SHARE"));
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        int live = await _db.EditReviewLinks.CountAsync(
            l => l.TimelineId == id && l.ProjectId == projectId && l.RevokedAt == null && l.ExpiresAt > now, ct);
        if (live >= _options.MaxLinksPerTimeline)
        {
            return Conflict(new ReviewLinkMessage(
                $"This timeline already has {_options.MaxLinksPerTimeline} active review links. Turn one off first.", "REVIEW_LINK_LIMIT"));
        }

        // Sharing a render with someone outside the workspace is publication: block/unavailable refuses (fail closed).
        if (_publishModeration is not null)
        {
            ModerationAttribution attribution = new(
                access.Project!.OrganizationId, projectId, _currentUser.UserId, render.ExecutionId, render.StepResultId,
                ModerationSurface.Publish, ModerationContentKind.Video);
            PublishModerationOutcome outcome = await _publishModeration.EvaluateObjectAsync(
                projectId, render.StorageKey, "video/mp4", attribution, ct);
            if (!outcome.Allowed)
            {
                return outcome.Code == "CONTENT_BLOCKED"
                    ? StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final })
                    : StatusCode(StatusCodes.Status409Conflict, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
            }
        }

        string token = ReviewLinkTokens.Generate();
        EditReviewLink row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = access.Project!.OrganizationId,
            TimelineId = id,
            CreatedByUserId = _currentUser.UserId,
            TokenHash = ReviewLinkTokens.Hash(token),
            Label = label,
            RenderExecutionId = render.ExecutionId,
            PinnedRevision = render.Revision > 0 ? render.Revision : timeline.Revision,
            AllowComments = request.AllowComments ?? true,
            ExpiresAt = now.AddDays(days),
            CreatedAt = now,
        };
        _db.EditReviewLinks.Add(row);
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new CreateReviewLinkResponse(
            row.Id, $"{_origin}/app/review/g/{token}", row.Label, row.ExpiresAt, render.CreatedAt,
            Stale: timeline.Revision > row.PinnedRevision));
    }

    /// <summary>The timeline's links, newest first (at most 100). Never the token or URL.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        List<EditReviewLink> links = await _db.EditReviewLinks.AsNoTracking()
            .Where(l => l.TimelineId == id && l.ProjectId == projectId)
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
            .Take(MaxListed)
            .ToListAsync(ct);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        Dictionary<Guid, ActivitySnapshot> activity = await LoadActivityAsync(links.Select(l => l.Id).ToList(), ct);
        return Ok(new ReviewLinkListResponse(links.Select(l => ToSummary(l, timeline, now, activity)).ToList()));
    }

    /// <summary>One link with its decision history; marks it seen by the owner (resets the unread badge).</summary>
    [HttpGet("{linkId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, Guid linkId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        EditTimelineEntity? timeline = await FindTimelineAsync(projectId, id, ct);
        if (timeline is null) return NotFound();

        EditReviewLink? link = await _db.EditReviewLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TimelineId == id && l.ProjectId == projectId, ct);
        if (link is null) return NotFound();

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        Dictionary<Guid, ActivitySnapshot> activity = await LoadActivityAsync([link.Id], ct);
        ReviewLinkSummaryResponse summary = ToSummary(link, timeline, now, activity);

        List<EditReviewDecision> history = await _db.EditReviewDecisions.AsNoTracking()
            .Where(d => d.LinkId == link.Id)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(MaxDecisionHistory)
            .ToListAsync(ct);

        ReviewRender? render = await _renders.ForExecutionAsync(projectId, id, link.RenderExecutionId, ct);

        // The summary above reflects what was unread BEFORE this view; opening the detail marks it seen.
        link.OwnerSeenAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(new ReviewLinkDetailResponse(
            summary.Id, summary.Label, summary.Status, summary.ExpiresAt, summary.CreatedAt, summary.ViewCount,
            summary.LastViewedAt, summary.Stale, link.AllowComments, render?.CreatedAt ?? link.CreatedAt,
            summary.Decision, history.Select(ToDecision).ToList(), summary.GuestComments, summary.UnreadGuestActivity));
    }

    /// <summary>Revokes the link now. <c>204</c>, idempotent; the link answers the uniform 404 from this moment.</summary>
    [HttpDelete("{linkId:guid}")]
    public async Task<IActionResult> Revoke(Guid projectId, Guid id, Guid linkId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        EditReviewLink? link = await _db.EditReviewLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.TimelineId == id && l.ProjectId == projectId, ct);
        if (link is null) return NotFound();

        if (link.RevokedAt is null)
        {
            link.RevokedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    private sealed record ActivitySnapshot(ReviewLinkDecisionResponse? Latest, int GuestComments, bool HasUnseen);

    private async Task<Dictionary<Guid, ActivitySnapshot>> LoadActivityAsync(List<Guid> linkIds, CancellationToken ct)
    {
        Dictionary<Guid, ActivitySnapshot> result = [];
        if (linkIds.Count == 0) return result;

        var seen = await _db.EditReviewLinks.AsNoTracking()
            .Where(l => linkIds.Contains(l.Id))
            .Select(l => new { l.Id, l.OwnerSeenAt })
            .ToListAsync(ct);
        Dictionary<Guid, DateTime?> since = seen.ToDictionary(s => s.Id, s => s.OwnerSeenAt);

        var comments = await _db.EditTimelineComments.AsNoTracking()
            .Where(c => c.GuestLinkId != null && linkIds.Contains(c.GuestLinkId.Value))
            .Select(c => new { LinkId = c.GuestLinkId!.Value, c.CreatedAt })
            .ToListAsync(ct);
        List<EditReviewDecision> decisions = await _db.EditReviewDecisions.AsNoTracking()
            .Where(d => linkIds.Contains(d.LinkId))
            .ToListAsync(ct);

        foreach (Guid linkId in linkIds)
        {
            // Never opened by the owner: all guest activity is unread; otherwise only what came after the last look.
            DateTime? floor = since.GetValueOrDefault(linkId);
            EditReviewDecision? latest = decisions.Where(d => d.LinkId == linkId)
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).FirstOrDefault();
            int count = comments.Count(c => c.LinkId == linkId);
            bool unseen = comments.Any(c => c.LinkId == linkId && (floor == null || c.CreatedAt > floor))
                          || decisions.Any(d => d.LinkId == linkId && (floor == null || d.CreatedAt > floor));
            result[linkId] = new ActivitySnapshot(latest is null ? null : ToDecision(latest), count, unseen);
        }

        return result;
    }

    private static ReviewLinkSummaryResponse ToSummary(
        EditReviewLink l, EditTimelineEntity timeline, DateTime now, Dictionary<Guid, ActivitySnapshot> activity)
    {
        ActivitySnapshot a = activity.GetValueOrDefault(l.Id) ?? new ActivitySnapshot(null, 0, false);
        string status = l.RevokedAt is not null ? "revoked" : l.ExpiresAt <= now ? "expired" : "active";
        return new ReviewLinkSummaryResponse(
            l.Id, l.Label, status, l.ExpiresAt, l.CreatedAt, l.ViewCount, l.LastViewedAt,
            Stale: timeline.Revision > l.PinnedRevision, a.Latest, a.GuestComments, a.HasUnseen);
    }

    internal static ReviewLinkDecisionResponse ToDecision(EditReviewDecision d) =>
        new(DecisionWord(d.Decision), d.GuestName, d.Note, d.CreatedAt);

    internal static string DecisionWord(EditReviewDecisionKind kind) =>
        kind == EditReviewDecisionKind.Approved ? "approved" : "changesRequested";

    private Task<EditTimelineEntity?> FindTimelineAsync(Guid projectId, Guid id, CancellationToken ct) =>
        _db.EditTimelines.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == projectId, ct);

    private static bool IsMp4(string fileName) =>
        fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase);

    private static string? AbsoluteOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? trimmed
            : null;
    }
}
