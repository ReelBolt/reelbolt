using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Runs the syncs people ask for — "Sync now", the first sync of a new link, the re-sync after a change
/// of branch or files — off the request that asked (see <see cref="GitHubSyncQueue"/> for why).
///
/// <para>
/// Each sync runs in its own DI scope (its own <see cref="InferenceApiDbContext"/>), under the system
/// tenant scope like every background path here, with the host's stopping token only: a closed browser
/// tab or a proxy timeout no longer cancels it. The outcome is recorded on the link exactly as the
/// request used to record it — <c>LastError</c> for a failure, the commit and file count for a success —
/// and the project's <see cref="GitHubSyncThrottle"/> lease is held until the pull ends, so "one sync per
/// project at a time" still holds and the dashboard's <c>syncing</c> flag is true for exactly that long.
/// </para>
///
/// <para>
/// A host shutdown cancels a running pull; what it wrote is already saved (the pull saves as it goes),
/// so the next sync resumes rather than starts over. The webhook path is separate
/// (<see cref="GitHubPushSyncService"/>) and unchanged.
/// </para>
/// </summary>
public sealed class GitHubSyncWorker : BackgroundService
{
    /// <summary>Syncs of different projects that may run at the same time.</summary>
    public const int MaxConcurrentSyncs = 3;

    /// <summary>What the link says when a sync stopped for a reason inside ReelBolt, not on GitHub.</summary>
    internal const string InternalFailureMessage =
        "The sync stopped because of a problem inside ReelBolt, not on GitHub. The files copied before it stopped "
        + "are kept, and the next sync carries on from there. Sync again in a few minutes; if it keeps happening, "
        + "contact support.";

    /// <summary>What the link says when GitHub could not be reached at all (a network failure or a timeout).</summary>
    internal const string UnreachableMessage =
        "ReelBolt could not reach GitHub: the connection failed or took too long. The files copied before it "
        + "stopped are kept. Sync again in a few minutes.";

    private readonly GitHubSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubSyncWorker> _logger;

    public GitHubSyncWorker(GitHubSyncQueue queue, IServiceScopeFactory scopeFactory, ILogger<GitHubSyncWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using SemaphoreSlim slots = new(MaxConcurrentSyncs);
        ConcurrentDictionary<Task, byte> running = new();

        try
        {
            await foreach (GitHubSyncRequest request in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await slots.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    request.Lease.Dispose();
                    throw;
                }

                Task task = Task.Run(async () =>
                {
                    try
                    {
                        await RunAsync(request, stoppingToken);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None);

                running[task] = 0;
                _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: fall through and let the running pulls save and stop.
        }

        // The pulls observe the same token; waiting lets each save its progress before the host exits.
        await Task.WhenAll(running.Keys);
    }

    /// <summary>
    /// Runs every sync queued right now, one after the other, and returns how many ran. For tests, which
    /// drive the queue without starting the hosted service.
    /// </summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        int ran = 0;
        while (_queue.TryDequeue(out GitHubSyncRequest? request))
        {
            await RunAsync(request, ct);
            ran++;
        }

        return ran;
    }

    /// <summary>
    /// One sync: load the link, pull, record the outcome on the link, release the project's slot (only
    /// after the outcome is saved, so <c>syncing</c> turns false when the result is readable). Never
    /// throws — not even on shutdown, since a cancelled pull has already saved what it wrote. Returns
    /// the outcome of a pull that finished, null otherwise.
    /// </summary>
    public async Task<GitHubPullOutcome?> RunAsync(GitHubSyncRequest request, CancellationToken ct)
    {
        using GitHubSyncThrottle.Lease lease = request.Lease;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            var pulls = scope.ServiceProvider.GetRequiredService<IGitHubRepositoryPullService>();
            // Nobody is "the current user" here: the work was authorized by the request that queued it,
            // so it runs under an explicit system scope, like every other background path.
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

            ProjectGitHubLink? link = await db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == request.ProjectId, ct);
            if (link is null)
            {
                // Unlinked while the sync waited: nothing to do.
                _logger.LogInformation("Skipped a queued GitHub sync of project {ProjectId}: it is no longer linked", request.ProjectId);
                return null;
            }

            try
            {
                GitHubPullOutcome outcome = await pulls.PullBranchAsync(link, request.Force, ct);
                _logger.LogInformation(
                    "GitHub sync of {Repository} into project {ProjectId}: {Status}",
                    link.RepositoryFullName, link.ProjectId, outcome.Status);
                return outcome;
            }
            catch (Exception ex) when (ex is GitHubApiException or GitHubSyncRefusedException)
            {
                // The pull service already wrote the plain-language reason on the link; this saves it,
                // together with whatever the pull wrote before it stopped.
                _logger.LogWarning("Syncing {Repository} into project {ProjectId} failed: {Message}", link.RepositoryFullName, link.ProjectId, ex.Message);
                if (link.LastError is null) Record(link, ex.Message);
                await db.SaveChangesAsync(CancellationToken.None);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("A GitHub sync of project {ProjectId} was stopped by shutdown; what it wrote is kept", request.ProjectId);
            return null;
        }
        catch (Exception ex)
        {
            // Not a GitHub answer — storage, the database, the network — so it is never worded as one.
            _logger.LogError(ex, "A GitHub sync of project {ProjectId} failed unexpectedly", request.ProjectId);
            await RecordUnexpectedAsync(request.ProjectId, IsUnreachable(ex) ? UnreachableMessage : InternalFailureMessage);
            return null;
        }
    }

    /// <summary>A network failure or an HTTP timeout on the way to GitHub, as opposed to a fault here.</summary>
    private static bool IsUnreachable(Exception ex) =>
        ex is HttpRequestException || (ex is TaskCanceledException && ex.InnerException is TimeoutException);

    /// <summary>
    /// Writes an unexpected failure on the link from a fresh scope: the scope the pull ran in may hold a
    /// context whose save is what failed.
    /// </summary>
    private async Task RecordUnexpectedAsync(Guid projectId, string message)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

            ProjectGitHubLink? link = await db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, CancellationToken.None);
            if (link is null) return;

            Record(link, message);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the failed GitHub sync of project {ProjectId} on its link", projectId);
        }
    }

    private static void Record(ProjectGitHubLink link, string message)
    {
        link.LastError = message.Length > 2000 ? message[..2000] : message;
        link.UpdatedAt = DateTime.UtcNow;
    }
}
