using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// Deletes moderation audit rows past their <c>ExpiresAt</c>. The table is append-only and carries no raw
/// content, but a retention window is part of the contract, so the sweeper runs a bounded delete every
/// six hours rather than leaving the window to an operator.
/// </summary>
public sealed class ContentModerationRetentionService : BackgroundService
{
    /// <summary>Rows deleted per statement; a large backlog is swept in bounded batches, not one long lock.</summary>
    public const int BatchSize = 500;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ContentModerationRetentionService> _logger;

    public ContentModerationRetentionService(
        IServiceScopeFactory scopes,
        ILogger<ContentModerationRetentionService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using PeriodicTimer timer = new(Interval);
        do
        {
            await SweepAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            DateTime now = DateTime.UtcNow;
            int total = 0;
            while (true)
            {
                // ExecuteDeleteAsync supports Where only: OrderBy/Take in the delete statement are
                // not translatable. Select the bounded id page first, then delete exactly those ids
                // (the same shape McpOAuthPurgeService uses).
                List<Guid> ids = await db.ContentModerationDecisions
                    .Where(d => d.ExpiresAt <= now)
                    .OrderBy(d => d.ExpiresAt)
                    .Take(BatchSize)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                if (ids.Count == 0) break;

                int deleted = await db.ContentModerationDecisions
                    .Where(d => ids.Contains(d.Id))
                    .ExecuteDeleteAsync(ct);
                total += deleted;
                if (ids.Count < BatchSize) break;
            }

            if (total > 0)
                _logger.LogInformation("Deleted {Count} expired content-moderation audit rows", total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A retention failure must not stop the host; the next tick retries.
            _logger.LogError(ex, "Content-moderation audit retention sweep failed");
        }
    }
}
