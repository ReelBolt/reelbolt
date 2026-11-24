using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Guest review links, public side (Wave 4, <c>docs/design/guest-review-links.md</c>): what someone holding the
/// URL can do, with no account, no cookie and no ambient authority.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is the credential and is treated like a password.</b> It is shape-checked before any database
/// access, looked up only by its SHA-256, never logged (this controller takes no logger; the nginx log format omits
/// the URI; the telemetry redaction processor rewrites the path), and never echoed. <b>Every failure is the same
/// empty 404</b>: malformed, unknown, expired, revoked, timeline or render gone, project or organization gone,
/// creator no longer allowed. Rate-limit and cap answers exist only AFTER the token proved live, so they are no oracle.
/// </para>
/// <para>
/// <b>One file.</b> <see cref="Media"/> streams the link's pinned render and nothing else; the storage key comes from
/// the database row, never from the request. A guest can add comments and decisions, never edit or delete.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v1/review-links/{token}")]
[ReviewLinkPublicResponse]
public sealed class ReviewLinksPublicController : ControllerBase
{
    private const int MaxCommentChars = 2000;
    private const int MaxNameChars = 80;
    private const int MaxNoteChars = 1000;
    private const int MaxBodyBytes = 16 * 1024;
    private const long FallbackMaxFrame = 10_000_000;

    private readonly InferenceApiDbContext _db;
    private readonly ReviewLinkResolver _resolver;
    private readonly ReviewLinkWriteLimiter _limiter;
    private readonly ReviewLinkActivityNotifier _notifier;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ReviewLinkOptions _options;
    private readonly PublishModeration? _publishModeration;
    private readonly TimeProvider _clock;

    public ReviewLinksPublicController(
        InferenceApiDbContext db,
        ReviewLinkResolver resolver,
        ReviewLinkWriteLimiter limiter,
        ReviewLinkActivityNotifier notifier,
        IAmazonS3 s3,
        IConfiguration configuration,
        IOptions<ReviewLinkOptions>? options = null,
        PublishModeration? publishModeration = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _resolver = resolver;
        _limiter = limiter;
        _notifier = notifier;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _options = options?.Value ?? new ReviewLinkOptions();
        _publishModeration = publishModeration;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The review page's data. Counts a view (at most one per minute per link).</summary>
    [HttpGet]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        LiveReviewLink? live = await _resolver.ResolveAsync(token, ct);
        if (live is null) return UniformNotFound();
        (EditReviewLink link, EditTimelineEntity timeline, ReviewRender render) = (live.Link, live.Timeline, live.Render);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (link.LastViewedAt is null || now - link.LastViewedAt.Value >= ReviewLinkOptions.ViewCountInterval)
        {
            link.ViewCount++;
            link.LastViewedAt = now;
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* a lost view count never fails the page */ }
        }

        // Only what this link's guests wrote, plus replies (any author) to those threads: never member top-level notes,
        // other links' comments or other threads.
        IQueryable<Guid> guestRoots = _db.EditTimelineComments.AsNoTracking()
            .Where(c => c.TimelineId == timeline.Id && c.ProjectId == link.ProjectId && c.GuestLinkId == link.Id)
            .Select(c => c.Id);
        List<EditTimelineComment> rows = await _db.EditTimelineComments.AsNoTracking()
            .Where(c => c.TimelineId == timeline.Id && c.ProjectId == link.ProjectId
                && (c.GuestLinkId == link.Id || (c.ParentId != null && guestRoots.Contains(c.ParentId.Value))))
            .OrderBy(c => c.Frame).ThenBy(c => c.CreatedAt)
            .Take(EditTimelineCommentRules.MaxCommentsPerTimeline)
            .ToListAsync(ct);

        Guid[] memberIds = rows.Where(c => c.GuestLinkId is null).Select(c => c.AuthorUserId).Distinct().ToArray();
        Dictionary<Guid, string?> names = memberIds.Length == 0
            ? []
            : (await _db.ApplicationUsers.AsNoTracking().Where(u => memberIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct))
                .ToDictionary(u => u.Id, u => (string?)u.DisplayName);

