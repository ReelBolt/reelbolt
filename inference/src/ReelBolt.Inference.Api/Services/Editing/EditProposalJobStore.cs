using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>The status and body a proposal request ends with; the poll route replays exactly what the synchronous route answers.</summary>
public sealed record EditProposalOutcome(int Status, object Body);

public enum EditProposalPollState
{
    /// <summary>Unknown, expired, or someone else's job (one indistinguishable answer).</summary>
    NotFound,
    Running,

    /// <summary>The answer was released (never collected in time): nothing was charged and the body is not returned.</summary>
    Expired,

    /// <summary>Final answer; any pending charge has been settled before this was returned.</summary>
    Ready
}

public sealed record EditProposalPoll(EditProposalPollState State, EditProposalOutcome? Outcome = null);

/// <summary>
/// In-memory store of background proposal jobs (<c>POST .../proposals?background=true</c>). Inference runs a single replica
/// and <see cref="EditProposalRateLimiter"/> is in-memory too, so a process-local store is acceptable: a restart cancels the
/// jobs (their holds are released) and the client simply asks again.
/// Charge on delivery: a finished job keeps its hold open; the first <see cref="PollAsync"/> that returns the final answer
/// settles it (once, under the job's lock, before the body goes out). A finished job nobody reads within
/// <see cref="EditProposalOptions.AbandonAfterSeconds"/> is released and answers <see cref="EditProposalPollState.Expired"/>.
/// All state changes of one job happen under <c>lock(job)</c>, so delivery and release never both happen to the same job.
/// </summary>
public sealed class EditProposalJobStore : IDisposable
{
    private enum JobState { Running, Finished, Delivering, Delivered, Released }

    private sealed class Job(Guid user, Guid project, Guid timeline, DateTimeOffset now, CancellationTokenSource cts)
    {
        public Guid User { get; } = user;
        public Guid Project { get; } = project;
        public Guid Timeline { get; } = timeline;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTimeOffset Created { get; } = now;

