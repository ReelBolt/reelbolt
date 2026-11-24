using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Recovers previews that never came back: a version still <c>preparing</c> after <c>PreviewStuckMinutes</c> is re-dispatched once
/// (recorded in its check json), then marked failed. Does nothing when the marketplace is off.
/// </summary>
public sealed class MarketplacePreviewSweeper(
    IServiceScopeFactory scopes,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplacePreviewSweeper> logger) : BackgroundService
{
    internal const string RedispatchedAtKey = "previewRedispatchedAt";
    private const int BatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MarketplaceOptions o = options.Value;
        if (!o.Enabled || o.PreviewSweepIntervalMinutes <= 0)
            return;

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(o.PreviewSweepIntervalMinutes));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                await SweepAsync(
                    scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IMarketplacePreviewDispatcher>(),
                    DateTime.UtcNow, stoppingToken,
                    scope.ServiceProvider.GetRequiredService<IMarketplacePreviewObjects>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A marketplace preview sweep failed");
            }
        }
    }

    /// <summary>The staging TTL: a failed version's staged objects (any run) are removed.</summary>
    private async Task TryDeleteStagingAsync(IMarketplacePreviewObjects? objects, MarketplaceRecipeVersion version, CancellationToken ct)
    {
        // Held evidence is never cleaned up.
        if (objects is null || MarketplaceEvidenceGuard.IsHeld(version.CheckJson))
            return;
        try
        {
            await objects.DeletePrefixAsync(ReelBolt.Shared.Storage.MarketplacePreviewKeys.StagingRoot(version.RecipeId, version.Id), ct, MarketplaceEvidenceGuard.HeldKeys(version.CheckJson));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleting the staged objects of version {VersionId} failed", version.Id);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
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

    /// <summary>One pass. Returns how many versions were re-dispatched and how many were failed.</summary>
    internal async Task<(int Redispatched, int Failed)> SweepAsync(
        InferenceApiDbContext db, IMarketplacePreviewDispatcher dispatcher, DateTime nowUtc, CancellationToken ct,
        IMarketplacePreviewObjects? objects = null)
    {
        TimeSpan stuck = TimeSpan.FromMinutes(Math.Max(1, options.Value.PreviewStuckMinutes));
        DateTime cutoff = nowUtc - stuck;
        List<MarketplaceRecipeVersion> versions = await db.MarketplaceRecipeVersions
            .Where(v => v.PreviewStatus == PreviewStatus.Preparing && v.CreatedAt < cutoff)
            .OrderBy(v => v.CreatedAt).Take(BatchSize).ToListAsync(ct);

        int redispatched = 0;
        int failed = 0;
        foreach (MarketplaceRecipeVersion version in versions)
        {
            // A result may have finalized the version since the batch was read: re-read it and leave it alone then.
            await db.Entry(version).ReloadAsync(ct);
            if (version.PreviewStatus != PreviewStatus.Preparing)
                continue;

            JsonObject check = MarketplacePreviewFinalizer.ParseCheck(version.CheckJson);
            DateTime? last = DateTime.TryParse(check[RedispatchedAtKey]?.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : null;

            if (last is null)
            {
                check[RedispatchedAtKey] = nowUtc.ToString("O");
                version.CheckJson = check.ToJsonString();
                await db.SaveChangesAsync(ct);
                try
                {
                    await dispatcher.DispatchAsync(version.RecipeId, version.Id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Re-dispatching the preview of version {VersionId} failed", version.Id);
                }

                redispatched++;
            }
            else if (last.Value < cutoff)
            {
                check["previewFailure"] = "timed_out";
                version.CheckJson = check.ToJsonString();
                version.PreviewStatus = PreviewStatus.Failed;
                db.MarketplaceAudit.Add(new MarketplaceAuditEntry
                {
                    Id = Guid.NewGuid(),
                    RecipeId = version.RecipeId,
                    VersionId = version.Id,
                    Action = "preview_failed",
                    ActorKind = "system",
                    DetailJson = new JsonObject { ["reason"] = "timed_out" }.ToJsonString(),
                    CreatedAt = nowUtc
                });
                await db.SaveChangesAsync(ct);
                failed++;
                await TryDeleteStagingAsync(objects, version, ct);
            }
        }

        return (redispatched, failed);
    }
}
