using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Controllers.Moderation;

/// <summary>One audit row as the platform-admin review queue shows it (never the raw content).</summary>
public sealed record ModerationReviewQueueItemResponse(
    Guid Id,
    string Surface,
    string Action,
    string ContentKind,
    string? TopCategory,
    double? TopScore,
    string? ReasonCode,
    string ReviewState,
    Guid? ProjectId,
    string? ProjectName,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>The queue page: the newest <see cref="Limit"/> rows plus the total matching the filter.</summary>
public sealed record ModerationReviewQueueResponse(
    IReadOnlyList<ModerationReviewQueueItemResponse> Items,
    int Total,
    int Limit);

/// <summary>The read-only moderation settings the admin page displays.</summary>
public sealed record ModerationSettingsResponse(
    bool Enabled,
    string Model,
    double BlockThreshold,
    double ReviewThreshold,
    IReadOnlyList<string> HardBlockCategories,
    int TimeoutSeconds,
    int AuditRetentionDays,
    bool FailClosedAtPublish);

/// <summary>
/// The platform-admin moderation review queue. Review-only by construction: acknowledging a row flips
/// <c>review_state</c> and the reviewer columns and touches nothing else. Routed under
/// <c>/api/v1/moderation</c>, NOT <c>/api/v1/admin/*</c> — nginx sends that prefix to the Go API.
/// </summary>
[ApiController]
[Route("api/v1/moderation")]
[Authorize]
public class ContentModerationReviewController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<ContentModerationOptions> options,
    TimeProvider? time = null) : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <summary>
    /// The pending review band (or every row with <c>state=all</c>), newest first. The limit is clamped to
    /// 1..500 so a client cannot ask for an unbounded audit-table read.
    /// </summary>
    [HttpGet("review-queue")]
    public async Task<ActionResult<ModerationReviewQueueResponse>> GetReviewQueue(
        [FromQuery] string? state,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        string normalized = string.IsNullOrWhiteSpace(state) ? "pending" : state.Trim().ToLowerInvariant();
        if (normalized is not ("pending" or "all"))
            return BadRequest(new { error = "state must be 'pending' or 'all'." });

        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        IQueryable<ContentModerationDecision> query = db.ContentModerationDecisions.AsNoTracking();
        if (normalized == "pending")
            query = query.Where(d => d.ReviewState == nameof(ModerationReviewState.Pending));

        int total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(d => d.CreatedAt)
            .Take(take)
            .Select(d => new
            {
                Decision = d,
                ProjectName = d.ProjectId == null
                    ? null
                    : db.Projects
                        .IgnoreQueryFilters(new[] { InferenceApiDbContext.TenantFilterName })
                        .Where(p => p.Id == d.ProjectId)
                        .Select(p => p.Name)
                        .FirstOrDefault()
            })
            .ToListAsync(ct);

        List<ModerationReviewQueueItemResponse> items = rows
            .Select(row => new ModerationReviewQueueItemResponse(
                row.Decision.Id,
                row.Decision.Surface,
                row.Decision.Action,
                row.Decision.ContentKind,
                row.Decision.TopCategory,
                row.Decision.TopScore,
                row.Decision.ReasonCode,
                row.Decision.ReviewState,
                row.Decision.ProjectId,
                row.ProjectName,
                row.Decision.CreatedAt,
                row.Decision.ExpiresAt))
            .ToList();

        return Ok(new ModerationReviewQueueResponse(items, total, take));
    }

    /// <summary>
    /// Acknowledges a review-band row. Only the audit row changes: a <c>Pending</c> row becomes
    /// <c>Acknowledged</c>, while an already-acknowledged row is a no-op (idempotent). No project, file or
    /// execution state is read or written — acknowledgement is not a moderation action.
    /// </summary>
    [HttpPost("review-queue/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        ContentModerationDecision? row = await db.ContentModerationDecisions
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return NotFound();

        if (row.ReviewState == nameof(ModerationReviewState.Pending))
        {
            row.ReviewState = nameof(ModerationReviewState.Acknowledged);
            row.ReviewedByUserId = currentUser.UserId;
            row.ReviewedAt = Now;
            await db.SaveChangesAsync(ct);
        }
        else if (row.ReviewState != nameof(ModerationReviewState.Acknowledged))
        {
            // A NotRequired row was never in the review band; acknowledging it would fabricate a review.
            return BadRequest(new { error = "Only a pending review can be acknowledged." });
        }

        return Ok(new { id = row.Id, reviewState = row.ReviewState });
    }

    /// <summary>The effective moderation configuration, read-only.</summary>
    [HttpGet("settings")]
    public ActionResult<ModerationSettingsResponse> GetSettings()
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        ContentModerationOptions value = options.Value;
        return Ok(new ModerationSettingsResponse(
            value.Enabled,
            value.Model,
            value.BlockThreshold,
            value.ReviewThreshold,
            value.EffectiveHardBlockCategories,
            value.TimeoutSeconds,
            value.AuditRetentionDays,
            value.FailClosedAtPublish));
    }
}
