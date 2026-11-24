using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Runs <see cref="ContactSyncService"/> on a timer, and answers an admin's Sync now within about ten seconds by checking
/// for the request on a short second timer. Inert unless <c>Marketing:ContactSync:Enabled</c> and the key and list are set;
/// does not need campaigns enabled. Never throws out of <c>ExecuteAsync</c>; logs counts and codes only.
/// </summary>
public sealed class BrevoContactSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<MarketingContactSyncOptions> options,
    ILogger<BrevoContactSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RequestPoll = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _one = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            Task requests = PollRequestsAsync(stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(5, options.Value.TickSeconds)));
            do
            {
                await PassAsync(onlyIfRequested: false, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
            await requests;
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError("The Brevo contact sync worker stopped: {Error}", MailLogRef.DescribeError(ex));
        }
    }

    private async Task PollRequestsAsync(CancellationToken ct)
    {
        try
        {
            using PeriodicTimer timer = new(RequestPoll);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await PassAsync(onlyIfRequested: true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task PassAsync(bool onlyIfRequested, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await _one.WaitAsync(ct);
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ContactSyncService>().PassAsync(ct, onlyIfRequested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The Brevo contact sync worker failed; retrying on the next tick: {Error}", MailLogRef.DescribeError(ex));
        }
        finally
        {
            _one.Release();
        }
    }
}
