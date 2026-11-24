using ReelBolt.Inference.Api.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Read-only, owner-scoped aggregates that power the dashboard and the projects list in one call,
/// so the client does not have to fan out across projects x workflows x executions.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private const int ActiveCap = 20;
    private const int RecentExecutionCount = 10;
    private const int RecentOutputCount = 8;
    private const int AttentionCap = 5;
    private const int AttentionScanWindow = 60;
    private const int TextCap = 240;
    private static readonly TimeSpan AttentionWindow = TimeSpan.FromDays(14);

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public DashboardController(InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    [HttpGet("overview")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<DashboardOverviewResponse>> Overview(CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;

        Guid userId = _currentUser.UserId;

        // The tenant filter scopes this to the active organization.
        List<Project> projects = await _db.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);

        if (projects.Count == 0)
        {
            return Ok(new DashboardOverviewResponse([], [], [], [], [], new DashboardTotals(0, 0, 0, 0, 0)));
        }

        Dictionary<Guid, string> projectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        List<Guid> projectIds = projects.Select(p => p.Id).ToList();

        // ---- per-project file aggregates (source media only: renders are counted from step results) ----
        var fileRows = await _db.ProjectFiles
            .AsNoTracking()
            .Where(f => projectIds.Contains(f.ProjectId) && f.Category != "outputFiles")
            .GroupBy(f => f.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key,
                Bytes = g.Sum(f => f.SizeBytes),
                Video = g.Count(f => f.MimeType.StartsWith("video/")),
                Audio = g.Count(f => f.MimeType.StartsWith("audio/")),
                Image = g.Count(f => f.MimeType.StartsWith("image/")),
                LastUpload = g.Max(f => f.UploadedAt),
            })
            .ToListAsync(ct);
        Dictionary<Guid, int> sourceCounts = await _db.ProjectFiles
            .AsNoTracking()
            .Where(f => projectIds.Contains(f.ProjectId) && f.Category != "outputFiles")
            .GroupBy(f => f.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        Dictionary<Guid, int> workflowCounts = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => projectIds.Contains(w.ProjectId) && w.Kind == WorkflowDefinitionKind.Standard)
            .GroupBy(w => w.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        var executionRows = await _db.WorkflowExecutions
            .AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId))
            .GroupBy(e => e.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key,
                Active = g.Count(e => e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Queued),
                Failed = g.Count(e => e.Status == ExecutionStatus.Failed),
                LastStarted = g.Max(e => e.StartedAt),
                LastCompleted = g.Max(e => e.CompletedAt),
            })
            .ToListAsync(ct);

        // ---- outputs (playable renders) ----
        string anyOutputMarker = "/outputFiles/";
        var outputRows = await _db.WorkflowStepResults
            .AsNoTracking()
            .Where(r =>
                r.OutputStorageKey != null &&
                r.OutputStorageKey.StartsWith("projects/") &&
                r.OutputStorageKey.Contains(anyOutputMarker) &&
                projectIds.Contains(r.WorkflowExecution.ProjectId))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => new
            {
                r.Id,
                r.WorkflowExecutionId,
                ProjectId = r.WorkflowExecution.ProjectId,
                Key = r.OutputStorageKey!,
                At = r.CompletedAt ?? r.ExecutedAt,
                r.ExecutedAt,
                RunStartedAt = r.WorkflowExecution.StartedAt,
                RunFirstStartedAt = r.WorkflowExecution.FirstStartedAt,
            })
            .Take(300)
            .ToListAsync(ct);

        // Scope check mirrors OutputsController: the key must live under THIS project's outputFiles prefix.
        // An output that is not served is not counted and is never the latest.
        var holds = await ReelBolt.Shared.Storage.ProjectOutputHold.ReadManyAsync(
            _db, outputRows.Select(o => o.ProjectId).Distinct().ToList(), ct);
        var outputs = outputRows
            .Where(o => o.Key.StartsWith($"projects/{o.ProjectId}/outputFiles/", StringComparison.Ordinal))
            .Where(o => !(holds.TryGetValue(o.ProjectId, out var hold) && hold.IsActive && hold.HoldsOutput(o.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(o.RunFirstStartedAt, o.RunStartedAt))))
            .ToList();

        Dictionary<Guid, int> renderCounts = outputs.GroupBy(o => o.ProjectId).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<Guid, OverviewOutput> latestOutput = outputs
            .GroupBy(o => o.ProjectId)
            .ToDictionary(g => g.Key, g =>
            {
                var o = g.First();
                return new OverviewOutput(o.Id, o.ProjectId, projectNames[o.ProjectId], Path.GetFileName(o.Key), o.At);
            });

        List<ProjectOverview> projectOverviews = projects.Select(p =>
        {
            var f = fileRows.FirstOrDefault(x => x.ProjectId == p.Id);
            var e = executionRows.FirstOrDefault(x => x.ProjectId == p.Id);
            DateTime lastActivity = new[]
            {
                p.UpdatedAt,
                f?.LastUpload ?? DateTime.MinValue,
                e?.LastStarted ?? DateTime.MinValue,
                e?.LastCompleted ?? DateTime.MinValue,
                latestOutput.TryGetValue(p.Id, out OverviewOutput? lo) ? lo.ProducedAt : DateTime.MinValue,
            }.Max();

            return new ProjectOverview(
                p.Id,
                sourceCounts.GetValueOrDefault(p.Id),
                f?.Video ?? 0,
                f?.Audio ?? 0,
                f?.Image ?? 0,
                f?.Bytes ?? 0,
                workflowCounts.GetValueOrDefault(p.Id),
                renderCounts.GetValueOrDefault(p.Id),
                e?.Active ?? 0,
                e?.Failed ?? 0,
                lastActivity,
                latestOutput.GetValueOrDefault(p.Id));
        }).ToList();

        // ---- executions ----
        List<ExecutionRow> activeRows = await QueryExecutions(
            _db.WorkflowExecutions.Where(e => projectIds.Contains(e.ProjectId) &&
                (e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Queued)),
            ActiveCap, ct);

        List<ExecutionRow> finishedRows = await QueryExecutions(
            _db.WorkflowExecutions.Where(e => projectIds.Contains(e.ProjectId) &&
                (e.Status == ExecutionStatus.Passed || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled)),
            Math.Max(AttentionScanWindow, RecentExecutionCount), ct);

        Dictionary<Guid, (Guid StepResultId, string FileName)> firstOutputByExecution = outputs
            .GroupBy(o => o.WorkflowExecutionId)
            .ToDictionary(g => g.Key, g => (g.First().Id, Path.GetFileName(g.First().Key)));

        DashboardExecution Map(ExecutionRow r)
        {
            firstOutputByExecution.TryGetValue(r.Id, out var output);
            return new DashboardExecution(
                r.Id,
                r.ProjectId,
                projectNames.GetValueOrDefault(r.ProjectId, string.Empty),
                r.WorkflowDefinitionId,
                r.WorkflowName,
                r.Status.ToString(),
                r.StartedAt,
                r.CompletedAt,
                Truncate(r.ErrorMessage),
                Truncate(r.UserRequest),
                output.FileName is null ? null : output.StepResultId,
                output.FileName,
                r.Status == ExecutionStatus.Passed ? ExecutionAttention.TryParse(r.AttentionJson) : null);
        }

        List<DashboardExecution> active = activeRows.Select(Map).ToList();
        List<DashboardExecution> recent = finishedRows.Take(RecentExecutionCount).Select(Map).ToList();

        // "Needs attention" = the most recent finished run of a workflow failed (a later pass clears it).
        DateTime cutoff = DateTime.UtcNow - AttentionWindow;
        List<DashboardExecution> attention = finishedRows
            .GroupBy(r => r.WorkflowDefinitionId)
            .Select(g => g.First())
            .Where(r => r.Status == ExecutionStatus.Failed && (r.At ?? DateTime.MinValue) >= cutoff)
            .Take(AttentionCap)
            .Select(Map)
            .ToList();

        DateTime weekAgo = DateTime.UtcNow.AddDays(-7);
        int passedWeek = await _db.WorkflowExecutions.CountAsync(
            e => projectIds.Contains(e.ProjectId) && e.Status == ExecutionStatus.Passed && e.CompletedAt >= weekAgo, ct);
        int failedWeek = await _db.WorkflowExecutions.CountAsync(
            e => projectIds.Contains(e.ProjectId) && e.Status == ExecutionStatus.Failed && e.CompletedAt >= weekAgo, ct);

        DashboardTotals totals = new(
            projects.Count,
            projectOverviews.Sum(p => p.SourceFileCount),
            renderCounts.Values.Sum(),
            passedWeek,
            failedWeek);

        List<OverviewOutput> recentOutputs = outputs
            .Take(RecentOutputCount)
            .Select(o => new OverviewOutput(o.Id, o.ProjectId, projectNames[o.ProjectId], Path.GetFileName(o.Key), o.At))
            .ToList();

        return Ok(new DashboardOverviewResponse(projectOverviews, active, recent, attention, recentOutputs, totals));
    }

    private static async Task<List<ExecutionRow>> QueryExecutions(IQueryable<WorkflowExecution> query, int take, CancellationToken ct)
    {
        List<ExecutionRow> rows = await query
            .AsNoTracking()
            .Select(e => new ExecutionRow(
                e.Id,
                e.ProjectId,
                e.WorkflowDefinitionId,
                e.WorkflowDefinition.Name,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                e.ErrorMessage,
                e.UserRequest,
                e.AttentionJson))
            .ToListAsync(ct);

        // Ordered client-side: StartedAt is null for queued rows, and the page is small and capped.
        return rows
            .OrderByDescending(r => r.At ?? DateTime.MinValue)
            .Take(take)
            .ToList();
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length <= TextCap ? trimmed : trimmed[..TextCap] + "...";
    }

    private sealed record ExecutionRow(
        Guid Id,
        Guid ProjectId,
        Guid WorkflowDefinitionId,
        string WorkflowName,
        ExecutionStatus Status,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        string? ErrorMessage,
        string? UserRequest,
        string? AttentionJson)
    {
        public DateTime? At => CompletedAt ?? StartedAt;
    }
}

