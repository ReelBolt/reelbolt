using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Performance of published posts (docs/social-publishing.md): one post's readings and the active organization's summary.
/// Read-only. A post of another organization, or one that does not exist, is the same <c>404 post_not_found</c>. Numbers a
/// platform does not provide are null, never 0.
/// </summary>
[ApiController]
[Route("api/v1/social")]
[Authorize]
public class SocialMetricsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;

    public SocialMetricsController(InferenceApiDbContext db, ICurrentUser currentUser, TimeProvider? clock = null)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock ?? TimeProvider.System;
    }

    [HttpGet("posts/{id:guid}/metrics")]
    public async Task<ActionResult<SocialPostMetricsResponse>> PostMetrics(Guid id, CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId || _currentUser.OrgRole is null) return PostNotFound();
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();

        SocialPost? post = await _db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId, ct);
        if (post is null) return PostNotFound();

        List<SocialPostMetric> rows = await _db.SocialPostMetrics.AsNoTracking()
            .Where(m => m.PostId == post.Id)
            .OrderBy(m => m.CapturedAt)
            .ToListAsync(ct);
        List<SocialMetricReadingResponse> readings = rows.Select(ToReading).ToList();

        DateTime? next = post.Status == SocialPostStatus.Published && post.PublishedAt is { } published
            ? SocialMetricsSchedule.NextReadingAt(published, rows.Count == 0 ? null : rows[^1].CapturedAt, _clock.GetUtcNow().UtcDateTime)
            : null;
        return Ok(new SocialPostMetricsResponse(post.Id, SocialPlatforms.Slug(post.Platform), post.Status, readings, readings.LastOrDefault(), next));
    }

    /// <summary>Totals over posts published in the last <paramref name="days"/> days (1 to 30), using each post's newest reading.</summary>
    [HttpGet("metrics/summary")]
    public async Task<ActionResult<SocialMetricsSummaryResponse>> Summary([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        if (_currentUser.OrganizationId is not { } organizationId || _currentUser.OrgRole is null)
            return Ok(new SocialMetricsSummaryResponse(days, 0, Totals([]), [], []));
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();

        DateTime since = _clock.GetUtcNow().UtcDateTime.AddDays(-days);
        List<SocialPost> posts = await _db.SocialPosts.AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.Status == SocialPostStatus.Published && p.PublishedAt != null && p.PublishedAt >= since)
            .OrderByDescending(p => p.PublishedAt)
            .Take(500)
            .ToListAsync(ct);
        List<Guid> ids = posts.Select(p => p.Id).ToList();
        List<SocialPostMetric> metrics = await _db.SocialPostMetrics.AsNoTracking().Where(m => ids.Contains(m.PostId)).ToListAsync(ct);
        Dictionary<Guid, SocialPostMetric> latest = metrics
            .GroupBy(m => m.PostId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CapturedAt).First());

        List<SocialPostMetricsItemResponse> items = posts.Select(p => new SocialPostMetricsItemResponse(
            p.Id, p.ProjectId, SocialPlatforms.Slug(p.Platform), p.Title, p.PublishedAt, p.RemotePostUrl,
            latest.TryGetValue(p.Id, out SocialPostMetric? m) ? ToReading(m) : null)).ToList();

        List<SocialPlatformMetricsResponse> platforms = posts
            .GroupBy(p => p.Platform)
            .OrderBy(g => g.Key)
            .Select(g => new SocialPlatformMetricsResponse(
                SocialPlatforms.Slug(g.Key), g.Count(),
                Totals(g.Select(p => latest.GetValueOrDefault(p.Id)).Where(m => m is not null).Select(m => m!).ToList())))
            .ToList();

        return Ok(new SocialMetricsSummaryResponse(days, posts.Count, Totals(latest.Values.ToList()), platforms, items));
    }

    private static SocialMetricTotalsResponse Totals(IReadOnlyList<SocialPostMetric> rows) => new(
        Sum(rows, m => m.Views), Sum(rows, m => m.Likes), Sum(rows, m => m.Comments), Sum(rows, m => m.Shares), Sum(rows, m => m.Saves));

    /// <summary>Null when no reading carries the number (not available), otherwise the sum of those that do.</summary>
    private static long? Sum(IReadOnlyList<SocialPostMetric> rows, Func<SocialPostMetric, long?> pick)
    {
        long? total = null;
        foreach (SocialPostMetric row in rows)
        {
            if (pick(row) is { } value) total = (total ?? 0) + value;
        }

        return total;
    }

    private static SocialMetricReadingResponse ToReading(SocialPostMetric m) =>
        new(DateTime.SpecifyKind(m.CapturedAt, DateTimeKind.Utc), m.Views, m.Likes, m.Comments, m.Shares, m.Saves, m.Source);

    private NotFoundObjectResult PostNotFound() => NotFound(new { error = "post_not_found", message = "That post could not be found." });
}
