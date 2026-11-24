using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Settings of the run-count sweeper (<c>Marketplace:RunCount</c>).</summary>
public sealed class MarketplaceRunCountOptions
{
    public const string SectionName = "Marketplace:RunCount";

    /// <summary>Time between sweeps, in minutes. Default one hour.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Lets migrations and seeding finish before the first sweep.</summary>
    public int StartupDelaySeconds { get; set; } = 60;
}

/// <summary>
/// One sweep: for every recipe that has copies, <c>run_count</c> becomes the number of PASSED executions of workflows whose
/// <c>template_key = 'market:&lt;id&gt;'</c>, counted read-only from the engine tables across all organizations. Only the totals are
/// stored, never which organization ran what. Absolute (not incremental), so a repeat changes nothing. On PostgreSQL one replica wins a
/// tick: <c>pg_try_advisory_xact_lock</c> inside the transaction that writes the counts.
/// </summary>
public sealed class MarketplaceRunCounts(InferenceApiDbContext db, ILogger<MarketplaceRunCounts> logger)
{
    private const string LockKey = "marketplace-run-count-sweep";

    /// <summary>Runs one sweep; returns how many recipes changed, or -1 when another replica holds this tick.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                transaction = await db.Database.BeginTransactionAsync(ct);
                bool won = await db.Database
                    .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({LockKey}, 0)) AS \"Value\"")
                    .SingleAsync(ct);
                if (!won)
                    return -1;
            }

            int changed = await CountAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return changed;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<int> CountAsync(CancellationToken ct)
    {
        List<Guid> recipeIds = await db.MarketplaceCopies.AsNoTracking().Select(c => c.RecipeId).Distinct().ToListAsync(ct);
        if (recipeIds.Count == 0)
            return 0;

        // One grouped read over the engine tables: executions joined to the copies' workflows, counted per template key.
        Dictionary<string, long> perKey = await db.WorkflowExecutions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(e => e.Status == ReelBolt.Shared.Data.Models.ExecutionStatus.Passed)
            .Join(
                db.WorkflowDefinitions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                    .Where(w => w.TemplateKey != null && w.TemplateKey.StartsWith(PortableRecipeMaterializer.TemplateKeyPrefix)),
                e => e.WorkflowDefinitionId, w => w.Id, (e, w) => w.TemplateKey!)
            .GroupBy(key => key)
            .Select(g => new { Key = g.Key, Runs = g.LongCount() })
            .ToDictionaryAsync(x => x.Key, x => x.Runs, ct);

        var current = await db.MarketplaceRecipes.AsNoTracking()
            .Where(r => recipeIds.Contains(r.Id))
            .Select(r => new { r.Id, r.RunCount })
            .ToListAsync(ct);

        int changed = 0;
        foreach (var recipe in current)
        {
            long runs = perKey.GetValueOrDefault(PortableRecipeMaterializer.TemplateKeyOf(recipe.Id));
            if (runs == recipe.RunCount)
                continue;
            await MarketplaceCounters.SetRunCountAsync(db, recipe.Id, runs, ct);
            changed++;
        }

        logger.LogDebug("Marketplace run counts swept: {Recipes} recipes with copies, {Changed} changed", current.Count, changed);
        return changed;
    }
}

/// <summary>Runs <see cref="MarketplaceRunCounts"/> on an interval, only while <c>Marketplace:Enabled</c> is true.</summary>
public sealed class MarketplaceRunCountSweeper(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<MarketplaceOptions> marketplace,
    IOptions<MarketplaceRunCountOptions> options,
    ILogger<MarketplaceRunCountSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (marketplace.CurrentValue.Enabled)
                {
                    try
                    {
                        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<MarketplaceRunCounts>().SweepOnceAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "The marketplace run-count sweep failed; retrying at the next interval");
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
