using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The attribution and running totals of one metered unit of work (one workflow step, one assistant
/// turn). Created by <see cref="UsageScope.Begin"/> and flowed to every metered call beneath it by an
/// <see cref="AsyncLocal{T}"/>; the totals are interlocked because a Parallel step, a room or an
/// agent's tool loop calls the model from several tasks under the same scope.
/// </summary>
public sealed class UsageScopeState
{
    private long _callSeq;
    private long _creditsMillis;
    private long _inputTokens;
    private long _outputTokens;
    private int _unpriced;
    private long _unpricedClaimMillis;

    internal UsageScopeState(
        Guid? organizationId, Guid? projectId, Guid? executionId, Guid? stepResultId, Guid? userId, UsageSource source)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        ExecutionId = executionId;
        StepResultId = stepResultId;
        UserId = userId;
        Source = source;
    }

    /// <summary>Unique per scope; the idempotency-key prefix when there is no step result.</summary>
    public Guid ScopeId { get; } = Guid.NewGuid();

    public Guid? OrganizationId { get; }
    public Guid? ProjectId { get; }
    public Guid? ExecutionId { get; }
    public Guid? StepResultId { get; }
    public Guid? UserId { get; }
    public UsageSource Source { get; }

    /// <summary>Millicredits priced so far inside this scope (the mid-step ceiling input for B6b).</summary>
    public long RunningCreditsMillis => Interlocked.Read(ref _creditsMillis);

    /// <summary>Chat/decision input tokens metered so far (cached tokens included).</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// True once a Platform-billed usage in this scope could not be priced (recorded at 0 credits): the turn it
    /// belongs to must not settle as if it cost nothing.
    /// </summary>
    public bool SawUnpricedPlatformUsage => Volatile.Read(ref _unpriced) != 0;

    /// <summary>
    /// How many Platform-billed usages in this scope could not be priced so far. A pre-claimed step compares it before and
    /// after it ran to tell whether IT metered unpriced usage (T-026).
    /// </summary>
    public int UnpricedPlatformUsageCount => Volatile.Read(ref _unpriced);

    /// <summary>
    /// T-026 (finding 3): the claims of the pre-claimed steps that metered unpriced Platform usage (which landed at 0
    /// credits). Such a step counts at its claim, not 0, towards the turn's ceiling, its funds check and its settlement.
    /// </summary>
    public long UnpricedStepClaimMillis => Interlocked.Read(ref _unpricedClaimMillis);

    /// <summary>Counts a step that metered unpriced Platform usage at its claim (see <see cref="UnpricedStepClaimMillis"/>).</summary>
    public void AddUnpricedStepClaim(long millis)
    {
        if (millis > 0) Interlocked.Add(ref _unpricedClaimMillis, millis);
    }

    internal void MarkUnpriced() => Interlocked.Increment(ref _unpriced);

    /// <summary>The next 1-based per-scope call number (the <c>callSeq</c> of the idempotency key).</summary>
    public long NextCallSeq() => Interlocked.Increment(ref _callSeq);

    internal void AddCredits(long millis) => Interlocked.Add(ref _creditsMillis, millis);

    internal void AddTokens(long input, long output)
    {
        Interlocked.Add(ref _inputTokens, input);
        Interlocked.Add(ref _outputTokens, output);
    }
}

/// <summary>
/// The ambient usage scope. Metered calls read <see cref="Current"/> instead of every call site passing
/// attribution down, so adding a model call never needs a metering change.
/// </summary>
public static class UsageScope
{
    private static readonly AsyncLocal<UsageScopeState?> CurrentState = new();

    public static UsageScopeState? Current => CurrentState.Value;

    /// <summary>Millicredits priced so far in the current scope; 0 outside any scope.</summary>
    public static long RunningCreditsMillis => Current?.RunningCreditsMillis ?? 0;

    /// <summary>Opens a scope for the current async flow; disposing restores the previous one.</summary>
    public static IDisposable Begin(
        Guid? organizationId, Guid? projectId, Guid? executionId, Guid? stepResultId, Guid? userId, UsageSource source)
    {
        UsageScopeState? previous = CurrentState.Value;
        CurrentState.Value = new UsageScopeState(organizationId, projectId, executionId, stepResultId, userId, source);
        return new Restore(previous);
    }

    /// <summary>
    /// Opens a scope only when the current flow has none, so an outer scope keeps its attribution. Always
    /// returns a disposable (a no-op when a scope already exists), so callers can use it in a <c>using</c>.
    /// </summary>
    public static IDisposable BeginIfNone(
        Guid? organizationId, Guid? projectId, Guid? userId, UsageSource source)
        => Current is null
            ? Begin(organizationId, projectId, null, null, userId, source)
            : NoopScope.Instance;

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Restore(UsageScopeState? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentState.Value = previous;
        }
    }
}
