using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Runs queued marketing campaigns and refreshes the results of sent ones. Safe with several replicas: a
/// campaign is claimed by a conditional update and only one campaign can be active at a time.
/// </summary>
public sealed class MarketingCampaignWorker(
    IServiceScopeFactory scopes,
    IOptions<MarketingCampaignOptions> options,
    ILogger<MarketingCampaignWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            // Let the API finish migrating first.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            DateTime nextRefresh = DateTime.UtcNow;
            using PeriodicTimer timer = new(Tick);
            do
            {
                await RecoverAndRunAsync(stoppingToken);
                if (DateTime.UtcNow >= nextRefresh)
                {
                    nextRefresh = DateTime.UtcNow.AddMinutes(Math.Max(1, options.Value.ResultsRefreshMinutes));
                    await SafelyAsync(p => p.RefreshResultsAsync(stoppingToken), stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// One pass: recovery, then every queued campaign. Recovery runs every tick, not only at start: a Syncing row is a
    /// lease that expires after SyncStaleMinutes without a touch, so a restart inside that window cannot leave it stuck.
    /// </summary>
    internal async Task RecoverAndRunAsync(CancellationToken ct)
    {
        await SafelyAsync(p => p.RecoverAsync(ct), ct);
        await SafelyAsync(async p =>
        {
            while (await p.ClaimAndRunAsync(ct))
            {
                // run every queued campaign, one at a time
            }
        }, ct);
    }

    private async Task SafelyAsync(Func<MarketingCampaignPipeline, Task> work, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            await work(scope.ServiceProvider.GetRequiredService<MarketingCampaignPipeline>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The marketing campaign worker failed; retrying on the next tick: {Error}", MailLogRef.DescribeError(ex));
        }
    }
}