public record DashboardOverviewResponse(
    List<ProjectOverview> Projects,
    List<DashboardExecution> ActiveExecutions,
    List<DashboardExecution> RecentExecutions,
    List<DashboardExecution> NeedsAttention,
    List<OverviewOutput> RecentOutputs,
    DashboardTotals Totals);

public record ProjectOverview(
    Guid ProjectId,
    int SourceFileCount,
    int VideoCount,
    int AudioCount,
    int ImageCount,
    long TotalBytes,
    int WorkflowCount,
    int RenderCount,
    int ActiveExecutionCount,
    int FailedExecutionCount,
    DateTime LastActivityAt,
    OverviewOutput? LatestOutput);

public record OverviewOutput(
    Guid StepResultId,
    Guid ProjectId,
    string ProjectName,
    string FileName,
    DateTime ProducedAt);

public record DashboardExecution(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    Guid WorkflowDefinitionId,
    string WorkflowName,
    string Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage,
    string? UserRequest,
    Guid? OutputStepResultId,
    string? OutputFileName,
    // Set when the run Passed but a review loop stopped below its minimum score (status stays Passed). Null otherwise.
    // Not named NeedsAttention: the overview already has a list of that name (failed runs).
    ExecutionAttention? RunAttention = null);

public record DashboardTotals(
    int Projects,
    int SourceFiles,
    int Renders,
    int PassedLast7Days,
    int FailedLast7Days);
