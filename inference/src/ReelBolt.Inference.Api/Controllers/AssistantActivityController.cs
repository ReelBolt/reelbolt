using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/assistant/activity</c>: what the assistant is doing, or has done, in the caller's threads while no
/// browser is attached to them, cheap enough to poll every 5 to 30 seconds. Titles only, never message content.
/// </summary>
[ApiController]
[Route("api/v1/assistant/activity")]
[Authorize]
public class AssistantActivityController : ControllerBase
{
    /// <summary>How long a response is reused for one caller and workspace.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    /// <summary>Unread replies are listed for threads updated within this window.</summary>
    public static readonly TimeSpan UnreadWindow = TimeSpan.FromDays(7);

    public const int MaxListed = 20;

    /// <summary>The response cache holds at most this many (user, workspace) entries; expired ones are dropped first.</summary>
    public const int MaxCacheEntries = 1000;

    internal static int CacheCount => Cache.Count;

    private static readonly ConcurrentDictionary<(Guid User, Guid? Org), (DateTime At, AssistantActivityDto Value)> Cache = new();

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly AssistantTurnHub? _hub;
    private readonly TimeProvider _clock;

    public AssistantActivityController(InferenceApiDbContext db, ICurrentUser currentUser, AssistantTurnHub? hub = null, TimeProvider? clock = null)
    {
        _db = db;
        _currentUser = currentUser;
        _hub = hub;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Drops the cached answer after the caller marks a thread read, so the badge clears at once.</summary>
    public static void Invalidate(Guid userId, Guid? organizationId) => Cache.TryRemove((userId, organizationId), out _);

    /// <summary>
    /// <c>writing</c>: this caller's live turns (running, or awaiting approval), from the process-local turn hub, so a turn
    /// running on another replica is not listed. <c>unread</c>: threads whose newest assistant message is above the thread's
    /// read marker. <c>pendingWatches</c>: runs the assistant is waiting on for this caller. Reused for
    /// <see cref="CacheFor"/> per caller and workspace.
    /// </summary>
    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<AssistantActivityDto>> Get(CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        if (Cache.TryGetValue((userId, orgId), out var hit) && now - hit.At < CacheFor && hit.At <= now)
        {
            Response.Headers["Cache-Control"] = "private, max-age=2";
            return Ok(hit.Value);
        }

        AssistantActivityDto value = await BuildAsync(userId, orgId, now, ct);
        if (Cache.Count >= MaxCacheEntries)
        {
            foreach (var stale in Cache.Where(e => now - e.Value.At >= CacheFor || e.Value.At > now).Select(e => e.Key).ToList())
                Cache.TryRemove(stale, out _);
            if (Cache.Count >= MaxCacheEntries)
                Cache.Clear();
        }

        Cache[(userId, orgId)] = (now, value);
        Response.Headers["Cache-Control"] = "private, max-age=2";
        return Ok(value);
    }

    private async Task<AssistantActivityDto> BuildAsync(Guid userId, Guid? orgId, DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now - UnreadWindow;

        // One query for the caller's recent threads.
        var threads = await _db.AssistantThreads.AsNoTracking()
            .Where(t => t.UserId == userId && t.OrganizationId == orgId && t.ArchivedAt == null && t.UpdatedAt >= cutoff)
            .Select(t => new { t.Id, t.Title, t.ReadThroughSequence, t.UpdatedAt })
            .ToListAsync(ct);
        List<Guid> ids = threads.Select(t => t.Id).ToList();

        // One grouped query for the newest assistant message of each.
        var newest = ids.Count == 0
            ? []
            : await _db.AssistantMessages.AsNoTracking()
                .Where(m => ids.Contains(m.ThreadId) && m.Role == "assistant")
                .GroupBy(m => m.ThreadId)
                .Select(g => new { ThreadId = g.Key, Sequence = g.Max(m => m.Sequence), At = g.Max(m => m.CreatedAt) })
                .ToListAsync(ct);

        var unreadThreads = (from n in newest
                             join t in threads on n.ThreadId equals t.Id
                             where n.Sequence > t.ReadThroughSequence
                             orderby t.UpdatedAt descending
                             select new { t.Id, t.Title, n.Sequence, n.At })
            .Take(MaxListed)
            .ToList();

        // One query for which of those answered a wake: the user turns that carry stored metadata.
        List<Guid> unreadIds = unreadThreads.Select(u => u.Id).ToList();
        var userMeta = unreadIds.Count == 0
            ? []
            : await _db.AssistantMessages.AsNoTracking()
                .Where(m => unreadIds.Contains(m.ThreadId) && m.Role == "user" && m.MetadataJson != null)
                .Select(m => new { m.ThreadId, m.Sequence, m.MetadataJson })
                .ToListAsync(ct);

        List<AssistantUnreadDto> unread = unreadThreads.Select(u =>
        {
            var before = userMeta.Where(m => m.ThreadId == u.Id && m.Sequence < u.Sequence).OrderByDescending(m => m.Sequence).FirstOrDefault();
            // The wake turn is the user message immediately before the reply: only then is the reply a wake reply.
            bool wake = before is not null && AssistantRunWakeSweeper.IsWake(before.MetadataJson) && before.Sequence == u.Sequence - 1;
            return new AssistantUnreadDto(u.Id, u.Title, u.Sequence, wake ? "run_wake" : "reply", DateTime.SpecifyKind(u.At, DateTimeKind.Utc));
        }).ToList();

        // The live turns of this caller (titles for threads older than the window come from one more query).
        IReadOnlyList<AssistantLiveTurn> live = (_hub ?? AssistantTurnHub.Shared).ListFor(userId, orgId);
        Dictionary<Guid, string> titles = threads.ToDictionary(t => t.Id, t => t.Title);
        List<Guid> missing = live.Select(l => l.ThreadId).Where(id => !titles.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var t in await _db.AssistantThreads.AsNoTracking()
                         .Where(t => missing.Contains(t.Id) && t.UserId == userId && t.OrganizationId == orgId)
                         .Select(t => new { t.Id, t.Title }).ToListAsync(ct))
                titles[t.Id] = t.Title;
        }

        List<AssistantWritingDto> writing = live
            .Where(l => titles.ContainsKey(l.ThreadId))
            .Take(MaxListed)
            .Select(l => new AssistantWritingDto(
                l.ThreadId, titles[l.ThreadId], AssistantTurnStateText.Of(l.State), DateTime.SpecifyKind(l.StartedAt, DateTimeKind.Utc), l.Kind))
            .ToList();

        int pending = await _db.AssistantRunWatches.CountAsync(
            w => w.UserId == userId && w.OrganizationId == orgId
                && (w.Status == AssistantRunWatchStatus.Pending || w.Status == AssistantRunWatchStatus.Claimed), ct);

        return new AssistantActivityDto(writing, unread, pending);
    }
}
