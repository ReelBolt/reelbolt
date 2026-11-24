using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Drains the delivery queue: every verified push whose pull has not run yet is executed here, once.
///
/// <para>
/// The webhook cannot do the pull itself. A repository can be large, and GitHub abandons a delivery
/// whose endpoint takes too long to answer — so the endpoint acknowledges in milliseconds and this
/// service owns the slow half. Because the row (and its unique delivery id) is durable before the
/// acknowledgement, an API restart between the two loses nothing: the delivery is simply still
/// unprocessed when the next sweep runs.
/// </para>
///
/// <para>
/// <b>Failure is bounded, not infinite.</b> A delivery is attempted <c>GitHub:MaxDeliveryAttempts</c>
/// times and then parked with <c>outcome=failed</c> and the reason on the row; the next push to the same
/// branch is a fresh delivery and repairs the project, because the reconciliation is by content and not
/// by delivery. One failing delivery never blocks the others: each is saved on its own.
/// </para>
/// </summary>
public sealed class GitHubPushSyncService : BackgroundService
{
    /// <summary>Deliveries handled in one sweep before the loop sleeps again.</summary>
    public const int BatchSize = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<GitHubOptions> _options;
    private readonly ILogger<GitHubPushSyncService> _logger;

    public GitHubPushSyncService(
        IServiceScopeFactory scopeFactory,
        IOptions<GitHubOptions> options,
        ILogger<GitHubPushSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Long enough for migrations and seeding to have finished before the first read.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(2, _options.Value.WorkerIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed = await SweepOnceAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(interval, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The GitHub delivery sweep failed; retrying after the interval");
                try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// One sweep. Exposed so a test can drive the queue without waiting for the timer.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<IGitHubDeliveryProcessor>();
        // A push arrives for a project nobody is currently requesting, so the work spans organizations
        // and runs under an explicit system scope, like every other background path here.
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        int maxAttempts = Math.Max(1, _options.Value.MaxDeliveryAttempts);

        List<GitHubWebhookDelivery> due = await db.GitHubWebhookDeliveries
            .Where(d => d.ProcessedAt == null && d.Attempts < maxAttempts)
            .OrderBy(d => d.ReceivedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        int processed = 0;
        foreach (GitHubWebhookDelivery delivery in due)
        {
            ct.ThrowIfCancellationRequested();
            delivery.Attempts++;

            try
            {
                GitHubDeliveryPlan plan = await processor.PlanAsync(delivery, ct);
                if (plan.RequiresPull)
                {
                    GitHubPullOutcome outcome = await processor.PullAsync(plan, delivery, ct);
                    delivery.Outcome = outcome.Status;
                }
                else
                {
                    delivery.Outcome = plan.Outcome;
                }

                delivery.ProcessedAt = DateTime.UtcNow;
                delivery.Error = null;
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                delivery.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                // Attempts is already incremented above; parking happens when the budget is spent, so a
                // transient GitHub outage is retried and a permanent one (a 404, a bad key) stops.
                if (delivery.Attempts >= maxAttempts)
                {
                    delivery.Outcome = "failed";
                    delivery.ProcessedAt = DateTime.UtcNow;
                    _logger.LogError(ex, "GitHub delivery {DeliveryId} failed permanently after {Attempts} attempts", delivery.DeliveryId, delivery.Attempts);
                }
                else
                {
                    _logger.LogWarning(ex, "GitHub delivery {DeliveryId} failed on attempt {Attempts}; it will be retried", delivery.DeliveryId, delivery.Attempts);
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Could not record the outcome of GitHub delivery {DeliveryId}", delivery.DeliveryId);
            }
        }

        return processed;
    }
}
