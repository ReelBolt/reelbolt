using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Wakes the assistant in a chat thread when a run it is watching (<c>WatchExecution</c>) reaches a terminal state, with
/// the tab open or closed. A sweep every <c>Assistant:RunWake:SweepIntervalSeconds</c> (0 or <c>Enabled=false</c> switches
/// it off); see <see cref="AssistantRunWakeSweeper"/> for what one sweep does. The turn hub it defers to is process-local,
/// so a replica other than the one holding a live turn may wake a thread: the claim is a single conditional UPDATE, so
/// a watch still fires once.
/// </summary>
public sealed class AssistantRunWakeService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AssistantRunWakeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int seconds = AssistantRunWakeLimits.SweepIntervalSeconds(configuration);
        if (seconds <= 0 || !AssistantRunWakeLimits.Enabled(configuration))
        {
            logger.LogInformation("The assistant run wake service is switched off");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(seconds));
        var ticks = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Each tick runs on its own and is not awaited by the loop, so a slow wake turn never delays the next tick's
                // housekeeping (expiry, released claims) or another thread's wake. Claims make the overlap safe.
                Task tick = TickAsync(stoppingToken);
                ticks[tick] = 0;
                _ = tick.ContinueWith(t => ticks.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await Task.WhenAll(ticks.Keys);
        }
        catch (Exception)
        {
            // Already logged by the tick that failed.
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            // Drain: claim nothing new, so a wake starts on the new instance.
            if (scope.ServiceProvider.GetService<ReelBolt.Shared.Drain.IDrainState>()?.IsDraining == true)
            {
                return;
            }

            // Housekeeping across every organization's threads (a wake turn itself runs as its own organization).
            using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
            AssistantRunWakeSweeper sweeper = ActivatorUtilities.CreateInstance<AssistantRunWakeSweeper>(
                scope.ServiceProvider,
                (Func<(InferenceApiDbContext Db, IDisposable Lease)>)(() =>
                {
                    IServiceScope worker = scopeFactory.CreateScope();
                    return (worker.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), worker);
                }));
            await sweeper.SweepOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A run wake sweep failed; the next one will retry");
        }
    }
}
