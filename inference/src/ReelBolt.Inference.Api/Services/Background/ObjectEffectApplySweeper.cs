using ReelBolt.Inference.Api.Services.ObjectAnalysis;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Applies finished object analyses to the timelines they were requested for (the assistant's "blur the license plate"). A sweep
/// every <c>ObjectEffects:ApplySweepSeconds</c> (default 15; 0 switches it off); see <see cref="ObjectEffectApplier"/> for what
/// one sweep does. Rows are claimed with a single conditional UPDATE, so several replicas never apply a request twice.
/// </summary>
public sealed class ObjectEffectApplySweeper(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ObjectEffectApplySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int seconds = ObjectEffectApplier.IntervalSeconds(configuration);
        if (seconds <= 0)
        {
            logger.LogInformation("The object-effect finalizer is switched off");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ObjectEffectApplier>().SweepOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "An object-effect finalizer sweep failed; the next one will retry");
        }
    }
}