        EditReviewDecision? decision = await LatestDecisionAsync(link.Id, ct);
        return Ok(new PublicReviewResponse(
            Title: timeline.Name,
            ExpiresAt: link.ExpiresAt,
            AllowComments: link.AllowComments,
            RenderCreatedAt: render.CreatedAt,
            Stale: timeline.Revision > link.PinnedRevision,
            DurationSec: render.DurationSec,
            Comments: rows.Select(c => ToComment(c, names)).ToList(),
            Decision: decision is null ? null : new PublicReviewDecisionResponse(ReviewLinksController.DecisionWord(decision.Decision), decision.GuestName, decision.CreatedAt),
            Rate: TimelineRate(timeline)));
    }

    /// <summary>The pinned render's mp4, streamed with Range support. The only file a token can reach.</summary>
    [HttpGet("media")]
    public async Task<IActionResult> Media(string token, CancellationToken ct)
    {
        LiveReviewLink? live = await _resolver.ResolveAsync(token, ct);
        if (live is null) return UniformNotFound();
        ReviewRender render = live.Render;

        // mp4 only: anything else (a retargeted or odd output) is the uniform 404.
        string contentType = OutputContentType(render.FileName);
        if (contentType != "video/mp4") return UniformNotFound();

        // Serving to a guest is publication; the identity cache makes this cheap after the first chunk. Fail closed;
        // the answer is the uniform 404 so a block is no oracle either.
        if (_publishModeration is not null)
        {
            ModerationAttribution attribution = new(
                live.Link.OrganizationId, live.Link.ProjectId, live.Link.CreatedByUserId, render.ExecutionId, render.StepResultId,
                ModerationSurface.Publish, ModerationContentKind.Video);
            PublishModerationOutcome outcome = await _publishModeration.EvaluateObjectAsync(
                live.Link.ProjectId, render.StorageKey, contentType, attribution, ct);
            if (!outcome.Allowed) return UniformNotFound();
        }

        try
        {
            IActionResult result = await RangedObjectStreamer.StreamAsync(
                this, _s3, _bucket, render.StorageKey, contentType, render.FileName, ct);
            // inline, and without the file name (it is derived from the owner's timeline name).
            Response.Headers[HeaderNames.ContentDisposition] = "inline";
            return result;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                                           ex.ErrorCode is "NoSuchKey" or "NotFound")
        {
            return UniformNotFound();
        }
    }

    /// <summary>A guest note (or reply) at a frame of the pinned render's timeline.</summary>
    [HttpPost("comments")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> AddComment(string token, [FromBody] PublicCreateReviewCommentRequest? request, CancellationToken ct)
    {
        LiveReviewLink? live = await _resolver.ResolveAsync(token, ct);
        if (live is null || !live.Link.AllowComments) return UniformNotFound();
        (EditReviewLink link, EditTimelineEntity timeline, ReviewRender render) = (live.Link, live.Timeline, live.Render);
        if (request is null) return BadRequest(new ReviewLinkMessage("Send a name and a note."));

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (!_limiter.TryAcquire(link.Id, now, _options.GuestCommentsPerMinute)) return TooManyRequests();

        List<string> errors = [];
        string? name = ReviewGuestText.NormalizeName(request.Name);
        if (name is null) errors.Add("Enter your name.");
        else if (name.Length > MaxNameChars) errors.Add($"Your name can be at most {MaxNameChars} characters.");

        string? body = ReviewGuestText.NormalizeBody(request.Body);
        if (body is null) errors.Add("Write a note first.");
        else if (body.Length > MaxCommentChars) errors.Add($"A note can be at most {MaxCommentChars} characters.");

        long maxFrame = MaxFrame(timeline, render);
        if (request.Frame < 0 || request.Frame > maxFrame) errors.Add("That moment is outside the video.");
        if (request.DurationFrames is { } d && (d < 1 || d > maxFrame || request.Frame + d > maxFrame + 1))
            errors.Add("That span is outside the video.");
        if (errors.Count > 0) return BadRequest(new { message = errors[0], errors });

        long frame = request.Frame;
        long? duration = request.DurationFrames;
        if (request.ParentId is { } parentId)
        {
            EditTimelineComment? parent = await _db.EditTimelineComments.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == parentId && c.TimelineId == timeline.Id && c.ProjectId == link.ProjectId, ct);
            if (parent is null) return BadRequest(new ReviewLinkMessage("The note you are replying to is no longer there."));
            if (parent.ParentId is not null) return BadRequest(new ReviewLinkMessage("Reply to the first note of the conversation."));
            frame = parent.Frame;
            duration = null;
        }

        int guestCount = await _db.EditTimelineComments.CountAsync(c => c.GuestLinkId == link.Id, ct);
        if (guestCount >= _options.MaxGuestCommentsPerLink)
            return Conflict(new ReviewLinkMessage("This link has reached its limit of notes.", "NOTE_LIMIT"));
        if (await _db.EditTimelineComments.CountAsync(c => c.TimelineId == timeline.Id, ct) >= EditTimelineCommentRules.MaxCommentsPerTimeline)
            return Conflict(new ReviewLinkMessage("This review has reached its limit of notes.", "NOTE_LIMIT"));

        EditTimelineComment row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = link.ProjectId,
            TimelineId = timeline.Id,
            ParentId = request.ParentId,
            AuthorUserId = Guid.Empty,
            AuthorName = name!,
            GuestLinkId = link.Id,
            GuestName = name,
            Frame = frame,
            DurationFrames = duration,
            Body = body!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.EditTimelineComments.Add(row);
        await _db.SaveChangesAsync(ct);

        await _notifier.NotifyCommentAsync(link, timeline.Name, name!, ct);
        return StatusCode(StatusCodes.Status201Created, ToComment(row, []));
    }

    /// <summary>Records "approved" or "changes requested". Append-only; the latest row is the link's state.</summary>
    [HttpPost("decision")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Decide(string token, [FromBody] PublicReviewDecisionRequest? request, CancellationToken ct)
    {
        LiveReviewLink? live = await _resolver.ResolveAsync(token, ct);
        if (live is null) return UniformNotFound();
        (EditReviewLink link, EditTimelineEntity timeline) = (live.Link, live.Timeline);
        if (request is null) return BadRequest(new ReviewLinkMessage("Choose approve or request changes."));

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (!_limiter.TryAcquire(link.Id, now, _options.GuestCommentsPerMinute)) return TooManyRequests();

        EditReviewDecisionKind? kind = request.Decision?.Trim() switch
        {
            "approved" => EditReviewDecisionKind.Approved,
            "changesRequested" => EditReviewDecisionKind.ChangesRequested,
            _ => null
        };
        if (kind is null) return BadRequest(new ReviewLinkMessage("Choose approve or request changes."));

        string? name = ReviewGuestText.NormalizeName(request.Name);
        if (name is null) return BadRequest(new ReviewLinkMessage("Enter your name."));
        if (name.Length > MaxNameChars) return BadRequest(new ReviewLinkMessage($"Your name can be at most {MaxNameChars} characters."));

        string? note = ReviewGuestText.NormalizeBody(request.Note);
        if (note is not null && note.Length > MaxNoteChars)
            return BadRequest(new ReviewLinkMessage($"A note can be at most {MaxNoteChars} characters."));

        if (kind == EditReviewDecisionKind.ChangesRequested && note is null
            && !await _db.EditTimelineComments.AnyAsync(c => c.GuestLinkId == link.Id, ct))
        {
            return BadRequest(new ReviewLinkMessage("Say what to change, or leave a note on the video first."));
        }

        if (await _db.EditReviewDecisions.CountAsync(d => d.LinkId == link.Id, ct) >= ReviewLinkOptions.MaxDecisionsPerLink)
            return Conflict(new ReviewLinkMessage("This link has reached its limit of answers.", "DECISION_LIMIT"));

        EditReviewDecision row = new()
        {
            Id = Guid.NewGuid(),
            LinkId = link.Id,
            ProjectId = link.ProjectId,
            TimelineId = timeline.Id,
            Decision = kind.Value,
            GuestName = name,
            Note = note,
            CreatedAt = now,
        };
        _db.EditReviewDecisions.Add(row);
        await _db.SaveChangesAsync(ct);

        await _notifier.NotifyDecisionAsync(link, timeline.Name, kind.Value, name, ct);
        return StatusCode(StatusCodes.Status201Created, new ReviewLinkDecisionResponse(
            ReviewLinksController.DecisionWord(kind.Value), row.GuestName, row.Note, row.CreatedAt));
    }

    private async Task<EditReviewDecision?> LatestDecisionAsync(Guid linkId, CancellationToken ct) =>
        await _db.EditReviewDecisions.AsNoTracking()
            .Where(d => d.LinkId == linkId)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The one answer to every failure: <c>404</c> with an empty JSON object, identical for all causes.</summary>
    private static ContentResult UniformNotFound() =>
        new() { StatusCode = StatusCodes.Status404NotFound, ContentType = "application/json", Content = "{}" };

    private ObjectResult TooManyRequests()
    {
        Response.Headers[HeaderNames.RetryAfter] = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests,
            new ReviewLinkMessage("You are adding notes very quickly. Wait a minute and try again.", "RATE_LIMITED"));
    }

    private static PublicReviewCommentResponse ToComment(EditTimelineComment c, Dictionary<Guid, string?> memberNames)
    {
        bool guest = c.GuestLinkId is not null;
        string author = guest
            ? c.GuestName ?? c.AuthorName
            : PublicMemberName(memberNames.GetValueOrDefault(c.AuthorUserId));
        return new PublicReviewCommentResponse(c.Id, c.ParentId, author, guest, c.Body, c.Frame, c.DurationFrames, c.Resolved, c.CreatedAt);
    }

    /// <summary>A member shows by display name only; an address (or nothing) becomes "Team member".</summary>
    private static string PublicMemberName(string? displayName)
    {
        string? name = ReviewGuestText.NormalizeName(displayName);
        return name is null || name.Contains('@') ? "Team member" : (name.Length > 80 ? name[..80] : name);
    }

    private static string OutputContentType(string fileName) =>
        fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
            ? "video/mp4"
            : "application/octet-stream";

    /// <summary>The last frame a guest may anchor a note to: the render's length at the timeline's rate, else a wide bound.</summary>
    private static long MaxFrame(EditTimelineEntity timeline, ReviewRender render)
    {
        if (render.DurationSec is not { } seconds) return FallbackMaxFrame;
        PublicReviewRate rate = TimelineRate(timeline);
        double fps = (double)rate.Num / rate.Den;
        return Math.Clamp((long)Math.Ceiling(seconds * fps) + 1, 1, FallbackMaxFrame);
    }

    /// <summary>The timeline's frame rate from its document, 30 fps when it cannot be read.</summary>
    private static PublicReviewRate TimelineRate(EditTimelineEntity timeline)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(timeline.DocumentJson);
            if (doc.RootElement.TryGetProperty("settings", out JsonElement settings)
                && settings.TryGetProperty("rate", out JsonElement rate)
                && rate.TryGetProperty("num", out JsonElement num) && rate.TryGetProperty("den", out JsonElement den)
                && num.TryGetInt32(out int n) && den.TryGetInt32(out int dn) && n > 0 && dn > 0 && n <= 1000 && dn <= 1001)
                return new PublicReviewRate(n, dn);
        }
        catch (JsonException) { /* default rate */ }

        return new PublicReviewRate(30, 1);
    }
}

/// <summary>
/// Headers on EVERY response of the public review controller, including the uniform 404 and framework-generated
/// 400s: no caching, no referrer, no sniffing, no indexing. There are no CORS headers (the page is same-origin).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ReviewLinkPublicResponseAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        IHeaderDictionary h = context.HttpContext.Response.Headers;
        h[HeaderNames.CacheControl] = "private, no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Robots-Tag"] = "noindex, nofollow";
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