        // Everything below is read and written under lock(job).
        public DateTimeOffset LastPolled { get; set; } = now;
        public DateTimeOffset? FinishedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public JobState State { get; set; } = JobState.Running;
        public EditProposalOutcome? Outcome { get; set; }
        public Func<bool, Task>? Close { get; set; }
        public Task? CloseTask { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly IOptions<EditProposalOptions> _options;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IDisposable? _stoppingRegistration;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _reserved = new();
    private volatile bool _disposed;

    public EditProposalJobStore(IOptions<EditProposalOptions> options, TimeProvider? clock = null, IHostApplicationLifetime? lifetime = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _stoppingRegistration = lifetime?.ApplicationStopping.Register(() =>
        {
            _stopping.Cancel();
            ReleasePending();
        });
        TimeSpan period = TimeSpan.FromSeconds(Math.Clamp(Math.Min(options.Value.AbandonAfterSeconds, options.Value.TimeoutSeconds) / 4, 0.05, 5));
        _timer = _clock.CreateTimer(_ => Sweep(), null, period, period);
    }

    public EditProposalOptions Options => _options.Value;

    private static bool IsRunning(Job j) { lock (j) return j.State == JobState.Running; }

    /// <summary>Background jobs of this user that are still running (or reserved).</summary>
    public int RunningFor(Guid user)
    {
        Sweep();
        lock (_gate) return _jobs.Values.Count(j => j.User == user && IsRunning(j)) + _reserved.GetValueOrDefault(user);
    }

    /// <summary>
    /// Atomically takes one running slot for the user (<see cref="EditProposalOptions.MaxRunningPerUser"/>), one place among
    /// running jobs store-wide (<see cref="EditProposalOptions.MaxJobs"/>: running plus reserved only, so finished results
    /// can never starve other users) and one stored place for the user (<see cref="EditProposalOptions.MaxStoredPerUser"/>:
    /// the oldest delivered or released results are evicted first). False = refused. A reservation is consumed by
    /// <see cref="Start"/> or given back by <see cref="CancelReservation"/>.
    /// </summary>
    public bool TryReserve(Guid user)
    {
        Sweep();
        lock (_gate)
        {
            EditProposalOptions o = _options.Value;
            List<Job> mine = _jobs.Values.Where(j => j.User == user).ToList();
            int reservedMine = _reserved.GetValueOrDefault(user);
            if (mine.Count(IsRunning) + reservedMine >= Math.Max(1, o.MaxRunningPerUser)) return false;
            if (_jobs.Values.Count(IsRunning) + _reserved.Values.Sum() >= Math.Max(1, o.MaxJobs)) return false;

            int storedCap = Math.Max(1, o.MaxStoredPerUser);
            while (mine.Count + reservedMine >= storedCap)
            {
                Job? victim = mine
                    .Where(j => { lock (j) return j.State is JobState.Delivered or JobState.Released; })
                    .OrderBy(j => j.EndedAt)
                    .FirstOrDefault();
                if (victim is null) return false;
                mine.Remove(victim);
                RemoveJob(victim);
            }

            _reserved[user] = reservedMine + 1;
            return true;
        }
    }

    public void CancelReservation(Guid user)
    {
        lock (_gate)
        {
            int n = _reserved.GetValueOrDefault(user) - 1;
            if (n > 0) _reserved[user] = n; else _reserved.Remove(user);
        }
    }

    private void RemoveJob(Job job)
    {
        foreach (KeyValuePair<Guid, Job> kv in _jobs)
        {
            if (ReferenceEquals(kv.Value, job) && _jobs.TryRemove(kv.Key, out Job? gone)) gone.Cts.Dispose();
        }
    }

    /// <summary>Starts <paramref name="work"/> on a background task; its token cancels on abandonment or host shutdown.</summary>
    public Guid Start(Guid user, Guid project, Guid timeline, Func<CancellationToken, Task<(EditProposalOutcome Outcome, Func<bool, Task>? Close)>> work)
    {
        Sweep();
        Guid id = Guid.NewGuid();
        Job job = new(user, project, timeline, _clock.GetUtcNow(), CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
        lock (_gate)
        {
            _jobs[id] = job;
            int n = _reserved.GetValueOrDefault(user) - 1;
            if (n > 0) _reserved[user] = n; else _reserved.Remove(user);
        }

        _ = Task.Run(async () =>
        {
            EditProposalOutcome outcome;
            Func<bool, Task>? close = null;
            try
            {
                (outcome, close) = await work(job.Cts.Token);
            }
            catch (Exception)
            {
                // Whatever threw has already released its hold; nothing is pending.
                close = null;
                outcome = new EditProposalOutcome(StatusCodes.Status424FailedDependency, new
                {
                    code = "PROPOSAL_FAILED",
                    creditsUsed = 0,
                    message = "The AI did not answer this time. You were not charged; please try again."
                });
            }

            Task? releasing = null;
            lock (job)
            {
                job.Outcome = outcome;
                job.Close = close;
                job.FinishedAt = _clock.GetUtcNow();
                job.State = JobState.Finished;
                if (_disposed) releasing = ReleaseLocked(job);
            }

            if (releasing is not null) await releasing;
        });
        return id;
    }

    /// <summary>True once the job has finished (its answer exists), without delivering it.</summary>
    public bool IsFinished(Guid jobId) =>
        _jobs.TryGetValue(jobId, out Job? job) && job is not null && !IsRunning(job);

    /// <summary>
    /// Reads a job for its owner (user, project and timeline must match, else <see cref="EditProposalPollState.NotFound"/>).
    /// A poll counts as liveness. The first read of a finished answer settles its pending charge, on this job instance and
    /// under its lock, before returning <see cref="EditProposalPollState.Ready"/>; a job already released answers
    /// <see cref="EditProposalPollState.Expired"/> and never returns its body.
    /// </summary>
    public async Task<EditProposalPoll> PollAsync(Guid jobId, Guid user, Guid project, Guid timeline)
    {
        Sweep();
        if (!_jobs.TryGetValue(jobId, out Job? job) || job.User != user || job.Project != project || job.Timeline != timeline)
            return new EditProposalPoll(EditProposalPollState.NotFound);

        Task? closing;
        lock (job)
        {
            job.LastPolled = _clock.GetUtcNow();
            switch (job.State)
            {
                case JobState.Running:
                    return new EditProposalPoll(EditProposalPollState.Running);
                case JobState.Released:
                    return new EditProposalPoll(EditProposalPollState.Expired);
                case JobState.Finished:
                    job.State = JobState.Delivering;
                    Func<bool, Task>? close = job.Close;
                    job.Close = null;
                    job.CloseTask = close is null ? null : Task.Run(() => close(true));
                    closing = job.CloseTask;
                    break;
                default:
                    closing = job.CloseTask;
                    break;
            }
        }

        if (closing is not null) await closing;
        lock (job)
        {
            if (job.State == JobState.Delivering)
            {
                job.State = JobState.Delivered;
                job.EndedAt = _clock.GetUtcNow();
            }

            return new EditProposalPoll(EditProposalPollState.Ready, job.Outcome);
        }
    }

    // Caller holds lock(job). Only an undelivered finished job is released; delivering and delivered jobs never are.
    private Task? ReleaseLocked(Job job)
    {
        if (job.State != JobState.Finished) return null;
        job.State = JobState.Released;
        job.EndedAt = _clock.GetUtcNow();
        Func<bool, Task>? close = job.Close;
        job.Close = null;
        return close is null ? null : job.CloseTask = Task.Run(() => close(false));
    }

    private void ReleasePending()
    {
        foreach (Job job in _jobs.Values)
        {
            Task? t;
            lock (job) t = ReleaseLocked(job);
            t?.GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Cancels running jobs nobody polls, releases finished answers nobody collected, and drops entries past their retention
    /// (delivered or released: <see cref="EditProposalOptions.DeliveredRetentionSeconds"/>; a finished answer that never had
    /// a charge pending: <see cref="EditProposalOptions.ResultRetentionSeconds"/>).
    /// </summary>
    public void Sweep()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        EditProposalOptions o = _options.Value;
        foreach (Job job in _jobs.Values)
        {
            bool remove = false;
            lock (job)
            {
                switch (job.State)
                {
                    case JobState.Running:
                        if ((now - job.LastPolled).TotalSeconds > o.AbandonAfterSeconds && !job.Cts.IsCancellationRequested) job.Cts.Cancel();
                        break;
                    case JobState.Finished:
                        if (job.Close is not null)
                        {
                            if ((now - job.FinishedAt!.Value).TotalSeconds > o.AbandonAfterSeconds) _ = ReleaseLocked(job);
                        }
                        else if ((now - job.FinishedAt!.Value).TotalSeconds > o.ResultRetentionSeconds)
                        {
                            remove = true;
                        }

                        break;
                    case JobState.Delivered:
                    case JobState.Released:
                        remove = (now - job.EndedAt!.Value).TotalSeconds > o.DeliveredRetentionSeconds;
                        break;
                }
            }

            if (remove) RemoveJob(job);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _stoppingRegistration?.Dispose();
        _stopping.Cancel();
        ReleasePending();
    }
}
