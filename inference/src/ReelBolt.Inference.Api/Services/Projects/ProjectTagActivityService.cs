using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Projects;

public sealed record ActivityTagRef(Guid Id, string Name, string Color);

public sealed record ActivityTotals(int Projects, int Runs, double? Credits);

public sealed record TagActivityRow(Guid TagId, string Name, string Color, int Projects, int Runs, int Passed, int Failed, double? Credits);

public sealed record TagActivityOverview(
    DateTime WindowStart,
    DateTime WindowEnd,
    bool BillingEnabled,
    ActivityTotals Workspace,
    IReadOnlyList<TagActivityRow> Tags,
    ActivityTotals Untagged);

public sealed record TagDetailTotals(int Projects, int Runs, int Passed, int Failed, double? Credits);

public sealed record TagProjectActivity(Guid ProjectId, string Name, int Runs, double? Credits);

public sealed record TagRecentRun(
    Guid ExecutionId,
    Guid ProjectId,
    Guid WorkflowId,
    string ProjectName,
    string WorkflowName,
    string Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    double? Credits);

public sealed record TagActivityDetail(
    ActivityTagRef Tag,
    DateTime WindowStart,
    DateTime WindowEnd,
    bool BillingEnabled,
    TagDetailTotals Totals,
    IReadOnlyList<TagProjectActivity> Projects,
    IReadOnlyList<TagRecentRun> RecentRuns);

