using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// The hosted loop of the publish engine: every <c>SweepIntervalSeconds</c> it asks <see cref="ISocialPublishingService"/>
/// to recover expired leases and claim due posts, then publishes each claim in its own scope (own database context, so the
/// lease renewal and the upload never share one). Safe on several replicas: the claim is a conditional UPDATE.
/// </summary>
public sealed class SocialPublishSweeper(
    IServiceScopeFactory scopes,
    IOptions<SocialPublishingOptions> options,
    ILogger<SocialPublishSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SocialPublishingOptions all = options.Value;
        int seconds = all.Engine.SweepIntervalSeconds;
        // Only the interval switches the loop off: the deleted-organization cleanup deletes tokens, so it runs even
        // when no provider is (any longer) configured; the publish half below is what needs a configured platform.
        if (seconds <= 0)
        {
            logger.LogInformation("The social publishing sweeper is off (interval 0)");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A social publishing sweep failed; retrying after the interval");
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep; public for tests. Returns how many posts were claimed and published.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using (IServiceScope scope = scopes.CreateScope())
        {
            // Tokens of a deleted workspace must not outlive it: revoke and delete them (best effort, retried next pass).
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISocialConnectionService>().PurgeDeletedOrganizationsAsync(20, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Purging social accounts of deleted organizations failed ({Type})", ex.GetType().Name);
            }
        }

        if (!options.Value.Enabled || !SocialPlatforms.All.Any(options.Value.IsConfigured)) return 0;

        SocialSweepResult swept;
        using (IServiceScope scope = scopes.CreateScope())
            swept = await scope.ServiceProvider.GetRequiredService<ISocialPublishingService>().ClaimDueAsync(ct);

        await Task.WhenAll(swept.Claims.Select(async claim =>
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISocialPublishingService>().PublishClaimedAsync(claim, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lease expires into `unknown`; never retried here.
                logger.LogError("Publishing social post {PostId} failed unexpectedly ({Type})", claim.PostId, ex.GetType().Name);
            }
        }));

        return swept.Claims.Count;
    }
}
