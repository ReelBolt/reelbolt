namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// Retention tunables of the editor (config section <c>Editing</c>): how long a deleted timeline
/// stays restorable and how long an automatic checkpoint is kept (decision D14: automatic
/// checkpoints 30 days, named versions forever).
/// </summary>
public sealed record EditingRetentionOptions
{
    public const string SectionName = "Editing";

    public const int DefaultDays = 30;

    /// <summary><c>Editing:TrashRetentionDays</c> — a deleted timeline is restorable for this long, then purged.</summary>
    public int TrashRetentionDays { get; init; } = DefaultDays;

    /// <summary><c>Editing:AutoCheckpointDays</c> — an automatic checkpoint expires this long after it was taken.</summary>
    public int AutoCheckpointDays { get; init; } = DefaultDays;

    /// <summary>The trash window, never below one day (a zero or negative value would purge instantly).</summary>
    public TimeSpan TrashWindow => TimeSpan.FromDays(Math.Max(1, TrashRetentionDays));

    /// <summary>The automatic-checkpoint lifetime, never below one day.</summary>
    public TimeSpan AutoCheckpointWindow => TimeSpan.FromDays(Math.Max(1, AutoCheckpointDays));
}
