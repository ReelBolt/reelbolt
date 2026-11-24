using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>Tunables of the render-orphan reaper (config section <c>Editing:RenderOrphanReaper</c>).</summary>
public sealed record TimelineRenderOrphanReaperOptions
{
    public const string SectionName = "Editing:RenderOrphanReaper";

    /// <summary>Run the reaper at all. Default true: it only deletes rows nothing can ever use.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often to sweep after the startup run, in minutes.</summary>
    public int IntervalMinutes { get; init; } = 60;

    /// <summary>
    /// How old an execution-less render workflow must be before it counts as an orphan, in minutes.
    /// A render start commits its workflow and execution together, so a live one never matches; the
    /// floor only keeps the rule obviously safe.
    /// </summary>
    public int MinAgeMinutes { get; init; } = 60;
}

/// <summary>
/// Deletes hidden <see cref="WorkflowDefinitionKind.TimelineRender"/> workflows that have no
/// execution and are older than <see cref="TimelineRenderOrphanReaperOptions.MinAgeMinutes"/>. Before
/// the render start became one transaction, every render attempt that failed after the workflow
/// insert left such a row behind (a hidden <c>Render: &lt;name&gt;</c> workflow nobody can see,
/// start or delete); this clears them on every deployment without a migration.
/// </summary>
/// <remarks>
/// Runs once at startup and then every <see cref="TimelineRenderOrphanReaperOptions.IntervalMinutes"/>.
/// Idempotent: the delete is one statement over the selection rule, so a second run finds nothing.
/// Steps go with their workflow through the <c>workflow_steps</c> foreign key's cascade. Logs the
/// count of each sweep that deleted something. Off with <c>Editing:RenderOrphanReaper:Enabled=false</c>.
/// </remarks>
public sealed class TimelineRenderOrphanReaper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<TimelineRenderOrphanReaperOptions> _options;
    private readonly ILogger<TimelineRenderOrphanReaper> _logger;
    private readonly TimeProvider _clock;

    public TimelineRenderOrphanReaper(
        IServiceScopeFactory scopeFactory,
        IOptions<TimelineRenderOrphanReaperOptions> options,
        ILogger<TimelineRenderOrphanReaper> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The selection rule: hidden render, audio-cleanup, video-enhance, avatar-build and subject-clip workflows with no
    /// execution, created before <paramref name="cutoffUtc"/>.
    /// </summary>
    public static IQueryable<WorkflowDefinition> SelectOrphans(IQueryable<WorkflowDefinition> workflows, DateTime cutoffUtc) =>
        workflows.Where(w => (w.Kind == WorkflowDefinitionKind.TimelineRender || w.Kind == WorkflowDefinitionKind.AudioCleanup
                || w.Kind == WorkflowDefinitionKind.VideoEnhance || w.Kind == WorkflowDefinitionKind.AvatarBuild
                || w.Kind == WorkflowDefinitionKind.SubjectClip)
            && w.CreatedAt < cutoffUtc
            && !w.Executions.Any());

    /// <summary>One sweep: deletes every orphan older than <paramref name="minAge"/> and returns how many went.</summary>
    public static Task<int> ReapOnceAsync(InferenceApiDbContext db, DateTime nowUtc, TimeSpan minAge, CancellationToken ct) =>
        SelectOrphans(db.WorkflowDefinitions, nowUtc - minAge).ExecuteDeleteAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimelineRenderOrphanReaperOptions options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogDebug("The render-orphan reaper is disabled ({Section}:Enabled)", TimelineRenderOrphanReaperOptions.SectionName);
            return;
        }

        TimeSpan minAge = TimeSpan.FromMinutes(Math.Max(1, options.MinAgeMinutes));
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Clamp(options.IntervalMinutes, 1, 24 * 60)));
            do
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                    int deleted = await ReapOnceAsync(db, _clock.GetUtcNow().UtcDateTime, minAge, stoppingToken);
                    _logger.LogInformation("The render-orphan reaper deleted {Count} hidden render workflow(s) with no execution", deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Hygiene, never a dependency: the next sweep covers the same rows.
                    _logger.LogError(ex, "The render-orphan reaper could not complete a sweep");
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
