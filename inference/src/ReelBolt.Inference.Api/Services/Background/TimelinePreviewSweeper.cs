using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// R10.4: deletes finished "Preview 3 s" files after <c>Editing:Preview:RetentionMinutes</c> (default 60), and the
/// hidden preview workflows (with their executions and results) once they are also older than the one-hour
/// rate-limit window, so the per-user cap keeps counting until then. Previews are never project files, so nothing
/// else ever removes them. Idempotent: a second run finds nothing left to delete.
/// </summary>
public sealed class TimelinePreviewSweeper : BackgroundService
{
    /// <summary>Server-set name prefix of every preview workflow (a normal render's is "Render: ").</summary>
    public const string WorkflowNamePrefix = "Preview: ";

    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<TimelinePreviewOptions> _options;
    private readonly ILogger<TimelinePreviewSweeper> _logger;
    private readonly TimeProvider _clock;

    public TimelinePreviewSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<TimelinePreviewOptions> options,
        ILogger<TimelinePreviewSweeper> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One sweep; returns how many preview files and how many preview workflows were deleted.</summary>
    public static async Task<(int Files, int Workflows)> SweepOnceAsync(
        InferenceApiDbContext db, IFileStorageService storage, DateTime nowUtc, TimeSpan retention, CancellationToken ct)
    {
        DateTime fileCutoff = nowUtc - retention;
        DateTime rowCutoff = nowUtc - (retention > RateWindow ? retention : RateWindow);

        List<WorkflowDefinition> expired = await db.WorkflowDefinitions
            .IgnoreQueryFilters()
            .Include(w => w.Executions).ThenInclude(e => e.StepResults)
            .Where(w => w.Kind == WorkflowDefinitionKind.TimelineRender
                && w.Name.StartsWith(WorkflowNamePrefix)
                && w.CreatedAt < fileCutoff)
            .OrderBy(w => w.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        int files = 0;
        int workflows = 0;
        // A project that keeps its outputs (or whose state cannot be read) is left alone.
        IReadOnlyDictionary<Guid, ProjectOutputHoldState> states =
            await ProjectOutputHold.ReadManyAsync(db, expired.Select(w => w.ProjectId).ToList(), ct);
        foreach (WorkflowDefinition workflow in expired)
        {
            if (states.TryGetValue(workflow.ProjectId, out ProjectOutputHoldState state) && state.IsActive)
                continue;

            List<string> keys = workflow.Executions
                .SelectMany(e => e.StepResults)
                .SelectMany(r => new[] { r.OutputStorageKey, r.ArtifactStorageKey, r.TimelineStorageKey })
                .OfType<string>()
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (keys.Count > 0)
            {
                try
                {
                    files += await storage.DeleteManyAsync(workflow.ProjectId, keys, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Try again next sweep; keep the row so the keys are not lost.
                    continue;
                }
            }

            if (workflow.CreatedAt < rowCutoff)
            {
                db.WorkflowStepResults.RemoveRange(workflow.Executions.SelectMany(e => e.StepResults));
                db.WorkflowExecutions.RemoveRange(workflow.Executions);
                db.WorkflowDefinitions.Remove(workflow);
                workflows++;
            }
        }

        if (workflows > 0) await db.SaveChangesAsync(ct);
        return (files, workflows);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            TimelinePreviewOptions options = _options.Value;
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Clamp(options.SweepIntervalMinutes, 1, 24 * 60)));
            do
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                    IFileStorageService storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
                    (int files, int workflows) = await SweepOnceAsync(db, storage, _clock.GetUtcNow().UtcDateTime, options.Retention, stoppingToken);
                    if (files > 0 || workflows > 0)
                        _logger.LogInformation("The preview sweeper deleted {Files} preview file(s) and {Workflows} preview job(s)", files, workflows);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The preview sweeper could not complete a sweep");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
