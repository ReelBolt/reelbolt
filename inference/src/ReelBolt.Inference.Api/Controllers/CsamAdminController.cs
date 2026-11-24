using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The platform-admin view of the CSAM control: which scans matched or failed open, and a manual
/// re-scan. No response carries content, a storage key, a URL or a token — the same rule as the
/// audit table. It also never un-quarantines: releasing evidence is a manual, documented decision.
/// </summary>
[ApiController]
[Route("api/v1/csam/admin")]
[Authorize]
public class CsamAdminController : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICsmaHashMatcher _matcher;
    private readonly IOptions<CsamOptions> _options;

    public CsamAdminController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        ICsmaHashMatcher matcher,
        IOptions<CsamOptions> options)
    {
        _db = db;
        _currentUser = currentUser;
        _matcher = matcher;
        _options = options;
    }

    /// <summary>
    /// Scans across every tenant, newest first. <paramref name="status"/> is <c>match</c>,
    /// <c>not-scanned</c>, <c>clean</c>, <c>disabled</c> or <c>all</c>; <paramref name="before"/> pages
    /// by createdAt.
    /// </summary>
    [HttpGet("scans")]
    public async Task<IActionResult> ListScans(
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] DateTime? before,
        CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        string filter = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        if (filter is not ("all" or "match" or "not-scanned" or "clean" or "disabled"))
            return BadRequest(new { code = "INVALID_STATUS", message = "status must be all, match, not-scanned, clean or disabled." });

        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Platform admin: the tenant filter is deliberately ignored, because the question this page
        // answers is "what did the control find anywhere", not "in my workspace".
        IQueryable<CsamScanAudit> audits = _db.CsamScanAudits.IgnoreQueryFilters().AsNoTracking();
        if (filter != "all") audits = audits.Where(a => a.Status == filter);
        if (before is { } cursor)
        {
            DateTime cutoff = cursor.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(cursor, DateTimeKind.Utc)
                : cursor.ToUniversalTime();
            audits = audits.Where(a => a.CreatedAt < cutoff);
        }

        List<ScanRow> rows = await (
                from audit in audits
                join f in _db.ProjectFiles.IgnoreQueryFilters().AsNoTracking()
                    on audit.ProjectFileId equals (Guid?)f.Id into joined
                from file in joined.DefaultIfEmpty()
                orderby audit.CreatedAt descending, audit.Id descending
                select new ScanRow(
                    audit.Id,
                    audit.ProjectFileId,
                    audit.ProjectId,
                    file != null ? file.OriginalFileName : audit.ProjectFileName,
                    audit.Status,
                    audit.Classification,
                    audit.MatchType,
                    audit.NearMatchCount,
                    audit.Quarantined,
                    audit.QuarantineSucceeded,
                    audit.ErrorCode,
                    audit.ProviderVersion,
                    audit.CreatedAt))
            .Take(take + 1)
            .ToListAsync(ct);

        DateTime? nextBefore = null;
        if (rows.Count > take)
        {
            rows = rows.Take(take).ToList();
            nextBefore = rows[^1].CreatedAt;
        }

        return Ok(new { enabled = _options.Value.Arachnid.Enabled, items = rows, nextBefore });
    }

    /// <summary>
    /// Runs the matcher again for one file. A previously quarantined file stays quarantined
    /// whatever this scan says: release is manual.
    /// </summary>
    [HttpPost("files/{fileId:guid}/rescan")]
    public async Task<IActionResult> Rescan(Guid fileId, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin) return Forbid();

        ProjectFile? file = await _db.ProjectFiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound();

        CsamScanOutcome outcome = await _matcher.ScanAsync(file, _currentUser.UserId, ct);

        return Ok(new
        {
            fileId = file.Id,
            status = outcome.Status.ToString(),
            classification = outcome.Classification,
            matchType = outcome.MatchType,
            quarantined = outcome.Quarantined,
            errorCode = outcome.ErrorCode
        });
    }

    /// <summary>One audit row as the page reads it. No storage key, URL, hash or content.</summary>
    private sealed record ScanRow(
        Guid Id,
        Guid? ProjectFileId,
        Guid? ProjectId,
        string? FileName,
        string Status,
        string? Classification,
        string? MatchType,
        int? NearMatchCount,
        bool Quarantined,
        bool QuarantineSucceeded,
        string? ErrorCode,
        string? ProviderVersion,
        DateTime CreatedAt);
}
