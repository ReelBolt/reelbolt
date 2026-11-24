using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Performs one-time cleanup tasks when the Inference API starts, and does it lease-aware (F4,
/// decision D21: a deploy must never cancel healthy runs).
///
/// <list type="bullet">
/// <item><b>Running executions are only touched when their lease has lapsed.</b> A run whose engine
/// instance is alive renews <c>workflow_executions.lease_expires_at</c> every 15 s, so restarting
/// this API leaves it <c>Running</c>. A row with a lapsed lease belongs to a dead engine. In cloud
/// (<c>StartupCleanup:PurgeQueues=false</c>) those are left to the engine's recovery sweeper, which
/// fails them with a provenance message and runs the refund hook.</item>
/// <item><b>Queue purge</b> (<c>StartupCleanup:PurgeQueues</c>, default <c>true</c> for self-host) drops
/// stale messages from the workflow queues so they do not linger across a restart. A purge strands
/// the <c>Queued</c> rows whose messages it removed, so with purging on those rows are cancelled as
/// they always were; with purging off (cloud, shared broker) nothing is purged and <c>Queued</c> rows
/// are left for the engine to re-publish when stale.</item>
/// </list>
/// </summary>
public class StartupCleanupService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public StartupCleanupService(IServiceProvider services, IConfiguration configuration)
    {
        _services = services;
        _configuration = configuration;
    }

    /// <summary>
    /// <c>StartupCleanup:PurgeQueues</c>: true (default, self-host) purges the workflow queues and
    /// cancels the rows they stranded; false (cloud) does neither.
    /// </summary>
    public bool PurgeQueues => _configuration.GetValue("StartupCleanup:PurgeQueues", true);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await CancelOrphanedExecutionsAsync(cancellationToken);
        if (PurgeQueues)
        {
            await PurgeRabbitMqQueuesAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Cancels orphaned rows and returns how many it changed. Each update is a single conditional
    /// statement (<c>WHERE status = …</c>), so a run that finishes, or whose lease is renewed, between
    /// the decision and the write is never overwritten.
    /// </summary>
    public async Task<int> CancelOrphanedExecutionsAsync(CancellationToken ct)
    {
        // workflow_executions is owned and migrated by the WorkflowEngine service, not this one
        // (ExcludeFromMigrations() here — see InferenceApiDbContext). On a brand-new database,
        // the WorkflowEngine's migrations may not have run yet by the time this service starts,
        // so the table can genuinely not exist. That must not be fatal: compose only starts the
        // WorkflowEngine once this API reports healthy, so an unhandled exception here would
        // permanently deadlock a fresh stack (this API can never become healthy, so the engine's
        // migrations that would create the table never get a chance to run). This cleanup is
        // best-effort, exactly like the RabbitMQ purge below — swallow and log, never block startup.
        if (!PurgeQueues)
        {
            // Cloud: the engine's recovery sweeper owns every orphan, with provenance and refunds.
            return 0;
        }

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

            DateTime now = DateTime.UtcNow;
            // A Running row with no lease predates leases (a pre-F4 engine started it): treat it as
            // orphaned once it is older than two lease windows, never while it could still be live.
            DateTime legacyCutoff = now.AddSeconds(-120);

            int runningCancelled = await db.WorkflowExecutions
                .Where(e => e.Status == ExecutionStatus.Running
                    && ((e.LeaseExpiresAt != null && e.LeaseExpiresAt < now)
                        || (e.LeaseExpiresAt == null && (e.StartedAt == null || e.StartedAt < legacyCutoff))))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                    .SetProperty(e => e.ErrorMessage, "Service restarted")
                    .SetProperty(e => e.CompletedAt, now), ct);

            // The purge below removes their messages, so these rows would otherwise wait forever.
            int queuedCancelled = await db.WorkflowExecutions
                .Where(e => e.Status == ExecutionStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                    .SetProperty(e => e.ErrorMessage, "Service restarted")
                    .SetProperty(e => e.CompletedAt, now), ct);

            return runningCancelled + queuedCancelled;
        }
        catch (Exception ex)
        {
            // non-critical; log via Console since no logger is available here (mirrors the
            // RabbitMQ purge's own error handling below)
            Console.WriteLine($"[startup-cleanup] failed to cancel stale executions: {ex.Message}");
            return 0;
        }
    }

    private async Task PurgeRabbitMqQueuesAsync(CancellationToken ct)
    {
        try
        {
            var factory = new RabbitMQ.Client.ConnectionFactory
            {
                HostName = _configuration["RabbitMQ:Host"] ?? "localhost",
                UserName = _configuration["RabbitMQ:Username"] ?? "guest",
                Password = _configuration["RabbitMQ:Password"] ?? "guest",
            };

            using var conn = await factory.CreateConnectionAsync();
            using var channel = await conn.CreateChannelAsync();

            // known queues used by the WorkflowEngine
            foreach (var queue in new[] { "workflow-execution", "workflow-stop-requests" })
            {
                try
                {
                    await channel.QueuePurgeAsync(queue);
                }
                catch
                {
                    // ignore if the queue doesn't exist yet
                }
            }
        }
        catch (Exception ex)
        {
            // non-critical; log via Console since no logger is available here
            Console.WriteLine($"[startup-cleanup] failed to purge queues: {ex.Message}");
        }

        return;
    }
}
