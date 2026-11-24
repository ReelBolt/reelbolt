using Microsoft.Extensions.Configuration;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Bounds on <c>WatchExecution</c>: whether the assistant may ask to be woken when a run finishes, and how many
/// such watches a thread and a workspace may hold at once. Config section <c>Assistant:RunWake</c>.
/// </summary>
public static class AssistantRunWakeLimits
{
    public const string EnabledKey = "Assistant:RunWake:Enabled";
    public const string MaxPendingPerThreadKey = "Assistant:RunWake:MaxPendingPerThread";
    public const string MaxPendingPerOrganizationKey = "Assistant:RunWake:MaxPendingPerOrganization";

    public const int DefaultMaxPendingPerThread = 3;
    public const int DefaultMaxPendingPerOrganization = 20;

    public const string SweepIntervalSecondsKey = "Assistant:RunWake:SweepIntervalSeconds";
    public const string WatchTtlHoursKey = "Assistant:RunWake:WatchTtlHours";
    public const string BatchSizeKey = "Assistant:RunWake:BatchSize";
    public const string MaxWakesPerThreadPerDayKey = "Assistant:RunWake:MaxWakesPerThreadPerDay";
    public const string MaxConsecutiveWakesKey = "Assistant:RunWake:MaxConsecutiveWakes";
    public const string MaxAttemptsKey = "Assistant:RunWake:MaxAttempts";

    public const int DefaultSweepIntervalSeconds = 5;
    public const int DefaultWatchTtlHours = 24;
    public const int DefaultBatchSize = 10;
    public const int DefaultMaxWakesPerThreadPerDay = 20;
    public const int DefaultMaxConsecutiveWakes = 3;
    public const int DefaultMaxAttempts = 5;

    /// <summary>Re-runs of a wake turn that ran but left no reply (it may already have done tool work).</summary>
    public const int MaxNoReplyReruns = 2;

    public const string MaxConcurrentWakesKey = "Assistant:RunWake:MaxConcurrentWakes";
    public const int DefaultMaxConcurrentWakes = 3;

    /// <summary>Wake turns one process runs at once (different threads only).</summary>
    public static int MaxConcurrentWakes(IConfiguration? configuration) =>
        Positive(configuration, MaxConcurrentWakesKey, DefaultMaxConcurrentWakes);

    /// <summary>
    /// How long a claim lasts before a replica that died holding it is presumed gone. It must outlast the longest wake turn:
    /// the turn wall clock plus one approval wait plus a margin, so a live turn is never re-claimed under it.
    /// </summary>
    public static TimeSpan ClaimLease(IConfiguration? configuration)
    {
        int approval = Positive(configuration, ReelBolt.Inference.Api.Services.Assistant.AssistantApprovalGate.TimeoutKey,
            ReelBolt.Inference.Api.Services.Assistant.AssistantApprovalGate.DefaultTimeoutSeconds);
        return TimeSpan.FromSeconds(AssistantChatLimits.MaxTurnSeconds(configuration) + approval + 120);
    }

    /// <summary>Seconds between sweeps; 0 (or less) switches the sweep off.</summary>
    public static int SweepIntervalSeconds(IConfiguration? configuration) =>
        Math.Max(0, configuration?.GetValue(SweepIntervalSecondsKey, DefaultSweepIntervalSeconds) ?? DefaultSweepIntervalSeconds);

    public static TimeSpan WatchTtl(IConfiguration? configuration) =>
        TimeSpan.FromHours(Positive(configuration, WatchTtlHoursKey, DefaultWatchTtlHours));

    public static int BatchSize(IConfiguration? configuration) => Positive(configuration, BatchSizeKey, DefaultBatchSize);

    public static int MaxWakesPerThreadPerDay(IConfiguration? configuration) =>
        Positive(configuration, MaxWakesPerThreadPerDayKey, DefaultMaxWakesPerThreadPerDay);

    public static int MaxConsecutiveWakes(IConfiguration? configuration) =>
        Positive(configuration, MaxConsecutiveWakesKey, DefaultMaxConsecutiveWakes);

    public static int MaxAttempts(IConfiguration? configuration) => Positive(configuration, MaxAttemptsKey, DefaultMaxAttempts);

    public static bool Enabled(IConfiguration? configuration) =>
        configuration?.GetValue(EnabledKey, true) ?? true;

    public static int MaxPendingPerThread(IConfiguration? configuration) =>
        Positive(configuration, MaxPendingPerThreadKey, DefaultMaxPendingPerThread);

    public static int MaxPendingPerOrganization(IConfiguration? configuration) =>
        Positive(configuration, MaxPendingPerOrganizationKey, DefaultMaxPendingPerOrganization);

    private static int Positive(IConfiguration? configuration, string key, int fallback)
    {
        int value = configuration?.GetValue(key, fallback) ?? fallback;
        return value > 0 ? value : fallback;
    }
}
