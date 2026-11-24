namespace ReelBolt.Shared.Drain;

/// <summary>
/// Read side of drain mode. Once <see cref="IsDraining"/> is true it stays true for the life of the process.
/// </summary>
public interface IDrainState
{
    bool IsDraining { get; }
}

/// <summary>A source of in-flight work counted into the status file's <c>activeWork</c>.</summary>
public interface IDrainWorkSource
{
    /// <summary>Short name used as a key in the status file's <c>detail</c> object.</summary>
    string Name { get; }

    /// <summary>Work accepted and not yet finished or handed back.</summary>
    int ActiveCount { get; }
}

/// <summary>Something that must stop taking new work when drain begins (e.g. broker receive endpoints).</summary>
public interface IDrainTarget
{
    /// <summary>Called once, when drain is first observed. Must not throw; failures are logged by the caller.</summary>
    Task BeginDrainAsync(CancellationToken cancellationToken);

    /// <summary>True once no new work can arrive through this target any more.</summary>
    bool IsFullyStopped { get; }
}

/// <summary>Paths and cadence of the drain contract.</summary>
public sealed class DrainOptions
{
    public const string DefaultSignalPath = "/tmp/reelbolt-drain";
    public const string DefaultStatusPath = "/tmp/reelbolt-drain-status.json";

    public string SignalPath { get; set; } = DefaultSignalPath;
    public string StatusPath { get; set; } = DefaultStatusPath;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(5);
}
