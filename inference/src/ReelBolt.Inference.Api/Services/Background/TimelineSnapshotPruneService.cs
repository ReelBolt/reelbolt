using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Daily prune of the version history (D14): deletes automatic checkpoints past their
/// <c>expires_at</c> (<c>Editing:AutoCheckpointDays</c>, default 30) and trims any timeline over its
/// automatic-checkpoint cap. Named versions and revision 1 are never touched. Idempotent.
/// </summary>
public sealed class TimelineSnapshotPruneService : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TimelineSnapshotPruneService> _logger;
    private readonly TimeProvider _clock;

    public TimelineSnapshotPruneService(
        IServiceScopeFactory scopeFactory,
        ILogger<TimelineSnapshotPruneService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One prune in its own scope, exposed for tests. Returns how many snapshots were deleted.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        return await TimelineSnapshotRetention.PruneAsync(db, _clock.GetUtcNow().UtcDateTime, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using PeriodicTimer timer = new(SweepInterval);
            do
            {
                try
                {
                    int pruned = await SweepOnceAsync(stoppingToken);
                    if (pruned > 0)
                        _logger.LogInformation("The version-history prune removed {Count} expired automatic checkpoint(s)", pruned);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The version-history prune could not complete a sweep; the next one covers the same rows");
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
