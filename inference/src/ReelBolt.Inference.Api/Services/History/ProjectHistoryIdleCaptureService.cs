using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The idle automatic version of contract section 9: every ten minutes, capture the projects whose
/// live content changed after their newest version and has been quiet since. A "change" is the newest
/// of a file's <c>UploadedAt</c>, a workflow's or timeline's <c>UpdatedAt</c>, and the project's own
/// <c>UpdatedAt</c>. Only projects changed in the last 24 h are considered, and at most 200 are
/// captured per pass. The master switch <see cref="ProjectHistoryOptions.AutoCapture"/> turns it off
/// together with the other two triggers.
/// </summary>
public sealed class ProjectHistoryIdleCaptureService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ProjectHistoryOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectHistoryIdleCaptureService> _logger;

    public ProjectHistoryIdleCaptureService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ProjectHistoryOptions> options,
        ILogger<ProjectHistoryIdleCaptureService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One sweep, exposed for tests. Returns how many projects were captured.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        ProjectHistoryOptions options = _options.CurrentValue;
        if (!options.AutoCapture)
            return 0;

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime idleBefore = now.AddMinutes(-Math.Max(1, options.IdleMinutes));
        DateTime lookback = now.AddHours(-Math.Max(1, options.IdleLookbackHours));
        int batch = Math.Max(1, options.IdleSweepBatchSize);

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IProjectHistoryService history = scope.ServiceProvider.GetRequiredService<IProjectHistoryService>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        Dictionary<Guid, DateTime> lastChange = [];

        // Only sources a version actually holds count (QA finding 12): user/agent files, Standard workflows,
        // timelines (the context filter drops deleted ones), pronunciation entries and the project's name/description.
        // Output files, hidden workflow kinds and the like never re-trigger a capture that would dedupe
        // to nothing. A file's creation or replacement is its UploadedAt; workflow/timeline/project edits are
        // their UpdatedAt. Deleted rows stop contributing, so a delete is seen through the remaining
        // newer change, or through the missing item in the next capture's digest.
        foreach (ProjectChangeRow row in await db.ProjectFiles
                     .Where(f => f.UploadedAt >= lookback
                                 && (f.Category == "userFiles" || f.Category == "agentFiles"))
                     .GroupBy(f => f.ProjectId)
                     .Select(g => new ProjectChangeRow(g.Key, g.Max(f => f.UploadedAt)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            Merge(lastChange, row);
        }

        foreach (ProjectChangeRow row in await db.WorkflowDefinitions
                     .Where(w => w.UpdatedAt >= lookback && w.Kind == WorkflowDefinitionKind.Standard)
                     .GroupBy(w => w.ProjectId)
                     .Select(g => new ProjectChangeRow(g.Key, g.Max(w => w.UpdatedAt)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            Merge(lastChange, row);
        }

        foreach (ProjectChangeRow row in await db.EditTimelines
                     .Where(t => t.UpdatedAt >= lookback)
                     .GroupBy(t => t.ProjectId)
                     .Select(g => new ProjectChangeRow(g.Key, g.Max(t => t.UpdatedAt)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            Merge(lastChange, row);
        }

        foreach (ProjectChangeRow row in await db.ProjectPronunciationEntries
                     .Where(e => e.UpdatedAt >= lookback)
                     .GroupBy(e => e.ProjectId)
                     .Select(g => new ProjectChangeRow(g.Key, g.Max(e => e.UpdatedAt)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            Merge(lastChange, row);
        }

        Dictionary<Guid, DateTime> newestVersion = await db.ProjectVersions
            .Where(v => v.CreatedAt >= lookback.AddDays(-1))
            .GroupBy(v => v.ProjectId)
            .Select(g => new ProjectChangeRow(g.Key, g.Max(v => v.CreatedAt)))
            .ToDictionaryAsync(r => r.ProjectId, r => r.ChangedAt, ct)
            .ConfigureAwait(false);

        // The project row: Project.UpdatedAt also moves for folder and tag changes, which no version holds, so
        // it only counts when the name/description document differs from the one the newest version holds.
        List<Project> touched = await db.Projects.AsNoTracking()
            .Where(p => p.UpdatedAt >= lookback)
            .ToListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, string?> newestProjectDigest = await NewestProjectDigestsAsync(
            db, touched.Select(p => p.Id).ToList(), lookback.AddDays(-1), ct).ConfigureAwait(false);
        foreach (Project project in touched)
        {
            bool unchanged = newestProjectDigest.TryGetValue(project.Id, out string? digest)
                             && digest is not null
                             && string.Equals(
                                 digest,
                                 ProjectHistoryDocuments.Sha256(ProjectHistoryDocuments.Project(project)),
                                 StringComparison.Ordinal);
            if (!unchanged)
                Merge(lastChange, new ProjectChangeRow(project.Id, project.UpdatedAt));
        }

        List<Guid> due = lastChange
            .Where(pair => pair.Value >= lookback
                           && pair.Value <= idleBefore
                           && (!newestVersion.TryGetValue(pair.Key, out DateTime newest) || pair.Value > newest))
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(batch)
            .Select(pair => pair.Key)
            .ToList();

        int captured = 0;
        foreach (Guid projectId in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await history.TryCaptureAutoAsync(projectId, ProjectVersionCause.Idle, userId: null, ct)
                    .ConfigureAwait(false);
                captured++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // TryCaptureAutoAsync already swallows failures; this keeps one bad project from
                // ending the sweep if its implementation ever changes.
                _logger.LogWarning(ex, "Idle project version capture failed for project {ProjectId}", projectId);
            }
        }

        return captured;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ProjectHistoryOptions options = _options.CurrentValue;
                if (options.AutoCapture)
                {
                    try
                    {
                        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "The project history idle sweep failed; the next one covers the same projects");
                    }
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, options.IdleSweepIntervalSeconds)),
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>The Project item digest of each project's newest version (null when it has none in the window).</summary>
    private static async Task<Dictionary<Guid, string?>> NewestProjectDigestsAsync(
        InferenceApiDbContext db, List<Guid> projectIds, DateTime since, CancellationToken ct)
    {
        var versions = await db.ProjectVersions.AsNoTracking()
            .Where(v => projectIds.Contains(v.ProjectId) && v.CreatedAt >= since)
            .Select(v => new { v.Id, v.ProjectId, v.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        Dictionary<Guid, Guid> newestId = versions
            .GroupBy(v => v.ProjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First().Id);
        List<Guid> versionIds = newestId.Values.ToList();

        Dictionary<Guid, string?> byVersion = (await db.ProjectVersionItems.AsNoTracking()
                .Where(i => versionIds.Contains(i.VersionId) && i.Kind == ProjectVersionItemKind.Project)
                .Select(i => new { i.VersionId, i.DocumentSha256 })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(i => i.VersionId)
            .ToDictionary(g => g.Key, g => g.First().DocumentSha256);

        return newestId.ToDictionary(
            pair => pair.Key,
            pair => byVersion.TryGetValue(pair.Value, out string? digest) ? digest : null);
    }

    private static void Merge(Dictionary<Guid, DateTime> lastChange, ProjectChangeRow row)
    {
        if (!lastChange.TryGetValue(row.ProjectId, out DateTime existing) || row.ChangedAt > existing)
            lastChange[row.ProjectId] = row.ChangedAt;
    }

    private sealed record ProjectChangeRow(Guid ProjectId, DateTime ChangedAt);
}
