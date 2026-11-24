namespace ReelBolt.Inference.Api.Services.Workflows;

using ReelBolt.Shared.Workflows;

/// <summary>
/// The answer to "may this user start another run in this project?".
/// </summary>
/// <param name="Allowed">True when the run was started (the callback ran) and counted.</param>
/// <param name="Bucket">The bucket the run was counted against.</param>
/// <param name="Used">Runs in the window, including this one when it was allowed.</param>
/// <param name="Limit">The bucket's hourly limit.</param>
/// <param name="WindowSeconds">The rolling window, 3600.</param>
/// <param name="RetryAfterSeconds">When refused, seconds until the oldest run leaves the window (at least 1); 0 when allowed.</param>
/// <param name="Busy">True when the refusal is "could not take the lock in time" rather than "limit reached".</param>
public sealed record RunRateDecision(
    bool Allowed,
    RunRateBucket Bucket,
    int Used,
    int Limit,
    int WindowSeconds,
    int RetryAfterSeconds,
    bool Busy = false);

/// <summary>
/// Enforces the hourly run limit per (user, project) in two buckets: runs that hold model or vendor
/// credits (<see cref="RunRateBucket.Paid"/>, 10 per hour) and free deterministic runs
/// (<see cref="RunRateBucket.Free"/>, 60 per hour). Chat, MCP and the assistant REST path share it, so moving
/// from one surface to another is not a way around the limit; revisions count as paid.
/// </summary>
public interface IAssistantExecutionGuard
{
    /// <summary>
    /// Counts the caller's runs of <paramref name="bucket"/> in the last hour and, below the limit, runs
    /// <paramref name="insertCallback"/> while still holding the (user, project) lock, then records the run it
    /// started. A callback that returns <c>null</c> started nothing (for example a plan refusal) and consumes nothing.
    /// </summary>
    /// <param name="source"><c>"execute"</c> or <c>"revision"</c>.</param>
    /// <param name="insertCallback">Queues the run and returns its execution id. Only called when allowed.</param>
    Task<RunRateDecision> TryAcquireAsync(
        Guid userId,
        Guid projectId,
        RunRateBucket bucket,
        string source,
        Func<Task<Guid?>> insertCallback,
        CancellationToken ct);

    /// <summary>
    /// The execute routes' overload (T-012, no TOCTOU): <paramref name="classify"/> runs INSIDE the (user, project)
    /// lock, after it is taken and before the count, so the bucket that is counted, recorded and handed to the run
    /// is the one computed from the steps as they are under the lock. While <paramref name="insertCallback"/> runs,
    /// <see cref="RunRateAdmission.Current"/> is that bucket, so the queued run carries it to the engine
    /// (<c>WorkflowExecutionRequested.AdmittedRunRateBucket</c>). A classify that throws starts nothing.
    /// A busy refusal (lock not taken) reports the paid bucket: nothing was classified, so nothing is assumed free.
    /// <para>The default body exists only so test doubles that implement the fixed-bucket overload keep compiling: it
    /// classifies BEFORE any lock and stamps nothing. <see cref="AssistantExecutionGuard"/> overrides it; a real guard
    /// must too.</para>
    /// </summary>
    async Task<RunRateDecision> TryAcquireAsync(
        Guid userId,
        Guid projectId,
        Func<Task<RunRateBucket>> classify,
        string source,
        Func<Task<Guid?>> insertCallback,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(classify);
        return await TryAcquireAsync(userId, projectId, await classify(), source, insertCallback, ct);
    }
}

/// <summary>
/// The bucket the run being started right now was admitted under, set by
/// <see cref="IAssistantExecutionGuard"/>'s classify-under-lock overload for the duration of its start callback
/// (an async-local, so it reaches <c>WorkflowExecutionService</c> without widening its interface). Null outside
/// such a callback: human REST routes, retries, revisions.
/// </summary>
public static class RunRateAdmission
{
    private static readonly AsyncLocal<RunRateBucket?> Ambient = new();

    public static RunRateBucket? Current => Ambient.Value;

    /// <summary>The wire value for <c>WorkflowExecutionRequested.AdmittedRunRateBucket</c>: "paid", "free" or null.</summary>
    public static string? CurrentWireValue => Current is { } bucket ? RunRateRefusal.BucketName(bucket) : null;

    internal static IDisposable Enter(RunRateBucket bucket)
    {
        RunRateBucket? previous = Ambient.Value;
        Ambient.Value = bucket;
        return new Restore(previous);
    }

    private sealed class Restore(RunRateBucket? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private static readonly AsyncLocal<CommittedRun?> Commit = new();

    /// <summary>
    /// S-11: called by <c>WorkflowExecutionService</c> right after the Queued execution row is committed (before the
    /// publish). Inside a guard callback it tells the guard a run now exists that the recovery sweeper will start
    /// even if the publish fails, so its run-rate event must be kept. A no-op anywhere else.
    /// </summary>
    public static void ReportCommitted(Guid executionId)
    {
        if (Commit.Value is { } run)
            run.ExecutionId ??= executionId;
    }

    internal static IDisposable TrackCommit(CommittedRun run)
    {
        CommittedRun? previous = Commit.Value;
        Commit.Value = run;
        return new RestoreCommit(previous);
    }

    /// <summary>A mutable holder, so a write deep inside the callback's async flow is visible to the guard.</summary>
    internal sealed class CommittedRun
    {
        public Guid? ExecutionId { get; set; }
    }

    private sealed class RestoreCommit(CommittedRun? previous) : IDisposable
    {
        public void Dispose() => Commit.Value = previous;
    }
}
