using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// Runs the avatar builds offered on <see cref="AvatarBuildQueue"/>, <see cref="AvatarBuildOptions.Concurrency"/> at a
/// time (docs/design/avatar-objects.md "Avatar variants": "one background worker").
/// <para>
/// Each job runs in its own DI scope (its own DbContext) under an explicit system tenant scope: a job belongs to
/// whichever project asked, not to a request. It is cancelled only by the host's stopping token, never by the token of
/// the request that queued it, so a job outlives the 60 seconds nginx gives a request.
/// </para>
/// </summary>
public sealed class AvatarBuildWorker : BackgroundService
{
    private readonly AvatarBuildQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AvatarBuildOptions _options;
    private readonly ILogger<AvatarBuildWorker> _logger;

    public AvatarBuildWorker(
        AvatarBuildQueue queue, IServiceScopeFactory scopeFactory, IOptions<AvatarBuildOptions> options, ILogger<AvatarBuildWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Clamp(_options.Concurrency, 1, 8)).Select(_ => DrainAsync(stoppingToken)));

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (Guid jobId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(jobId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. A job in hand gave its lease back; whatever was only queued is offered again by the
            // sweeper of the next process.
        }
    }

    /// <summary>Runs one offered job and always gives its queue entry back. Public so a test can drive the worker without the host.</summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
            AvatarBuildProcessor processor = scope.ServiceProvider.GetRequiredService<AvatarBuildProcessor>();
            await processor.RunAsync(jobId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The processor records its own failures; this is the net under it, so one broken job never stops the worker.
            _logger.LogError(ex, "Avatar build {JobId} failed unexpectedly", jobId);
        }
        finally
        {
            _queue.Complete(jobId);
        }
    }
}

/// <summary>
/// Offers the avatar builds that need a worker, every <see cref="AvatarBuildOptions.SweepInterval"/>: a job still
/// <c>queued</c> (the process that queued it stopped before a worker took it), a job that waits and whose time to look
/// again has come, a job in a working state whose lease has passed (its worker stopped: a restart, a deploy), and an
/// ended job whose hold was never closed.
/// <para>
/// <b>Restart rule (documented choice):</b> a lease in the job's notes, not a column. A worker writes
/// <c>LeaseUntil = now + StaleAfter</c> (default 300 s) when it takes a job and with every step it saves, under the
/// row's <c>xmin</c> token; no step takes longer than one bounded picture edit (90 s). A job nobody saved for longer
/// than that is taken again by whichever replica sweeps first, and goes on after the last picture it noted.
/// </para>
/// </summary>
public sealed class AvatarBuildSweeper : BackgroundService
{
    /// <summary>Lets migrations and seeding finish before the first sweep.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>The most open jobs one sweep reads.</summary>
    public const int MaxPerSweep = 200;

    /// <summary>How far back an ended job is looked at for a hold that was never closed.</summary>
    public static readonly TimeSpan EndedLookBack = TimeSpan.FromDays(1);

    private readonly AvatarBuildQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AvatarBuildOptions _options;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<AvatarBuildSweeper> _logger;

    public AvatarBuildSweeper(
        AvatarBuildQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<AvatarBuildOptions> options,
        IConfiguration configuration,
        ILogger<AvatarBuildSweeper> logger,
        TimeProvider? time = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    _logger.LogError(ex, "The avatar build sweep failed; retrying after the interval");
                }

                await Task.Delay(_options.SweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep: offers the jobs that are due and answers how many it offered. Public for tests.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        // No avatar switch on = no job can have been asked for; nothing to read.
        if (!AvatarOptions.AnyEnabledIn(_configuration)) return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        DateTime now = _time.GetUtcNow().UtcDateTime;
        var open = await db.AvatarBuildJobs.AsNoTracking()
            .Where(j => j.State != AvatarBuildStates.Ready && j.State != AvatarBuildStates.Failed && j.State != AvatarBuildStates.Cancelled)
            .OrderBy(j => j.UpdatedAt)
            .Select(j => new { j.Id, j.State, j.ParamsJson })
            .Take(MaxPerSweep)
            .ToListAsync(ct);
        DateTime endedSince = now - EndedLookBack;
        var ended = await db.AvatarBuildJobs.AsNoTracking()
            .Where(j => (j.State == AvatarBuildStates.Failed || j.State == AvatarBuildStates.Cancelled || j.State == AvatarBuildStates.Ready)
                        && j.HoldId != null && j.UpdatedAt >= endedSince)
            .OrderBy(j => j.UpdatedAt)
            .Select(j => new { j.Id, j.State, j.ParamsJson })
            .Take(MaxPerSweep)
            .ToListAsync(ct);

        int offered = 0;
        foreach (var job in open)
        {
            if (_queue.Holds(job.Id)) continue;
            if (job.State != AvatarBuildStates.Queued && !AvatarBuildParams.Read(job.ParamsJson).IsDue(now)) continue;
            if (_queue.Enqueue(job.Id)) offered++;
        }

        foreach (var job in ended)
        {
            if (_queue.Holds(job.Id) || AvatarBuildParams.Read(job.ParamsJson).HoldClosed) continue;
            if (_queue.Enqueue(job.Id)) offered++;
        }

        if (offered > 0) _logger.LogInformation("The avatar build sweep offered {Count} job(s)", offered);
        return offered;
    }
}