/// <summary>
/// Per-customer activity: runs and credits per project tag over a window. Aggregated in SQL per project
/// (runs by status, credits from the usage ledger) and folded per tag in memory. A project with several
/// tags counts under each of them; the workspace and untagged rows are computed from projects directly.
/// It never reads or returns cost (COGS).
/// </summary>
public sealed class ProjectTagActivityService(InferenceApiDbContext db, TimeProvider? time = null)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;
    private const int RecentRunLimit = 50;

    public static int ClampDays(int? days) => Math.Clamp(days ?? DefaultDays, 1, MaxDays);

    private static double Credits(long millis) => millis / 1000.0;

    private sealed record ProjectStats(Guid Id, string Name, HashSet<Guid> TagIds, int Runs, int Passed, int Failed, long Millis);

    private async Task<List<ProjectStats>> LoadAsync(Guid orgId, DateTime start, DateTime end, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.OrganizationId == orgId)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        var links = await db.ProjectTagLinks.AsNoTracking()
            .Where(l => l.Tag.OrganizationId == orgId)
            .Select(l => new { l.ProjectId, l.TagId })
            .ToListAsync(ct);
        var runs = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.OrganizationId == orgId && e.StartedAt != null && e.StartedAt >= start && e.StartedAt < end)
            .GroupBy(e => new { e.ProjectId, e.Status })
            .Select(g => new { g.Key.ProjectId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var credits = await db.UsageEvents.AsNoTracking()
            .Where(u => u.OrganizationId == orgId && u.ProjectId != null && u.OccurredAt >= start && u.OccurredAt < end)
            .GroupBy(u => u.ProjectId)
            .Select(g => new { ProjectId = g.Key, Millis = g.Sum(u => u.CreditsMillis) })
            .ToListAsync(ct);

        ILookup<Guid, Guid> tagsByProject = links.ToLookup(l => l.ProjectId, l => l.TagId);
        Dictionary<Guid, long> millisByProject = credits.ToDictionary(c => c.ProjectId!.Value, c => c.Millis);
        ILookup<Guid, (ExecutionStatus Status, int Count)> runsByProject = runs.ToLookup(r => r.ProjectId, r => (r.Status, r.Count));
        return projects.Select(p =>
        {
            var mine = runsByProject[p.Id].ToList();
            return new ProjectStats(
                p.Id, p.Name, tagsByProject[p.Id].ToHashSet(),
                mine.Sum(r => r.Count),
                mine.Where(r => r.Status == ExecutionStatus.Passed).Sum(r => r.Count),
                mine.Where(r => r.Status == ExecutionStatus.Failed).Sum(r => r.Count),
                millisByProject.GetValueOrDefault(p.Id));
        }).ToList();
    }

    private (DateTime Start, DateTime End) Window(int days)
    {
        DateTime end = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return (end.AddDays(-days), end);
    }

    public async Task<TagActivityOverview> OverviewAsync(Guid orgId, int days, bool billingEnabled, CancellationToken ct)
    {
        (DateTime start, DateTime end) = Window(days);
        List<ProjectStats> stats = await LoadAsync(orgId, start, end, ct);
        List<ProjectTag> tags = await db.ProjectTags.AsNoTracking()
            .Where(t => t.OrganizationId == orgId).OrderBy(t => t.NameKey).ToListAsync(ct);

        double? C(long millis) => billingEnabled ? Credits(millis) : null;

        List<TagActivityRow> rows = tags.Select(t =>
        {
            List<ProjectStats> mine = stats.Where(s => s.TagIds.Contains(t.Id)).ToList();
            return new TagActivityRow(t.Id, t.Name, t.Color, mine.Count, mine.Sum(s => s.Runs),
                mine.Sum(s => s.Passed), mine.Sum(s => s.Failed), C(mine.Sum(s => s.Millis)));
        }).ToList();
        List<ProjectStats> untagged = stats.Where(s => s.TagIds.Count == 0).ToList();
        return new TagActivityOverview(start, end, billingEnabled,
            new ActivityTotals(stats.Count, stats.Sum(s => s.Runs), C(stats.Sum(s => s.Millis))),
            rows,
            new ActivityTotals(untagged.Count, untagged.Sum(s => s.Runs), C(untagged.Sum(s => s.Millis))));
    }

    /// <summary>Null when the tag does not exist in the organization.</summary>
    public async Task<TagActivityDetail?> DetailAsync(Guid orgId, Guid tagId, int days, bool billingEnabled, CancellationToken ct)
    {
        ProjectTag? tag = await db.ProjectTags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == orgId, ct);
        if (tag is null) return null;

        (DateTime start, DateTime end) = Window(days);
        List<ProjectStats> mine = (await LoadAsync(orgId, start, end, ct)).Where(s => s.TagIds.Contains(tagId)).ToList();
        HashSet<Guid> projectIds = mine.Select(s => s.Id).ToHashSet();
        Dictionary<Guid, string> names = mine.ToDictionary(s => s.Id, s => s.Name);

        var recent = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.OrganizationId == orgId && e.StartedAt != null && e.StartedAt >= start && e.StartedAt < end
                && db.ProjectTagLinks.Any(l => l.TagId == tagId && l.ProjectId == e.ProjectId))
            .OrderByDescending(e => e.StartedAt).ThenBy(e => e.Id)
            .Take(RecentRunLimit)
            .Select(e => new
            {
                e.Id,
                e.ProjectId,
                e.WorkflowDefinitionId,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                WorkflowName = db.WorkflowDefinitions.Where(d => d.Id == e.WorkflowDefinitionId).Select(d => d.Name).FirstOrDefault()
            })
            .ToListAsync(ct);
        List<Guid> runIds = recent.Select(r => r.Id).ToList();
        Dictionary<Guid, long> runMillis = runIds.Count == 0
            ? []
            : (await db.UsageEvents.AsNoTracking()
                .Where(u => u.OrganizationId == orgId && u.ExecutionId != null && runIds.Contains(u.ExecutionId.Value))
                .GroupBy(u => u.ExecutionId)
                .Select(g => new { Id = g.Key, Millis = g.Sum(u => u.CreditsMillis) })
                .ToListAsync(ct)).ToDictionary(x => x.Id!.Value, x => x.Millis);

        double? C(long millis) => billingEnabled ? Credits(millis) : null;
        return new TagActivityDetail(
            new ActivityTagRef(tag.Id, tag.Name, tag.Color), start, end, billingEnabled,
            new TagDetailTotals(mine.Count, mine.Sum(s => s.Runs), mine.Sum(s => s.Passed), mine.Sum(s => s.Failed), C(mine.Sum(s => s.Millis))),
            mine.OrderByDescending(s => s.Runs).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new TagProjectActivity(s.Id, s.Name, s.Runs, C(s.Millis))).ToList(),
            recent.Select(r => new TagRecentRun(r.Id, r.ProjectId, r.WorkflowDefinitionId, names.GetValueOrDefault(r.ProjectId, string.Empty),
                r.WorkflowName ?? string.Empty, r.Status.ToString(), r.StartedAt, r.CompletedAt,
                C(runMillis.GetValueOrDefault(r.Id)))).ToList());
    }
}
