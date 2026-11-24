using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Caller-scoped CRUD over server-persisted assistant conversations. Deliberately
/// <b>caller-scoped rather than route-scoped</b>: the assistant is mounted globally, so there is
/// no project in the URL to hang a conversation off.
/// </summary>
/// <remarks>
/// Every thread lookup filters on <c>UserId</c> <b>inside the LINQ predicate</b>, so the ownership
/// filter is applied by the database before anything is materialized. A thread id that exists but
/// belongs to another user is reported as <b>404, never 403</b>: a 403 would confirm the thread
/// exists to a user who has no right to know that.
/// </remarks>
[ApiController]
[Route("api/v1/assistant/threads")]
[Authorize]
public class AssistantThreadsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly AssistantTurnRegistry? _turns;
    private readonly AssistantTurnHub? _turnHub;

    /// <summary>Set by nginx on the first attempt of a turn-state probe so a slot with no live turn can answer 404 and fall back.</summary>
    internal const string SlotFallbackHeader = "X-ReelBolt-Slot-Fallback";
    private readonly IProjectAccessService _projectAccess;

    public AssistantThreadsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        AssistantTurnRegistry? turns = null,
        AssistantTurnHub? turnHub = null)
    {
        _projectAccess = projectAccess;
        _db = db;
        _currentUser = currentUser;
        _turns = turns;
        _turnHub = turnHub;
    }

    /// <summary>
    /// Lists the caller's non-archived threads, most recently updated first.
    /// </summary>
    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<IReadOnlyList<AssistantThreadSummaryDto>>> ListThreads(CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        var rows = await _db.AssistantThreads
            .Where(t => t.UserId == userId && t.OrganizationId == orgId && t.ArchivedAt == null && t.Messages.Any())
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.ProjectId,
                t.UpdatedAt,
                LastRole = t.Messages.OrderByDescending(m => m.Sequence).Select(m => m.Role).FirstOrDefault()
            })
            .ToListAsync(ct);

        List<AssistantThreadSummaryDto> threads = rows
            .Select(t =>
            {
                bool inProgress = _turns?.IsInFlight(t.Id) ?? false;
                return new AssistantThreadSummaryDto(
                    t.Id, t.Title, t.ProjectId, t.UpdatedAt,
                    AwaitingReply: t.LastRole == "user" && !inProgress,
                    ReplyInProgress: inProgress);
            })
            .ToList();

        return Ok(threads);
    }

    /// <summary>
    /// Creates a thread owned by the caller and returns its id.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CreatedAssistantThreadResponse>> CreateThread(
        [FromBody] CreateAssistantThreadRequest? request,
        CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;
        DateTime now = DateTime.UtcNow;

        Guid? projectId = request?.ProjectId is { } requested && requested != Guid.Empty
            ? requested
            : null;

        // A project outside the caller's active organization is dropped rather than rejected, so a page handing the
        // assistant a stale or foreign project id cannot make conversation history unopenable.
        if (projectId.HasValue &&
            !(await _projectAccess.GetAsync(projectId.Value, ProjectPermission.View, ct)).IsFound)
        {
            projectId = null;
        }

        // Opening the drawer must not litter the list: with no explicit title, hand back the caller's
        // newest EMPTY thread instead of creating another, and sweep out stale empty ones. Empty
        // threads are also hidden from the list.
        if (string.IsNullOrWhiteSpace(request?.Title))
        {
            List<AssistantThread> empties = await _db.AssistantThreads
                .Where(t => t.UserId == userId && t.OrganizationId == orgId && t.ArchivedAt == null && !t.Messages.Any())
                .OrderByDescending(t => t.UpdatedAt)
                .ToListAsync(ct);

            AssistantThread? reusable = empties.FirstOrDefault();
            DateTime cutoff = now.AddHours(-1);
            _db.AssistantThreads.RemoveRange(empties.Skip(1).Where(t => t.UpdatedAt < cutoff));

            if (reusable is not null)
            {
                reusable.ProjectId = projectId;
                reusable.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                return CreatedAtAction(
                    nameof(GetThread),
                    new { threadId = reusable.Id },
                    new CreatedAssistantThreadResponse(reusable.Id));
            }
        }

        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = ActiveOrganization.Require(_currentUser),
            ProjectId = projectId,
            Title = string.IsNullOrWhiteSpace(request?.Title) ? "New chat" : request!.Title!.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.AssistantThreads.Add(thread);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(
            nameof(GetThread),
            new { threadId = thread.Id },
            new CreatedAssistantThreadResponse(thread.Id));
    }

    /// <summary>
    /// Returns one thread with its full ordered history.
    /// </summary>
    [HttpGet("{threadId:guid}")]
    public async Task<ActionResult<AssistantThreadDetailDto>> GetThread(Guid threadId, CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        AssistantThread? thread = await _db.AssistantThreads
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == orgId, ct);

        if (thread is null)
            return NotFound();

        var pending = await _db.AssistantRunWatches.AsNoTracking()
            .Where(w => w.ThreadId == thread.Id && (w.Status == AssistantRunWatchStatus.Pending || w.Status == AssistantRunWatchStatus.Claimed))
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.WorkflowName, w.CreatedAt })
            .ToListAsync(ct);

        return Ok(ToDetail(thread) with
        {
            ReplyInProgress = _turns?.IsInFlight(thread.Id) ?? false,
            PendingRunWatches = pending.Select(w => new AssistantPendingRunWatchDto(AssistantRunWakeText.Name(w.WorkflowName), w.CreatedAt)).ToList()
        });
    }

    /// <summary>
    /// The state of the caller's thread's most recent turn, so a reopening client can tell an idle thread
    /// from one whose turn is still running and re-attach to it (<c>POST /api/v1/assistant/chat</c> with
    /// <c>attach: true</c>). Never a turn's content — only its state and the ids to name.
    /// </summary>
    /// <remarks>
    /// <c>idle</c> when this process holds no turn for the thread (its turn never started here, it was
    /// trimmed, or the process restarted); otherwise <c>running</c>, <c>awaiting_approval</c>,
    /// <c>completed</c> or <c>failed</c>. A foreign or unknown thread is a 404, never a 403.
    /// </remarks>
    [HttpGet("{threadId:guid}/turn")]
    public async Task<ActionResult<AssistantTurnStateDto>> GetTurn(Guid threadId, CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        bool ownsThread = await _db.AssistantThreads
            .AnyAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == orgId, ct);

        if (!ownsThread)
            return NotFound();

        int pendingWatches = await _db.AssistantRunWatches.CountAsync(
            w => w.ThreadId == threadId && (w.Status == AssistantRunWatchStatus.Pending || w.Status == AssistantRunWatchStatus.Claimed), ct);

        AssistantLiveTurn? live = (_turnHub ?? AssistantTurnHub.Shared).FindFor(threadId, userId, orgId);
        if (live is null)
        {
            var idle = new AssistantTurnStateDto(AssistantTurnStateText.Idle, PendingWatches: pendingWatches);

            // During a slot drain nginx sets this header itself (overwriting any client's) so an "idle" from the
            // new slot becomes a 404 it can retry on the previous slot. Reached only after the ownership check,
            // so it never reveals a thread the caller does not own; a client sending it just gets the 404 it asked for.
            if (Request?.Headers[SlotFallbackHeader].ToString() == "1")
                return NotFound(idle);

            return Ok(idle);
        }

        return Ok(new AssistantTurnStateDto(
            AssistantTurnStateText.Of(live.State),
            live.TurnId,
            live.MessageId,
            live.PendingApprovalId,
            pendingWatches));
    }

    /// <summary>
    /// Renames and/or archives the caller's thread. A null field is left unchanged, so a rename
    /// never implicitly un-archives.
    /// </summary>
    [HttpPatch("{threadId:guid}")]
    public async Task<ActionResult<AssistantThreadSummaryDto>> UpdateThread(
        Guid threadId,
        [FromBody] UpdateAssistantThreadRequest request,
        CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        AssistantThread? thread = await _db.AssistantThreads
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == orgId, ct);

        if (thread is null)
            return NotFound();

        if (request.Title is { } title)
            thread.Title = string.IsNullOrWhiteSpace(title) ? thread.Title : title.Trim();

        if (request.Archived is { } archived)
            thread.ArchivedAt = archived ? DateTime.UtcNow : null;

        thread.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new AssistantThreadSummaryDto(thread.Id, thread.Title, thread.ProjectId, thread.UpdatedAt));
    }

    /// <summary>
    /// Marks the thread read through a message sequence (default: the latest). Monotonic: it never lowers the marker, so a
    /// late or repeated call is harmless. Owner-only; a foreign or unknown thread is a 404. Opening a thread does NOT call
    /// this: the client does, because only it knows whether the panel is visible.
    /// </summary>
    [HttpPost("{threadId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid threadId, [FromBody] MarkAssistantThreadReadRequest? request, CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        AssistantThread? thread = await _db.AssistantThreads
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == orgId, ct);
        if (thread is null)
            return NotFound();

        int latest = await _db.AssistantMessages.Where(m => m.ThreadId == threadId).Select(m => (int?)m.Sequence).MaxAsync(ct) ?? 0;
        int through = Math.Min(request?.ThroughSequence ?? latest, latest);
        if (through > thread.ReadThroughSequence)
        {
            thread.ReadThroughSequence = through;
            await _db.SaveChangesAsync(ct);
        }

        AssistantActivityController.Invalidate(userId, orgId);
        return NoContent();
    }

    /// <summary>
    /// Deletes the caller's thread and its messages.
    /// </summary>
    [HttpDelete("{threadId:guid}")]
    public async Task<IActionResult> DeleteThread(Guid threadId, CancellationToken ct)
    {
        Guid userId = _currentUser.UserId;
        Guid? orgId = _currentUser.OrganizationId;

        // Messages are included so the cascade runs from the change tracker on every provider —
        // the relational cascade only fires in the database, which InMemory does not emulate.
        AssistantThread? thread = await _db.AssistantThreads
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.OrganizationId == orgId, ct);

        if (thread is null)
            return NotFound();

        // A workflow the assistant created in this thread keeps no pointer to it (no foreign key by design).
        foreach (WorkflowDefinition created in await _db.WorkflowDefinitions.Where(w => w.CreatedInThreadId == threadId).ToListAsync(ct))
            created.CreatedInThreadId = null;

        // The thread's run watches go with it (the foreign key cascades in the database; explicit for providers that do not).
        _db.AssistantRunWatches.RemoveRange(await _db.AssistantRunWatches.Where(w => w.ThreadId == threadId).ToListAsync(ct));
        _db.AssistantThreads.Remove(thread);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static AssistantThreadDetailDto ToDetail(AssistantThread thread) =>
        new(
            thread.Id,
            thread.Title,
            thread.ProjectId,
            thread.Messages
                .OrderBy(m => m.Sequence)
                .Select(m => ToMessageDto(m))
                .ToList());

    /// <summary>A message plus, for a platform-written wake turn, its <c>kind</c> and <c>wake</c> (read from the stored metadata, never from the content).</summary>
    private static AssistantMessageDto ToMessageDto(AssistantMessage m)
    {
        if (m.Role == "user" && AssistantRunWakeSweeper.IsWake(m.MetadataJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(m.MetadataJson!);
                string status = doc.RootElement.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "Cancelled";
                string label = doc.RootElement.TryGetProperty("runLabel", out JsonElement rl) && rl.ValueKind == JsonValueKind.String ? rl.GetString()! : "workflow";
                return new AssistantMessageDto(m.Id, m.Role, m.Content, m.Sequence, m.CreatedAt, "run_wake", new AssistantWakeDto(status, label));
            }
            catch (JsonException)
            {
            }
        }

        return new AssistantMessageDto(m.Id, m.Role, m.Content, m.Sequence, m.CreatedAt);
    }
}
